//! Real kernel and FileJournal composition with deliberately synthetic owners.
//! These controls do not qualify physical writers, native schemas or installs.
#[path = "../../bridge-engine/tests/configuration_support/mod.rs"]
mod configuration_support;
use bridge_app::configuration::ConfigurationServices;
use bridge_contracts::v1::*;
use bridge_engine::{configuration::*, operations::*};
use configuration_support::*;
use std::{
    cell::RefCell,
    fs::File,
    path::PathBuf,
    rc::Rc,
    sync::atomic::{AtomicUsize, Ordering},
};

struct NoOtherOperations;
struct NoLease;
impl ResourceLease for NoLease {
    fn resources(&self) -> &[ResourceKey] {
        &[]
    }
}
impl OperationPorts for NoOtherOperations {
    type Custody = ();
    type Lease = NoLease;
    fn capture(
        &mut self,
        _: &MutationIntent,
        _: &HostEpoch,
    ) -> Result<(CapturedOperation, ()), Box<BridgeError>> {
        panic!("configuration must not delegate capture")
    }
    fn acquire(
        &mut self,
        _: &PlanSemantics,
        _: &(),
        _: &[ResourceKey],
    ) -> Result<NoLease, Box<BridgeError>> {
        panic!("configuration must not delegate acquisition")
    }
    fn acquire_recovery(
        &mut self,
        _: &OperationSnapshot,
        _: &RecoveryRef,
        _: &[ResourceKey],
    ) -> Result<((), NoLease), Box<BridgeError>> {
        panic!("configuration must not delegate recovery")
    }
    fn revalidate(
        &mut self,
        _: &PlanSemantics,
        _: &(),
        _: &NoLease,
    ) -> Result<(), Box<BridgeError>> {
        panic!("configuration must not delegate revalidation")
    }
    fn recovery_binding(
        &mut self,
        _: &OperationId,
        _: &PlanSemantics,
        _: &(),
        _: &NoLease,
    ) -> Result<RecoveryRef, Box<BridgeError>> {
        panic!("configuration must not delegate binding")
    }
    fn advance(
        &mut self,
        _: &OperationSnapshot,
        _: &RecoveryRef,
        _: &mut (),
        _: &NoLease,
        _: bool,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        panic!("configuration must not delegate advancement")
    }
    fn recover(
        &mut self,
        _: &OperationSnapshot,
        _: &RecoveryRef,
        _: &mut (),
        _: &NoLease,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        panic!("configuration must not delegate inspection")
    }
    fn handoff_session(
        &mut self,
        _: &SessionBinding,
        _: &mut (),
        _: &NoLease,
    ) -> Result<bool, Box<BridgeError>> {
        panic!("configuration has no session custody")
    }
}
struct Clock;
impl HostClock for Clock {
    fn now(&self) -> Result<ClockReading, ProviderFailure> {
        Ok(ClockReading {
            monotonic_millis: 1,
            utc: utc(),
        })
    }
    fn deadline(
        &self,
        sample: &ClockReading,
        lifetime: u64,
    ) -> Result<ClockReading, ProviderFailure> {
        Ok(ClockReading {
            monotonic_millis: sample.monotonic_millis + lifetime,
            utc: UtcTimestamp::new("2026-10-06T05:01:00Z").unwrap(),
        })
    }
}
fn utc() -> UtcTimestamp {
    UtcTimestamp::new("2026-10-06T05:00:00Z").unwrap()
}
struct KernelIds(u32);
impl IdentitySource for KernelIds {
    fn plan_id(&mut self) -> Result<PlanId, ProviderFailure> {
        self.0 += 1;
        Ok(PlanId::new(id(self.0)).unwrap())
    }
    fn operation_id(&mut self) -> Result<OperationId, ProviderFailure> {
        self.0 += 1;
        Ok(OperationId::new(id(self.0)).unwrap())
    }
}
#[derive(Default)]
struct Audit {
    records: Vec<JournalRecord>,
    fail: Option<(DurablePhase, bool)>,
}
struct AuditedJournal {
    disk: FileJournal,
    audit: Rc<RefCell<Audit>>,
    state: Rc<RefCell<State>>,
}
impl DurableJournal for AuditedJournal {
    fn records(&self) -> &[JournalRecord] {
        self.disk.records()
    }
    fn append(&mut self, record: &JournalRecord) -> Result<(), KernelFailure> {
        let phase = match record {
            JournalRecord::Operation { value } => Some(value.phase),
            _ => None,
        };
        let fail = self.audit.borrow().fail;
        if fail.is_some_and(|(target, after)| Some(target) == phase && !after) {
            return Err(KernelFailure::Storage);
        }
        if phase == Some(DurablePhase::Terminal) && self.state.borrow().live_leases.is_empty() {
            // Kernel replay may durably cancel admission that never reached
            // executing state; there was no owner work to reacquire.
            assert!(matches!(record, JournalRecord::Operation { value }
                    if matches!(value.snapshot.state, OperationState::Completed { outcome: CompletionOutcome::CancelledBeforeCommit { .. } })));
        }
        self.disk.append(record)?;
        self.audit.borrow_mut().records.push(record.clone());
        if fail.is_some_and(|(target, after)| Some(target) == phase && after) {
            return Err(KernelFailure::Storage);
        }
        Ok(())
    }
}
type Services = ConfigurationServices<NoOtherOperations, Codec, Schemas, Owner, Entry, Ids>;
type Kernel = Engine<Services, AuditedJournal, Clock, KernelIds>;
static NEXT: AtomicUsize = AtomicUsize::new(0);
struct Fixture {
    root: PathBuf,
    state: Rc<RefCell<State>>,
    audit: Rc<RefCell<Audit>>,
    schema_unavailable: Rc<std::cell::Cell<bool>>,
}
impl Fixture {
    fn new() -> (Self, Workspace) {
        let root = std::env::temp_dir().join(format!(
            "bridge-config-custody-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        std::fs::create_dir(&root).unwrap();
        File::create(root.join("engine.journal"))
            .unwrap()
            .sync_all()
            .unwrap();
        let schema_unavailable = Rc::new(std::cell::Cell::new(false));
        let (workspace, state, _) = workspace_with_options(
            "",
            vec![],
            None,
            false,
            false,
            FixtureOptions {
                operations_available: true,
                schema_unavailable: schema_unavailable.clone(),
                ..FixtureOptions::default()
            },
        );
        (
            Self {
                root,
                state,
                audit: Rc::new(RefCell::new(Audit::default())),
                schema_unavailable,
            },
            workspace,
        )
    }
    fn kernel(&self, workspace: Workspace, budget: usize) -> Kernel {
        let host = HostConfiguration {
            epoch: workspace.host_epoch().clone(),
            stream: StreamId::new(id(
                if workspace.host_epoch() == &HostEpoch::new(id(42)).unwrap() {
                    43
                } else {
                    45
                },
            ))
            .unwrap(),
            kind: HostKind::WindowsX64,
            preparation_lifetime_millis: 60_000,
        };
        Engine::open_with_observation_budget(
            ConfigurationServices::new(NoOtherOperations, workspace),
            AuditedJournal {
                disk: FileJournal::open_existing(&self.root.join("engine.journal")).unwrap(),
                audit: self.audit.clone(),
                state: self.state.clone(),
            },
            Clock,
            KernelIds(30_000),
            host,
            budget,
        )
        .unwrap()
    }
    fn restart(&self) -> Kernel {
        let (workspace, _, _) = workspace_with_options(
            "",
            vec![],
            None,
            false,
            false,
            FixtureOptions {
                host_epoch: Some(HostEpoch::new(id(44)).unwrap()),
                owner_state: Some(self.state.clone()),
                ..FixtureOptions::default()
            },
        );
        self.kernel(workspace, MAX_MESSAGE_BYTES)
    }
    fn check_wal_at_begin(&self) {
        let audit = self.audit.clone();
        self.state.borrow_mut().before_begin = Some(Box::new(move || {
            let audit = audit.borrow();
            let Some(JournalRecord::Operation { value }) = audit.records.last() else {
                panic!("executing record required before begin")
            };
            assert_eq!(value.phase, DurablePhase::Executing);
            assert_eq!(value.recovery.operation_id, value.snapshot.operation_id);
            assert!(
                matches!(&value.recovery.target, RecoveryTarget::Configuration { document } if document == match &value.snapshot.semantics.capture { PreparedCapture::SaveConfiguration { input } => &input.draft.draft.document, PreparedCapture::RestoreConfiguration { input } => &input.document, _ => panic!("configuration") })
            );
        }));
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        std::fs::remove_file(self.root.join("engine.journal")).unwrap();
        std::fs::remove_dir(&self.root).unwrap();
    }
}
fn command(kernel: &mut Kernel, command: Command) -> Result<CommandResult, Box<BridgeError>> {
    let request = decode_request(
        &serde_json::to_vec(&Request {
            protocol_version: ProtocolVersion,
            request_id: RequestId::new(id(100)).unwrap(),
            body: RequestBody::Command { command },
        })
        .unwrap(),
    )
    .unwrap();
    match kernel.dispatch(request).unwrap().into_inner().body {
        ReplyBody::Result {
            result: ResultPayload::Command { command },
        } => Ok(*command),
        ReplyBody::Rejected { error } => Err(error),
        _ => panic!("command result"),
    }
}
fn prepare(kernel: &mut Kernel, intent: MutationIntent) -> PreparedPlan {
    match command(kernel, Command::Prepare(Box::new(PrepareInput { intent }))).unwrap() {
        CommandResult::Prepare(plan) => plan,
        _ => panic!("plan"),
    }
}
fn input(plan: &PreparedPlan) -> CommitInput {
    CommitInput {
        plan_ref: plan.plan_ref.clone(),
        idempotency_key: IdempotencyKey::new(id(50_000)).unwrap(),
    }
}
fn commit(kernel: &mut Kernel, plan: &PreparedPlan) -> OperationSnapshot {
    match command(kernel, Command::Commit(input(plan))).unwrap() {
        CommandResult::Commit(operation) => operation,
        _ => panic!("operation"),
    }
}
fn save(draft: &DraftSnapshot) -> MutationIntent {
    MutationIntent::SaveConfiguration(SaveConfigurationInput {
        draft: draft.draft.clone(),
    })
}
fn stage_boolean(workspace: &mut Workspace, state: &Rc<RefCell<State>>) -> DraftSnapshot {
    let draft = open(workspace, state);
    stage(
        workspace,
        &draft.draft,
        vec![ConfigurationEdit::SetPublic {
            field_id: field("setting.boolean"),
            value: PublicConfigValue::Boolean(true),
        }],
    )
    .unwrap()
    .snapshot
}
fn lookup(kernel: &mut Kernel, draft: &DraftRef) -> DraftSnapshot {
    let request = decode_request(
        &serde_json::to_vec(&Request {
            protocol_version: ProtocolVersion,
            request_id: RequestId::new(id(101)).unwrap(),
            body: RequestBody::Query {
                query: Query::GetDraft(GetDraftInput {
                    host_epoch: draft.host_epoch.clone(),
                    draft_id: draft.draft_id.clone(),
                }),
            },
        })
        .unwrap(),
    )
    .unwrap();
    let ReplyBody::Result {
        result: ResultPayload::Query { query },
    } = kernel.dispatch(request).unwrap().into_inner().body
    else {
        panic!("lookup")
    };
    let QueryResult::GetDraft(result) = *query else {
        panic!("draft result")
    };
    match result.draft {
        Observation::Observed { value, .. } => value,
        _ => panic!("observed"),
    }
}
fn snapshot(
    binding: &CapturedOperation,
    operation: OperationId,
    state: OperationState,
) -> OperationSnapshot {
    OperationSnapshot {
        operation_id: operation,
        operation_revision: RevisionCounter::new(2),
        semantics: binding.semantics.clone(),
        state,
    }
}
fn running() -> OperationState {
    OperationState::Running {
        progress: Progress {
            phase: PhaseId::new("starting").unwrap(),
            measurement: Measurement::Unknown,
        },
    }
}

#[test]
fn capture_is_read_only_exact_and_host_bound() {
    let (fixture, mut workspace) = Fixture::new();
    let draft = stage_boolean(&mut workspace, &fixture.state);
    let mut services = ConfigurationServices::new(NoOtherOperations, workspace);
    assert_eq!(
        services
            .capture(&save(&draft), &HostEpoch::new(id(99)).unwrap())
            .err()
            .unwrap()
            .code,
        ErrorCode::PlanHostMismatch
    );
    let (binding, custody) = services
        .capture(&save(&draft), &draft.draft.host_epoch)
        .unwrap();
    assert!(
        matches!(binding.semantics.capture, PreparedCapture::SaveConfiguration { ref input } if input.draft == draft && input.candidate_digest == hash(b"setting.boolean = true\n"))
    );
    assert_eq!(binding.resources.len(), 3);
    assert_eq!(fixture.state.borrow().acquisitions, 0);
    assert_eq!(fixture.state.borrow().effects, 0);
    drop(custody);
    assert_eq!(fixture.state.borrow().begins, 0);
}
#[test]
fn losing_admission_retains_draft_and_creates_no_writer_artifact() {
    let (fixture, mut workspace) = Fixture::new();
    let draft = stage_boolean(&mut workspace, &fixture.state);
    let mut kernel = fixture.kernel(workspace, MAX_MESSAGE_BYTES);
    let plan = prepare(&mut kernel, save(&draft));
    fixture.state.borrow_mut().busy = true;
    assert_eq!(
        command(&mut kernel, Command::Commit(input(&plan)))
            .err()
            .unwrap()
            .code,
        ErrorCode::OperationBusy
    );
    assert_eq!(lookup(&mut kernel, &draft.draft), draft);
    assert_eq!(fixture.state.borrow().begins, 0);
    assert_eq!(fixture.state.borrow().effects, 0);
    assert!(fixture.state.borrow().live_leases.is_empty());
}
#[test]
fn changed_draft_after_preparation_refuses_without_begin() {
    let (fixture, mut workspace) = Fixture::new();
    let draft = stage_boolean(&mut workspace, &fixture.state);
    let mut kernel = fixture.kernel(workspace, MAX_MESSAGE_BYTES);
    let plan = prepare(&mut kernel, save(&draft));
    command(
        &mut kernel,
        Command::SetDraftChanges(SetDraftChangesInput {
            draft: draft.draft.clone(),
            edits: list(vec![]),
        }),
    )
    .unwrap();
    assert_eq!(
        command(&mut kernel, Command::Commit(input(&plan)))
            .err()
            .unwrap()
            .code,
        ErrorCode::StaleRevision
    );
    assert_eq!(fixture.state.borrow().begins, 0);
}
#[test]
fn physical_and_schema_changes_refuse_under_admission_lease() {
    for physical in [true, false] {
        let (fixture, mut workspace) = Fixture::new();
        let draft = stage_boolean(&mut workspace, &fixture.state);
        let mut kernel = fixture.kernel(workspace, MAX_MESSAGE_BYTES);
        let plan = prepare(&mut kernel, save(&draft));
        if physical {
            fixture.state.borrow_mut().read.binding.revision =
                OpaqueRevision::new("foreign-revision").unwrap();
        } else {
            fixture.schema_unavailable.set(true);
        }
        assert!(command(&mut kernel, Command::Commit(input(&plan))).is_err());
        assert_eq!(fixture.state.borrow().begins, 0);
        assert_eq!(fixture.state.borrow().effects, 0);
        assert_eq!(fixture.state.borrow().drops, 1);
    }
}
#[test]
fn foreign_capture_semantics_resources_lease_and_recovery_refuse() {
    let (fixture, mut workspace) = Fixture::new();
    let draft = stage_boolean(&mut workspace, &fixture.state);
    let mut services = ConfigurationServices::new(NoOtherOperations, workspace);
    let (binding, mut custody) = services
        .capture(&save(&draft), &draft.draft.host_epoch)
        .unwrap();
    let mut wrong = binding.semantics.clone();
    wrong.effects = list(vec![]);
    assert!(
        services
            .acquire(&wrong, &custody, &binding.resources)
            .is_err()
    );
    assert!(
        services
            .acquire(&binding.semantics, &custody, &binding.resources[..2])
            .is_err()
    );
    let (_, other) = services
        .capture(&save(&draft), &draft.draft.host_epoch)
        .unwrap();
    let other_lease = services
        .acquire(&binding.semantics, &other, &binding.resources)
        .unwrap();
    assert!(
        services
            .revalidate(&binding.semantics, &custody, &other_lease)
            .is_err()
    );
    drop(other_lease);
    let lease = services
        .acquire(&binding.semantics, &custody, &binding.resources)
        .unwrap();
    let operation = OperationId::new(id(60_000)).unwrap();
    let recovery = services
        .recovery_binding(&operation, &binding.semantics, &custody, &lease)
        .unwrap();
    let mut foreign = recovery.clone();
    foreign.transaction = NativeTransactionRef::new("foreign-transaction").unwrap();
    let operation = snapshot(&binding, operation, running());
    assert!(
        services
            .advance(&operation, &foreign, &mut custody, &lease, false)
            .is_err()
    );
    assert_eq!(fixture.state.borrow().begins, 0);
}
#[test]
fn composed_save_obeys_wal_retains_one_lease_and_publishes_once() {
    let (fixture, mut workspace) = Fixture::new();
    let draft = stage_boolean(&mut workspace, &fixture.state);
    fixture.check_wal_at_begin();
    let mut kernel = fixture.kernel(workspace, MAX_MESSAGE_BYTES);
    let plan = prepare(&mut kernel, save(&draft));
    let admitted = commit(&mut kernel, &plan);
    assert_eq!(fixture.state.borrow().begins, 0);
    assert_eq!(fixture.state.borrow().live_leases.len(), 1);
    kernel.advance(&admitted.operation_id).unwrap();
    assert_eq!(lookup(&mut kernel, &draft.draft), draft);
    assert!(matches!(
        kernel.close_disposition().unwrap(),
        CloseDisposition::Deferred { .. }
    ));
    let completed = kernel.advance(&admitted.operation_id).unwrap();
    assert!(matches!(
        completed.state,
        OperationState::Completed {
            outcome: CompletionOutcome::Changed { .. }
        }
    ));
    assert_eq!(lookup(&mut kernel, &draft.draft).state, DraftState::Clean);
    assert_eq!(fixture.state.borrow().begins, 1);
    assert_eq!(fixture.state.borrow().steps, 2);
    assert_eq!(fixture.state.borrow().drops, 1);
    assert_eq!(commit(&mut kernel, &plan), completed);
    assert_eq!(kernel.advance(&admitted.operation_id).unwrap(), completed);
    assert!(matches!(
        kernel.close_disposition().unwrap(),
        CloseDisposition::Ready
    ));
    drop(kernel);
    let bytes = std::fs::read(fixture.root.join("engine.journal")).unwrap();
    let protected = b"setting.boolean = true\n";
    assert!(!bytes.windows(protected.len()).any(|part| part == protected));
}
#[test]
fn no_change_save_cleans_exact_edits_without_materializing_missing_file() {
    let (fixture, mut workspace) = Fixture::new();
    let draft = open(&mut workspace, &fixture.state);
    let draft = stage(
        &mut workspace,
        &draft.draft,
        vec![ConfigurationEdit::RemoveOverride {
            field_id: field("setting.boolean"),
        }],
    )
    .unwrap()
    .snapshot;
    let mut kernel = fixture.kernel(workspace, MAX_MESSAGE_BYTES);
    let plan = prepare(&mut kernel, save(&draft));
    let operation = commit(&mut kernel, &plan);
    assert!(matches!(
        kernel.advance(&operation.operation_id).unwrap().state,
        OperationState::Completed {
            outcome: CompletionOutcome::NoChange { .. }
        }
    ));
    assert_eq!(lookup(&mut kernel, &draft.draft).state, DraftState::Clean);
    assert_eq!(fixture.state.borrow().begins, 0);
    assert_eq!(fixture.state.borrow().effects, 0);
    assert!(matches!(
        fixture.state.borrow().read.binding.baseline,
        DocumentBaseline::Missing
    ));
}
#[test]
fn newer_edit_after_native_start_is_preserved_as_stale() {
    let (fixture, mut workspace) = Fixture::new();
    let draft = stage_boolean(&mut workspace, &fixture.state);
    let mut kernel = fixture.kernel(workspace, MAX_MESSAGE_BYTES);
    let plan = prepare(&mut kernel, save(&draft));
    let operation = commit(&mut kernel, &plan);
    kernel.advance(&operation.operation_id).unwrap();
    command(
        &mut kernel,
        Command::SetDraftChanges(SetDraftChangesInput {
            draft: draft.draft.clone(),
            edits: list(vec![ConfigurationEdit::SetPublic {
                field_id: field("setting.boolean"),
                value: PublicConfigValue::Boolean(false),
            }]),
        }),
    )
    .unwrap();
    let newer = lookup(&mut kernel, &draft.draft);
    kernel.advance(&operation.operation_id).unwrap();
    let stale = lookup(&mut kernel, &draft.draft);
    assert_eq!(stale.state, DraftState::Stale);
    assert_eq!(stale.edits, newer.edits);
    assert_eq!(stale.draft, newer.draft);
}
#[test]
fn failed_or_uncertain_begin_never_retries_native_begin() {
    for failure in [
        ConfigurationFailure::NativeUnavailable,
        ConfigurationFailure::RecoveryRequired,
    ] {
        let (fixture, mut workspace) = Fixture::new();
        let draft = stage_boolean(&mut workspace, &fixture.state);
        fixture.state.borrow_mut().begin_failure = Some(failure);
        let mut kernel = fixture.kernel(workspace, MAX_MESSAGE_BYTES);
        let plan = prepare(&mut kernel, save(&draft));
        let operation = commit(&mut kernel, &plan);
        assert!(matches!(
            kernel.advance(&operation.operation_id).unwrap().state,
            OperationState::RecoveryRequired { .. }
        ));
        assert_eq!(fixture.state.borrow().begins, 1);
        assert_eq!(lookup(&mut kernel, &draft.draft), draft);
        if failure == ConfigurationFailure::RecoveryRequired {
            assert!(matches!(
                kernel.recover(&operation.operation_id).unwrap().state,
                OperationState::RecoveryRequired { .. }
            ));
            assert!(matches!(
                kernel.close_disposition().unwrap(),
                CloseDisposition::Deferred { .. }
            ));
            assert_eq!(fixture.state.borrow().live_leases.len(), 1);
        } else {
            assert!(matches!(
                kernel.recover(&operation.operation_id).unwrap().state,
                OperationState::Completed {
                    outcome: CompletionOutcome::CancelledBeforeCommit { .. }
                }
            ));
            assert!(fixture.state.borrow().live_leases.is_empty());
        }
        assert_eq!(fixture.state.borrow().begins, 1);
    }
}
#[test]
fn failed_executing_append_prevents_begin_and_restart_proves_unstarted() {
    let (fixture, mut workspace) = Fixture::new();
    let draft = stage_boolean(&mut workspace, &fixture.state);
    let mut kernel = fixture.kernel(workspace, MAX_MESSAGE_BYTES);
    let plan = prepare(&mut kernel, save(&draft));
    let operation = commit(&mut kernel, &plan);
    fixture.audit.borrow_mut().fail = Some((DurablePhase::Executing, false));
    assert!(kernel.advance(&operation.operation_id).is_err());
    assert_eq!(fixture.state.borrow().begins, 0);
    assert_eq!(fixture.state.borrow().effects, 0);
    assert_eq!(fixture.state.borrow().live_leases.len(), 1);
    drop(kernel);
    fixture.audit.borrow_mut().fail = None;
    let mut restarted = fixture.restart();
    assert!(matches!(
        commit(&mut restarted, &plan).state,
        OperationState::Completed {
            outcome: CompletionOutcome::CancelledBeforeCommit { .. }
        }
    ));
    assert_eq!(fixture.state.borrow().begins, 0);
}
#[test]
fn refused_terminal_append_keeps_private_custody_and_restart_inspects_exact_result() {
    let (fixture, mut workspace) = Fixture::new();
    let draft = stage_boolean(&mut workspace, &fixture.state);
    let mut kernel = fixture.kernel(workspace, MAX_MESSAGE_BYTES);
    let plan = prepare(&mut kernel, save(&draft));
    let operation = commit(&mut kernel, &plan);
    kernel.advance(&operation.operation_id).unwrap();
    fixture.audit.borrow_mut().fail = Some((DurablePhase::Terminal, false));
    assert!(kernel.advance(&operation.operation_id).is_err());
    assert_eq!(lookup(&mut kernel, &draft.draft), draft);
    assert_eq!(fixture.state.borrow().live_leases.len(), 1);
    assert_eq!(fixture.state.borrow().begins, 1);
    drop(kernel);
    fixture.audit.borrow_mut().fail = None;
    let mut restarted = fixture.restart();
    assert!(matches!(
        restarted.recover(&operation.operation_id).unwrap().state,
        OperationState::Completed {
            outcome: CompletionOutcome::Changed { .. }
        }
    ));
    assert_eq!(fixture.state.borrow().begins, 1);
    assert_eq!(fixture.state.borrow().steps, 2);
}
#[test]
fn ambiguous_terminal_append_restart_does_not_replay_committed_writer() {
    let (fixture, mut workspace) = Fixture::new();
    let draft = stage_boolean(&mut workspace, &fixture.state);
    let mut kernel = fixture.kernel(workspace, MAX_MESSAGE_BYTES);
    let plan = prepare(&mut kernel, save(&draft));
    let operation = commit(&mut kernel, &plan);
    kernel.advance(&operation.operation_id).unwrap();
    fixture.audit.borrow_mut().fail = Some((DurablePhase::Terminal, true));
    assert!(kernel.advance(&operation.operation_id).is_err());
    assert_eq!(lookup(&mut kernel, &draft.draft), draft);
    drop(kernel);
    fixture.audit.borrow_mut().fail = None;
    let mut restarted = fixture.restart();
    let committed = commit(&mut restarted, &plan);
    assert!(matches!(
        committed.state,
        OperationState::Completed {
            outcome: CompletionOutcome::Changed { .. }
        }
    ));
    assert!(fixture.state.borrow().live_leases.is_empty());
    assert_eq!(fixture.state.borrow().begins, 1);
    assert_eq!(fixture.state.borrow().steps, 2);
}
#[test]
fn cancelled_admission_has_no_begin_or_local_edit_loss() {
    let (fixture, mut workspace) = Fixture::new();
    let draft = stage_boolean(&mut workspace, &fixture.state);
    let mut kernel = fixture.kernel(workspace, MAX_MESSAGE_BYTES);
    let plan = prepare(&mut kernel, save(&draft));
    let operation = commit(&mut kernel, &plan);
    command(
        &mut kernel,
        Command::CancelOperation(CancelOperationInput {
            operation_id: operation.operation_id,
            expected_operation_revision: operation.operation_revision,
        }),
    )
    .unwrap();
    assert_eq!(fixture.state.borrow().begins, 0);
    assert_eq!(lookup(&mut kernel, &draft.draft), draft);
    assert!(fixture.state.borrow().live_leases.is_empty());
}
#[test]
fn publication_refusal_retries_cached_terminal_without_advancing_writer() {
    let (fixture, mut workspace) = Fixture::new();
    let draft = stage_boolean(&mut workspace, &fixture.state);
    let mut services = ConfigurationServices::new(NoOtherOperations, workspace);
    let (binding, mut custody) = services
        .capture(&save(&draft), &draft.draft.host_epoch)
        .unwrap();
    let lease = services
        .acquire(&binding.semantics, &custody, &binding.resources)
        .unwrap();
    let op_id = OperationId::new(id(60_000)).unwrap();
    let recovery = services
        .recovery_binding(&op_id, &binding.semantics, &custody, &lease)
        .unwrap();
    let running = snapshot(&binding, op_id.clone(), running());
    services
        .advance(&running, &recovery, &mut custody, &lease, false)
        .unwrap();
    let TransactionStep::Complete { outcome, .. } = services
        .advance(&running, &recovery, &mut custody, &lease, false)
        .unwrap()
    else {
        panic!("complete")
    };
    let terminal = snapshot(
        &binding,
        op_id.clone(),
        OperationState::Completed { outcome },
    );
    assert_eq!(
        services
            .publish_completion(&terminal, &mut custody, &lease, &mut |_| Err(error(
                ErrorCode::ResnapshotRequired
            )))
            .err()
            .unwrap()
            .code,
        ErrorCode::ResnapshotRequired
    );
    assert!(services.completion_pending(&custody));
    let recovering = snapshot(
        &binding,
        op_id,
        OperationState::RecoveryRequired {
            recovery: Box::new(recovery.clone()),
            reason: RecoveryReason::NativeCustodyUnresolved,
        },
    );
    services
        .recover(&recovering, &recovery, &mut custody, &lease)
        .unwrap();
    let mut published = 0;
    services
        .publish_completion(&terminal, &mut custody, &lease, &mut |changed| {
            assert_eq!(changed.len(), 1);
            assert_eq!(changed[0].state, DraftState::Clean);
            published += 1;
            Ok(())
        })
        .unwrap();
    assert_eq!(published, 1);
    assert!(!services.completion_pending(&custody));
    assert_eq!(fixture.state.borrow().begins, 1);
    assert_eq!(fixture.state.borrow().steps, 2);
    assert_eq!(fixture.state.borrow().live_leases.len(), 1);
}
#[test]
fn restore_revalidates_retained_backup_before_begin_and_preserves_local_draft() {
    for tamper in [true, false] {
        let (fixture, mut workspace) = Fixture::new();
        let draft = open(&mut workspace, &fixture.state);
        let backup = retain_backup(&fixture.state, "setting.boolean = true\n");
        let mut kernel = fixture.kernel(workspace, MAX_MESSAGE_BYTES);
        let plan = prepare(
            &mut kernel,
            MutationIntent::RestoreConfiguration(RestoreConfigurationInput {
                document: draft.draft.document.clone(),
                backup: backup.clone(),
            }),
        );
        if tamper {
            fixture
                .state
                .borrow_mut()
                .backup_bytes
                .insert(backup.backup_id, b"changed".to_vec());
            assert_eq!(
                command(&mut kernel, Command::Commit(input(&plan)))
                    .err()
                    .unwrap()
                    .code,
                ErrorCode::BackupUnavailable
            );
            assert_eq!(fixture.state.borrow().begins, 0);
        } else {
            let operation = commit(&mut kernel, &plan);
            kernel.advance(&operation.operation_id).unwrap();
            kernel.advance(&operation.operation_id).unwrap();
            assert_eq!(lookup(&mut kernel, &draft.draft).state, DraftState::Stale);
            assert_eq!(fixture.state.borrow().begins, 1);
        }
    }
}

#[test]
fn admission_binding_refusal_allows_fresh_identity_without_authorizing_old_start() {
    let (fixture, mut workspace) = Fixture::new();
    let draft = stage_boolean(&mut workspace, &fixture.state);
    let mut services = ConfigurationServices::new(NoOtherOperations, workspace);
    let (binding, mut custody) = services
        .capture(&save(&draft), &draft.draft.host_epoch)
        .unwrap();
    let lease = services
        .acquire(&binding.semantics, &custody, &binding.resources)
        .unwrap();
    let first = OperationId::new(id(60_000)).unwrap();
    let old = services
        .recovery_binding(&first, &binding.semantics, &custody, &lease)
        .unwrap();
    let second = OperationId::new(id(60_001)).unwrap();
    let fresh = services
        .recovery_binding(&second, &binding.semantics, &custody, &lease)
        .unwrap();
    assert!(
        services
            .advance(
                &snapshot(&binding, first, running()),
                &old,
                &mut custody,
                &lease,
                false
            )
            .is_err()
    );
    services
        .advance(
            &snapshot(&binding, second, running()),
            &fresh,
            &mut custody,
            &lease,
            false,
        )
        .unwrap();
    assert_eq!(fixture.state.borrow().begins, 1);
}

#[test]
fn protected_save_cleans_only_after_disk_terminal_and_never_serializes_payload() {
    let (fixture, _) = Fixture::new();
    let marker = "synthetic-composed-secret-payload";
    let entry = ProtectedEntryOutcome::Captured(
        ProtectedValue::new(format!("\"{marker}\"").into_bytes()).unwrap(),
    );
    let (mut workspace, _, _) = workspace_with_options(
        "",
        vec![],
        Some(entry),
        false,
        false,
        FixtureOptions {
            owner_state: Some(fixture.state.clone()),
            ..FixtureOptions::default()
        },
    );
    let draft = open(&mut workspace, &fixture.state);
    let captured = workspace
        .request_sensitive_input(&RequestSensitiveInputInput {
            draft: draft.draft.clone(),
            field_id: field("sync.token"),
            sensitivity: SensitiveInputKind::Secret,
        })
        .unwrap();
    let SensitiveInputOutcome::CapturedSecret { reference } = captured.outcome else {
        panic!("secret capture")
    };
    let draft = stage(
        &mut workspace,
        &draft.draft,
        vec![ConfigurationEdit::ReplaceSecret {
            field_id: field("sync.token"),
            reference,
        }],
    )
    .unwrap()
    .snapshot;
    let mut kernel = fixture.kernel(workspace, MAX_MESSAGE_BYTES);
    let plan = prepare(&mut kernel, save(&draft));
    assert!(!serde_json::to_string(&plan).unwrap().contains(marker));
    let operation = commit(&mut kernel, &plan);
    kernel.advance(&operation.operation_id).unwrap();
    assert_eq!(lookup(&mut kernel, &draft.draft), draft);
    kernel.advance(&operation.operation_id).unwrap();
    let clean = lookup(&mut kernel, &draft.draft);
    assert_eq!(clean.state, DraftState::Clean);
    assert!(clean.edits.as_slice().is_empty());
    assert!(!serde_json::to_string(&clean).unwrap().contains(marker));
    drop(kernel);
    let bytes = std::fs::read(fixture.root.join("engine.journal")).unwrap();
    assert!(
        !bytes
            .windows(marker.len())
            .any(|part| part == marker.as_bytes())
    );
    assert!(
        std::str::from_utf8(&fixture.state.borrow().read.bytes)
            .unwrap()
            .contains(marker)
    );
}

#[test]
fn forced_death_during_stage_reacquires_identity_only_and_retains_unresolved_custody() {
    let (fixture, mut workspace) = Fixture::new();
    let draft = stage_boolean(&mut workspace, &fixture.state);
    let mut kernel = fixture.kernel(workspace, MAX_MESSAGE_BYTES);
    let plan = prepare(&mut kernel, save(&draft));
    let operation = commit(&mut kernel, &plan);
    kernel.advance(&operation.operation_id).unwrap();
    drop(kernel);
    assert!(fixture.state.borrow().live_leases.is_empty());
    let mut restarted = fixture.restart();
    assert!(matches!(
        restarted.recover(&operation.operation_id).unwrap().state,
        OperationState::RecoveryRequired { .. }
    ));
    assert_eq!(fixture.state.borrow().acquisitions, 2);
    assert_eq!(fixture.state.borrow().begins, 1);
    assert_eq!(fixture.state.borrow().steps, 1);
    assert_eq!(fixture.state.borrow().live_leases.len(), 1);
    assert!(matches!(
        restarted.close_disposition().unwrap(),
        CloseDisposition::Deferred { .. }
    ));
}
