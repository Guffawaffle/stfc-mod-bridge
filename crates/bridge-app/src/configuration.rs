//! Portable adoption of one existing configuration workspace into Engine's
//! owned ports. This supplies no production owner, writer, schema or entry UI.
use bridge_contracts::v1::*;
use bridge_engine::{configuration::*, operations::*, services::*};

pub struct ConfigurationServices<P, T, S, O, E, I> {
    operations: P,
    workspace: ConfigurationWorkspace<T, S, O, E, I>,
}
impl<P, T, S, O, E, I> ConfigurationServices<P, T, S, O, E, I> {
    pub fn new(operations: P, workspace: ConfigurationWorkspace<T, S, O, E, I>) -> Self {
        Self {
            operations,
            workspace,
        }
    }
}

/// Preserve the exact engine refusal across the workspace's closed-error port.
fn preflight_result<T>(
    invoke: impl FnOnce(&mut dyn FnMut(&T) -> ConfigurationResult<()>) -> ConfigurationResult<T>,
    preflight: &mut Preflight<'_, T>,
) -> ApplicationResult<T> {
    let mut refusal = None;
    let result = invoke(&mut |value| {
        preflight(value).map_err(|error| {
            refusal = Some(error);
            ConfigurationFailure::Capacity
        })
    });
    match refusal {
        Some(error) => Err(error),
        None => result.map_err(ConfigurationFailure::bridge_error),
    }
}

impl<
    P,
    T: TomlPreparation,
    S: SchemaSource,
    O: DocumentOwner,
    E: SensitiveEntry,
    I: ConfigurationIds,
> ApplicationServices for ConfigurationServices<P, T, S, O, E, I>
{
    fn configuration_host_epoch(&self) -> Option<&HostEpoch> {
        Some(self.workspace.host_epoch())
    }
    fn implemented_commands(&self) -> Vec<CommandId> {
        let mut commands = vec![
            CommandId::OpenDraft,
            CommandId::SetDraftChanges,
            CommandId::DiscardDraft,
        ];
        if self.workspace.sensitive_entry_available() {
            commands.push(CommandId::RequestSensitiveInput);
        }
        commands
    }
    fn read_configuration(
        &mut self,
        input: &ReadConfigurationInput,
        preflight: &mut ReadPreflight<'_>,
    ) -> ApplicationResult<DocumentSnapshot> {
        let mut refusal = None;
        let result =
            self.workspace
                .read_configuration_with_preflight(&input.target, |snapshot, changed| {
                    preflight(snapshot, changed).map_err(|error| {
                        refusal = Some(error);
                        ConfigurationFailure::Capacity
                    })
                });
        match refusal {
            Some(error) => Err(error),
            None => result.map_err(ConfigurationFailure::bridge_error),
        }
    }
    fn configuration_history(
        &mut self,
        input: &ConfigurationHistoryInput,
    ) -> ApplicationResult<Inventory<BackupReceiptRef>> {
        self.workspace
            .configuration_history(input)
            .map_err(ConfigurationFailure::bridge_error)
    }
    fn get_draft(&self, input: &GetDraftInput) -> ApplicationResult<Option<DraftSnapshot>> {
        self.workspace
            .current_draft(&input.host_epoch, &input.draft_id)
            .map_err(ConfigurationFailure::bridge_error)
    }
    fn open_draft(
        &mut self,
        input: &OpenDraftInput,
        preflight: &mut Preflight<'_, DraftSnapshot>,
    ) -> ApplicationResult<DraftSnapshot> {
        preflight_result(
            |check| self.workspace.open_draft_with_preflight(input, check),
            preflight,
        )
    }
    fn set_draft_changes(
        &mut self,
        input: SetDraftChangesInput,
        preflight: &mut StagePreflight<'_>,
    ) -> ApplicationResult<SetDraftChangesResult> {
        let previous = self
            .workspace
            .current_draft(&input.draft.host_epoch, &input.draft.draft_id)
            .map_err(ConfigurationFailure::bridge_error)?;
        let mut refusal = None;
        let result = self.workspace.set_draft_changes(input, |receipt| {
            preflight(receipt, previous.as_ref() != Some(&receipt.snapshot)).map_err(|error| {
                refusal = Some(error);
                ConfigurationFailure::Capacity
            })
        });
        match refusal {
            Some(error) => Err(error),
            None => result.map_err(ConfigurationFailure::bridge_error),
        }
    }
    fn discard_draft(
        &mut self,
        input: &DiscardDraftInput,
        preflight: &mut Preflight<'_, DiscardedDraft>,
    ) -> ApplicationResult<DiscardedDraft> {
        preflight_result(
            |check| self.workspace.discard_draft_with_preflight(input, check),
            preflight,
        )
    }
    fn request_sensitive_input(
        &mut self,
        input: &RequestSensitiveInputInput,
        preflight: &mut Preflight<'_, SensitiveInputResult>,
    ) -> ApplicationResult<SensitiveInputResult> {
        if !self.workspace.sensitive_entry_available() {
            return Err(error(ErrorCode::UnsupportedCapability));
        }
        preflight_result(
            |check| {
                self.workspace
                    .request_sensitive_input_with_preflight(input, check)
            },
            preflight,
        )
    }
}

