use super::schema::{ResolvedSyncEdit, SemanticMutation};
use super::*;
use bridge_contracts::v1::*;
use bridge_toml::{TomlPath, TomlSnapshot};
use std::collections::{BTreeMap, BTreeSet};

/// Read-only compilation custody. Raw candidate is private and zeroed on drop.
/// This object is not a permission, exclusion, preparation token or journal.
pub struct PreparedConfiguration {
    baseline: DocumentBinding,
    destination_schema: SchemaBinding,
    candidate: ProtectedValue,
    candidate_digest: Sha256,
    changed: bool,
    draft: Option<DraftSnapshot>,
    restore_source: Option<BackupReceiptRef>,
}
/// Restart custody contains identities only. Native recovery must inspect its
/// own retained candidate/stage/backup subjects; it cannot recreate a secret.
#[derive(Clone, Debug)]
pub struct RecoveryConfiguration {
    pub baseline: DocumentBinding,
    pub destination_schema: SchemaBinding,
    pub candidate_digest: Sha256,
}
impl RecoveryConfiguration {
    pub fn from_capture(capture: &PreparedCapture) -> ConfigurationResult<Self> {
        match capture {
            PreparedCapture::SaveConfiguration { input } => Ok(Self {
                baseline: input.draft.draft.document.clone(),
                destination_schema: input.draft.draft.document.schema.clone(),
                candidate_digest: input.candidate_digest.clone(),
            }),
            PreparedCapture::RestoreConfiguration { input } => Ok(Self {
                baseline: input.document.clone(),
                destination_schema: input.document.schema.clone(),
                candidate_digest: input.backup.retained_digest.clone(),
            }),
            _ => Err(ConfigurationFailure::InvalidInput),
        }
    }
    pub fn from_participant(effect: &PreparedConfigurationEffect) -> ConfigurationResult<Self> {
        match effect {
            PreparedConfigurationEffect::Write {
                baseline,
                destination_schema,
                candidate_digest,
            } => Ok(Self {
                baseline: baseline.clone(),
                destination_schema: destination_schema.clone(),
                candidate_digest: candidate_digest.clone(),
            }),
            _ => Err(ConfigurationFailure::InvalidInput),
        }
    }
}
impl PreparedConfiguration {
    pub fn baseline(&self) -> &DocumentBinding {
        &self.baseline
    }
    pub fn destination_schema(&self) -> &SchemaBinding {
        &self.destination_schema
    }
    pub fn candidate_digest(&self) -> &Sha256 {
        &self.candidate_digest
    }
    pub fn candidate_bytes(&self) -> ConfigurationResult<&[u8]> {
        Ok(self.candidate.source()?.as_bytes())
    }
    pub fn changed(&self) -> bool {
        self.changed
    }
    pub fn restore_source(&self) -> Option<&BackupReceiptRef> {
        self.restore_source.as_ref()
    }
    pub fn save_capture(&self) -> ConfigurationResult<SaveConfigurationCapture> {
        Ok(SaveConfigurationCapture {
            draft: self
                .draft
                .clone()
                .ok_or(ConfigurationFailure::InvalidInput)?,
            candidate_digest: self.candidate_digest.clone(),
        })
    }
    pub fn participant_effect(&self) -> PreparedConfigurationEffect {
        // A selected SaveReviewedDraft/CompatibleMigration is represented as a
        // Write even when compilation is a byte no-op; native admission later
        // returns AlreadySatisfied without a backup or empty file.
        PreparedConfigurationEffect::Write {
            baseline: self.baseline.clone(),
            destination_schema: self.destination_schema.clone(),
            candidate_digest: self.candidate_digest.clone(),
        }
    }
}

pub struct ConfigurationTransaction<X> {
    pub(crate) prepared: PreparedConfiguration,
    pub(crate) native: Option<X>,
    pub(crate) recovery: RecoveryRef,
    phase: ConfigurationPhase,
    cancellable: bool,
    terminal: Option<CompletionOutcome>,
    completion_document: Option<DocumentRead>,
    published: bool,
}
impl<X> ConfigurationTransaction<X> {
    pub fn recovery(&self) -> &RecoveryRef {
        &self.recovery
    }
    pub fn status(&self) -> (ConfigurationPhase, bool) {
        (self.phase, self.cancellable && self.terminal.is_none())
    }
    pub fn completion_pending(&self) -> bool {
        self.terminal.is_some() && !self.published
    }
    pub fn terminal_outcome(&self) -> Option<&CompletionOutcome> {
        self.terminal.as_ref()
    }
}

