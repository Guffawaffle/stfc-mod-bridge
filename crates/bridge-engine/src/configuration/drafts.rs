use super::schema::{SyncProjectedProxy, accepts};
use super::*;
use bridge_contracts::v1::*;
use bridge_toml::{TomlOverride, TomlSnapshot};
use std::collections::{BTreeMap, BTreeSet};

struct DraftRecord {
    baseline: DocumentRead,
    snapshot: DraftSnapshot,
    acknowledgment: Option<SetDraftChangesResult>,
}
impl DraftRecord {
    fn projection(&self) -> Self {
        Self {
            baseline: DocumentRead {
                binding: self.baseline.binding.clone(),
                bytes: self.baseline.bytes.clone(),
            },
            snapshot: self.snapshot.clone(),
            acknowledgment: self.acknowledgment.clone(),
        }
    }
}
pub(crate) struct CompletionPublication {
    drafts: Vec<(DraftId, DraftRecord)>,
    clear_vault: Vec<DraftId>,
    document: Option<DocumentRead>,
}
impl CompletionPublication {
    pub(crate) fn snapshots(&self) -> Vec<DraftSnapshot> {
        self.drafts
            .iter()
            .map(|(_, record)| record.snapshot.clone())
            .collect()
    }
}
enum VaultReference {
    Private(PrivateValueRef),
    Secret(SecretRef),
}
#[derive(PartialEq, Eq)]
enum SavedSubject {
    ScalarField,
    SyncEndpoint(DestinationId),
    SyncProxy(DestinationId),
}
struct VaultEntry {
    reference: VaultReference,
    value: ProtectedValue,
    saved_subject: Option<SavedSubject>,
}

