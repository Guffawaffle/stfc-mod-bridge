//! One actor-local custody sum; configuration never delegates to another writer.
use super::*;
use std::{cell::RefCell, rc::Rc};

/// Opaque, nonclone custody. Protected preparation remains inside the owned
/// workspace's transaction; no serialized handle or second workspace exists.
///
/// ```compile_fail
/// use bridge_app::configuration::ConfigurationCustody;
/// fn serialize(custody: &ConfigurationCustody<(), ()>) {
///     serde_json::to_vec(custody).unwrap();
/// }
/// ```
/// ```compile_fail
/// use bridge_app::configuration::ConfigurationCustody;
/// fn duplicate(custody: ConfigurationCustody<(), ()>) {
///     let _ = custody.clone();
/// }
/// ```
pub struct ConfigurationCustody<C, X> {
    kind: CustodyKind<C, X>,
    binding: CapturedOperation,
    token: Rc<()>,
    recovery: RefCell<Option<RecoveryRef>>,
}
enum CustodyKind<C, X> {
    Delegated(C),
    Prepared {
        slot: Box<Option<PreparedConfiguration>>,
        attempted: bool,
        recovery: ConfigurationRecoveryTransaction,
    },
    Active(Box<ConfigurationTransaction<X>>),
    Recovery(ConfigurationRecoveryTransaction),
}
/// The actual owning lease lives through native result inspection, durable
/// terminal append and local publication. A matching token prevents exchanging
/// leases between two captures with otherwise identical semantic resources.
pub struct ConfigurationLease<L, D> {
    kind: LeaseKind<L, D>,
    resources: Vec<ResourceKey>,
    token: Rc<()>,
}
enum LeaseKind<L, D> {
    Delegated(L),
    Configuration(D),
}
impl<L, D> ResourceLease for ConfigurationLease<L, D> {
    fn resources(&self) -> &[ResourceKey] {
        &self.resources
    }
}
fn invalid() -> Box<BridgeError> {
    error(ErrorCode::InvalidRequest)
}
fn same_resources(left: &[ResourceKey], right: &[ResourceKey]) -> bool {
    left.len() == right.len()
        && right
            .iter()
            .enumerate()
            .all(|(index, value)| left.contains(value) && !right[..index].contains(value))
}
fn configuration_semantics(capture: PreparedCapture) -> PlanSemantics {
    PlanSemantics {
        hash_profile: HashProfile::BridgePlanSemanticJsonV1,
        action: match capture {
            PreparedCapture::SaveConfiguration { .. } => ActionId::SaveConfiguration,
            _ => ActionId::RestoreConfiguration,
        },
        capture,
        trust_domain: TrustDomain::Configuration,
        effects: BoundedList::new(vec![ProposedEffect::WriteConfiguration]).expect("one effect"),
    }
}
fn configuration_resources(
    capture: &PreparedCapture,
) -> Result<Vec<ResourceKey>, Box<BridgeError>> {
    let document = match capture {
        PreparedCapture::SaveConfiguration { input } => &input.draft.draft.document,
        PreparedCapture::RestoreConfiguration { input } => &input.document,
        _ => return Err(invalid()),
    };
    let profile = match &document.target.profile {
        ProfileBinding::Ordinary { owner_scope, .. } => ResourceKey::OrdinaryProfile {
            owner: owner_scope.clone(),
        },
        ProfileBinding::Isolated { id, .. } => ResourceKey::IsolatedProfile { id: id.clone() },
    };
    Ok(vec![
        ResourceKey::Installation {
            physical_id: document.target.installation.physical_id().clone(),
        },
        profile,
        ResourceKey::Document {
            id: document.document_id.clone(),
            target: document.target.clone(),
        },
    ])
}
fn is_configuration(semantics: &PlanSemantics) -> bool {
    matches!(
        semantics.capture,
        PreparedCapture::SaveConfiguration { .. } | PreparedCapture::RestoreConfiguration { .. }
    )
}
impl<C, X> ConfigurationCustody<C, X> {
    fn check<L, D>(
        &self,
        semantics: &PlanSemantics,
        lease: &ConfigurationLease<L, D>,
    ) -> ApplicationResult<()> {
        if self.binding.semantics != *semantics
            || !same_resources(&self.binding.resources, &lease.resources)
            || !Rc::ptr_eq(&self.token, &lease.token)
        {
            return Err(invalid());
        }
        Ok(())
    }
    fn check_recovery(
        &self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
    ) -> ApplicationResult<()> {
        if self.recovery.borrow().as_ref() != Some(recovery)
            || recovery.operation_id != operation.operation_id
        {
            return Err(invalid());
        }
        Ok(())
    }
}
impl<
    P: OperationPorts,
    T: TomlPreparation,
    S: SchemaSource,
    O: DocumentOwner,
    E: SensitiveEntry,
    I: ConfigurationIds,