/// Recovery publication custody contains public identities and inspected
/// results only. No prepared candidate or old protected entry is reconstructed.
pub struct ConfigurationRecoveryTransaction {
    captured: RecoveryConfiguration,
    draft: Option<DraftSnapshot>,
    terminal: Option<CompletionOutcome>,
    completion_document: Option<DocumentRead>,
    published: bool,
}
impl ConfigurationRecoveryTransaction {
    pub fn from_capture(capture: &PreparedCapture) -> ConfigurationResult<Self> {
        Ok(Self {
            captured: RecoveryConfiguration::from_capture(capture)?,
            draft: match capture {
                PreparedCapture::SaveConfiguration { input } => Some(input.draft.clone()),
                _ => None,
            },
            terminal: None,
            completion_document: None,
            published: false,
        })
    }
    pub fn identities(&self) -> &RecoveryConfiguration {
        &self.captured
    }
    pub fn completion_pending(&self) -> bool {
        self.terminal.is_some() && !self.published
    }
    pub fn terminal_outcome(&self) -> Option<&CompletionOutcome> {
        self.terminal.as_ref()
    }
}

impl<T: TomlPreparation, S: SchemaSource, O: DocumentOwner, E: SensitiveEntry, I: ConfigurationIds>
    ConfigurationWorkspace<T, S, O, E, I>
{
    pub fn configuration_operations_available(&self) -> bool {
        self.owner.operations_available()
    }
    pub fn configuration_requires_terminal_settlement(&self) -> bool {
        self.owner.requires_terminal_settlement()
    }
    pub fn settle_configuration_terminal(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        lease: &O::Lease,
    ) -> ConfigurationResult<crate::operations::SettlementStep> {
        let captured = RecoveryConfiguration::from_capture(&operation.semantics.capture)?;
        let OperationState::Completed { outcome } = &operation.state else {
            return Err(ConfigurationFailure::InvalidInput);
        };
        if operation.operation_id != recovery.operation_id
            || !matches!(&recovery.target, RecoveryTarget::Configuration { document } if document == &captured.baseline)
        {
            return Err(ConfigurationFailure::InvalidInput);
        }
        self.owner
            .settle_terminal(&captured, recovery, outcome, lease)
    }
    pub fn prepare_save(
        &mut self,
        input: &SaveConfigurationInput,
    ) -> ConfigurationResult<PreparedConfiguration> {
        let (baseline, snapshot) = self.baseline(&input.draft)?;
        let read = DocumentRead {
            binding: baseline.binding.clone(),
            bytes: baseline.bytes.clone(),
        };
        let snapshot = snapshot.clone();
        let schema = self.schemas.resolve(&read.binding.schema)?;
        schema.validate(&read.binding.schema)?;
        let mut candidate = read.text()?.to_owned();
        self.toml.validate(&candidate)?;
        let initial = self.toml.read(&candidate)?;
        let mut mutations = Vec::new();
        let mut sync = Vec::new();
        for edit in snapshot.edits.as_slice() {
            let field = match edit {
                ConfigurationEdit::SetPublic { field_id, .. }
                | ConfigurationEdit::SetPrivate { field_id, .. }
                | ConfigurationEdit::ReplaceSecret { field_id, .. }
                | ConfigurationEdit::ClearSecret { field_id }
                | ConfigurationEdit::RemoveOverride { field_id } => Some(field_id),
                _ => None,
            };
            if let Some(field) = field {
                let definition = schema
                    .definition(field)
                    .ok_or(ConfigurationFailure::UnsupportedSchema)?;
                if !definition.platforms.as_slice().contains(&schema.platform) {
                    return Err(ConfigurationFailure::UnsupportedSchema);
                }
                let policy = schema.field(field)?;
                let paths = std::iter::once(&policy.canonical)
                    .chain(&policy.aliases)
                    .collect::<Vec<_>>();
                let current = paths
                    .iter()
                    .filter_map(|p| initial.overrides.iter().find(|v| &v.path == *p))
                    .collect::<Vec<_>>();
                if current.len() > 1 && policy.precedence == AliasPolicy::RejectMultiple {
                    return Err(ConfigurationFailure::UnsupportedSchema);
                }
                let value = match edit {
                    ConfigurationEdit::SetPublic { value, .. } => {
                        Some(self.schemas.encode_public(&schema, definition, value)?)
                    }
                    ConfigurationEdit::SetPrivate { reference, .. } => Some(
                        self.protected_source(reference.value_id.as_str())?
                            .to_owned(),
                    ),
                    ConfigurationEdit::ReplaceSecret { reference, .. } => Some(
                        self.protected_source(reference.secret_id.as_str())?
                            .to_owned(),
                    ),
                    _ => None,
                };
                if let Some(value) = value {
                    let normalized = self.toml.normalize(&value)?;
                    let effective = match &policy.precedence {
                        AliasPolicy::RejectMultiple => current.first().copied(),
                        AliasPolicy::Precedence(order) => order
                            .iter()
                            .find_map(|p| current.iter().find(|v| &v.path == p).copied()),
                    };
                    if effective.is_some_and(|v| v.semantic_value == normalized) {
                        continue;
                    }
                    for alias in &policy.aliases {
                        mutations.push(SemanticMutation::Remove {
                            path: alias.clone(),
                        });
                    }
                    mutations.push(SemanticMutation::Set {
                        path: policy.canonical.clone(),
                        value,
                    });
                } else {
                    for path in paths {
                        mutations.push(SemanticMutation::Remove { path: path.clone() });
                    }
                }
            } else {
                sync.push(match edit {
                    ConfigurationEdit::AddSyncDestination { destination } => {
                        ResolvedSyncEdit::Add {
                            destination: destination.clone(),
                            endpoint: self
                                .protected_source(destination.endpoint.value_id.as_str())?
                                .to_owned(),
                            secret: self
                                .protected_source(destination.secret.secret_id.as_str())?
                                .to_owned(),
                            proxy: match &destination.proxy {
                                ProxyChoice::Custom { reference } => Some(
                                    self.protected_source(reference.value_id.as_str())?
                                        .to_owned(),
                                ),
                                _ => None,
                            },
                        }
                    }
                    ConfigurationEdit::RemoveSyncDestination { destination_id } => {
                        ResolvedSyncEdit::Remove(destination_id.clone())
                    }
                    ConfigurationEdit::SetSyncFeed {
                        destination_id,
                        feed_id,
                        value,
                    } => ResolvedSyncEdit::Feed {
                        destination: destination_id.clone(),
                        feed: feed_id.clone(),
                        value: *value,
                    },
                    ConfigurationEdit::SetSyncProxy {
                        destination_id,
                        value,
                    } => ResolvedSyncEdit::Proxy {
                        destination: destination_id.clone(),
                        choice: value.clone(),
                        value: match value {
                            ProxyChoice::Custom { reference } => Some(
                                self.protected_source(reference.value_id.as_str())?
                                    .to_owned(),
                            ),
                            _ => None,
                        },
                    },
                    _ => return Err(ConfigurationFailure::InvalidInput),
                });
            }
        }
        let sync_mutations = self.schemas.compile_sync(&schema, &initial, &sync)?;
        let ownership = self.schemas.owned_paths(&schema)?;
        check_ownership(&sync_mutations, &ownership, &initial)?;
        mutations.extend(sync_mutations);
        candidate = apply_mutations(&mut self.toml, &candidate, &initial, &mutations, &ownership)?;
        self.schemas
            .validate_candidate(&schema, &self.toml.read(&candidate)?)?;
        let changed = candidate.as_bytes() != read.bytes;
        let candidate_digest = digest(candidate.as_bytes());
        Ok(PreparedConfiguration {
            baseline: read.binding.clone(),
            destination_schema: read.binding.schema,
            candidate: ProtectedValue::new(candidate.into_bytes())?,
            candidate_digest,
            changed,
            draft: Some(snapshot),
            restore_source: None,
        })
    }
    pub fn prepare_restore(
        &mut self,
        input: &RestoreConfigurationInput,
    ) -> ConfigurationResult<PreparedConfiguration> {
        if input.document.document_id != input.backup.document.document_id
            || input.document.target != input.backup.document.target
            || input.document.schema.provider_id != input.backup.document.schema.provider_id
        {
            return Err(ConfigurationFailure::InvalidInput);
        }
        let read = self.owner.read(&input.document)?;
        if read.binding != input.document {
            return Err(ConfigurationFailure::Stale);
        }
        read.text()?;
        let bytes = self.owner.backup(&input.backup)?;
        if digest(&bytes) != input.backup.retained_digest
            || !matches!(&input.backup.document.baseline,DocumentBaseline::Existing { content_digest,.. } if content_digest == &input.backup.retained_digest)
        {
            return Err(ConfigurationFailure::BackupUnavailable);
        }
        let text =
            std::str::from_utf8(&bytes).map_err(|_| ConfigurationFailure::InvalidDocument)?;
        let schema = self.schemas.resolve(&input.document.schema)?;
        schema.validate(&input.document.schema)?;
        self.toml.validate(text)?;
        let restored = self.toml.read(text)?;
        self.schemas.validate_candidate(&schema, &restored)?;
        // Complete known values are validated against the destination schema.
        for field in schema
            .schema
            .fields
            .as_slice()
            .iter()
            .filter(|f| f.sensitivity == Sensitivity::Public)
        {
            let policy = schema.field(&field.field_id)?;
            for value in restored
                .overrides
                .iter()
                .filter(|v| v.path == policy.canonical || policy.aliases.contains(&v.path))
            {
                let public = self
                    .schemas
                    .decode_public(&schema, field, &value.semantic_value)?;
                if !super::schema::accepts(&field.value_type, &public) {
                    return Err(ConfigurationFailure::InvalidDocument);
                }
            }
        }
        Ok(PreparedConfiguration {
            baseline: input.document.clone(),
            destination_schema: input.document.schema.clone(),
            candidate_digest: digest(&bytes),
            changed: bytes != read.bytes,
            candidate: ProtectedValue::new(bytes)?,
            draft: None,
            restore_source: Some(input.backup.clone()),
        })
    }
    pub fn prepare_participant(
        &mut self,
        participant: &ConfigurationParticipant,
        selected: &SchemaBinding,
    ) -> ConfigurationResult<Option<PreparedConfiguration>> {
        match participant {
            ConfigurationParticipant::Unchanged { document } => {
                let read = self.owner.read(document)?;
                if read.binding != *document || !same_schema_contract(&document.schema, selected) {
                    return Err(ConfigurationFailure::Stale);
                }
                read.text()?;
                Ok(None)
            }
            ConfigurationParticipant::SaveReviewedDraft { draft } => {
                if !same_schema_contract(&draft.document.schema, selected) {
                    return Err(ConfigurationFailure::UnsupportedSchema);
                }
                let mut prepared = self.prepare_save(&SaveConfigurationInput {
                    draft: draft.clone(),
                })?;
                prepared.destination_schema = selected.clone();
                Ok(Some(prepared))
            }
            ConfigurationParticipant::CompatibleMigration {
                document,
                destination,
            } => {
                if destination != selected {
                    return Err(ConfigurationFailure::UnsupportedSchema);
                }
                let read = self.owner.read(document)?;
                if read.binding != *document {
                    return Err(ConfigurationFailure::Stale);
                }
                let source = self.schemas.resolve(&document.schema)?;
                source.validate(&document.schema)?;
                let target = self.schemas.resolve(destination)?;
                target.validate(destination)?;
                let original = self.toml.read(read.text()?)?;
                let mutations = self.schemas.migrate(&source, &target, &original)?;
                let mut ownership = self.schemas.owned_paths(&source)?;
                ownership.extend(self.schemas.owned_paths(&target)?);
                check_ownership(&mutations, &ownership, &original)?;
                let candidate = apply_mutations(
                    &mut self.toml,
                    read.text()?,
                    &original,
                    &mutations,
                    &ownership,
                )?;
                self.schemas
                    .validate_candidate(&target, &self.toml.read(&candidate)?)?;
                Ok(Some(PreparedConfiguration {
                    baseline: document.clone(),
                    destination_schema: destination.clone(),
                    candidate_digest: digest(candidate.as_bytes()),
                    changed: candidate.as_bytes() != read.bytes || &document.schema != destination,
                    candidate: ProtectedValue::new(candidate.into_bytes())?,
                    draft: None,
                    restore_source: None,
                }))
            }
        }
    }
    pub fn configuration_history(
        &mut self,
        input: &ConfigurationHistoryInput,
    ) -> ConfigurationResult<Inventory<BackupReceiptRef>> {
        let inventory = self.owner.history(&input.document)?;
        if inventory.items.as_slice().iter().any(|backup| backup.document.document_id != input.document.document_id || backup.document.target != input.document.target || !matches!(&backup.document.baseline,DocumentBaseline::Existing { content_digest,.. } if content_digest == &backup.retained_digest)) || (inventory.completeness == Completeness::Partial && inventory.issues.as_slice().is_empty()) { return Err(ConfigurationFailure::InvalidOwnerResult); }
        Ok(inventory)
    }
    pub fn acquire_configuration(
        &mut self,
        prepared: &PreparedConfiguration,
    ) -> ConfigurationResult<O::Lease> {
        self.revalidate_prepared_draft(prepared)?;
        let lease = self.owner.acquire(prepared.baseline())?;
        self.revalidate_configuration(prepared, &lease)?;
        Ok(lease)
    }
    /// Revalidate captured intent and native subjects under the caller's exact
    /// held lease. This never acquires a replacement exclusion.
    pub fn revalidate_configuration(
        &mut self,
        prepared: &PreparedConfiguration,
        lease: &O::Lease,
    ) -> ConfigurationResult<()> {
        self.revalidate_prepared_draft(prepared)?;
        self.owner.revalidate(prepared.baseline(), lease)?;
        if let Some(backup) = prepared.restore_source() {
            self.owner.revalidate_backup(backup, lease)?;
        }
        // Schema/runtime adoption remains exact at admission.
        self.schemas
            .resolve(prepared.destination_schema())?
            .validate(prepared.destination_schema())?;
        Ok(())
    }
    fn revalidate_prepared_draft(
        &self,
        prepared: &PreparedConfiguration,
    ) -> ConfigurationResult<()> {
        if let Some(captured) = &prepared.draft
            && self.draft(&captured.draft)? != *captured
        {
            return Err(ConfigurationFailure::Stale);
        }
        Ok(())
    }
    pub fn configuration_recovery_binding(
        &mut self,
        operation: &OperationId,
        prepared: &PreparedConfiguration,
        lease: &O::Lease,
    ) -> ConfigurationResult<RecoveryRef> {
        self.owner.revalidate(prepared.baseline(), lease)?;
        let recovery = self.owner.recovery_binding(operation, prepared, lease)?;
        if recovery.operation_id != *operation {
            return Err(ConfigurationFailure::InvalidOwnerResult);
        }
        match &recovery.target {
            RecoveryTarget::Configuration { document } if document == prepared.baseline() => {}
            _ => return Err(ConfigurationFailure::InvalidOwnerResult),
        }
        Ok(recovery)
    }
    /// Root's OperationPorts adapter invokes this only after durable executing
    /// admission and retains the actual owner lease through the terminal step.
    /// Every error leaves the exact candidate in the supplied slot. An error
    /// from native begin does not establish no effect or authorize a retry;
    /// unresolved disposition must use the durable recovery binding.
    pub fn begin_configuration(
        &mut self,
        prepared: &mut Option<PreparedConfiguration>,
        recovery: RecoveryRef,
        lease: &O::Lease,
    ) -> ConfigurationResult<ConfigurationTransaction<O::Transaction>> {
        let captured = prepared
            .as_ref()
            .ok_or(ConfigurationFailure::InvalidInput)?;
        self.revalidate_configuration(captured, lease)?;
        if !matches!(&recovery.target,RecoveryTarget::Configuration { document } if document == captured.baseline())
        {
            return Err(ConfigurationFailure::InvalidOwnerResult);
        }
        let native = if captured.changed {
            Some(self.owner.begin(captured, &recovery, lease)?)
        } else {
            None
        };
        // No fallible work after native success and the one custody transfer.
        let prepared = prepared.take().expect("validated occupied candidate slot");
        Ok(ConfigurationTransaction {
            prepared,
            native,
            recovery,
            phase: ConfigurationPhase::Admitted,
            cancellable: true,
            terminal: None,
            completion_document: None,
            published: false,
        })
    }
    pub fn advance_configuration(
        &mut self,
        transaction: &mut ConfigurationTransaction<O::Transaction>,
        lease: &O::Lease,
        cancellation_requested: bool,
    ) -> ConfigurationResult<Option<CompletionOutcome>> {
        if let Some(outcome) = &transaction.terminal {
            return Ok(Some(outcome.clone()));
        }
        let Some(native) = &mut transaction.native else {
            self.owner
                .revalidate(transaction.prepared.baseline(), lease)?;
            let outcome = CompletionOutcome::NoChange {
                reason: CompletionReason::AlreadySatisfied,
            };
            transaction.terminal = Some(outcome.clone());
            return Ok(Some(outcome));
        };
        let owner = self.owner.advance(native, lease, cancellation_requested)?;
        if let OwnerOutcome::Pending { phase, cancellable } = owner {
            if phase < transaction.phase {
                return Err(ConfigurationFailure::InvalidOwnerResult);
            }
            transaction.phase = phase;
            transaction.cancellable = cancellable;
            return Ok(None);
        }
        if matches!(owner, OwnerOutcome::CancelledBeforeCommit)
            && transaction.phase >= ConfigurationPhase::PromotionInvoked
        {
            return Err(ConfigurationFailure::InvalidOwnerResult);
        }
        let outcome = validate_outcome(
            &RecoveryConfiguration {
                baseline: transaction.prepared.baseline.clone(),
                destination_schema: transaction.prepared.destination_schema.clone(),
                candidate_digest: transaction.prepared.candidate_digest.clone(),
            },
            owner,
        )?;
        transaction.terminal = Some(outcome.clone());
        Ok(Some(outcome))
    }
    /// Prepare all local successors, invoke the kernel's durable commit, then
    /// publish infallibly. Refusal keeps terminal/candidate/lease custody and
    /// every current local edit. No native advancement is repeated.
    pub fn publish_configuration_completion(
        &mut self,
        transaction: &mut ConfigurationTransaction<O::Transaction>,
        _lease: &O::Lease,
        commit: impl FnOnce(&[DraftSnapshot]) -> ConfigurationResult<()>,
    ) -> ConfigurationResult<()> {
        self.publish_configuration_outcome(
            transaction.prepared.draft.as_ref(),
            transaction.terminal.as_ref(),
            &mut transaction.completion_document,
            &mut transaction.published,
            commit,
        )
    }
    fn publish_configuration_outcome(
        &mut self,
        draft: Option<&DraftSnapshot>,
        terminal: Option<&CompletionOutcome>,
        completion_document: &mut Option<DocumentRead>,
        published: &mut bool,
        commit: impl FnOnce(&[DraftSnapshot]) -> ConfigurationResult<()>,
    ) -> ConfigurationResult<()> {
        if *published {
            return Ok(());
        }
        let outcome = terminal.ok_or(ConfigurationFailure::InvalidInput)?;
        if let CompletionOutcome::Changed {
            receipt: Some(receipt),
            ..
        } = outcome
        {
            let EffectReceipt::ConfigurationWritten { document, .. } = receipt.as_ref() else {
                return Err(ConfigurationFailure::InvalidOwnerResult);
            };
            if completion_document.is_none() {
                let read = self.owner.read(document)?;
                if &read.binding != document
                    || !matches!(&document.baseline, DocumentBaseline::Existing { content_digest, .. } if content_digest == &digest(&read.bytes))
                {
                    return Err(ConfigurationFailure::RecoveryRequired);
                }
                read.text()?;
                *completion_document = Some(read);
            }
        }
        let publication = self.prepare_completion(draft, outcome, completion_document.as_ref())?;
        let changed = publication.snapshots();
        commit(&changed)?;
        // The exclusive workspace borrow spans projection, durable commit and
        // publication. No local command can interleave after the preflight.
        self.publish_completion(publication);
        *published = true;
        Ok(())
    }
    /// A live transaction keeps its candidate/native handle while inspecting
    /// uncertain owner disposition. A cached terminal is never reinspected.
    pub fn recover_configuration_transaction(
        &mut self,
        transaction: &mut ConfigurationTransaction<O::Transaction>,
        lease: &O::Lease,
    ) -> ConfigurationResult<Option<CompletionOutcome>> {
        if let Some(outcome) = &transaction.terminal {
            return Ok(Some(outcome.clone()));
        }
        let identities = RecoveryConfiguration {
            baseline: transaction.prepared.baseline.clone(),
            destination_schema: transaction.prepared.destination_schema.clone(),
            candidate_digest: transaction.prepared.candidate_digest.clone(),
        };
        let outcome = self.recover_configuration(&identities, &transaction.recovery, lease)?;
        transaction.terminal = outcome.clone();
        Ok(outcome)
    }
    pub fn advance_configuration_recovery(
        &mut self,
        transaction: &mut ConfigurationRecoveryTransaction,
        recovery: &RecoveryRef,
        lease: &O::Lease,
    ) -> ConfigurationResult<Option<CompletionOutcome>> {
        if let Some(outcome) = &transaction.terminal {
            return Ok(Some(outcome.clone()));
        }
        let outcome = self.recover_configuration(&transaction.captured, recovery, lease)?;
        transaction.terminal = outcome.clone();
        Ok(outcome)
    }
    pub fn publish_configuration_recovery_completion(
        &mut self,
        transaction: &mut ConfigurationRecoveryTransaction,
        _lease: &O::Lease,
        commit: impl FnOnce(&[DraftSnapshot]) -> ConfigurationResult<()>,
    ) -> ConfigurationResult<()> {
        self.publish_configuration_outcome(
            transaction.draft.as_ref(),
            transaction.terminal.as_ref(),
            &mut transaction.completion_document,
            &mut transaction.published,
            commit,
        )
    }
    pub fn acquire_configuration_recovery(
        &mut self,
        captured: &RecoveryConfiguration,
    ) -> ConfigurationResult<O::Lease> {
        self.owner.acquire(&captured.baseline)
    }
    pub fn recover_configuration(
        &mut self,
        captured: &RecoveryConfiguration,
        recovery: &RecoveryRef,
        lease: &O::Lease,
    ) -> ConfigurationResult<Option<CompletionOutcome>> {
        if !matches!(&recovery.target,RecoveryTarget::Configuration { document } if document == &captured.baseline)
        {
            return Err(ConfigurationFailure::InvalidOwnerResult);
        }
        // Revalidation here is performed by owner recovery against deterministic
        // subjects; ordinary baseline revalidation would reject a committed file.
        match self.owner.recover(captured, recovery, lease)? {
            OwnerOutcome::Pending { .. } => Ok(None),
            outcome => Ok(Some(validate_outcome(captured, outcome)?)),
        }
    }
}

