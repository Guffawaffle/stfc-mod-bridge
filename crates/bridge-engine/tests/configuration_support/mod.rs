#![allow(dead_code)]
//! Deliberately synthetic portable owners. These tests do not qualify native
//! persistence, producer schemas, ABI compatibility or game installations.
use bridge_contracts::v1::*;
use bridge_engine::configuration::*;
use bridge_toml::{TomlOverride, TomlPath, TomlSnapshot, TomlTable};
use sha2::{Digest, Sha256 as Hasher};
use std::{
    cell::{Cell, RefCell},
    collections::{BTreeMap, BTreeSet, VecDeque},
    rc::Rc,
};

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
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum IdentityKind {
    Draft,
    Private,
    Secret,
}
#[derive(Default)]
pub struct IdentityControl {
    pub calls: Vec<IdentityKind>,
    pub outcomes: VecDeque<(IdentityKind, ConfigurationResult<u32>)>,
}
pub struct Ids {
    next: u32,
    control: Rc<RefCell<IdentityControl>>,
}
impl Ids {
    fn allocate(&mut self, kind: IdentityKind) -> ConfigurationResult<String> {
        let mut control = self.control.borrow_mut();
        control.calls.push(kind);
        if let Some((expected, outcome)) = control.outcomes.pop_front() {
            assert_eq!(kind, expected, "unexpected identity allocation or retry");
            return outcome.map(id);
        }
        self.next += 1;
        Ok(id(self.next))
    }
}
impl ConfigurationIds for Ids {
    fn draft_id(&mut self) -> ConfigurationResult<DraftId> {
        Ok(DraftId::new(self.allocate(IdentityKind::Draft)?).unwrap())
    }
    fn private_id(&mut self) -> ConfigurationResult<PrivateValueId> {
        Ok(PrivateValueId::new(self.allocate(IdentityKind::Private)?).unwrap())
    }
    fn secret_id(&mut self) -> ConfigurationResult<SecretRefId> {
        Ok(SecretRefId::new(self.allocate(IdentityKind::Secret)?).unwrap())
    }
}
pub struct Entry {
    pub outcome: Option<ProtectedEntryOutcome>,
    pub additional: VecDeque<ProtectedEntryOutcome>,
    pub unavailable: bool,
}
impl SensitiveEntry for Entry {
    fn is_available(&self) -> bool {
        !self.unavailable
    }
    fn capture(
        &mut self,
        _: &RequestSensitiveInputInput,
    ) -> ConfigurationResult<ProtectedEntryOutcome> {
        Ok(self
            .outcome
            .take()
            .or_else(|| self.additional.pop_front())
            .unwrap_or(ProtectedEntryOutcome::Unavailable(
                SensitiveInputUnavailableReason::ProtectedEntryUnavailable,
            )))
    }
}
pub struct Codec {
    snapshots: BTreeMap<String, TomlSnapshot>,
    pub calls: Rc<RefCell<Vec<&'static str>>>,
    pub sabotage: bool,
    pub scripted_candidate: Option<(String, TomlSnapshot)>,
}
impl Codec {
    pub fn new(text: &str, overrides: Vec<TomlOverride>) -> Self {
        let mut tables = vec![];
        for entry in &overrides {
            add_parents(&mut tables, &entry.path);
        }
        Self {
            snapshots: BTreeMap::from([(text.to_owned(), TomlSnapshot { overrides, tables })]),
            calls: Rc::new(RefCell::new(vec![])),
            sabotage: false,
            scripted_candidate: None,
        }
    }
    fn apply(
        &mut self,
        text: &str,
        path: &TomlPath,
        value: Option<&str>,
    ) -> ConfigurationResult<String> {
        if let Some(candidate) = self.scripted_candidate.take() {
            self.snapshots.insert(candidate.0.clone(), candidate.1);
            return Ok(candidate.0);
        }
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
            add_parents(&mut snapshot.tables, path);
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
fn add_parents(tables: &mut Vec<TomlTable>, path: &TomlPath) {
    for length in 1..path.segments().len() {
        let prefix = TomlPath::new(path.segments()[..length].to_vec()).unwrap();
        if !tables.iter().any(|table| table.path == prefix) {
            tables.push(TomlTable {
                canonical_path: prefix.segments().join("."),
                path: prefix,
                line: std::num::NonZeroU32::new(1).unwrap(),
            });
        }
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
        self.scripted_table_result()
    }
    fn rename_table(&mut self, _: &str, _: &TomlPath, _: &TomlPath) -> ConfigurationResult<String> {
        self.scripted_table_result()
    }
}
impl Codec {
    fn scripted_table_result(&mut self) -> ConfigurationResult<String> {
        let (text, snapshot) = self
            .scripted_candidate
            .take()
            .ok_or(ConfigurationFailure::UnsupportedSyntax)?;
        self.snapshots.insert(text.clone(), snapshot);
        Ok(text)
    }
}
pub fn table(id: &str) -> TomlTable {
    TomlTable {
        path: path(id),
        canonical_path: id.to_owned(),
        line: std::num::NonZeroU32::new(1).unwrap(),
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
    pub sync: Rc<RefCell<Vec<SyncFixture>>>,
    pub mutations: Vec<SemanticMutation>,
    pub additional_owned: Vec<TomlPath>,
    pub unavailable: Rc<Cell<bool>>,
    pub owner_state: Rc<RefCell<State>>,
}
pub struct SyncFixture {
    pub id: String,
    pub endpoint: String,
    pub proxy: String,
}
impl SchemaSource for Schemas {
    fn resolve(&mut self, binding: &SchemaBinding) -> ConfigurationResult<AdoptedSchema> {
        let state = self.owner_state.borrow();
        let leases = state.live_leases.iter().copied().collect();
        state.schema_lease_observations.borrow_mut().push(leases);
        if !self.unavailable.get() && &self.schema.schema.binding == binding {
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
        self.sync
            .borrow()
            .iter()
            .map(|destination| {
                Ok(SyncProjection {
                    id: DestinationId::new(&destination.id).unwrap(),
                    mode: SyncMode::Legacy,
                    exposure: SyncExposure::Creatable,
                    endpoint_field: field("sync.endpoint"),
                    endpoint: ProtectedValue::new(destination.endpoint.as_bytes().to_vec())?,
                    secret_configured: false,
                    proxy: SyncProjectedProxy::Custom {
                        field: field("sync.proxy"),
                        value: ProtectedValue::new(destination.proxy.as_bytes().to_vec())?,
                    },
                    feeds: list(vec![]),
                })
            })
            .collect()
    }
    fn compile_sync(
        &mut self,
        _: &AdoptedSchema,
        _: &TomlSnapshot,
        edits: &[ResolvedSyncEdit],
    ) -> ConfigurationResult<Vec<SemanticMutation>> {
        if !self.mutations.is_empty() {
            Ok(std::mem::take(&mut self.mutations))
        } else if edits.is_empty() {
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
            .chain(self.additional_owned.iter().cloned())
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
    pub operations_available: bool,
    pub settlement_required: bool,
    pub settlement_pending: bool,
    pub settlement_calls: usize,
    pub settled: BTreeSet<OperationId>,
    pub before_begin: Option<Box<dyn Fn()>>,
    pub read: DocumentRead,
    pub resolves: Cell<usize>,
    pub reads: Cell<usize>,
    pub effects: usize,
    pub busy: bool,
    pub fail_backup: bool,
    pub ambiguous: bool,
    pub steps: usize,
    pub drops: usize,
    pub backups: Vec<BackupReceiptRef>,
    pub backup_bytes: BTreeMap<BackupId, Vec<u8>>,
    pub acquisitions: usize,
    pub live_leases: BTreeSet<usize>,
    pub lease_observations: Vec<(&'static str, usize)>,
    pub schema_lease_observations: RefCell<Vec<Vec<usize>>>,
    pub begins: usize,
    pub begin_failure: Option<ConfigurationFailure>,
}
pub struct Owner(pub Rc<RefCell<State>>);
pub struct Lease {
    state: Rc<RefCell<State>>,
    pub id: usize,
}
impl Drop for Lease {
    fn drop(&mut self) {
        let mut state = self.state.borrow_mut();
        assert!(state.live_leases.remove(&self.id));
        state.drops += 1;
    }
}
impl Owner {
    fn observe_lease(&self, lease: &Lease, call: &'static str) {
        assert!(Rc::ptr_eq(&self.0, &lease.state), "foreign owner lease");
        let mut state = self.0.borrow_mut();
        assert!(state.live_leases.contains(&lease.id), "lease is not live");
        state.lease_observations.push((call, lease.id));
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
    fn operations_available(&self) -> bool {
        self.0.borrow().operations_available
    }
    fn requires_terminal_settlement(&self) -> bool {
        self.0.borrow().settlement_required
    }
    fn settle_terminal(
        &mut self,
        captured: &RecoveryConfiguration,
        recovery: &RecoveryRef,
        _: &CompletionOutcome,
        lease: &Lease,
    ) -> ConfigurationResult<bridge_engine::operations::SettlementStep> {
        self.observe_lease(lease, "settle");
        assert!(
            matches!(&recovery.target, RecoveryTarget::Configuration { document } if document == &captured.baseline)
        );
        let mut state = self.0.borrow_mut();
        state.settlement_calls += 1;
        if state.settlement_pending {
            return Ok(bridge_engine::operations::SettlementStep::Pending);
        }
        state.settled.insert(recovery.operation_id.clone());
        Ok(bridge_engine::operations::SettlementStep::Settled)
    }
    fn resolve(&mut self, _: &TargetSelector) -> ConfigurationResult<DocumentRead> {
        let s = self.0.borrow();
        s.resolves.set(s.resolves.get() + 1);
        Ok(DocumentRead {
            binding: s.read.binding.clone(),
            bytes: s.read.bytes.clone(),
        })
    }
    fn read(&mut self, _: &DocumentBinding) -> ConfigurationResult<DocumentRead> {
        let s = self.0.borrow();
        s.reads.set(s.reads.get() + 1);
        Ok(DocumentRead {
            binding: s.read.binding.clone(),
            bytes: s.read.bytes.clone(),
        })
    }
    fn acquire(&mut self, _: &DocumentBinding) -> ConfigurationResult<Lease> {
        let mut state = self.0.borrow_mut();
        state.acquisitions += 1;
        if state.busy {
            Err(ConfigurationFailure::Busy)
        } else {
            let id = state.acquisitions;
            assert!(state.live_leases.insert(id));
            Ok(Lease {
                state: self.0.clone(),
                id,
            })
        }
    }
    fn revalidate(&mut self, expected: &DocumentBinding, lease: &Lease) -> ConfigurationResult<()> {
        self.observe_lease(lease, "revalidate");
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
        lease: &Lease,
    ) -> ConfigurationResult<RecoveryRef> {
        self.observe_lease(lease, "recovery_binding");
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
        lease: &Lease,
    ) -> ConfigurationResult<Tx> {
        if let Some(check) = &self.0.borrow().before_begin {
            check();
        }
        self.observe_lease(lease, "begin");
        let mut state = self.0.borrow_mut();
        state.begins += 1;
        if let Some(failure) = &state.begin_failure {
            let failure = *failure;
            if failure == ConfigurationFailure::RecoveryRequired {
                // Simulate native mutation followed by unresolved disposition.
                state.effects += 1;
            }
            return Err(failure);
        }
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
        lease: &Lease,
        cancel: bool,
    ) -> ConfigurationResult<OwnerOutcome> {
        self.observe_lease(lease, "advance");
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
        lease: &Lease,
    ) -> ConfigurationResult<OwnerOutcome> {
        self.observe_lease(lease, "recover");
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
        lease: &Lease,
    ) -> ConfigurationResult<()> {
        self.observe_lease(lease, "backup");
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
    workspace_with_sync(
        text,
        overrides,
        entry,
        sabotage,
        invalid_candidate,
        Rc::new(RefCell::new(vec![])),
    )
}
pub fn workspace_with_sync(
    text: &str,
    overrides: Vec<TomlOverride>,
    entry: Option<ProtectedEntryOutcome>,
    sabotage: bool,
    invalid_candidate: bool,
    sync: Rc<RefCell<Vec<SyncFixture>>>,
) -> TestWorkspace {
    workspace_with_options(
        text,
        overrides,
        entry,
        sabotage,
        invalid_candidate,
        FixtureOptions {
            sync,
            ..FixtureOptions::default()
        },
    )
}
#[derive(Default)]
pub struct FixtureOptions {
    pub host_epoch: Option<HostEpoch>,
    pub owner_state: Option<Rc<RefCell<State>>>,
    pub operations_available: bool,
    pub sync: Rc<RefCell<Vec<SyncFixture>>>,
    pub tables: Vec<TomlTable>,
    pub mutations: Vec<SemanticMutation>,
    pub additional_owned: Vec<TomlPath>,
    pub candidate: Option<(String, TomlSnapshot)>,
    pub ids: Rc<RefCell<IdentityControl>>,
    pub captures: Vec<ProtectedEntryOutcome>,
    pub entry_unavailable: bool,
    pub schema_unavailable: Rc<Cell<bool>>,
}
pub fn workspace_with_options(
    text: &str,
    overrides: Vec<TomlOverride>,
    entry: Option<ProtectedEntryOutcome>,
    sabotage: bool,
    invalid_candidate: bool,
    options: FixtureOptions,
) -> TestWorkspace {
    let sync = options.sync;
    let mut f = fixture();
    if !sync.borrow().is_empty() {
        let mut definitions = f.schema.fields.as_slice().to_vec();
        let mut proxy = definitions
            .iter()
            .find(|definition| definition.field_id == field("sync.endpoint"))
            .unwrap()
            .clone();
        proxy.field_id = field("sync.proxy");
        proxy.path = list(vec![
            TomlPathSegment::new("sync").unwrap(),
            TomlPathSegment::new("proxy").unwrap(),
        ]);
        definitions.push(proxy);
        f.schema.fields = list(definitions);
        let mut modes = f.schema.sync.as_slice().to_vec();
        for mode in &mut modes {
            mode.proxy_field_id = Some(field("sync.proxy"));
            let mut fields = mode.fields.as_slice().to_vec();
            fields.push(field("sync.proxy"));
            mode.fields = list(fields);
        }
        f.schema.sync = list(modes);
    }
    let mut binding = f.draft.document;
    if !text.is_empty() {
        binding.baseline = DocumentBaseline::Existing {
            file_identity: NativeFileIdentity::new("synthetic-file-original").unwrap(),
            content_digest: hash(text.as_bytes()),
        };
    }
    let state = options.owner_state.unwrap_or_else(|| {
        Rc::new(RefCell::new(State {
            operations_available: options.operations_available,
            settlement_required: false,
            settlement_pending: false,
            settlement_calls: 0,
            settled: BTreeSet::new(),
            before_begin: None,
            resolves: Cell::new(0),
            reads: Cell::new(0),
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
            acquisitions: 0,
            live_leases: BTreeSet::new(),
            lease_observations: vec![],
            schema_lease_observations: RefCell::new(vec![]),
            begins: 0,
            begin_failure: None,
        }))
    });
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
        sync,
        mutations: options.mutations,
        additional_owned: options.additional_owned,
        unavailable: options.schema_unavailable,
        owner_state: state.clone(),
    };
    let mut codec = Codec::new(text, overrides);
    let initial = codec.snapshots.get_mut(text).unwrap();
    for table in options.tables {
        if !initial
            .tables
            .iter()
            .any(|existing| existing.path == table.path)
        {
            initial.tables.push(table);
        }
    }
    codec.scripted_candidate = options.candidate;
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
            options
                .host_epoch
                .unwrap_or_else(|| HostEpoch::new(id(42)).unwrap()),
            codec,
            schemas,
            Owner(state.clone()),
            Entry {
                outcome: entry,
                additional: options.captures.into(),
                unavailable: options.entry_unavailable,
            },
            Ids {
                next: 20000,
                control: options.ids,
            },
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
