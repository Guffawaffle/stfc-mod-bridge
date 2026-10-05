//! Actual portable dispatcher/HostHandle adoption with test-only owners. No
//! native configuration writer, producer schema or installed game is exercised.
#[path = "../../bridge-engine/tests/configuration_support/mod.rs"]
mod configuration_support;
use bridge_app::configuration::ConfigurationServices;
use bridge_contracts::v1::*;
use bridge_engine::{configuration::*, host::*, operations::*, services::ApplicationServices};
use configuration_support::*;
use std::{
    cell::{Cell, RefCell},
    rc::Rc,
    time::Duration,
};

type Services = ConfigurationServices<Operations, Codec, Schemas, Owner, Entry, Ids>;
type TestEngine = Engine<Services, Journal, Clock, KernelIds>;

struct Clock;
impl HostClock for Clock {
    fn now(&self) -> Result<ClockReading, ProviderFailure> {
        Ok(ClockReading {
            monotonic_millis: 1,
            utc: timestamp(),
        })
    }
    fn deadline(
        &self,
        sample: &ClockReading,
        lifetime: u64,
    ) -> Result<ClockReading, ProviderFailure> {
        Ok(ClockReading {
            monotonic_millis: sample.monotonic_millis + lifetime,
            utc: UtcTimestamp::new("2026-10-05T12:01:00Z").unwrap(),
        })
    }
}
fn timestamp() -> UtcTimestamp {
    UtcTimestamp::new("2026-10-05T12:00:00Z").unwrap()
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
struct JournalAudit {
    records: RefCell<Vec<JournalRecord>>,
    fail: Cell<bool>,
}
struct Journal {
    records: Vec<JournalRecord>,
    audit: Rc<JournalAudit>,
}
impl DurableJournal for Journal {
    fn records(&self) -> &[JournalRecord] {
        &self.records
    }
    fn append(&mut self, record: &JournalRecord) -> Result<(), KernelFailure> {
        if self.audit.fail.get() {
            return Err(KernelFailure::Storage);
        }
        self.records.push(record.clone());
        self.audit.records.borrow_mut().push(record.clone());
        Ok(())
    }
}
struct Operations;
struct OperationLease(Vec<ResourceKey>);
impl ResourceLease for OperationLease {
    fn resources(&self) -> &[ResourceKey] {
        &self.0
    }
}
impl OperationPorts for Operations {
    type Lease = OperationLease;
    fn capture(
        &mut self,
        intent: &MutationIntent,
        _: &HostEpoch,
    ) -> Result<CapturedOperation, Box<BridgeError>> {
        if !matches!(intent, MutationIntent::LaunchOrdinary(_)) {
            return Err(error(ErrorCode::UnsupportedCapability));
        }
        let ReplyBody::Result {
            result: ResultPayload::Command { command },
        } = decode_reply(include_bytes!(
            "../../../contracts/fixtures/sc-02-ordinary-absent-prepare-reply.json"
        ))
        .unwrap()
        .into_inner()
        .body
        else {
            panic!("plan fixture")
        };
        let CommandResult::Prepare(plan) = *command else {
            panic!("plan fixture")
        };
        let PreparedCapture::LaunchOrdinary { target, .. } = &plan.semantics.capture else {
            panic!("launch fixture")
        };
        let ProfileBinding::Ordinary { owner_scope, .. } = &target.profile else {
            panic!("ordinary fixture")
        };
        let resources = vec![
            ResourceKey::Installation {
                physical_id: target.installation.physical_id().clone(),
            },
            ResourceKey::OrdinaryProfile {
                owner: owner_scope.clone(),
            },
        ];
        Ok(CapturedOperation {
            semantics: plan.semantics,
            resources,
        })
    }
    fn acquire(&mut self, resources: &[ResourceKey]) -> Result<Self::Lease, Box<BridgeError>> {
        Ok(OperationLease(resources.to_vec()))
    }
    fn revalidate(&mut self, _: &PlanSemantics, _: &Self::Lease) -> Result<(), Box<BridgeError>> {
        Ok(())
    }
    fn recovery_binding(
        &mut self,
        operation: &OperationId,
        semantics: &PlanSemantics,
        _: &Self::Lease,
    ) -> Result<RecoveryRef, Box<BridgeError>> {
        let PreparedCapture::LaunchOrdinary { target, .. } = &semantics.capture else {
            panic!("launch only")
        };
        Ok(RecoveryRef {
            operation_id: operation.clone(),
            transaction: NativeTransactionRef::new("test-only-launch").unwrap(),
            target: RecoveryTarget::Launch {
                target: target.clone(),
            },
        })
    }
    fn advance(
        &mut self,
        _: &OperationSnapshot,
        _: &RecoveryRef,
        _: &Self::Lease,
        _: bool,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        Ok(TransactionStep::Complete {
            outcome: CompletionOutcome::NoChange {
                reason: CompletionReason::AlreadySatisfied,
            },
            session_custody: None,
        })
    }
    fn recover(
        &mut self,
        _: &OperationSnapshot,
        _: &RecoveryRef,
        _: &Self::Lease,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        Err(error(ErrorCode::UnsupportedCapability))
    }
    fn handoff_session(
        &mut self,
        _: &SessionBinding,
        _: &Self::Lease,
    ) -> Result<bool, Box<BridgeError>> {
        Ok(false)
    }
}
fn new_engine(workspace: Workspace, budget: usize) -> (TestEngine, Rc<JournalAudit>) {
    let host = HostConfiguration {
        epoch: workspace.host_epoch().clone(),
        stream: StreamId::new(id(43)).unwrap(),
        kind: HostKind::WindowsX64,
        preparation_lifetime_millis: 60_000,
    };
    let audit = Rc::new(JournalAudit::default());
    let engine = Engine::open_with_observation_budget(
        ConfigurationServices::new(Operations, workspace),
        Journal {
            records: vec![],
            audit: audit.clone(),
        },
        Clock,
        KernelIds(30_000),
        host,
        budget,
    )
    .unwrap();
    (engine, audit)
}
fn request(body: RequestBody) -> ValidatedRequest {
    decode_request(
        &serde_json::to_vec(&Request {
            protocol_version: ProtocolVersion,
            request_id: RequestId::new(id(100)).unwrap(),
            body,
        })
        .unwrap(),
    )
    .unwrap()
}
#[track_caller]
fn dispatch(engine: &mut TestEngine, body: RequestBody) -> Reply {
    let reply = engine.dispatch(request(body)).unwrap().into_inner();
    assert_eq!(
        reply.request_id,
        ReplyRequestId::new(Some(RequestId::new(id(100)).unwrap()))
    );
    reply
}
fn command_result(reply: Reply) -> CommandResult {
    let ReplyBody::Result {
        result: ResultPayload::Command { command },
    } = reply.body
    else {
        panic!("command result: {:?}", reply.body)
    };
    *command
}
fn query_result(reply: Reply) -> QueryResult {
    let ReplyBody::Result {
        result: ResultPayload::Query { query },
    } = reply.body
    else {
        panic!("query result: {:?}", reply.body)
    };
    *query
}
fn cmd(command: Command) -> RequestBody {
    RequestBody::Command { command }
}
fn qry(query: Query) -> RequestBody {
    RequestBody::Query { query }
}
fn selector(document: &DocumentBinding) -> TargetSelector {
    let InstallationBinding::Registered {
        registration_id,
        registration_revision,
        ..
    } = &document.target.installation
    else {
        panic!("registered fixture")
    };
    TargetSelector {
        installation: InstallationSelector::Registered {
            id: registration_id.clone(),
            directory_assertion: None,
            revision_assertion: Some(registration_revision.clone()),
        },
        profile: ProfileSelector::Ordinary {
            catalog_id_assertion: None,
        },
    }
}
fn lookup(engine: &mut TestEngine, draft: &DraftRef) -> GetDraftResult {
    let QueryResult::GetDraft(result) = query_result(dispatch(
        engine,
        qry(Query::GetDraft(GetDraftInput {
            host_epoch: draft.host_epoch.clone(),
            draft_id: draft.draft_id.clone(),
        })),
    )) else {
        panic!("draft")
    };
    result
}
fn opened(engine: &mut TestEngine, document: DocumentBinding) -> DraftSnapshot {
    let CommandResult::OpenDraft(draft) = command_result(dispatch(
        engine,
        cmd(Command::OpenDraft(OpenDraftInput { document })),
    )) else {
        panic!("open")
    };
    draft
}
fn staged(engine: &mut TestEngine, input: SetDraftChangesInput) -> SetDraftChangesResult {
    let CommandResult::SetDraftChanges(receipt) =
        command_result(dispatch(engine, cmd(Command::SetDraftChanges(input))))
    else {
        panic!("stage")
    };
    *receipt
}
fn events(engine: &mut TestEngine, after: Cursor) -> EventBatch {
    let QueryResult::ResumeEvents(batch) = query_result(dispatch(
        engine,
        qry(Query::ResumeEvents(ResumeEventsInput {
            after,
            maximum_events: ProgressCount::new(128),
        })),
    )) else {
        panic!("events")
    };
    batch
}
fn observed(result: GetDraftResult) -> DraftSnapshot {
    let Observation::Observed { value, .. } = result.draft else {
        panic!("observed draft")
    };
    value
}
fn no_protected_bytes(bytes: &[u8]) {
    let text = std::str::from_utf8(bytes).unwrap();
    for forbidden in ["fixture-secret-payload", "fixture-private-endpoint"] {
        assert!(!text.contains(forbidden), "protected bytes escaped");
    }
}

#[test]
fn encoded_dispatch_read_history_stage_current_successor_missing_discard() {
    let (workspace, state, _) = configuration_support::workspace("", vec![], None, false, false);
    let document = state.borrow().read.binding.clone();
    let backup = retain_backup(&state, "setting.boolean = true\n");
    let (mut engine, journal) = new_engine(workspace, MAX_MESSAGE_BYTES);
    let after = engine.cursor();
    let QueryResult::ReadConfiguration(Observation::Observed { value: read, .. }) =
        query_result(dispatch(
            &mut engine,
            qry(Query::ReadConfiguration(ReadConfigurationInput {
                target: selector(&document),
            })),
        ))
    else {
        panic!("read")
    };
    assert_eq!(read.binding, document);
    let QueryResult::ConfigurationHistory(Observation::Observed { value: history, .. }) =
        query_result(dispatch(
            &mut engine,
            qry(Query::ConfigurationHistory(ConfigurationHistoryInput {
                document: document.clone(),
            })),
        ))
    else {
        panic!("history")
    };
    assert_eq!(history.items.as_slice(), &[backup]);
    let draft = opened(&mut engine, document);
    let input = SetDraftChangesInput {
        draft: draft.draft.clone(),
        edits: list(vec![boolean(true)]),
    };
    let receipt = staged(&mut engine, input.clone());
    let cursor = engine.cursor();
    assert_eq!(staged(&mut engine, input), receipt);
    assert_eq!(
        engine.cursor(),
        cursor,
        "exact staged retry emits no duplicate event"
    );
    let current = lookup(&mut engine, &draft.draft);
    assert_eq!(current.cursor, cursor);
    assert_eq!(observed(current), receipt.snapshot);
    let CommandResult::DiscardDraft(discarded) = command_result(dispatch(
        &mut engine,
        cmd(Command::DiscardDraft(DiscardDraftInput {
            draft: receipt.snapshot.draft.clone(),
        })),
    )) else {
        panic!("discard")
    };
    assert_eq!(discarded.previous_revision, receipt.snapshot.draft.revision);
    assert!(matches!(
        lookup(&mut engine, &draft.draft).draft,
        Observation::Missing { .. }
    ));
    assert_eq!(
        engine.cursor(),
        cursor,
        "discard receipt has no invented tombstone event"
    );
    let batch = events(&mut engine, after);
    assert_eq!(batch.events.as_slice().len(), 2);
    assert_eq!(state.borrow().effects, 0);
    assert_eq!(
        journal.records.borrow().len(),
        1,
        "read/stage creates no durable operation record"
    );
}

#[test]
fn immutable_getter_returns_clean_dirty_invalid_stale_without_io_or_ids() {
    let ids = Rc::new(RefCell::new(IdentityControl::default()));
    let (mut workspace, state, calls) = workspace_with_options(
        "",
        vec![],
        None,
        false,
        false,
        FixtureOptions {
            ids: ids.clone(),
            ..FixtureOptions::default()
        },
    );
    let draft = open(&mut workspace, &state);
    let (mut engine, _) = new_engine(workspace, MAX_MESSAGE_BYTES);
    let before = (
        state.borrow().reads.get(),
        state.borrow().resolves.get(),
        ids.borrow().calls.len(),
        calls.borrow().len(),
    );
    assert_eq!(observed(lookup(&mut engine, &draft.draft)), draft);
    assert_eq!(
        before,
        (
            state.borrow().reads.get(),
            state.borrow().resolves.get(),
            ids.borrow().calls.len(),
            calls.borrow().len()
        )
    );
    let dirty = staged(
        &mut engine,
        SetDraftChangesInput {
            draft: draft.draft.clone(),
            edits: list(vec![boolean(true)]),
        },
    );
    let invalid = staged(
        &mut engine,
        SetDraftChangesInput {
            draft: dirty.snapshot.draft.clone(),
            edits: list(vec![ConfigurationEdit::SetPublic {
                field_id: field("setting.integer"),
                value: PublicConfigValue::Boolean(true),
            }]),
        },
    );
    assert_eq!(
        observed(lookup(&mut engine, &draft.draft)),
        invalid.snapshot
    );
    let clean = staged(
        &mut engine,
        SetDraftChangesInput {
            draft: invalid.snapshot.draft.clone(),
            edits: list(vec![]),
        },
    );
    state.borrow_mut().read.binding.revision =
        OpaqueRevision::new("owner-observed-successor").unwrap();
    let selector = selector(&state.borrow().read.binding);
    dispatch(
        &mut engine,
        qry(Query::ReadConfiguration(ReadConfigurationInput {
            target: selector,
        })),
    );
    let before = (
        state.borrow().reads.get(),
        state.borrow().resolves.get(),
        ids.borrow().calls.len(),
        calls.borrow().len(),
        engine.cursor(),
    );
    let stale = observed(lookup(&mut engine, &draft.draft));
    assert_eq!(stale.state, DraftState::Stale);
    assert_eq!(
        stale.draft, clean.snapshot.draft,
        "lookup preserves the actual captured binding"
    );
    assert_eq!(
        before,
        (
            state.borrow().reads.get(),
            state.borrow().resolves.get(),
            ids.borrow().calls.len(),
            calls.borrow().len(),
            engine.cursor()
        )
    );
}

#[test]
fn get_draft_foreign_host_refuses_before_lookup_and_same_host_absence_is_missing() {
    let (workspace, state, calls) =
        configuration_support::workspace("", vec![], None, false, false);
    let (mut engine, _) = new_engine(workspace, MAX_MESSAGE_BYTES);
    let draft = DraftRef {
        draft_id: DraftId::new(id(999)).unwrap(),
        host_epoch: HostEpoch::new(id(42)).unwrap(),
        revision: RevisionCounter::new(99),
        document: state.borrow().read.binding.clone(),
    };
    assert!(matches!(
        lookup(&mut engine, &draft).draft,
        Observation::Missing { .. }
    ));
    let ReplyBody::Rejected { error } = dispatch(
        &mut engine,
        qry(Query::GetDraft(GetDraftInput {
            host_epoch: HostEpoch::new(id(800)).unwrap(),
            draft_id: draft.draft_id,
        })),
    )
    .body
    else {
        panic!("foreign host")
    };
    assert_eq!(error.code, ErrorCode::PlanHostMismatch);
    assert_eq!(
        state.borrow().reads.get() + state.borrow().resolves.get(),
        0
    );
    assert!(calls.borrow().is_empty());
}

#[test]
fn metadata_requires_injected_protected_entry_and_save_restore_remain_unavailable() {
    let ids = Rc::new(RefCell::new(IdentityControl::default()));
    let entry = ProtectedEntryOutcome::Captured(
        ProtectedValue::new(b"\"fixture-secret-payload\"".to_vec()).unwrap(),
    );
    let (mut workspace, state, _) = workspace_with_options(
        "",
        vec![],
        Some(entry),
        false,
        false,
        FixtureOptions {
            ids: ids.clone(),
            entry_unavailable: true,
            ..FixtureOptions::default()
        },
    );
    let draft = open(&mut workspace, &state);
    let backup = retain_backup(&state, "setting.boolean = true\n");
    let (mut engine, journal) = new_engine(workspace, MAX_MESSAGE_BYTES);
    let QueryResult::Hello(hello) =
        query_result(dispatch(&mut engine, qry(Query::Hello(EmptyInput {}))))
    else {
        panic!("hello")
    };
    assert!(
        hello
            .implemented_commands
            .as_slice()
            .contains(&CommandId::OpenDraft)
    );
    assert!(
        !hello
            .implemented_commands
            .as_slice()
            .contains(&CommandId::RequestSensitiveInput)
    );
    let before = ids.borrow().calls.len();
    let inputs = vec![
        Command::RequestSensitiveInput(RequestSensitiveInputInput {
            draft: draft.draft.clone(),
            field_id: field("sync.token"),
            sensitivity: SensitiveInputKind::Secret,
        }),
        Command::Prepare(Box::new(PrepareInput {
            intent: MutationIntent::SaveConfiguration(SaveConfigurationInput {
                draft: draft.draft.clone(),
            }),
        })),
        Command::Prepare(Box::new(PrepareInput {
            intent: MutationIntent::RestoreConfiguration(RestoreConfigurationInput {
                document: draft.draft.document.clone(),
                backup,
            }),
        })),
    ];
    for command in inputs {
        let ReplyBody::Rejected { error } = dispatch(&mut engine, cmd(command)).body else {
            panic!("unsupported capability")
        };
        assert_eq!(error.code, ErrorCode::UnsupportedCapability);
    }
    assert_eq!(before, ids.borrow().calls.len());
    assert_eq!(observed(lookup(&mut engine, &draft.draft)), draft);
    assert_eq!(state.borrow().effects, 0);
    assert_eq!(journal.records.borrow().len(), 1);
}

#[test]
fn draft_and_operation_changes_share_one_consecutive_kernel_event_sequence() {
    let (workspace, state, _) = configuration_support::workspace("", vec![], None, false, false);
    let (mut engine, _) = new_engine(workspace, MAX_MESSAGE_BYTES);
    let after = engine.cursor();
    let draft = opened(&mut engine, state.borrow().read.binding.clone());
    let prepare_request = decode_request(include_bytes!(
        "../../../contracts/fixtures/sc-02-ordinary-absent-prepare-request.json"
    ))
    .unwrap();
    let CommandResult::Prepare(plan) =
        command_result(engine.dispatch(prepare_request).unwrap().into_inner())
    else {
        panic!("prepare")
    };
    let CommandResult::Commit(_) = command_result(dispatch(
        &mut engine,
        cmd(Command::Commit(CommitInput {
            plan_ref: plan.plan_ref,
            idempotency_key: IdempotencyKey::new(id(500)).unwrap(),
        })),
    )) else {
        panic!("commit")
    };
    staged(
        &mut engine,
        SetDraftChangesInput {
            draft: draft.draft.clone(),
            edits: list(vec![boolean(true)]),
        },
    );
    state.borrow_mut().read.binding.revision = OpaqueRevision::new("successor-observed").unwrap();
    let target = selector(&state.borrow().read.binding);
    dispatch(
        &mut engine,
        qry(Query::ReadConfiguration(ReadConfigurationInput { target })),
    );
    let batch = events(&mut engine, after);
    assert_eq!(batch.events.as_slice().len(), 4);
    for (index, event) in batch.events.as_slice().iter().enumerate() {
        assert_eq!(event.cursor.sequence.get(), index as u64 + 1);
    }
    assert!(matches!(
        batch.events.as_slice()[1].body,
        EventBody::OperationChanged { .. }
    ));
    assert_eq!(
        observed(lookup(&mut engine, &draft.draft)).state,
        DraftState::Stale
    );
}

#[test]
fn protected_capture_and_transfer_never_escape_replies_events_or_journal() {
    let entry = ProtectedEntryOutcome::Captured(
        ProtectedValue::new(b"\"fixture-secret-payload\"".to_vec()).unwrap(),
    );
    let (workspace, state, _) = configuration_support::workspace(
        "sync.endpoint = \"fixture-private-endpoint\"\n",
        vec![override_("sync.endpoint", "\"fixture-private-endpoint\"")],
        Some(entry),
        false,
        false,
    );
    let document = state.borrow().read.binding.clone();
    let (mut engine, journal) = new_engine(workspace, MAX_MESSAGE_BYTES);
    let after = engine.cursor();
    let read = dispatch(
        &mut engine,
        qry(Query::ReadConfiguration(ReadConfigurationInput {
            target: selector(&document),
        })),
    );
    no_protected_bytes(&serde_json::to_vec(&read).unwrap());
    let draft = opened(&mut engine, document);
    let input = RequestSensitiveInputInput {
        draft: draft.draft.clone(),
        field_id: field("sync.token"),
        sensitivity: SensitiveInputKind::Secret,
    };
    let capture = dispatch(&mut engine, cmd(Command::RequestSensitiveInput(input)));
    no_protected_bytes(&serde_json::to_vec(&capture).unwrap());
    let CommandResult::RequestSensitiveInput(SensitiveInputResult {
        outcome: SensitiveInputOutcome::CapturedSecret { reference },
        ..
    }) = command_result(capture)
    else {
        panic!("secret ref")
    };
    let receipt = staged(
        &mut engine,
        SetDraftChangesInput {
            draft: draft.draft.clone(),
            edits: list(vec![ConfigurationEdit::ReplaceSecret {
                field_id: field("sync.token"),
                reference,
            }]),
        },
    );
    assert_eq!(receipt.protected_transfers.as_slice().len(), 1);
    no_protected_bytes(&serde_json::to_vec(&receipt).unwrap());
    no_protected_bytes(&serde_json::to_vec(&lookup(&mut engine, &draft.draft)).unwrap());
    no_protected_bytes(&serde_json::to_vec(&events(&mut engine, after)).unwrap());
    no_protected_bytes(&serde_json::to_vec(&*journal.records.borrow()).unwrap());
    assert_eq!(state.borrow().effects, 0);
}

#[test]
fn failed_stage_reply_preflight_keeps_revision_and_original_protected_ref() {
    let entry = ProtectedEntryOutcome::Captured(
        ProtectedValue::new(b"\"fixture-secret-payload\"".to_vec()).unwrap(),
    );
    let (mut workspace, state, _) =
        configuration_support::workspace("", vec![], Some(entry), false, false);
    let draft = open(&mut workspace, &state);
    let capture = workspace
        .request_sensitive_input(&RequestSensitiveInputInput {
            draft: draft.draft.clone(),
            field_id: field("sync.token"),
            sensitivity: SensitiveInputKind::Secret,
        })
        .unwrap();
    let SensitiveInputOutcome::CapturedSecret { reference } = capture.outcome else {
        panic!("capture")
    };
    let (mut engine, _) = new_engine(workspace, 10_000);
    let cursor = engine.cursor();
    let secret = ConfigurationEdit::ReplaceSecret {
        field_id: field("sync.token"),
        reference,
    };
    let input = SetDraftChangesInput {
        draft: draft.draft.clone(),
        edits: list(vec![
            secret.clone(),
            ConfigurationEdit::SetPublic {
                field_id: field("setting.integer"),
                value: PublicConfigValue::String(ConfigString::new("x".repeat(4096)).unwrap()),
            },
        ]),
    };
    assert!(matches!(
        engine.dispatch(request(cmd(Command::SetDraftChanges(input)))),
        Err(KernelFailure::Capacity)
    ));
    assert_eq!(observed(lookup(&mut engine, &draft.draft)), draft);
    assert_eq!(engine.cursor(), cursor);
    let receipt = staged(
        &mut engine,
        SetDraftChangesInput {
            draft: draft.draft.clone(),
            edits: list(vec![secret]),
        },
    );
    assert_eq!(
        receipt.snapshot.draft.revision.get(),
        1,
        "refusal did not transfer the original protected value"
    );
}

fn open_envelopes(snapshot: &DraftSnapshot) -> (usize, usize) {
    let reply = Reply {
        protocol_version: ProtocolVersion,
        request_id: ReplyRequestId::new(Some(RequestId::new(id(100)).unwrap())),
        body: ReplyBody::Result {
            result: ResultPayload::Command {
                command: Box::new(CommandResult::OpenDraft(snapshot.clone())),
            },
        },
    };
    let event = Event {
        protocol_version: ProtocolVersion,
        cursor: Cursor {
            host_epoch: snapshot.draft.host_epoch.clone(),
            stream_id: StreamId::new(id(43)).unwrap(),
            sequence: Sequence::new(1),
        },
        body: EventBody::DraftChanged {
            draft: Box::new(snapshot.clone()),
        },
    };
    (
        serde_json::to_vec(&reply).unwrap().len(),
        serde_json::to_vec(&event).unwrap().len(),
    )
}
#[test]
fn failed_open_event_reservation_publishes_neither_draft_nor_cursor() {
    let (mut probe, probe_state, _) =
        configuration_support::workspace("", vec![], None, false, false);
    let expected = open(&mut probe, &probe_state);
    let (reply_bytes, event_bytes) = open_envelopes(&expected);
    assert!(event_bytes > reply_bytes && reply_bytes >= 1024);
    let (workspace, state, _) = configuration_support::workspace("", vec![], None, false, false);
    let (mut engine, _) = new_engine(workspace, reply_bytes);
    let cursor = engine.cursor();
    let input = OpenDraftInput {
        document: state.borrow().read.binding.clone(),
    };
    assert!(matches!(
        engine.dispatch(request(cmd(Command::OpenDraft(input)))),
        Err(KernelFailure::Capacity)
    ));
    assert_eq!(engine.cursor(), cursor);
    assert!(matches!(
        lookup(&mut engine, &expected.draft).draft,
        Observation::Missing { .. }
    ));
}

#[test]
fn read_projection_preflight_failure_preserves_all_existing_draft_states() {
    let (mut workspace, state, _) =
        configuration_support::workspace("", vec![], None, false, false);
    let first = open(&mut workspace, &state);
    let second = open(&mut workspace, &state);
    state.borrow_mut().read.binding.revision = OpaqueRevision::new("owner-successor").unwrap();
    let target = selector(&state.borrow().read.binding);
    assert!(matches!(
        workspace.read_configuration_with_preflight(&target, |_, changed| {
            assert_eq!(changed.len(), 2);
            assert!(changed.iter().all(|draft| draft.state == DraftState::Stale));
            Err(ConfigurationFailure::Capacity)
        }),
        Err(ConfigurationFailure::Capacity)
    ));
    assert_eq!(
        workspace
            .current_draft(&first.draft.host_epoch, &first.draft.draft_id)
            .unwrap(),
        Some(first.clone())
    );
    assert_eq!(
        workspace
            .current_draft(&second.draft.host_epoch, &second.draft.draft_id)
            .unwrap(),
        Some(second)
    );
    let (mut engine, _) = new_engine(workspace, MAX_MESSAGE_BYTES);
    let after = engine.cursor();
    dispatch(
        &mut engine,
        qry(Query::ReadConfiguration(ReadConfigurationInput { target })),
    );
    let batch = events(&mut engine, after);
    assert_eq!(batch.events.as_slice().len(), 2);
    assert_eq!(batch.events.as_slice()[0].cursor.sequence.get(), 1);
    assert_eq!(batch.events.as_slice()[1].cursor.sequence.get(), 2);
}

#[test]
fn construction_rejects_foreign_workspace_and_services_cannot_advertise_kernel_controls() {
    let (workspace, _, _) = configuration_support::workspace("", vec![], None, false, false);
    let audit = Rc::new(JournalAudit::default());
    let result = Engine::open(
        ConfigurationServices::new(Operations, workspace),
        Journal {
            records: vec![],
            audit: audit.clone(),
        },
        Clock,
        KernelIds(30_000),
        HostConfiguration {
            epoch: HostEpoch::new(id(999)).unwrap(),
            stream: StreamId::new(id(43)).unwrap(),
            kind: HostKind::WindowsX64,
            preparation_lifetime_millis: 60_000,
        },
    );
    assert!(matches!(result, Err(KernelFailure::InvalidPortResult)));
    assert!(
        audit.records.borrow().is_empty(),
        "invalid epoch is refused before journal publication"
    );
    struct Dishonest(Operations);
    impl ApplicationServices for Dishonest {
        fn implemented_commands(&self) -> Vec<CommandId> {
            vec![CommandId::Commit]
        }
    }
    impl OperationPorts for Dishonest {
        type Lease = OperationLease;
        fn capture(
            &mut self,
            i: &MutationIntent,
            h: &HostEpoch,
        ) -> Result<CapturedOperation, Box<BridgeError>> {
            self.0.capture(i, h)
        }
        fn acquire(&mut self, r: &[ResourceKey]) -> Result<Self::Lease, Box<BridgeError>> {
            self.0.acquire(r)
        }
        fn revalidate(
            &mut self,
            s: &PlanSemantics,
            l: &Self::Lease,
        ) -> Result<(), Box<BridgeError>> {
            self.0.revalidate(s, l)
        }
        fn recovery_binding(
            &mut self,
            o: &OperationId,
            s: &PlanSemantics,
            l: &Self::Lease,
        ) -> Result<RecoveryRef, Box<BridgeError>> {
            self.0.recovery_binding(o, s, l)
        }
        fn advance(
            &mut self,
            o: &OperationSnapshot,
            r: &RecoveryRef,
            l: &Self::Lease,
            c: bool,
        ) -> Result<TransactionStep, Box<BridgeError>> {
            self.0.advance(o, r, l, c)
        }
        fn recover(
            &mut self,
            o: &OperationSnapshot,
            r: &RecoveryRef,
            l: &Self::Lease,
        ) -> Result<TransactionStep, Box<BridgeError>> {
            self.0.recover(o, r, l)
        }
        fn handoff_session(
            &mut self,
            s: &SessionBinding,
            l: &Self::Lease,
        ) -> Result<bool, Box<BridgeError>> {
            self.0.handoff_session(s, l)
        }
    }
    assert!(matches!(
        Engine::open(
            Dishonest(Operations),
            Journal {
                records: vec![],
                audit
            },
            Clock,
            KernelIds(30_000),
            HostConfiguration {
                epoch: HostEpoch::new(id(42)).unwrap(),
                stream: StreamId::new(id(43)).unwrap(),
                kind: HostKind::WindowsX64,
                preparation_lifetime_millis: 60_000
            }
        ),
        Err(KernelFailure::InvalidPortResult)
    ));
}

#[test]
fn closing_and_poisoned_engines_refuse_configuration_effects_but_getter_stays_immutable() {
    let (mut workspace, state, _) =
        configuration_support::workspace("", vec![], None, false, false);
    let draft = open(&mut workspace, &state);
    let (mut engine, _) = new_engine(workspace, MAX_MESSAGE_BYTES);
    let close = RequestHostCloseInput {
        expected_cursor: engine.cursor(),
    };
    dispatch(&mut engine, cmd(Command::RequestHostClose(close)));
    let ReplyBody::Rejected { error } = dispatch(
        &mut engine,
        cmd(Command::SetDraftChanges(SetDraftChangesInput {
            draft: draft.draft.clone(),
            edits: list(vec![boolean(true)]),
        })),
    )
    .body
    else {
        panic!("closed refusal")
    };
    assert_eq!(error.code, ErrorCode::OperationBusy);
    assert_eq!(observed(lookup(&mut engine, &draft.draft)), draft);
    let (workspace, state, _) = configuration_support::workspace("", vec![], None, false, false);
    let (mut engine, journal) = new_engine(workspace, MAX_MESSAGE_BYTES);
    let CommandResult::Prepare(plan) = command_result(
        engine
            .dispatch(
                decode_request(include_bytes!(
                    "../../../contracts/fixtures/sc-02-ordinary-absent-prepare-request.json"
                ))
                .unwrap(),
            )
            .unwrap()
            .into_inner(),
    ) else {
        panic!("prepare")
    };
    journal.fail.set(true);
    dispatch(
        &mut engine,
        cmd(Command::Commit(CommitInput {
            plan_ref: plan.plan_ref,
            idempotency_key: IdempotencyKey::new(id(777)).unwrap(),
        })),
    );
    let target = selector(&state.borrow().read.binding);
    let ReplyBody::Rejected { error } = dispatch(
        &mut engine,
        qry(Query::ReadConfiguration(ReadConfigurationInput { target })),
    )
    .body
    else {
        panic!("poison refusal")
    };
    assert_eq!(error.code, ErrorCode::PersistenceFailed);
    assert_eq!(
        state.borrow().reads.get() + state.borrow().resolves.get(),
        0
    );
}

#[test]
fn real_host_handle_reconciles_staged_draft_and_discard_on_original_owner_thread() {
    let (workspace, state, _) = configuration_support::workspace("", vec![], None, false, false);
    let document = state.borrow().read.binding.clone();
    let (engine, journal) = new_engine(workspace, MAX_MESSAGE_BYTES);
    let (handle, inbox) = owner_channel();
    let owner = EmbeddedOwner::on_current_thread(inbox, || Ok(KernelHost::new(engine))).unwrap();
    let tick = || {
        let turn = owner.turn(|_| Ok(DriveControl::Continue));
        assert!(
            matches!(turn, EmbeddedTurn::Retained { failure: None, .. }),
            "{turn:?}"
        );
    };
    let subscription = handle.subscribe(None).unwrap();
    tick();
    let ready = subscription
        .ready_timeout(Duration::from_millis(10))
        .unwrap()
        .unwrap();
    let exchange = |body| {
        let encoded = serde_json::to_vec(request(body).as_inner()).unwrap();
        let pending = handle.exchange(OwnedFrame::new(encoded).unwrap()).unwrap();
        tick();
        let frame = pending.try_recv().unwrap().unwrap();
        no_protected_bytes(frame.as_bytes());
        let reply = decode_reply(frame.as_bytes()).unwrap().into_inner();
        assert_eq!(
            reply.request_id,
            ReplyRequestId::new(Some(RequestId::new(id(100)).unwrap()))
        );
        reply
    };
    let QueryResult::ReadConfiguration(Observation::Observed { value: read, .. }) = query_result(
        exchange(qry(Query::ReadConfiguration(ReadConfigurationInput {
            target: selector(&document),
        }))),
    ) else {
        panic!("read")
    };
    let CommandResult::OpenDraft(draft) =
        command_result(exchange(cmd(Command::OpenDraft(OpenDraftInput {
            document: read.binding,
        }))))
    else {
        panic!("open")
    };
    let CommandResult::SetDraftChanges(receipt) = command_result(exchange(cmd(
        Command::SetDraftChanges(SetDraftChangesInput {
            draft: draft.draft.clone(),
            edits: list(vec![boolean(true)]),
        }),
    ))) else {
        panic!("stage")
    };
    let QueryResult::GetDraft(current) =
        query_result(exchange(qry(Query::GetDraft(GetDraftInput {
            host_epoch: draft.draft.host_epoch.clone(),
            draft_id: draft.draft.draft_id.clone(),
        }))))
    else {
        panic!("get")
    };
    assert_eq!(current.cursor.sequence.get(), ready.sequence.get() + 2);
    assert_eq!(observed(current), receipt.snapshot);
    exchange(cmd(Command::DiscardDraft(DiscardDraftInput {
        draft: receipt.snapshot.draft.clone(),
    })));
    let QueryResult::GetDraft(missing) =
        query_result(exchange(qry(Query::GetDraft(GetDraftInput {
            host_epoch: draft.draft.host_epoch,
            draft_id: draft.draft.draft_id,
        }))))
    else {
        panic!("missing")
    };
    assert!(matches!(missing.draft, Observation::Missing { .. }));
    for sequence in [1, 2] {
        let HostObservation::Event(frame) = subscription.try_recv().unwrap() else {
            panic!("event")
        };
        assert_eq!(
            decode_event(frame.as_bytes())
                .unwrap()
                .as_inner()
                .cursor
                .sequence
                .get(),
            sequence
        );
    }
    assert_eq!(journal.records.borrow().len(), 1);
    assert_eq!(state.borrow().effects, 0);
    owner.signal_close();
    assert!(matches!(
        owner.turn(|_| Ok(DriveControl::Continue)),
        EmbeddedTurn::Closed(_)
    ));
}