fn same_schema_contract(left: &SchemaBinding, right: &SchemaBinding) -> bool {
    left.provider_id == right.provider_id
        && left.schema_id == right.schema_id
        && left.schema_version == right.schema_version
        && left.digest == right.digest
}
fn validate_outcome(
    prepared: &RecoveryConfiguration,
    outcome: OwnerOutcome,
) -> ConfigurationResult<CompletionOutcome> {
    Ok(match outcome {
        OwnerOutcome::Committed { document, backup } => {
            if document.document_id != prepared.baseline.document_id
                || document.target != prepared.baseline.target
                || document.schema != prepared.destination_schema
                || document.revision == prepared.baseline.revision
                || !matches!(&document.baseline,DocumentBaseline::Existing { content_digest,.. } if content_digest == &prepared.candidate_digest)
            {
                return Err(ConfigurationFailure::InvalidOwnerResult);
            }
            match (&prepared.baseline.baseline, &backup) {
                (DocumentBaseline::Missing, None) => {}
                (DocumentBaseline::Existing { content_digest, .. }, Some(receipt))
                    if receipt.document == prepared.baseline
                        && &receipt.retained_digest == content_digest => {}
                _ => return Err(ConfigurationFailure::InvalidOwnerResult),
            }
            if matches!((&prepared.baseline.baseline,&document.baseline),(DocumentBaseline::Existing { file_identity:old,.. },DocumentBaseline::Existing { file_identity:new,.. }) if old == new)
            {
                return Err(ConfigurationFailure::InvalidOwnerResult);
            }
            CompletionOutcome::Changed {
                reason: CompletionReason::Applied,
                receipt: Some(Box::new(EffectReceipt::ConfigurationWritten {
                    document: *document,
                    backup,
                })),
            }
        }
        OwnerOutcome::CancelledBeforeCommit => CompletionOutcome::CancelledBeforeCommit {
            reason: CompletionReason::CancellationAccepted,
        },
        OwnerOutcome::RolledBack => CompletionOutcome::RolledBack {
            reason: CompletionReason::RollbackCompleted,
        },
        OwnerOutcome::RecoveryRequired => return Err(ConfigurationFailure::RecoveryRequired),
        OwnerOutcome::Pending { .. } => return Err(ConfigurationFailure::InvalidOwnerResult),
    })
}