/// One actor-local store is shared by Settings and Data Sync. No selection or
/// second host epoch is invented by a screen. Drafts never hold writer leases.
pub struct ConfigurationWorkspace<T, S, O, E, I> {
    pub(crate) toml: T,
    pub(crate) schemas: S,
    pub(crate) owner: O,
    entry: E,
    ids: I,
    host: HostEpoch,
    drafts: BTreeMap<DraftId, DraftRecord>,
    documents: BTreeMap<DocumentId, DocumentRead>,
    vault: BTreeMap<String, VaultEntry>,
    used_ids: BTreeSet<String>,
}
impl<T: TomlPreparation, S: SchemaSource, O: DocumentOwner, E: SensitiveEntry, I: ConfigurationIds>
    ConfigurationWorkspace<T, S, O, E, I>
{
    pub fn new(host: HostEpoch, toml: T, schemas: S, owner: O, entry: E, ids: I) -> Self {
        Self {
            toml,
            schemas,
            owner,
            entry,
            ids,
            host,
            drafts: BTreeMap::new(),
            documents: BTreeMap::new(),
            vault: BTreeMap::new(),
            used_ids: BTreeSet::new(),
        }
    }
    pub fn host_epoch(&self) -> &HostEpoch {
        &self.host
    }
    pub fn sensitive_entry_available(&self) -> bool {
        self.entry.is_available()
    }
    fn issue_id(&mut self, id: String) -> ConfigurationResult<String> {
        if self.used_ids.len() >= 65536 || !self.used_ids.insert(id.clone()) {
            return Err(ConfigurationFailure::Capacity);
        }
        Ok(id)
    }
    pub fn read_configuration(
        &mut self,
        target: &TargetSelector,
    ) -> ConfigurationResult<DocumentSnapshot> {
        self.read_configuration_with_preflight(target, |_, _| Ok(()))
    }
    /// Validate the exact observation and stale-draft events before publishing
    /// document projections or protected handles into this actor-local store.
    pub fn read_configuration_with_preflight(
        &mut self,
        target: &TargetSelector,
        preflight: impl FnOnce(&DocumentSnapshot, &[DraftSnapshot]) -> ConfigurationResult<()>,
    ) -> ConfigurationResult<DocumentSnapshot> {
        let read = self.owner.resolve(target)?;
        validate_resolved_target(target, &read.binding.target)?;
        self.observe(read, preflight)
    }
    pub fn refresh_document(
        &mut self,
        expected: &DocumentBinding,
    ) -> ConfigurationResult<DocumentSnapshot> {
        let read = self.owner.read(expected)?;
        if &read.binding != expected {
            return Err(ConfigurationFailure::Stale);
        }
        self.observe(read, |_, _| Ok(()))
    }
    fn observe(
        &mut self,
        read: DocumentRead,
        preflight: impl FnOnce(&DocumentSnapshot, &[DraftSnapshot]) -> ConfigurationResult<()>,
    ) -> ConfigurationResult<DocumentSnapshot> {
        let text = read.text()?;
        let schema = self.schemas.resolve(&read.binding.schema)?;
        schema.validate(&read.binding.schema)?;
        self.toml.validate(text)?;
        let source = self.toml.read(text)?;
        if self.documents.len() >= MAX_DRAFTS
            && !self.documents.contains_key(&read.binding.document_id)
        {
            return Err(ConfigurationFailure::Capacity);
        }
        let mut fields = Vec::new();
        let mut additions = Vec::new();
        for definition in schema.schema.fields.as_slice() {
            let policy = schema.field(&definition.field_id)?;
            let value = select_override(policy, &source)?;
            let projected = match (definition.sensitivity, value) {
                (Sensitivity::Public, Some(v)) => {
                    let public =
                        self.schemas
                            .decode_public(&schema, definition, &v.semantic_value)?;
                    if !accepts(&definition.value_type, &public) {
                        return Err(ConfigurationFailure::InvalidDocument);
                    }
                    ProjectedValue::Public { value: public }
                }
                (Sensitivity::Private, Some(v)) => {
                    let value = ProtectedValue::new(v.semantic_value.as_bytes().to_vec())?;
                    let subject = SavedSubject::ScalarField;
                    let reference = self.saved_reference(
                        &read.binding,
                        &definition.field_id,
                        &subject,
                        &value,
                    )?;
                    additions.push((
                        reference.value_id.as_str().to_owned(),
                        VaultEntry {
                            reference: VaultReference::Private(reference.clone()),
                            value,
                            saved_subject: Some(subject),
                        },
                    ));
                    ProjectedValue::Private {
                        reference: Box::new(reference),
                    }
                }
                (Sensitivity::Secret, value) => ProjectedValue::Secret {
                    configured: value.is_some(),
                },
                (Sensitivity::Public, None) => definition
                    .default_value
                    .clone()
                    .map(|value| ProjectedValue::Public { value })
                    .unwrap_or(ProjectedValue::Absent),
                (_, None) => ProjectedValue::Absent,
            };
            fields.push(FieldState {
                field_id: definition.field_id.clone(),
                overridden: value.is_some(),
                value: projected,
            });
        }
        let mut sync = Vec::new();
        for projected in self.schemas.project_sync(&schema, &source)? {
            let subject = SavedSubject::SyncEndpoint(projected.id.clone());
            let endpoint = self.saved_reference(
                &read.binding,
                &projected.endpoint_field,
                &subject,
                &projected.endpoint,
            )?;
            additions.push((
                endpoint.value_id.as_str().to_owned(),
                VaultEntry {
                    reference: VaultReference::Private(endpoint.clone()),
                    value: projected.endpoint,
                    saved_subject: Some(subject),
                },
            ));
            let desired_proxy = match projected.proxy {
                SyncProjectedProxy::Global => ProxyChoice::Global,
                SyncProjectedProxy::None => ProxyChoice::None,
                SyncProjectedProxy::Custom { field, value } => {
                    let subject = SavedSubject::SyncProxy(projected.id.clone());
                    let reference =
                        self.saved_reference(&read.binding, &field, &subject, &value)?;
                    additions.push((
                        reference.value_id.as_str().to_owned(),
                        VaultEntry {
                            reference: VaultReference::Private(reference.clone()),
                            value,
                            saved_subject: Some(subject),
                        },
                    ));
                    ProxyChoice::Custom {
                        reference: Box::new(reference),
                    }
                }
            };
            sync.push(SyncDestination {
                id: projected.id,
                mode: projected.mode,
                exposure: projected.exposure,
                endpoint,
                secret_configured: projected.secret_configured,
                desired_proxy,
                resolved_proxy: Observation::Unavailable {
                    reason: ObservationReason::NativeUnavailable,
                },
                feeds: projected.feeds,
            });
        }
        let new_entries = additions
            .iter()
            .filter(|(id, _)| !self.vault.contains_key(id))
            .count();
        if self.vault.len() + new_entries > MAX_PROTECTED_ENTRIES {
            return Err(ConfigurationFailure::Capacity);
        }
        let snapshot = DocumentSnapshot {
            binding: read.binding.clone(),
            schema: schema.schema,
            fields: bounded(fields)?,
            preservation: PreservationState::Supported,
            sync: bounded(sync)?,
        };
        let stale = self
            .drafts
            .values()
            .filter(|d| {
                d.snapshot.draft.document.document_id == read.binding.document_id
                    && d.snapshot.draft.document != read.binding
                    && !matches!(d.snapshot.state, DraftState::Invalid | DraftState::Stale)
            })
            .map(|record| {
                let mut snapshot = record.snapshot.clone();
                snapshot.state = DraftState::Stale;
                snapshot.validation = bounded(vec![]).expect("empty validation");
                snapshot
            })
            .collect::<Vec<_>>();
        preflight(&snapshot, &stale)?;
        for (id, value) in additions {
            self.vault.insert(id, value);
        }
        for snapshot in stale {
            let record = self
                .drafts
                .get_mut(&snapshot.draft.draft_id)
                .expect("preflighted stored draft");
            record.snapshot = snapshot;
        }
        self.documents
            .insert(read.binding.document_id.clone(), read);
        Ok(snapshot)
    }
    fn saved_reference(
        &mut self,
        document: &DocumentBinding,
        field_id: &FieldId,
        subject: &SavedSubject,
        value: &ProtectedValue,
    ) -> ConfigurationResult<PrivateValueRef> {
        let source = value.source()?;
        for entry in self.vault.values() {
            if let VaultReference::Private(reference) = &entry.reference
                && reference.document == *document
                && reference.field_id == *field_id
                && reference.captured_for.is_none()
                && entry.saved_subject.as_ref() == Some(subject)
                && entry.value.source()? == source
            {
                // A saved handle is immutable. Different subjects or bytes
                // receive new IDs even when a producer repeats the binding.
                return Ok(reference.clone());
            }
        }
        let value_id = self.ids.private_id()?;
        self.issue_id(value_id.as_str().to_owned())?;
        Ok(PrivateValueRef {
            value_id,
            document: document.clone(),
            field_id: field_id.clone(),
            revision: document.revision.clone(),
            captured_for: None,
        })
    }
    pub fn open_draft(&mut self, input: &OpenDraftInput) -> ConfigurationResult<DraftSnapshot> {
        self.open_draft_with_preflight(input, |_| Ok(()))
    }
    pub fn open_draft_with_preflight(
        &mut self,
        input: &OpenDraftInput,
        preflight: impl FnOnce(&DraftSnapshot) -> ConfigurationResult<()>,
    ) -> ConfigurationResult<DraftSnapshot> {
        super::types::validate_command(Command::OpenDraft(input.clone()))?;
        if self.drafts.len() >= MAX_DRAFTS {
            return Err(ConfigurationFailure::Capacity);
        }
        let read = self.owner.read(&input.document)?;
        if read.binding != input.document {
            return Err(ConfigurationFailure::Stale);
        }
        read.text()?;
        self.toml.validate(read.text()?)?;
        let schema = self.schemas.resolve(&read.binding.schema)?;
        schema.validate(&read.binding.schema)?;
        let draft_id = self.ids.draft_id()?;
        self.issue_id(draft_id.as_str().to_owned())?;
        let snapshot = DraftSnapshot {
            draft: DraftRef {
                draft_id: draft_id.clone(),
                host_epoch: self.host.clone(),
                revision: RevisionCounter::new(0),
                document: read.binding.clone(),
            },
            schema: schema.schema,
            edits: bounded(vec![])?,
            apply: bounded(vec![])?,
            state: DraftState::Clean,
            validation: bounded(vec![])?,
        };
        super::types::validate_result(CommandResult::OpenDraft(snapshot.clone()))?;
        preflight(&snapshot)?;
        self.drafts.insert(
            draft_id,
            DraftRecord {
                baseline: read,
                snapshot: snapshot.clone(),
                acknowledgment: None,
            },
        );
        Ok(snapshot)
    }
    fn current(&self, draft: &DraftRef) -> ConfigurationResult<&DraftRecord> {
        if draft.host_epoch != self.host {
            return Err(ConfigurationFailure::HostMismatch);
        }
        let record = self
            .drafts
            .get(&draft.draft_id)
            .ok_or(ConfigurationFailure::Stale)?;
        if &record.snapshot.draft != draft {
            return Err(ConfigurationFailure::Stale);
        }
        Ok(record)
    }
    pub fn draft(&self, draft: &DraftRef) -> ConfigurationResult<DraftSnapshot> {
        Ok(self.current(draft)?.snapshot.clone())
    }
    /// Immutable current-generation reconciliation. Host identity is checked
    /// before lookup; neither a missing ID nor an old revision triggers I/O.
    pub fn current_draft(
        &self,
        host_epoch: &HostEpoch,
        draft_id: &DraftId,
    ) -> ConfigurationResult<Option<DraftSnapshot>> {
        if host_epoch != &self.host {
            return Err(ConfigurationFailure::HostMismatch);
        }
        Ok(self
            .drafts
            .get(draft_id)
            .map(|record| record.snapshot.clone()))
    }
    /// Completion refresh is bound to the captured Save, never current UI
    /// selection. A later local stage cannot be erased by an older completion.
    pub fn draft_after_save(&self, captured: &DraftRef) -> ConfigurationResult<DraftSnapshot> {
        if captured.host_epoch != self.host {
            return Err(ConfigurationFailure::HostMismatch);
        }
        let record = self
            .drafts
            .get(&captured.draft_id)
            .ok_or(ConfigurationFailure::Stale)?;
        if record.snapshot.draft.document.document_id != captured.document.document_id
            || record.snapshot.draft.document.target != captured.document.target
            || record.snapshot.draft.document.schema != captured.document.schema
            || record.snapshot.draft.revision.get() <= captured.revision.get()
            || record.snapshot.state != DraftState::Clean
        {
            return Err(ConfigurationFailure::Stale);
        }
        Ok(record.snapshot.clone())
    }
    pub fn request_sensitive_input(
        &mut self,
        input: &RequestSensitiveInputInput,
    ) -> ConfigurationResult<SensitiveInputResult> {
        self.request_sensitive_input_with_preflight(input, |_| Ok(()))
    }
    pub fn request_sensitive_input_with_preflight(
        &mut self,
        input: &RequestSensitiveInputInput,
        preflight: impl FnOnce(&SensitiveInputResult) -> ConfigurationResult<()>,
    ) -> ConfigurationResult<SensitiveInputResult> {
        super::types::validate_command(Command::RequestSensitiveInput(input.clone()))?;
        let record = self.current(&input.draft)?;
        let field = record
            .snapshot
            .schema
            .fields
            .as_slice()
            .iter()
            .find(|f| f.field_id == input.field_id)
            .ok_or(ConfigurationFailure::UnsupportedSchema)?;
        if !matches!(
            (field.sensitivity, input.sensitivity),
            (Sensitivity::Private, SensitiveInputKind::Private)
                | (Sensitivity::Secret, SensitiveInputKind::Secret)
        ) {
            return Err(ConfigurationFailure::ProtectedRefInvalid);
        }
        if self.vault.len() >= MAX_PROTECTED_ENTRIES || self.used_ids.len() >= 65536 {
            return Err(ConfigurationFailure::Capacity);
        }
        let mut addition = None;
        let outcome = match self.entry.capture(input)? {
            ProtectedEntryOutcome::Cancelled => SensitiveInputOutcome::Cancelled,
            ProtectedEntryOutcome::Unavailable(reason) => {
                SensitiveInputOutcome::Unavailable { reason }
            }
            ProtectedEntryOutcome::Captured(value) => {
                // Verify this is exactly one producer-valid TOML value. Entry
                // itself owns native protected capture, not renderer plaintext.
                self.toml.normalize(value.source()?)?;
                match input.sensitivity {
                    SensitiveInputKind::Private => {
                        let value_id = self.ids.private_id()?;
                        self.issue_id(value_id.as_str().to_owned())?;
                        let reference = PrivateValueRef {
                            value_id: value_id.clone(),
                            document: input.draft.document.clone(),
                            field_id: input.field_id.clone(),
                            revision: input.draft.document.revision.clone(),
                            captured_for: Some(Box::new(input.draft.clone())),
                        };
                        addition = Some((
                            value_id.as_str().to_owned(),
                            VaultEntry {
                                reference: VaultReference::Private(reference.clone()),
                                value,
                                saved_subject: None,
                            },
                        ));
                        SensitiveInputOutcome::CapturedPrivate {
                            reference: Box::new(reference),
                        }
                    }
                    SensitiveInputKind::Secret => {
                        let secret_id = self.ids.secret_id()?;
                        self.issue_id(secret_id.as_str().to_owned())?;
                        let reference = SecretRef {
                            secret_id: secret_id.clone(),
                            draft: input.draft.clone(),
                            field_id: input.field_id.clone(),
                        };
                        addition = Some((
                            secret_id.as_str().to_owned(),
                            VaultEntry {
                                reference: VaultReference::Secret(reference.clone()),
                                value,
                                saved_subject: None,
                            },
                        ));
                        SensitiveInputOutcome::CapturedSecret {
                            reference: Box::new(reference),
                        }
                    }
                }
            }
        };
        let result = SensitiveInputResult {
            binding: input.clone(),
            outcome,
        };
        super::types::validate_result(CommandResult::RequestSensitiveInput(result.clone()))?;
        preflight(&result)?;
        if let Some((id, entry)) = addition {
            self.vault.insert(id, entry);
        }
        Ok(result)
    }
    /// Preflight serializes the actual reply envelope (including correlation
    /// and cursor) before any revision or vault payload ownership changes.
    pub fn set_draft_changes(
        &mut self,
        input: SetDraftChangesInput,
        preflight: impl FnOnce(&SetDraftChangesResult) -> ConfigurationResult<()>,
    ) -> ConfigurationResult<SetDraftChangesResult> {
        super::types::validate_command(Command::SetDraftChanges(input.clone()))?;
        if input.draft.host_epoch != self.host {
            return Err(ConfigurationFailure::HostMismatch);
        }
        if let Some(record) = self.drafts.get(&input.draft.draft_id)
            && let Some(receipt) = &record.acknowledgment
            && receipt.accepted == input
            && receipt.snapshot == record.snapshot
        {
            preflight(receipt)?;
            return Ok(receipt.clone());
        }
        let record = self.current(&input.draft)?;
        if record.snapshot.state == DraftState::Stale {
            return Err(ConfigurationFailure::Stale);
        }
        let schema = record.snapshot.schema.clone();
        let (validation, apply) = validate_edits(&schema, input.edits.as_slice())?;
        let mut successor = input.draft.clone();
        // The exact lost-ack replay above returns its original receipt. Every
        // fresh synchronization of the current draft advances once, including
        // unchanged edits after Stay or a refused preparation.
        successor.revision = RevisionCounter::new(
            successor
                .revision
                .get()
                .checked_add(1)
                .ok_or(ConfigurationFailure::Capacity)?,
        );
        let mut edits = input.edits.as_slice().to_vec();
        let mut transfers: Vec<ProtectedReferenceTransfer> = Vec::new();
        let mut seen: BTreeMap<String, ProtectedReferenceTransfer> = BTreeMap::new();
        visit_refs(&mut edits, &mut |reference| {
            let key = reference.key().to_owned();
            self.validate_reference(&reference, &input.draft)?;
            if reference.captured() {
                if let Some(transfer) = seen.get(&key) {
                    match (reference, transfer) {
                        (
                            MutableReference::Private(old),
                            ProtectedReferenceTransfer::Private { to, .. },
                        ) => *old = to.as_ref().clone(),
                        (
                            MutableReference::Secret(old),
                            ProtectedReferenceTransfer::Secret { to, .. },
                        ) => *old = to.as_ref().clone(),
                        _ => return Err(ConfigurationFailure::ProtectedRefInvalid),
                    }
                    return Ok(());
                }
                match reference {
                    MutableReference::Private(old) => {
                        let value_id = self.ids.private_id()?;
                        self.issue_id(value_id.as_str().to_owned())?;
                        let mut new = old.clone();
                        new.value_id = value_id;
                        new.captured_for = Some(Box::new(successor.clone()));
                        transfers.push(ProtectedReferenceTransfer::Private {
                            from: Box::new(old.clone()),
                            to: Box::new(new.clone()),
                        });
                        *old = new;
                    }
                    MutableReference::Secret(old) => {
                        let secret_id = self.ids.secret_id()?;
                        self.issue_id(secret_id.as_str().to_owned())?;
                        let mut new = old.clone();
                        new.secret_id = secret_id;
                        new.draft = successor.clone();
                        transfers.push(ProtectedReferenceTransfer::Secret {
                            from: Box::new(old.clone()),
                            to: Box::new(new.clone()),
                        });
                        *old = new;
                    }
                }
                seen.insert(
                    key,
                    transfers
                        .last()
                        .ok_or(ConfigurationFailure::InvalidOwnerResult)?
                        .clone(),
                );
            }
            Ok(())
        })?;
        let snapshot = DraftSnapshot {
            draft: successor,
            schema,
            edits: bounded(edits)?,
            apply: bounded(apply)?,
            state: if !validation.is_empty() {
                DraftState::Invalid
            } else if input.edits.as_slice().is_empty() {
                DraftState::Clean
            } else {
                DraftState::Dirty
            },
            validation: bounded(validation)?,
        };
        let receipt = SetDraftChangesResult {
            accepted: input,
            snapshot,
            protected_transfers: bounded(transfers)?,
        };
        super::types::validate_result(CommandResult::SetDraftChanges(Box::new(receipt.clone())))?;
        preflight(&receipt)?;
        for transfer in receipt.protected_transfers.as_slice() {
            let (old, new, reference) = match transfer {
                ProtectedReferenceTransfer::Private { from, to } => (
                    from.value_id.as_str(),
                    to.value_id.as_str(),
                    VaultReference::Private((**to).clone()),
                ),
                ProtectedReferenceTransfer::Secret { from, to } => (
                    from.secret_id.as_str(),
                    to.secret_id.as_str(),
                    VaultReference::Secret((**to).clone()),
                ),
            };
            let mut entry = self
                .vault
                .remove(old)
                .ok_or(ConfigurationFailure::InvalidOwnerResult)?;
            entry.reference = reference;
            self.vault.insert(new.to_owned(), entry);
        }
        let record = self
            .drafts
            .get_mut(&receipt.snapshot.draft.draft_id)
            .ok_or(ConfigurationFailure::Stale)?;
        record.snapshot = receipt.snapshot.clone();
        record.acknowledgment = Some(receipt.clone());
        // Captures omitted by a complete edit acknowledgment no longer have
        // any admitted custody. Historical receipt refs carry no payload.
        let mut used = BTreeSet::new();
        let mut active = receipt.snapshot.edits.as_slice().to_vec();
        visit_refs(&mut active, &mut |r| {
            used.insert(r.key().to_owned());
            Ok(())
        })?;
        let draft_id = &receipt.snapshot.draft.draft_id;
        self.vault
            .retain(|id, e| !entry_belongs(e, draft_id) || used.contains(id));
        Ok(receipt)
    }
    fn validate_reference(
        &self,
        reference: &MutableReference<'_>,
        draft: &DraftRef,
    ) -> ConfigurationResult<()> {
        let entry = self
            .vault
            .get(reference.key())
            .ok_or(ConfigurationFailure::ProtectedRefInvalid)?;
        let valid = match (reference, &entry.reference) {
            (MutableReference::Private(p), VaultReference::Private(saved)) => {
                **p == *saved
                    && p.document == draft.document
                    && p.captured_for.as_deref().is_none_or(|d| d == draft)
            }
            (MutableReference::Secret(s), VaultReference::Secret(saved)) => {
                **s == *saved && s.draft == *draft
            }
            _ => false,
        };
        if valid {
            Ok(())
        } else {
            Err(ConfigurationFailure::ProtectedRefInvalid)
        }
    }
    pub fn discard_draft(
        &mut self,
        input: &DiscardDraftInput,
    ) -> ConfigurationResult<DiscardedDraft> {
        self.discard_draft_with_preflight(input, |_| Ok(()))
    }
    pub fn discard_draft_with_preflight(
        &mut self,
        input: &DiscardDraftInput,
        preflight: impl FnOnce(&DiscardedDraft) -> ConfigurationResult<()>,
    ) -> ConfigurationResult<DiscardedDraft> {
        super::types::validate_command(Command::DiscardDraft(input.clone()))?;
        self.current(&input.draft)?;
        let result = DiscardedDraft {
            draft_id: input.draft.draft_id.clone(),
            host_epoch: self.host.clone(),
            previous_revision: input.draft.revision,
        };
        super::types::validate_result(CommandResult::DiscardDraft(result.clone()))?;
        preflight(&result)?;
        self.drafts.remove(&input.draft.draft_id);
        self.vault
            .retain(|_, e| !entry_belongs(e, &input.draft.draft_id));
        Ok(result)
    }
    pub(crate) fn baseline(
        &self,
        draft: &DraftRef,
    ) -> ConfigurationResult<(&DocumentRead, &DraftSnapshot)> {
        let record = self.current(draft)?;
        if matches!(
            record.snapshot.state,
            DraftState::Invalid | DraftState::Stale
        ) {
            return Err(ConfigurationFailure::InvalidDocument);
        }
        Ok((&record.baseline, &record.snapshot))
    }
    pub(crate) fn protected_source(&self, id: &str) -> ConfigurationResult<&str> {
        self.vault
            .get(id)
            .ok_or(ConfigurationFailure::ProtectedRefInvalid)?
            .value
            .source()
    }
    pub(crate) fn prepare_completion(
        &self,
        captured: Option<&DraftSnapshot>,
        outcome: &CompletionOutcome,
        read: Option<&DocumentRead>,
    ) -> ConfigurationResult<CompletionPublication> {
        let mut publication = CompletionPublication {
            drafts: vec![],
            clear_vault: vec![],
            document: None,
        };
        if matches!(outcome, CompletionOutcome::NoChange { .. }) {
            if let Some(captured) = captured
                && let Some(original) = self.drafts.get(&captured.draft.draft_id)
                && &original.snapshot == captured
                && !original.snapshot.edits.as_slice().is_empty()
            {
                let mut record = original.projection();
                record.snapshot.draft.revision = RevisionCounter::new(
                    record
                        .snapshot
                        .draft
                        .revision
                        .get()
                        .checked_add(1)
                        .ok_or(ConfigurationFailure::Capacity)?,
                );
                record.snapshot.edits = bounded(vec![])?;
                record.snapshot.apply = bounded(vec![])?;
                record.snapshot.validation = bounded(vec![])?;
                record.snapshot.state = DraftState::Clean;
                record.acknowledgment = None;
                super::types::validate_result(CommandResult::OpenDraft(record.snapshot.clone()))?;
                publication
                    .drafts
                    .push((captured.draft.draft_id.clone(), record));
                publication
                    .clear_vault
                    .push(captured.draft.draft_id.clone());
            }
            return Ok(publication);
        }
        let CompletionOutcome::Changed {
            receipt: Some(receipt),
            ..
        } = outcome
        else {
            return Ok(publication);
        };
        let EffectReceipt::ConfigurationWritten { document, .. } = receipt.as_ref() else {
            return Err(ConfigurationFailure::InvalidOwnerResult);
        };
        let read = read.ok_or(ConfigurationFailure::InvalidOwnerResult)?;
        if &read.binding != document {
            return Err(ConfigurationFailure::RecoveryRequired);
        }
        read.text()?;
        for original in self.drafts.values().filter(|record| {
            record.snapshot.draft.document.document_id == document.document_id
                && record.snapshot.draft.document.target == document.target
        }) {
            let mut record = original.projection();
            if captured.is_some_and(|snapshot| snapshot == &record.snapshot) {
                record.snapshot.draft.revision = RevisionCounter::new(
                    record
                        .snapshot
                        .draft
                        .revision
                        .get()
                        .checked_add(1)
                        .ok_or(ConfigurationFailure::Capacity)?,
                );
                record.snapshot.draft.document = document.clone();
                record.baseline = DocumentRead {
                    binding: read.binding.clone(),
                    bytes: read.bytes.clone(),
                };
                record.snapshot.edits = bounded(vec![])?;
                record.snapshot.apply = bounded(vec![])?;
                record.snapshot.validation = bounded(vec![])?;
                record.snapshot.state = DraftState::Clean;
                record.acknowledgment = None;
                publication
                    .clear_vault
                    .push(record.snapshot.draft.draft_id.clone());
            } else if record.snapshot.draft.document != *document
                && record.snapshot.state != DraftState::Invalid
            {
                record.snapshot.state = DraftState::Stale;
                record.snapshot.validation = bounded(vec![])?;
            }
            if record.snapshot != original.snapshot {
                super::types::validate_result(CommandResult::OpenDraft(record.snapshot.clone()))?;
                publication
                    .drafts
                    .push((record.snapshot.draft.draft_id.clone(), record));
            }
        }
        publication.document = Some(DocumentRead {
            binding: read.binding.clone(),
            bytes: read.bytes.clone(),
        });
        Ok(publication)
    }
    pub(crate) fn publish_completion(&mut self, publication: CompletionPublication) {
        for (id, record) in publication.drafts {
            self.drafts.insert(id, record);
        }
        self.vault.retain(|_, entry| {
            !publication
                .clear_vault
                .iter()
                .any(|id| entry_belongs(entry, id))
        });
        if let Some(read) = publication.document {
            self.documents
                .insert(read.binding.document_id.clone(), read);
        }
    }
    pub fn owner_mut(&mut self) -> &mut O {
        &mut self.owner
    }
}