impl<P: OperationPorts, T, S, O, E, I> OperationPorts for ConfigurationServices<P, T, S, O, E, I> {
    type Custody = P::Custody;
    type Lease = P::Lease;
    fn capture(
        &mut self,
        intent: &MutationIntent,
        host: &HostEpoch,
    ) -> Result<(CapturedOperation, Self::Custody), Box<BridgeError>> {
        if matches!(
            intent,
            MutationIntent::SaveConfiguration(_) | MutationIntent::RestoreConfiguration(_)
        ) {
            return Err(error(ErrorCode::UnsupportedCapability));
        }
        self.operations.capture(intent, host)
    }
    fn acquire(
        &mut self,
        semantics: &PlanSemantics,
        custody: &Self::Custody,
        resources: &[ResourceKey],
    ) -> Result<Self::Lease, Box<BridgeError>> {
        refuse_configuration(semantics)?;
        self.operations.acquire(semantics, custody, resources)
    }
    fn acquire_recovery(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        resources: &[ResourceKey],
    ) -> Result<(Self::Custody, Self::Lease), Box<BridgeError>> {
        refuse_configuration(&operation.semantics)?;
        self.operations
            .acquire_recovery(operation, recovery, resources)
    }
    fn revalidate(
        &mut self,
        semantics: &PlanSemantics,
        custody: &Self::Custody,
        lease: &Self::Lease,
    ) -> Result<(), Box<BridgeError>> {
        refuse_configuration(semantics)?;
        self.operations.revalidate(semantics, custody, lease)
    }
    fn recovery_binding(
        &mut self,
        operation: &OperationId,
        semantics: &PlanSemantics,
        custody: &Self::Custody,
        lease: &Self::Lease,
    ) -> Result<RecoveryRef, Box<BridgeError>> {
        refuse_configuration(semantics)?;
        self.operations
            .recovery_binding(operation, semantics, custody, lease)
    }
    fn advance(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        custody: &mut Self::Custody,
        lease: &Self::Lease,
        cancellation_requested: bool,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        refuse_configuration(&operation.semantics)?;
        self.operations
            .advance(operation, recovery, custody, lease, cancellation_requested)
    }
    fn recover(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        custody: &mut Self::Custody,
        lease: &Self::Lease,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        refuse_configuration(&operation.semantics)?;
        self.operations.recover(operation, recovery, custody, lease)
    }
    fn handoff_session(
        &mut self,
        session: &SessionBinding,
        custody: &mut Self::Custody,
        lease: &Self::Lease,
    ) -> Result<bool, Box<BridgeError>> {
        self.operations.handoff_session(session, custody, lease)
    }
}
fn refuse_configuration(semantics: &PlanSemantics) -> ApplicationResult<()> {
    if matches!(
        semantics.capture,
        PreparedCapture::SaveConfiguration { .. } | PreparedCapture::RestoreConfiguration { .. }
    ) {
        Err(error(ErrorCode::UnsupportedCapability))
    } else {
        Ok(())
    }
}