fn check_ownership(
    mutations: &[SemanticMutation],
    allowed: &[TomlPath],
    snapshot: &TomlSnapshot,
) -> ConfigurationResult<()> {
    let keys = tree(snapshot)?;
    let tables = table_tree(snapshot)?;
    for mutation in mutations {
        match mutation {
            SemanticMutation::RemoveTable { .. } | SemanticMutation::RenameTable { .. } => {
                check_table_ownership(mutation, allowed, &keys, &tables)?;
            }
            _ if mutation.paths().iter().any(|path| !allowed.contains(path)) => {
                return Err(ConfigurationFailure::UnsupportedSchema);
            }
            _ => {}
        }
    }
    Ok(())
}
fn tree(snapshot: &TomlSnapshot) -> ConfigurationResult<BTreeMap<Vec<String>, String>> {
    let mut result = BTreeMap::new();
    for entry in &snapshot.overrides {
        if result
            .insert(entry.path.segments().to_vec(), entry.semantic_value.clone())
            .is_some()
        {
            return Err(ConfigurationFailure::InvalidDocument);
        }
    }
    Ok(result)
}
fn table_tree(snapshot: &TomlSnapshot) -> ConfigurationResult<BTreeSet<Vec<String>>> {
    let mut result = BTreeSet::new();
    for table in &snapshot.tables {
        if !result.insert(table.path.segments().to_vec()) {
            return Err(ConfigurationFailure::InvalidDocument);
        }
    }
    Ok(result)
}
fn owns_path(allowed: &[TomlPath], path: &[String]) -> bool {
    allowed.iter().any(|owned| owned.segments() == path)
}
fn proper_prefixes(path: &[String]) -> impl Iterator<Item = Vec<String>> + '_ {
    (1..path.len()).map(|length| path[..length].to_vec())
}
fn check_table_ownership(
    mutation: &SemanticMutation,
    allowed: &[TomlPath],
    keys: &BTreeMap<Vec<String>, String>,
    tables: &BTreeSet<Vec<String>>,
) -> ConfigurationResult<()> {
    let (source, destination) = match mutation {
        SemanticMutation::RemoveTable { path } => (path, None),
        SemanticMutation::RenameTable { path, destination } => (path, Some(destination)),
        _ => return Err(ConfigurationFailure::InvalidInput),
    };
    if !owns_path(allowed, source.segments())
        || destination.is_some_and(|path| !owns_path(allowed, path.segments()))
    {
        return Err(ConfigurationFailure::UnsupportedSchema);
    }
    for path in keys
        .keys()
        .chain(tables.iter())
        .filter(|path| path.starts_with(source.segments()))
    {
        if !owns_path(allowed, path) {
            return Err(ConfigurationFailure::UnsupportedSchema);
        }
        if let Some(destination) = destination {
            let mut relocated = destination.segments().to_vec();
            relocated.extend_from_slice(&path[source.segments().len()..]);
            if !owns_path(allowed, &relocated) {
                return Err(ConfigurationFailure::UnsupportedSchema);
            }
        }
    }
    Ok(())
}
fn apply_mutations<T: TomlPreparation>(
    toml: &mut T,
    text: &str,
    initial: &TomlSnapshot,
    mutations: &[SemanticMutation],
    allowed: &[TomlPath],
) -> ConfigurationResult<String> {
    let mut candidate = text.to_owned();
    let mut expected = tree(initial)?;
    let original_tables = table_tree(initial)?;
    let mut expected_tables = original_tables.clone();
    let mut removable_ancestors = BTreeSet::new();
    let mut touched = BTreeSet::new();
    for mutation in mutations {
        match mutation {
            SemanticMutation::Set { path, value } => {
                if !touched.insert(path.segments().to_vec()) {
                    return Err(ConfigurationFailure::InvalidInput);
                }
                let normalized = toml.normalize(value)?;
                expected.insert(path.segments().to_vec(), normalized);
                expected_tables.extend(proper_prefixes(path.segments()));
                candidate = toml.set(&candidate, path, value)?;
            }
            SemanticMutation::Remove { path } => {
                if !touched.insert(path.segments().to_vec()) {
                    return Err(ConfigurationFailure::InvalidInput);
                }
                // Scalar removal must not consume an inline aggregate or table.
                if expected_tables.contains(path.segments()) {
                    return Err(ConfigurationFailure::UnsupportedSyntax);
                }
                if expected.remove(path.segments()).is_some() {
                    removable_ancestors.extend(proper_prefixes(path.segments()));
                }
                candidate = toml.remove(&candidate, path)?;
            }
            SemanticMutation::RemoveTable { path } => {
                check_table_ownership(mutation, allowed, &expected, &expected_tables)?;
                if expected_tables.contains(path.segments()) {
                    removable_ancestors.extend(proper_prefixes(path.segments()));
                }
                expected.retain(|key, _| !key.starts_with(path.segments()));
                expected_tables.retain(|key| !key.starts_with(path.segments()));
                candidate = toml.remove_table(&candidate, path)?;
            }
            SemanticMutation::RenameTable { path, destination } => {
                check_table_ownership(mutation, allowed, &expected, &expected_tables)?;
                if path.segments().starts_with(destination.segments())
                    || destination.segments().starts_with(path.segments())
                    || !expected_tables.contains(path.segments())
                    || expected
                        .keys()
                        .chain(expected_tables.iter())
                        .any(|key| key.starts_with(destination.segments()))
                {
                    return Err(ConfigurationFailure::InvalidInput);
                }
                removable_ancestors.extend(proper_prefixes(path.segments()));
                let removed = expected
                    .iter()
                    .filter(|(key, _)| key.starts_with(path.segments()))
                    .map(|(k, v)| (k.clone(), v.clone()))
                    .collect::<Vec<_>>();
                for (key, value) in removed {
                    expected.remove(&key);
                    let mut new = destination.segments().to_vec();
                    new.extend_from_slice(&key[path.segments().len()..]);
                    if expected.insert(new, value).is_some() {
                        return Err(ConfigurationFailure::InvalidInput);
                    }
                }
                let moved_tables = expected_tables
                    .iter()
                    .filter(|key| key.starts_with(path.segments()))
                    .cloned()
                    .collect::<Vec<_>>();
                for key in moved_tables {
                    expected_tables.remove(&key);
                    let mut relocated = destination.segments().to_vec();
                    relocated.extend_from_slice(&key[path.segments().len()..]);
                    expected_tables.insert(relocated);
                }
                expected_tables.extend(proper_prefixes(destination.segments()));
                candidate = toml.rename_table(&candidate, path, destination)?;
            }
        }
        if candidate.len() > MAX_DOCUMENT_BYTES {
            return Err(ConfigurationFailure::Capacity);
        }
    }
    toml.validate(&candidate)?;
    let observed = toml.read(&candidate)?;
    let observed_tables = table_tree(&observed)?;
    if tree(&observed)? != expected || !observed_tables.is_subset(&expected_tables) {
        return Err(ConfigurationFailure::InvalidOwnerResult);
    }
    // ABI v1 does not report explicit headers. Permit only schema-owned,
    // newly emptied ancestors to be pruned; every unowned and initially empty
    // table is preserved. Remaining table additions/moves must match exactly.
    for missing in expected_tables.difference(&observed_tables) {
        let initially_empty = original_tables.contains(missing)
            && !initial
                .overrides
                .iter()
                .any(|entry| entry.path.segments().starts_with(missing))
            && !original_tables
                .iter()
                .any(|path| path != missing && path.starts_with(missing));
        if !removable_ancestors.contains(missing)
            || !owns_path(allowed, missing)
            || initially_empty
            || expected.keys().any(|path| path.starts_with(missing))
            || observed_tables.iter().any(|path| path.starts_with(missing))
        {
            return Err(ConfigurationFailure::InvalidOwnerResult);
        }
    }
    Ok(candidate)
}