fn select_override<'a>(
    policy: &FieldPolicy,
    snapshot: &'a TomlSnapshot,
) -> ConfigurationResult<Option<&'a TomlOverride>> {
    let values = std::iter::once(&policy.canonical)
        .chain(&policy.aliases)
        .filter_map(|path| snapshot.overrides.iter().find(|v| v.path == *path))
        .collect::<Vec<_>>();
    if values.len() <= 1 {
        return Ok(values.first().copied());
    }
    match &policy.precedence {
        AliasPolicy::RejectMultiple => Err(ConfigurationFailure::UnsupportedSchema),
        AliasPolicy::Precedence(order) => Ok(order
            .iter()
            .find_map(|p| values.iter().find(|v| &v.path == p).copied())),
    }
}
fn validate_resolved_target(
    selector: &TargetSelector,
    binding: &ResolvedTarget,
) -> ConfigurationResult<()> {
    let installation = match (&selector.installation, &binding.installation) {
        (
            InstallationSelector::Registered {
                id,
                revision_assertion,
                ..
            },
            InstallationBinding::Registered {
                registration_id,
                registration_revision,
                ..
            },
        ) => {
            id == registration_id
                && revision_assertion
                    .as_ref()
                    .is_none_or(|r| r == registration_revision)
        }
        (InstallationSelector::Directory { .. }, _) => true, // physical path assertion belongs to the retained native owner
        _ => false,
    };
    let profile = match (&selector.profile, &binding.profile) {
        (
            ProfileSelector::Ordinary {
                catalog_id_assertion,
            },
            ProfileBinding::Ordinary { ordinary_id, .. },
        ) => catalog_id_assertion
            .as_ref()
            .is_none_or(|id| ordinary_id.as_ref() == Some(id)),
        (
            ProfileSelector::Isolated {
                id,
                revision_assertion,
            },
            ProfileBinding::Isolated {
                id: captured,
                revision,
            },
        ) => id == captured && revision_assertion.as_ref().is_none_or(|r| r == revision),
        _ => false,
    };
    if installation && profile {
        Ok(())
    } else {
        Err(ConfigurationFailure::InvalidOwnerResult)
    }
}
enum MutableReference<'a> {
    Private(&'a mut PrivateValueRef),
    Secret(&'a mut SecretRef),
}
impl MutableReference<'_> {
    fn key(&self) -> &str {
        match self {
            Self::Private(p) => p.value_id.as_str(),
            Self::Secret(s) => s.secret_id.as_str(),
        }
    }
    fn captured(&self) -> bool {
        match self {
            Self::Private(p) => p.captured_for.is_some(),
            Self::Secret(_) => true,
        }
    }
}
fn visit_refs(
    edits: &mut [ConfigurationEdit],
    visit: &mut impl FnMut(MutableReference<'_>) -> ConfigurationResult<()>,
) -> ConfigurationResult<()> {
    for edit in edits {
        match edit {
            ConfigurationEdit::SetPrivate { reference, .. } => {
                visit(MutableReference::Private(reference))?
            }
            ConfigurationEdit::ReplaceSecret { reference, .. } => {
                visit(MutableReference::Secret(reference))?
            }
            ConfigurationEdit::AddSyncDestination { destination } => {
                visit(MutableReference::Private(&mut destination.endpoint))?;
                visit(MutableReference::Secret(&mut destination.secret))?;
                if let ProxyChoice::Custom { reference } = &mut destination.proxy {
                    visit(MutableReference::Private(reference))?;
                }
            }
            ConfigurationEdit::SetSyncProxy {
                value: ProxyChoice::Custom { reference },
                ..
            } => visit(MutableReference::Private(reference))?,
            _ => {}
        }
    }
    Ok(())
}
fn entry_belongs(entry: &VaultEntry, id: &DraftId) -> bool {
    match &entry.reference {
        VaultReference::Private(p) => p.captured_for.as_deref().is_some_and(|d| &d.draft_id == id),
        VaultReference::Secret(s) => &s.draft.draft_id == id,
    }
}

fn validate_edits(
    schema: &ConfigurationSchema,
    edits: &[ConfigurationEdit],
) -> ConfigurationResult<(Vec<DraftViolation>, Vec<ApplyTiming>)> {
    let mut keys = BTreeSet::new();
    let mut apply = BTreeSet::new();
    let mut violations = Vec::new();
    for edit in edits {
        let (id, key) = match edit {
            ConfigurationEdit::SetPublic { field_id, .. }
            | ConfigurationEdit::SetPrivate { field_id, .. }
            | ConfigurationEdit::ReplaceSecret { field_id, .. }
            | ConfigurationEdit::ClearSecret { field_id }
            | ConfigurationEdit::RemoveOverride { field_id } => {
                (Some(field_id), format!("field:{}", field_id.as_str()))
            }
            ConfigurationEdit::AddSyncDestination { destination } => {
                (None, format!("destination:{}", destination.id.as_str()))
            }
            ConfigurationEdit::RemoveSyncDestination { destination_id } => {
                (None, format!("destination:{}", destination_id.as_str()))
            }
            ConfigurationEdit::SetSyncFeed {
                destination_id,
                feed_id,
                ..
            } => (
                None,
                format!("feed:{}:{}", destination_id.as_str(), feed_id.as_str()),
            ),
            ConfigurationEdit::SetSyncProxy { destination_id, .. } => {
                (None, format!("proxy:{}", destination_id.as_str()))
            }
        };
        if !keys.insert(key) {
            return Err(ConfigurationFailure::InvalidInput);
        }
        if let Some(id) = id {
            let Some(field) = schema.fields.as_slice().iter().find(|f| &f.field_id == id) else {
                if !matches!(edit, ConfigurationEdit::RemoveOverride { .. }) {
                    return Err(ConfigurationFailure::ProtectedRefInvalid);
                }
                violations.push(DraftViolation {
                    field_id: Some(id.clone()),
                    code: DraftViolationCode::UnknownField,
                });
                continue;
            };
            apply.insert(field.apply);
            match edit {
                ConfigurationEdit::SetPublic { value, .. } => {
                    if field.sensitivity != Sensitivity::Public {
                        return Err(ConfigurationFailure::ProtectedRefInvalid);
                    }
                    if !accepts(&field.value_type, value) {
                        violations.push(DraftViolation {
                            field_id: Some(id.clone()),
                            code: DraftViolationCode::ConstraintViolation,
                        });
                    }
                }
                ConfigurationEdit::SetPrivate { reference, .. }
                    if field.sensitivity != Sensitivity::Private || reference.field_id != *id =>
                {
                    return Err(ConfigurationFailure::ProtectedRefInvalid);
                }
                ConfigurationEdit::ReplaceSecret { reference, .. }
                    if field.sensitivity != Sensitivity::Secret || reference.field_id != *id =>
                {
                    return Err(ConfigurationFailure::ProtectedRefInvalid);
                }
                ConfigurationEdit::ClearSecret { .. }
                    if field.sensitivity != Sensitivity::Secret =>
                {
                    return Err(ConfigurationFailure::ProtectedRefInvalid);
                }
                _ => {}
            }
        } else {
            apply.insert(ApplyTiming::NextLaunch);
            let sensitivity = |id: &FieldId, required| {
                schema
                    .fields
                    .as_slice()
                    .iter()
                    .any(|f| &f.field_id == id && f.sensitivity == required)
            };
            match edit {
                ConfigurationEdit::AddSyncDestination { destination } => {
                    if !sensitivity(&destination.endpoint.field_id, Sensitivity::Private)
                        || !sensitivity(&destination.secret.field_id, Sensitivity::Secret)
                        || matches!(&destination.proxy,ProxyChoice::Custom { reference } if !sensitivity(&reference.field_id,Sensitivity::Private))
                    {
                        return Err(ConfigurationFailure::ProtectedRefInvalid);
                    }
                    if !schema.sync.as_slice().iter().any(|d| {
                        d.mode == destination.mode
                            && d.exposure == SyncExposure::Creatable
                            && d.endpoint_field_id == destination.endpoint.field_id
                            && d.secret_field_id == destination.secret.field_id
                            && match &destination.proxy {
                                ProxyChoice::Global => d.inherits_global_proxy,
                                ProxyChoice::None => true,
                                ProxyChoice::Custom { reference } => {
                                    d.proxy_field_id.as_ref() == Some(&reference.field_id)
                                }
                            }
                            && destination
                                .feeds
                                .as_slice()
                                .iter()
                                .all(|f| d.feeds.as_slice().contains(&f.feed_id))
                    }) {
                        violations.push(DraftViolation {
                            field_id: None,
                            code: DraftViolationCode::UnsupportedSyncField,
                        });
                    }
                }
                ConfigurationEdit::SetSyncProxy {
                    value: ProxyChoice::Custom { reference },
                    ..
                } if !sensitivity(&reference.field_id, Sensitivity::Private) => {
                    return Err(ConfigurationFailure::ProtectedRefInvalid);
                }
                ConfigurationEdit::SetSyncFeed { feed_id, .. }
                    if !schema
                        .sync
                        .as_slice()
                        .iter()
                        .any(|d| d.feeds.as_slice().contains(feed_id)) =>
                {
                    violations.push(DraftViolation {
                        field_id: None,
                        code: DraftViolationCode::UnsupportedSyncField,
                    })
                }
                _ => {}
            }
        }
    }
    Ok((violations, apply.into_iter().collect()))
}
