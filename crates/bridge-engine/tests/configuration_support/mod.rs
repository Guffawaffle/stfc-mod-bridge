#![allow(dead_code)]
//! Deliberately synthetic portable owners. These tests do not qualify native
//! persistence, producer schemas, ABI compatibility or game installations.
use bridge_contracts::v1::*;
use bridge_engine::configuration::*;
use bridge_toml::{TomlOverride, TomlPath, TomlSnapshot};
use sha2::{Digest, Sha256 as Hasher};
use std::{cell::RefCell, collections::BTreeMap, rc::Rc};

pub fn list<T, const N: usize>(values: Vec<T>) -> BoundedList<T, N> {
    BoundedList::new(values).unwrap()
}
pub fn id(n: u32) -> String {
    format!("{n:08x}-1111-4111-8111-111111111111")
}
pub fn hash(bytes: &[u8]) -> Sha256 {
    Sha256::new(format!(
        "sha256:{}",
        Hasher::digest(bytes)
            .iter()
            .map(|b| format!("{b:02x}"))
            .collect::<String>()
    ))
    .unwrap()
}
pub fn fixture() -> DraftSnapshot {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../contracts/fixtures/sc08-open-clean-draft-reply.json");
    let value: serde_json::Value = serde_json::from_slice(&std::fs::read(path).unwrap()).unwrap();
    serde_json::from_value(value["body"]["result"]["command"]["output"].clone()).unwrap()
}
pub fn field(id: &str) -> FieldId {
    FieldId::new(id).unwrap()
}
pub fn path(id: &str) -> TomlPath {
    TomlPath::new(id.split('.').map(str::to_owned).collect()).unwrap()
}
pub struct Ids(u32);
impl Ids {
    fn next(&mut self) -> String {
        self.0 += 1;
        id(self.0)
    }
}
impl ConfigurationIds for Ids {
    fn draft_id(&mut self) -> DraftId {
        DraftId::new(self.next()).unwrap()
    }
    fn private_id(&mut self) -> PrivateValueId {
        PrivateValueId::new(self.next()).unwrap()
    }
    fn secret_id(&mut self) -> SecretRefId {
        SecretRefId::new(self.next()).unwrap()
    }
}
pub struct Entry {
    pub outcome: Option<ProtectedEntryOutcome>,
}
impl SensitiveEntry for Entry {
    fn capture(
        &mut self,
        _: &RequestSensitiveInputInput,
    ) -> ConfigurationResult<ProtectedEntryOutcome> {
        Ok(self
            .outcome
            .take()
            .unwrap_or(ProtectedEntryOutcome::Unavailable(
                SensitiveInputUnavailableReason::ProtectedEntryUnavailable,
            )))
    }
}
pub struct Codec {
    snapshots: BTreeMap<String, TomlSnapshot>,
    pub calls: Rc<RefCell<Vec<&'static str>>>,
    pub sabotage: bool,
}
impl Codec {
    pub fn new(text: &str, overrides: Vec<TomlOverride>) -> Self {
        Self {
            snapshots: BTreeMap::from([(
                text.to_owned(),
                TomlSnapshot {
                    overrides,
                    tables: vec![],
                },
            )]),
            calls: Rc::new(RefCell::new(vec![])),
            sabotage: false,
        }
    }
    fn apply(
        &mut self,
        text: &str,
        path: &TomlPath,
        value: Option<&str>,
    ) -> ConfigurationResult<String> {
        let mut snapshot = self.read(text)?;
        let old = snapshot.overrides.iter().find(|v| &v.path == path).cloned();
        snapshot.overrides.retain(|v| &v.path != path);
        let key = path.segments().join(".");
        let mut output = text.to_owned();
        if let Some(old) = old {
            output = output.replace(
                &format!("{key} = {}", old.value),
                &value.map(|v| format!("{key} = {v}")).unwrap_or_default(),
            );
        } else if let Some(value) = value {
            output.push_str(&format!("{key} = {value}\n"));
        }
        if let Some(value) = value {
            snapshot.overrides.push(TomlOverride {
                path: path.clone(),
                canonical_path: key,
                value: value.to_owned(),
                semantic_value: self.normalize(value)?,
                line: std::num::NonZeroU32::new(1).unwrap(),
            });
        }
        if self.sabotage {
            snapshot.overrides.push(override_("unowned", "true"));
        }
        self.snapshots.insert(output.clone(), snapshot);
        Ok(output)
    }
}
impl TomlPreparation for Codec {
    fn validate(&mut self, text: &str) -> ConfigurationResult<()> {
        self.calls.borrow_mut().push("validate");
        if self.snapshots.contains_key(text) {
            Ok(())
        } else {
            Err(ConfigurationFailure::InvalidDocument)
        }
    }
    fn read(&mut self, text: &str) -> ConfigurationResult<TomlSnapshot> {
        self.calls.borrow_mut().push("read");
        self.snapshots
            .get(text)
            .cloned()
            .ok_or(ConfigurationFailure::InvalidDocument)
    }
    fn normalize(&mut self, value: &str) -> ConfigurationResult<String> {
        if value.contains('\n') {
            return Err(ConfigurationFailure::InvalidInput);
        }
        Ok(match value {
            "0x1" => "1".to_owned(),
            _ => value.replace('_', ""),
        })
    }
    fn set(&mut self, text: &str, path: &TomlPath, value: &str) -> ConfigurationResult<String> {
        self.calls.borrow_mut().push("set");
        self.apply(text, path, Some(value))
    }
    fn remove(&mut self, text: &str, path: &TomlPath) -> ConfigurationResult<String> {
        self.calls.borrow_mut().push("remove");
        self.apply(text, path, None)
    }
    fn remove_table(&mut self, _: &str, _: &TomlPath) -> ConfigurationResult<String> {
        Err(ConfigurationFailure::UnsupportedSyntax)
    }
    fn rename_table(&mut self, _: &str, _: &TomlPath, _: &TomlPath) -> ConfigurationResult<String> {
        Err(ConfigurationFailure::UnsupportedSyntax)
    }
}
pub fn override_(id: &str, value: &str) -> TomlOverride {
    TomlOverride {
        path: path(id),
        canonical_path: id.to_owned(),
        value: value.to_owned(),
        semantic_value: match value {
            "0x1" => "1".to_owned(),
            _ => value.to_owned(),
        },
        line: std::num::NonZeroU32::new(1).unwrap(),
    }
}
pub struct Schemas {
    pub schema: AdoptedSchema,
    pub invalid_candidate: bool,
}
impl SchemaSource for Schemas {
    fn resolve(&mut self, binding: &SchemaBinding) -> ConfigurationResult<AdoptedSchema> {
        if &self.schema.schema.binding == binding {
            Ok(self.schema.clone())
        } else {
            Err(ConfigurationFailure::UnsupportedSchema)
        }
    }
    fn encode_public(
        &mut self,
        _: &AdoptedSchema,
        _: &FieldDefinition,
        value: &PublicConfigValue,
    ) -> ConfigurationResult<String> {
        Ok(match value {
            PublicConfigValue::Boolean(v) => v.to_string(),
            PublicConfigValue::Integer(v) => v.get().to_string(),
            PublicConfigValue::Number(v) => v.as_str().to_owned(),
            PublicConfigValue::String(v) => serde_json::to_string(v.as_str()).unwrap(),
            PublicConfigValue::Enum(v) => serde_json::to_string(v.as_str()).unwrap(),
            _ => serde_json::to_string(&serde_json::to_string(value).unwrap()).unwrap(),
        })
    }
    fn decode_public(
        &mut self,
        _: &AdoptedSchema,
        field: &FieldDefinition,
        source: &str,
    ) -> ConfigurationResult<PublicConfigValue> {
        match field.value_type {
            FieldType::Boolean => source
                .parse::<bool>()
                .map(PublicConfigValue::Boolean)
                .map_err(|_| ConfigurationFailure::InvalidDocument),
            FieldType::Integer { .. } => {
                serde_json::from_value(serde_json::Value::String(source.to_owned()))
                    .map(PublicConfigValue::Integer)
                    .map_err(|_| ConfigurationFailure::InvalidDocument)
            }
            _ => Err(ConfigurationFailure::UnsupportedSchema),
        }
    }
    fn project_sync(
        &mut self,
        _: &AdoptedSchema,
        _: &TomlSnapshot,
    ) -> ConfigurationResult<Vec<SyncProjection>> {
        Ok(vec![])
    }
    fn compile_sync(
        &mut self,
        _: &AdoptedSchema,
        _: &TomlSnapshot,
        edits: &[ResolvedSyncEdit],
    ) -> ConfigurationResult<Vec<SemanticMutation>> {
        if edits.is_empty() {
            Ok(vec![])
        } else {
            Err(ConfigurationFailure::UnsupportedSchema)
        }
    }
    fn migrate(
        &mut self,
        _: &AdoptedSchema,
        _: &AdoptedSchema,
        _: &TomlSnapshot,
    ) -> ConfigurationResult<Vec<SemanticMutation>> {
        Ok(vec![])
    }
    fn owned_paths(&mut self, _: &AdoptedSchema) -> ConfigurationResult<Vec<TomlPath>> {
        Ok(self
            .schema
            .fields
            .iter()
            .map(|f| f.canonical.clone())
            .collect())
    }
    fn validate_candidate(
        &mut self,
        _: &AdoptedSchema,
        _: &TomlSnapshot,
    ) -> ConfigurationResult<()> {
        if self.invalid_candidate {
            Err(ConfigurationFailure::InvalidDocument)
        } else {
            Ok(())
        }
    }
}
pub struct State {
    pub read: DocumentRead,
    pub effects: usize,
    pub busy: bool,
    pub fail_backup: bool,
    pub ambiguous: bool,
    pub steps: usize,
    pub drops: usize,
    pub backups: Vec<BackupReceiptRef>,
    pub backup_bytes: BTreeMap<BackupId, Vec<u8>>,
}
pub struct Owner(pub Rc<RefCell<State>>);
pub struct Lease(Rc<RefCell<State>>);
impl Drop for Lease {
    fn drop(&mut self) {
        self.0.borrow_mut().drops += 1;
    }
}
pub struct Tx {
    pub document: DocumentBinding,
    pub digest: Sha256,
    pub bytes: Vec<u8>,
    pub step: usize,
}
impl DocumentOwner for Owner {
    type Lease = Lease;
    type Transaction = Tx;
    fn resolve(&mut self, _: &TargetSelector) -> ConfigurationResult<DocumentRead> {
        let s = self.0.borrow();
        Ok(DocumentRead {
            binding: s.read.binding.clone(),
            bytes: s.read.bytes.clone(),
        })
    }
    fn read(&mut self, _: &DocumentBinding) -> ConfigurationResult<DocumentRead> {
        let s = self.0.borrow();
        Ok(DocumentRead {
            binding: s.read.binding.clone(),
            bytes: s.read.bytes.clone(),
        })
    }
    fn acquire(&mut self, _: &DocumentBinding) -> ConfigurationResult<Lease> {
        if self.0.borrow().busy {
            Err(ConfigurationFailure::Busy)
        } else {
            Ok(Lease(self.0.clone()))
        }
    }
    fn revalidate(&mut self, expected: &DocumentBinding, _: &Lease) -> ConfigurationResult<()> {
        if &self.0.borrow().read.binding == expected {
            Ok(())
        } else {
            Err(ConfigurationFailure::Stale)
        }
    }
    fn recovery_binding(
        &mut self,
        operation: &OperationId,
        prepared: &PreparedConfiguration,
        _: &Lease,
    ) -> ConfigurationResult<RecoveryRef> {
        Ok(RecoveryRef {
            operation_id: operation.clone(),
            transaction: NativeTransactionRef::new("synthetic-owned-transaction").unwrap(),
            target: RecoveryTarget::Configuration {
                document: prepared.baseline().clone(),
            },
        })
    }
    fn begin(
        &mut self,
        p: &PreparedConfiguration,
        _: &RecoveryRef,
        _: &Lease,
    ) -> ConfigurationResult<Tx> {
        Ok(Tx {
            document: p.baseline().clone(),
            digest: p.candidate_digest().clone(),
            bytes: p.candidate_bytes()?.to_vec(),
            step: 0,
        })
    }
    fn advance(
        &mut self,
        t: &mut Tx,
        _: &Lease,
        cancel: bool,
    ) -> ConfigurationResult<OwnerOutcome> {
        let mut s = self.0.borrow_mut();
        s.steps += 1;
        if cancel && t.step == 0 {
            return Ok(OwnerOutcome::CancelledBeforeCommit);
        }
        t.step += 1;
        if t.step == 1 {
            s.effects += 1;
            return Ok(OwnerOutcome::Pending {
                phase: ConfigurationPhase::StageFlushed,
                cancellable: true,
            });
        }
        if s.fail_backup {
            return Err(ConfigurationFailure::BackupUnavailable);
        }
        if s.ambiguous {
            return Ok(OwnerOutcome::RecoveryRequired);
        }
        let backup = match &t.document.baseline {
            DocumentBaseline::Existing { content_digest, .. } => Some(Box::new(BackupReceiptRef {
                backup_id: BackupId::new(id(900)).unwrap(),
                document: t.document.clone(),
                retained_digest: content_digest.clone(),
                native_backup_ref: NativeTargetRef::new("synthetic-backup").unwrap(),
                created_at: UtcTimestamp::new("2026-10-03T23:00:00Z").unwrap(),
            })),
            _ => None,
        };
        let mut document = t.document.clone();
        document.revision = OpaqueRevision::new("synthetic-written-2").unwrap();
        document.baseline = DocumentBaseline::Existing {
            file_identity: NativeFileIdentity::new("synthetic-written-file").unwrap(),
            content_digest: t.digest.clone(),
        };
        s.read = DocumentRead {
            binding: document.clone(),
            bytes: t.bytes.clone(),
        };
        if let Some(backup) = &backup {
            s.backups.push((**backup).clone());
        }
        s.effects += 1;
        Ok(OwnerOutcome::Committed {
            document: Box::new(document),
            backup,
        })
    }
    fn recover(
        &mut self,
        p: &RecoveryConfiguration,
        _: &RecoveryRef,
        _: &Lease,
    ) -> ConfigurationResult<OwnerOutcome> {
        let s = self.0.borrow();
        if s.read.binding == p.baseline && s.effects == 0 {
            return Ok(OwnerOutcome::CancelledBeforeCommit);
        }
        if matches!(&s.read.binding.baseline,DocumentBaseline::Existing { content_digest,.. } if content_digest == &p.candidate_digest)
        {
            return Ok(OwnerOutcome::Committed {
                document: Box::new(s.read.binding.clone()),
                backup: s.backups.last().cloned().map(Box::new),
            });
        }
        Ok(OwnerOutcome::RecoveryRequired)
    }
    fn history(&mut self, d: &DocumentBinding) -> ConfigurationResult<Inventory<BackupReceiptRef>> {
        let s = self.0.borrow();
        Ok(Inventory {
            items: list(s.backups.clone()),
            completeness: Completeness::Complete,
            issues: list(vec![]),
            revision: d.revision.clone(),
        })
    }
    fn backup(&mut self, receipt: &BackupReceiptRef) -> ConfigurationResult<Vec<u8>> {
        let state = self.0.borrow();
        if !state.backups.contains(receipt) {
            return Err(ConfigurationFailure::BackupUnavailable);
        }
        let bytes = state
            .backup_bytes
            .get(&receipt.backup_id)
            .ok_or(ConfigurationFailure::BackupUnavailable)?;
        if hash(bytes) != receipt.retained_digest {
            return Err(ConfigurationFailure::BackupUnavailable);
        }
        Ok(bytes.clone())
    }
    fn revalidate_backup(
        &mut self,
        receipt: &BackupReceiptRef,
        _: &Lease,
    ) -> ConfigurationResult<()> {
        self.backup(receipt).map(|_| ())
    }
}
pub type Workspace = ConfigurationWorkspace<Codec, Schemas, Owner, Entry, Ids>;
pub type TestWorkspace = (
    Workspace,
    Rc<RefCell<State>>,
    Rc<RefCell<Vec<&'static str>>>,
);
pub fn workspace(
    text: &str,
    overrides: Vec<TomlOverride>,
    entry: Option<ProtectedEntryOutcome>,
    sabotage: bool,
    invalid_candidate: bool,
) -> TestWorkspace {
    let f = fixture();
    let mut binding = f.draft.document;
    if !text.is_empty() {
        binding.baseline = DocumentBaseline::Existing {
            file_identity: NativeFileIdentity::new("synthetic-file-original").unwrap(),
            content_digest: hash(text.as_bytes()),
        };
    }
    let state = Rc::new(RefCell::new(State {
        read: DocumentRead {
            binding: binding.clone(),
            bytes: text.as_bytes().to_vec(),
        },
        effects: 0,
        busy: false,
        fail_backup: false,
        ambiguous: false,
        steps: 0,
        drops: 0,
        backups: vec![],
        backup_bytes: BTreeMap::new(),
    }));
    let fields = f
        .schema
        .fields
        .as_slice()
        .iter()
        .map(|d| FieldPolicy {
            field_id: d.field_id.clone(),
            canonical: TomlPath::new(
                d.path
                    .as_slice()
                    .iter()
                    .map(|s| s.as_str().to_owned())
                    .collect(),
            )
            .unwrap(),
            aliases: vec![],
            precedence: AliasPolicy::RejectMultiple,
        })
        .collect();
    let schemas = Schemas {
        schema: AdoptedSchema {
            schema: f.schema,
            fields,
            platform: SupportedPlatform::Windows,
        },
        invalid_candidate,
    };
    let mut codec = Codec::new(text, overrides);
    for value in ["false", "true"] {
        let text = format!("setting.boolean = {value}\n");
        codec.snapshots.entry(text).or_insert(TomlSnapshot {
            overrides: vec![override_("setting.boolean", value)],
            tables: vec![],
        });
    }
    codec.sabotage = sabotage;
    let calls = codec.calls.clone();
    (
        ConfigurationWorkspace::new(
            HostEpoch::new(id(42)).unwrap(),
            codec,
            schemas,
            Owner(state.clone()),
            Entry { outcome: entry },
            Ids(20000),
        ),
        state,
        calls,
    )
}
pub fn open(w: &mut Workspace, s: &Rc<RefCell<State>>) -> DraftSnapshot {
    w.open_draft(&OpenDraftInput {
        document: s.borrow().read.binding.clone(),
    })
    .unwrap()
}
pub fn stage(
    w: &mut Workspace,
    draft: &DraftRef,
    edits: Vec<ConfigurationEdit>,
) -> ConfigurationResult<SetDraftChangesResult> {
    w.set_draft_changes(SetDraftChangesInput { draft:draft.clone(),edits:list(edits) },|receipt| {
        let reply=serde_json::json!({"protocolVersion":1,"requestId":id(123),"body":{"type":"result","result":{"type":"command","command":{"name":"set_draft_changes","output":receipt}}}});
        decode_reply(&serde_json::to_vec(&reply).unwrap()).map(|_|()).map_err(|_|ConfigurationFailure::InvalidInput)
    })
}
pub fn boolean(value: bool) -> ConfigurationEdit {
    ConfigurationEdit::SetPublic {
        field_id: field("setting.boolean"),
        value: PublicConfigValue::Boolean(value),
    }
}
pub fn prepare(w: &mut Workspace, d: &DraftSnapshot) -> PreparedConfiguration {
    w.prepare_save(&SaveConfigurationInput {
        draft: d.draft.clone(),
    })
    .unwrap()
}
pub fn retain_backup(state: &Rc<RefCell<State>>, text: &str) -> BackupReceiptRef {
    let mut state = state.borrow_mut();
    let mut document = state.read.binding.clone();
    document.revision = OpaqueRevision::new("synthetic-backup-baseline-1").unwrap();
    document.baseline = DocumentBaseline::Existing {
        file_identity: NativeFileIdentity::new("synthetic-historical-file").unwrap(),
        content_digest: hash(text.as_bytes()),
    };
    let backup = BackupReceiptRef {
        backup_id: BackupId::new(id(901)).unwrap(),
        document,
        retained_digest: hash(text.as_bytes()),
        native_backup_ref: NativeTargetRef::new("synthetic-retained-backup").unwrap(),
        created_at: UtcTimestamp::new("2026-10-03T23:00:00Z").unwrap(),
    };
    state
        .backup_bytes
        .insert(backup.backup_id.clone(), text.as_bytes().to_vec());
    state.backups.push(backup.clone());
    backup
}