> OperationPorts for ConfigurationServices<P, T, S, O, E, I>
{
    type Custody = ConfigurationCustody<P::Custody, O::Transaction>;
    type Lease = ConfigurationLease<P::Lease, O::Lease>;

    fn capture(
        &mut self,
        intent: &MutationIntent,
        host: &HostEpoch,
    ) -> Result<(CapturedOperation, Self::Custody), Box<BridgeError>> {
        let (binding, kind) = match intent {
            MutationIntent::SaveConfiguration(_) | MutationIntent::RestoreConfiguration(_) => {
                if !self.workspace.configuration_operations_available() {
                    return Err(error(ErrorCode::UnsupportedCapability));
                }
                if host != self.workspace.host_epoch() {
                    return Err(error(ErrorCode::PlanHostMismatch));
                }
                let (prepared, capture) = match intent {
                    MutationIntent::SaveConfiguration(input) => {
                        let prepared = self
                            .workspace
                            .prepare_save(input)
                            .map_err(ConfigurationFailure::bridge_error)?;
                        let capture = PreparedCapture::SaveConfiguration {
                            input: prepared
                                .save_capture()
                                .map_err(ConfigurationFailure::bridge_error)?,
                        };
                        (prepared, capture)
                    }
                    MutationIntent::RestoreConfiguration(input) => (
                        self.workspace
                            .prepare_restore(input)
                            .map_err(ConfigurationFailure::bridge_error)?,
                        PreparedCapture::RestoreConfiguration {
                            input: input.clone(),
                        },
                    ),
                    _ => unreachable!(),
                };
                let recovery = ConfigurationRecoveryTransaction::from_capture(&capture)
                    .map_err(ConfigurationFailure::bridge_error)?;
                let binding = CapturedOperation {
                    resources: configuration_resources(&capture)?,
                    semantics: configuration_semantics(capture),
                };
                (
                    binding,
                    CustodyKind::Prepared {
                        slot: Box::new(Some(prepared)),
                        attempted: false,
                        recovery,
                    },
                )
            }
            _ => {
                let (binding, custody) = self.operations.capture(intent, host)?;
                if is_configuration(&binding.semantics) {
                    return Err(invalid());
                }
                (binding, CustodyKind::Delegated(custody))
            }
        };
        let custody = ConfigurationCustody {
            kind,
            binding: binding.clone(),
            token: Rc::new(()),
            recovery: RefCell::new(None),
        };
        Ok((binding, custody))
    }
    fn acquire(
        &mut self,
        semantics: &PlanSemantics,
        custody: &Self::Custody,
        resources: &[ResourceKey],
    ) -> Result<Self::Lease, Box<BridgeError>> {
        if custody.binding.semantics != *semantics
            || !same_resources(&custody.binding.resources, resources)
        {
            return Err(invalid());
        }
        let kind = match &custody.kind {
            CustodyKind::Delegated(inner) if !is_configuration(semantics) => {
                let lease = self.operations.acquire(semantics, inner, resources)?;
                if lease.resources() != resources {
                    return Err(invalid());
                }
                LeaseKind::Delegated(lease)
            }
            CustodyKind::Prepared {
                slot,
                attempted: false,
                ..
            } => LeaseKind::Configuration(
                self.workspace
                    .acquire_configuration(slot.as_ref().as_ref().ok_or_else(invalid)?)
                    .map_err(ConfigurationFailure::bridge_error)?,
            ),
            _ => return Err(invalid()),
        };
        Ok(ConfigurationLease {
            kind,
            resources: resources.to_vec(),
            token: custody.token.clone(),
        })
    }
    fn acquire_recovery(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        resources: &[ResourceKey],
    ) -> Result<(Self::Custody, Self::Lease), Box<BridgeError>> {
        if operation.operation_id != recovery.operation_id {
            return Err(invalid());
        }
        let (kind, lease) = if is_configuration(&operation.semantics) {
            if !self.workspace.configuration_operations_available() {
                return Err(error(ErrorCode::UnsupportedCapability));
            }
            if configuration_semantics(operation.semantics.capture.clone()) != operation.semantics
                || !same_resources(
                    &configuration_resources(&operation.semantics.capture)?,
                    resources,
                )
            {
                return Err(invalid());
            }
            let state =
                ConfigurationRecoveryTransaction::from_capture(&operation.semantics.capture)
                    .map_err(ConfigurationFailure::bridge_error)?;
            if !matches!(&recovery.target, RecoveryTarget::Configuration { document } if document == &state.identities().baseline)
            {
                return Err(invalid());
            }
            let lease = self
                .workspace
                .acquire_configuration_recovery(state.identities())
                .map_err(ConfigurationFailure::bridge_error)?;
            (
                CustodyKind::Recovery(state),
                LeaseKind::Configuration(lease),
            )
        } else {
            let (custody, lease) = self
                .operations
                .acquire_recovery(operation, recovery, resources)?;
            if lease.resources() != resources {
                return Err(invalid());
            }
            (CustodyKind::Delegated(custody), LeaseKind::Delegated(lease))
        };
        let token = Rc::new(());
        Ok((
            ConfigurationCustody {
                kind,
                binding: CapturedOperation {
                    semantics: operation.semantics.clone(),
                    resources: resources.to_vec(),
                },
                token: token.clone(),
                recovery: RefCell::new(Some(recovery.clone())),
            },
            ConfigurationLease {
                kind: lease,
                resources: resources.to_vec(),
                token,
            },
        ))
    }
    fn revalidate(
        &mut self,
        semantics: &PlanSemantics,
        custody: &Self::Custody,
        lease: &Self::Lease,
    ) -> ApplicationResult<()> {
        custody.check(semantics, lease)?;
        match (&custody.kind, &lease.kind) {
            (CustodyKind::Delegated(inner), LeaseKind::Delegated(owner)) => {
                self.operations.revalidate(semantics, inner, owner)
            }
            (
                CustodyKind::Prepared {
                    slot,
                    attempted: false,
                    ..
                },
                LeaseKind::Configuration(owner),
            ) => self
                .workspace
                .revalidate_configuration(slot.as_ref().as_ref().ok_or_else(invalid)?, owner)
                .map_err(ConfigurationFailure::bridge_error),
            _ => Err(invalid()),
        }
    }
    fn recovery_binding(
        &mut self,
        operation: &OperationId,
        semantics: &PlanSemantics,
        custody: &Self::Custody,
        lease: &Self::Lease,
    ) -> Result<RecoveryRef, Box<BridgeError>> {
        custody.check(semantics, lease)?;
        let recovery = match (&custody.kind, &lease.kind) {
            (CustodyKind::Delegated(inner), LeaseKind::Delegated(owner)) => self
                .operations
                .recovery_binding(operation, semantics, inner, owner)?,
            (
                CustodyKind::Prepared {
                    slot,
                    attempted: false,
                    ..
                },
                LeaseKind::Configuration(owner),
            ) => self
                .workspace
                .configuration_recovery_binding(
                    operation,
                    slot.as_ref().as_ref().ok_or_else(invalid)?,
                    owner,
                )
                .map_err(ConfigurationFailure::bridge_error)?,
            _ => return Err(invalid()),
        };
        if &recovery.operation_id != operation {
            return Err(invalid());
        }
        *custody.recovery.borrow_mut() = Some(recovery.clone());
        Ok(recovery)
    }
    fn advance(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        custody: &mut Self::Custody,
        lease: &Self::Lease,
        cancellation_requested: bool,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        custody.check(&operation.semantics, lease)?;
        custody.check_recovery(operation, recovery)?;
        if let (CustodyKind::Delegated(inner), LeaseKind::Delegated(owner)) =
            (&mut custody.kind, &lease.kind)
        {
            return self.operations.advance(
                operation,
                recovery,
                inner,
                owner,
                cancellation_requested,
            );
        }
        let LeaseKind::Configuration(owner) = &lease.kind else {
            return Err(invalid());
        };
        if !matches!(
            operation.state,
            OperationState::Running { .. } | OperationState::CancellationRequested { .. }
        ) {
            return Err(invalid());
        }
        if let CustodyKind::Prepared {
            slot, attempted, ..
        } = &mut custody.kind
        {
            if *attempted {
                return Err(error(ErrorCode::RecoveryRequired));
            }
            // Set before invoking a possibly effectful native begin. A refusal
            // keeps the occupied slot but never grants another begin attempt.
            *attempted = true;
            let transaction = self
                .workspace
                .begin_configuration(slot, recovery.clone(), owner)
                .map_err(ConfigurationFailure::bridge_error)?;
            custody.kind = CustodyKind::Active(Box::new(transaction));
        }
        let CustodyKind::Active(transaction) = &mut custody.kind else {
            return Err(invalid());
        };
        let outcome = self
            .workspace
            .advance_configuration(transaction, owner, cancellation_requested)
            .map_err(ConfigurationFailure::bridge_error)?;
        let (phase, cancellable) = transaction.status();
        Ok(match outcome {
            Some(outcome) => complete(outcome),
            None => TransactionStep::Progress {
                progress: Progress {
                    phase: PhaseId::new(match phase {
                        ConfigurationPhase::Admitted => "configuration_admitted",
                        ConfigurationPhase::StageWritten => "configuration_staged",
                        ConfigurationPhase::StageFlushed => "configuration_flushed",
                        ConfigurationPhase::BackupRetained => "configuration_backup",
                        ConfigurationPhase::PromotionInvoked => "configuration_promoting",
                    })
                    .expect("static phase"),
                    measurement: Measurement::Unknown,
                },
                cancellable,
            },
        })
    }
    fn recover(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        custody: &mut Self::Custody,
        lease: &Self::Lease,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        custody.check(&operation.semantics, lease)?;
        custody.check_recovery(operation, recovery)?;
        let outcome = match (&mut custody.kind, &lease.kind) {
            (CustodyKind::Delegated(inner), LeaseKind::Delegated(owner)) => {
                return self.operations.recover(operation, recovery, inner, owner);
            }
            (CustodyKind::Active(transaction), LeaseKind::Configuration(owner)) => self
                .workspace
                .recover_configuration_transaction(transaction, owner),
            (
                CustodyKind::Prepared {
                    attempted: true,
                    recovery: transaction,
                    ..
                }
                | CustodyKind::Recovery(transaction),
                LeaseKind::Configuration(owner),
            ) => self
                .workspace
                .advance_configuration_recovery(transaction, recovery, owner),
            _ => return Err(invalid()),
        }
        .map_err(ConfigurationFailure::bridge_error)?;
        Ok(match outcome {
            Some(outcome) => complete(outcome),
            None => TransactionStep::RecoveryRequired {
                reason: RecoveryReason::NativeCustodyUnresolved,
                safe_owner_boundary: false,
            },
        })
    }
    fn completion_pending(&self, custody: &Self::Custody) -> bool {
        match &custody.kind {
            CustodyKind::Delegated(inner) => self.operations.completion_pending(inner),
            CustodyKind::Active(transaction) => transaction.completion_pending(),
            CustodyKind::Prepared { recovery, .. } | CustodyKind::Recovery(recovery) => {
                recovery.completion_pending()
            }
        }
    }
    fn publish_completion(
        &mut self,
        operation: &OperationSnapshot,
        custody: &mut Self::Custody,
        lease: &Self::Lease,
        commit: &mut CompletionCommit<'_>,
    ) -> ApplicationResult<()> {
        custody.check(&operation.semantics, lease)?;
        if custody
            .recovery
            .borrow()
            .as_ref()
            .is_none_or(|bound| bound.operation_id != operation.operation_id)
        {
            return Err(invalid());
        }
        if let (CustodyKind::Delegated(inner), LeaseKind::Delegated(owner)) =
            (&mut custody.kind, &lease.kind)
        {
            return self
                .operations
                .publish_completion(operation, inner, owner, commit);
        }
        let terminal = match &custody.kind {
            CustodyKind::Active(transaction) => transaction.terminal_outcome(),
            CustodyKind::Prepared { recovery, .. } | CustodyKind::Recovery(recovery) => {
                recovery.terminal_outcome()
            }
            _ => None,
        };
        if !matches!(&operation.state, OperationState::Completed { outcome } if Some(outcome) == terminal)
        {
            return Err(invalid());
        }
        let mut refusal = None;
        let mut callback = |changed: &[DraftSnapshot]| {
            commit(changed).map_err(|error| {
                refusal = Some(error);
                ConfigurationFailure::Capacity
            })
        };
        let result = match (&mut custody.kind, &lease.kind) {
            (CustodyKind::Active(transaction), LeaseKind::Configuration(owner)) => self
                .workspace
                .publish_configuration_completion(transaction, owner, &mut callback),
            (
                CustodyKind::Prepared {
                    recovery: transaction,
                    ..
                }
                | CustodyKind::Recovery(transaction),
                LeaseKind::Configuration(owner),
            ) => self.workspace.publish_configuration_recovery_completion(
                transaction,
                owner,
                &mut callback,
            ),
            _ => return Err(invalid()),
        };
        match refusal {
            Some(error) => Err(error),
            None => result.map_err(ConfigurationFailure::bridge_error),
        }
    }
    fn requires_terminal_settlement(
        &self,
        semantics: &PlanSemantics,
        custody: &Self::Custody,
    ) -> bool {
        match &custody.kind {
            CustodyKind::Delegated(inner) => self
                .operations
                .requires_terminal_settlement(semantics, inner),
            _ => self.workspace.configuration_requires_terminal_settlement(),
        }
    }
    fn settle_terminal(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        custody: &mut Self::Custody,
        lease: &Self::Lease,
    ) -> Result<SettlementStep, Box<BridgeError>> {
        custody.check(&operation.semantics, lease)?;
        custody.check_recovery(operation, recovery)?;
        if self.completion_pending(custody) {
            return Err(invalid());
        }
        match (&mut custody.kind, &lease.kind) {
            (CustodyKind::Delegated(inner), LeaseKind::Delegated(owner)) => self
                .operations
                .settle_terminal(operation, recovery, inner, owner),
            (_, LeaseKind::Configuration(owner)) => self
                .workspace
                .settle_configuration_terminal(operation, recovery, owner)
                .map_err(ConfigurationFailure::bridge_error),
            _ => Err(invalid()),
        }
    }
    fn handoff_session(
        &mut self,
        session: &SessionBinding,
        custody: &mut Self::Custody,
        lease: &Self::Lease,
    ) -> Result<bool, Box<BridgeError>> {
        custody.check(&custody.binding.semantics, lease)?;
        match (&mut custody.kind, &lease.kind) {
            (CustodyKind::Delegated(inner), LeaseKind::Delegated(owner)) => {
                self.operations.handoff_session(session, inner, owner)
            }
            _ => Err(invalid()),
        }
    }
}
fn complete(outcome: CompletionOutcome) -> TransactionStep {
    TransactionStep::Complete {
        outcome,
        session_custody: None,
    }
}
