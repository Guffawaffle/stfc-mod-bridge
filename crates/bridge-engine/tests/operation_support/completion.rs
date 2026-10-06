//! Real kernel/WAL plus the synthetic configuration workspace. No native
//! producer, canonical writer, runtime installation or release qualification.
use super::*;
use bridge_engine::configuration::*;
use std::{
    cell::{Cell, RefCell},
    rc::Rc,
};
#[path = "../configuration_support/mod.rs"]
mod cfg;

#[derive(Default)]
struct Control {
    oversized: Cell<bool>,
    replay_oversized: Cell<bool>,
    double_commit: Cell<bool>,
    fail_after_commit: Cell<bool>,
    fail_terminal_wal: Cell<bool>,
    write_then_fail: Cell<bool>,
    terminal_written: Cell<bool>,
    force_safe_recovery: Cell<bool>,
    published: Cell<usize>,
    order: RefCell<Vec<&'static str>>,
}
struct Custody {
    prepared: Option<PreparedConfiguration>,
    transaction: Option<ConfigurationTransaction<cfg::Tx>>,
    recovery: Option<RecoveryConfiguration>,
}
struct Held {
    native: cfg::Lease,
    resources: Vec<ResourceKey>,
}
impl ResourceLease for Held {
    fn resources(&self) -> &[ResourceKey] {
        &self.resources
    }
}
struct Ports {
    workspace: cfg::Workspace,
    control: Rc<Control>,
}
impl ApplicationServices for Ports {
    fn configuration_host_epoch(&self) -> Option<&HostEpoch> {
        Some(self.workspace.host_epoch())
    }
    fn get_draft(&self, input: &GetDraftInput) -> Result<Option<DraftSnapshot>, Box<BridgeError>> {
        self.workspace
            .current_draft(&input.host_epoch, &input.draft_id)
            .map_err(ConfigurationFailure::bridge_error)
    }
}
impl OperationPorts for Ports {
    type Custody = Custody;
    type Lease = Held;
    fn capture(
        &mut self,
        intent: &MutationIntent,
        _: &HostEpoch,
    ) -> Result<(CapturedOperation, Custody), Box<BridgeError>> {
        let MutationIntent::SaveConfiguration(input) = intent else {
            return Err(error(ErrorCode::UnsupportedCapability));
        };
        let prepared = self
            .workspace
            .prepare_save(input)
            .map_err(ConfigurationFailure::bridge_error)?;
        let document = prepared.baseline();
        let ProfileBinding::Ordinary { owner_scope, .. } = &document.target.profile else {
            unreachable!()
        };
        let resources = vec![
            ResourceKey::Installation {
                physical_id: document.target.installation.physical_id().clone(),
            },
            ResourceKey::OrdinaryProfile {
                owner: owner_scope.clone(),
            },
            ResourceKey::Document {
                id: document.document_id.clone(),
                target: document.target.clone(),
            },
        ];
        let semantics = PlanSemantics {
            hash_profile: HashProfile::BridgePlanSemanticJsonV1,
            action: ActionId::SaveConfiguration,
            capture: PreparedCapture::SaveConfiguration {
                input: prepared
                    .save_capture()
                    .map_err(ConfigurationFailure::bridge_error)?,
            },
            trust_domain: TrustDomain::Configuration,
            effects: cfg::list(vec![ProposedEffect::WriteConfiguration]),
        };
        Ok((
            CapturedOperation {
                semantics,
                resources,
            },
            Custody {
                prepared: Some(prepared),
                transaction: None,
                recovery: None,
            },
        ))
    }
    fn acquire(
        &mut self,
        _: &PlanSemantics,
        custody: &Custody,
        resources: &[ResourceKey],
    ) -> Result<Held, Box<BridgeError>> {
        Ok(Held {
            native: self
                .workspace
                .acquire_configuration(custody.prepared.as_ref().unwrap())
                .map_err(ConfigurationFailure::bridge_error)?,
            resources: resources.to_vec(),
        })
    }
    fn acquire_recovery(
        &mut self,
        operation: &OperationSnapshot,
        _: &RecoveryRef,
        resources: &[ResourceKey],
    ) -> Result<(Custody, Held), Box<BridgeError>> {
        let recovery = RecoveryConfiguration::from_capture(&operation.semantics.capture)
            .map_err(ConfigurationFailure::bridge_error)?;
        let native = self
            .workspace
            .acquire_configuration_recovery(&recovery)
            .map_err(ConfigurationFailure::bridge_error)?;
        Ok((
            Custody {
                prepared: None,
                transaction: None,
                recovery: Some(recovery),
            },
            Held {
                native,
                resources: resources.to_vec(),
            },
        ))
    }
    fn revalidate(
        &mut self,
        _: &PlanSemantics,
        custody: &Custody,
        lease: &Held,
    ) -> Result<(), Box<BridgeError>> {
        self.workspace
            .revalidate_configuration(custody.prepared.as_ref().unwrap(), &lease.native)
            .map_err(ConfigurationFailure::bridge_error)
    }
    fn recovery_binding(
        &mut self,
        operation: &OperationId,
        _: &PlanSemantics,
        custody: &Custody,
        lease: &Held,
    ) -> Result<RecoveryRef, Box<BridgeError>> {
        self.workspace
            .configuration_recovery_binding(
                operation,
                custody.prepared.as_ref().unwrap(),
                &lease.native,
            )
            .map_err(ConfigurationFailure::bridge_error)
    }
    fn advance(
        &mut self,
        _: &OperationSnapshot,
        recovery: &RecoveryRef,
        custody: &mut Custody,
        lease: &Held,
        cancel: bool,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        if custody.transaction.is_none() {
            custody.transaction = Some(
                self.workspace
                    .begin_configuration(&mut custody.prepared, recovery.clone(), &lease.native)
                    .map_err(ConfigurationFailure::bridge_error)?,
            );
        }
        let tx = custody.transaction.as_mut().unwrap();
        Ok(
            match self
                .workspace
                .advance_configuration(tx, &lease.native, cancel)
                .map_err(ConfigurationFailure::bridge_error)?
            {
                Some(outcome) => TransactionStep::Complete {
                    outcome,
                    session_custody: None,
                },
                None => TransactionStep::Progress {
                    progress: Progress {
                        phase: PhaseId::new("configuration-stage").unwrap(),
                        measurement: Measurement::Unknown,
                    },
                    cancellable: tx.status().1,
                },
            },
        )
    }
    fn recover(
        &mut self,
        _: &OperationSnapshot,
        recovery: &RecoveryRef,
        custody: &mut Custody,
        lease: &Held,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        if self.control.force_safe_recovery.get() {
            return Ok(TransactionStep::RecoveryRequired {
                reason: RecoveryReason::NativeCustodyUnresolved,
                safe_owner_boundary: true,
            });
        }
        let outcome = if let Some(tx) = custody.transaction.as_mut() {
            self.workspace
                .advance_configuration(tx, &lease.native, false)
        } else {
            self.workspace.recover_configuration(
                custody.recovery.as_ref().unwrap(),
                recovery,
                &lease.native,
            )
        }
        .map_err(ConfigurationFailure::bridge_error)?;
        Ok(match outcome {
            Some(outcome) => TransactionStep::Complete {
                outcome,
                session_custody: None,
            },
            None => TransactionStep::RecoveryRequired {
                reason: RecoveryReason::NativeCustodyUnresolved,
                safe_owner_boundary: false,
            },
        })
    }
    fn completion_pending(&self, custody: &Custody) -> bool {
        custody
            .transaction
            .as_ref()
            .is_some_and(ConfigurationTransaction::completion_pending)
    }
    fn publish_completion(
        &mut self,
        _: &OperationSnapshot,
        custody: &mut Custody,
        lease: &Held,
        commit: &mut CompletionCommit<'_>,
    ) -> Result<(), Box<BridgeError>> {
        let Some(tx) = custody.transaction.as_mut() else {
            return commit(&[]);
        };
        let control = self.control.clone();
        let host_epoch = self.workspace.host_epoch().clone();
        self.workspace
            .publish_configuration_completion(tx, &lease.native, |changed| {
                control.order.borrow_mut().push("preflight");
                let mut changed = changed.to_vec();
                if control.oversized.get() {
                    let template = changed[0].schema.fields.as_slice()[0].clone();
                    changed[0].schema.fields = cfg::list(
                        (0..512)
                            .map(|i| {
                                let mut field = template.clone();
                                field.field_id = FieldId::new(format!("oversized.{i}")).unwrap();
                                field.search_terms =
                                    cfg::list(vec![SchemaText::new("x".repeat(64)).unwrap(); 32]);
                                field
                            })
                            .collect(),
                    );
                    assert!(serde_json::to_vec(&changed[0]).unwrap().len() > MAX_MESSAGE_BYTES);
                }
                if control.replay_oversized.get() {
                    changed[0] = replay_oversized_draft(&changed[0], &host_epoch);
                }
                commit(&changed).map_err(|_| ConfigurationFailure::Capacity)?;
                if control.double_commit.get() {
                    commit(&changed).map_err(|_| ConfigurationFailure::Capacity)?;
                }
                Ok(())
            })
            .map_err(ConfigurationFailure::bridge_error)?;
        control.order.borrow_mut().push("local-publication");
        control.published.set(control.published.get() + 1);
        if control.fail_after_commit.get() {
            return Err(error(ErrorCode::InternalFailure));
        }
        Ok(())
    }
    fn handoff_session(
        &mut self,
        _: &SessionBinding,
        _: &mut Custody,
        _: &Held,
    ) -> Result<bool, Box<BridgeError>> {
        Ok(false)
    }
}
struct Journal {
    inner: FileJournal,
    control: Rc<Control>,
    state: Rc<RefCell<cfg::State>>,
}
impl DurableJournal for Journal {
    fn records(&self) -> &[JournalRecord] {
        self.inner.records()
    }
    fn append(&mut self, record: &JournalRecord) -> Result<(), KernelFailure> {
        if matches!(record,JournalRecord::Operation {value} if value.phase==DurablePhase::Terminal)
        {
            self.control.order.borrow_mut().push("terminal-wal");
            assert_eq!(self.control.published.get(), 0);
            assert_eq!(self.state.borrow().live_leases.len(), 1);
            assert_eq!(self.state.borrow().drops, 0);
            if self.control.fail_terminal_wal.get() {
                if self.control.write_then_fail.get() {
                    self.inner.append(record)?;
                    self.control.terminal_written.set(true);
                }
                return Err(KernelFailure::Storage);
            }
        }
        self.inner.append(record)
    }
}
type TestEngine = Engine<Ports, Journal, Clock, Ids>;
fn setup() -> (
    Fixture,
    TestEngine,
    Rc<Control>,
    Rc<RefCell<cfg::State>>,
    DraftSnapshot,
) {
    let fixture = Fixture::new();
    let control = Rc::new(Control::default());
    let (mut workspace, state, _) = cfg::workspace("", vec![], None, false, false);
    let d = cfg::open(&mut workspace, &state);
    let staged = cfg::stage(&mut workspace, &d.draft, vec![cfg::boolean(true)])
        .unwrap()
        .snapshot;
    let mut host = config(42);
    host.epoch = workspace.host_epoch().clone();
    let journal = Journal {
        inner: FileJournal::open_existing(&fixture.root.join("engine.journal")).unwrap(),
        control: control.clone(),
        state: state.clone(),
    };
    let engine = Engine::open(
        Ports {
            workspace,
            control: control.clone(),
        },
        journal,
        fixture.clock.clone(),
        Ids { next: 1000 },
        host,
    )
    .unwrap();
    (fixture, engine, control, state, staged)
}
fn execute(engine: &mut TestEngine, draft: &DraftSnapshot) -> OperationSnapshot {
    let reply = engine
        .dispatch(request(RequestBody::Command {
            command: Command::Prepare(Box::new(PrepareInput {
                intent: MutationIntent::SaveConfiguration(SaveConfigurationInput {
                    draft: draft.draft.clone(),
                }),
            })),
        }))
        .unwrap()
        .into_inner();
    let ReplyBody::Result {
        result: ResultPayload::Command { command },
    } = reply.body
    else {
        panic!("prepare failed");
    };
    let CommandResult::Prepare(plan) = *command else {
        panic!("prepare output");
    };
    let reply = engine
        .dispatch(request(RequestBody::Command {
            command: Command::Commit(commit_input(plan, 900)),
        }))
        .unwrap()
        .into_inner();
    let ReplyBody::Result {
        result: ResultPayload::Command { command },
    } = reply.body
    else {
        panic!("commit failed");
    };
    let CommandResult::Commit(operation) = *command else {
        panic!("commit output");
    };
    engine.advance(&operation.operation_id).unwrap();
    operation
}
fn current(engine: &mut TestEngine, draft: &DraftSnapshot) -> DraftSnapshot {
    let reply = engine
        .dispatch(request(RequestBody::Query {
            query: Query::GetDraft(GetDraftInput {
                host_epoch: draft.draft.host_epoch.clone(),
                draft_id: draft.draft.draft_id.clone(),
            }),
        }))
        .unwrap()
        .into_inner();
    let ReplyBody::Result {
        result: ResultPayload::Query { query },
    } = reply.body
    else {
        panic!("draft lookup");
    };
    let QueryResult::GetDraft(result) = *query else {
        panic!("draft output");
    };
    let Observation::Observed { value, .. } = result.draft else {
        panic!("current draft missing");
    };
    value
}

// A semantically valid projection fits as a standalone event but cannot fit
// even a single-event replay reply. The actual kernel must refuse before WAL
// or local publication rather than retaining an event that stalls every read.
fn replay_oversized_draft(draft: &DraftSnapshot, host_epoch: &HostEpoch) -> DraftSnapshot {
    let mut draft = draft.clone();
    let template = draft.schema.fields.as_slice()[0].clone();
    draft.schema.sync = cfg::list(vec![]);
    let target_bytes = MAX_MESSAGE_BYTES - 300;
    for count in 1..=512 {
        let fields = (0..count)
            .map(|i| {
                let mut field = template.clone();
                field.field_id = FieldId::new(format!("envelope.{i}")).unwrap();
                field.path = cfg::list(vec![TomlPathSegment::new(format!("envelope{i}")).unwrap()]);
                field.aliases = cfg::list(vec![]);
                field.search_terms = cfg::list(vec![SchemaText::new("x".repeat(64)).unwrap(); 32]);
                field
            })
            .collect();
        let mut next = draft.clone();
        next.schema.fields = cfg::list(fields);
        if serde_json::to_vec(&next).unwrap().len() > target_bytes {
            break;
        }
        draft = next;
    }
    let mut fields = draft.schema.fields.as_slice().to_vec();
    let remaining = target_bytes - serde_json::to_vec(&draft).unwrap().len();
    assert!(remaining < 4096);
    let mut remaining = remaining;
    for field in &mut fields {
        let terms = field
            .search_terms
            .as_slice()
            .iter()
            .map(|term| {
                let extra = remaining.min(448);
                remaining -= extra;
                SchemaText::new(format!("{}{}", term.as_str(), "y".repeat(extra))).unwrap()
            })
            .collect();
        field.search_terms = cfg::list(terms);
    }
    assert_eq!(remaining, 0);
    draft.schema.fields = cfg::list(fields);
    let after = Cursor {
        host_epoch: host_epoch.clone(),
        stream_id: StreamId::new(uuid(4242)).unwrap(),
        sequence: Sequence::new(10),
    };
    let event = Event {
        protocol_version: ProtocolVersion,
        cursor: Cursor {
            sequence: Sequence::new(11),
            ..after.clone()
        },
        body: EventBody::DraftChanged {
            draft: Box::new(draft.clone()),
        },
    };
    let raw = serde_json::to_vec(&event).unwrap();
    assert!(raw.len() <= MAX_MESSAGE_BYTES);
    decode_event(&raw).unwrap();
    let open = Reply {
        protocol_version: ProtocolVersion,
        request_id: ReplyRequestId::new(Some(RequestId::new(uuid(1)).unwrap())),
        body: ReplyBody::Result {
            result: ResultPayload::Command {
                command: Box::new(CommandResult::OpenDraft(draft.clone())),
            },
        },
    };
    decode_reply(&serde_json::to_vec(&open).unwrap()).unwrap();
    let reply = Reply {
        protocol_version: ProtocolVersion,
        request_id: ReplyRequestId::new(Some(RequestId::new(uuid(1)).unwrap())),
        body: ReplyBody::Result {
            result: ResultPayload::Query {
                query: Box::new(QueryResult::ResumeEvents(EventBatch {
                    after,
                    next: event.cursor.clone(),
                    events: cfg::list(vec![event]),
                })),
            },
        },
    };
    assert!(serde_json::to_vec(&reply).unwrap().len() > MAX_MESSAGE_BYTES);
    draft
}

#[test]
fn terminal_wal_precedes_local_publication_and_consecutive_operation_draft_events() {
    let (_fixture, mut engine, control, state, draft) = setup();
    let operation = execute(&mut engine, &draft);
    assert_eq!(current(&mut engine, &draft), draft);
    let before = engine.cursor();
    assert!(matches!(
        engine.advance(&operation.operation_id).unwrap().state,
        OperationState::Completed { .. }
    ));
    assert_eq!(
        *control.order.borrow(),
        vec!["preflight", "terminal-wal", "local-publication"]
    );
    assert_eq!(state.borrow().drops, 1);
    assert_eq!(current(&mut engine, &draft).state, DraftState::Clean);
    let reply = engine
        .dispatch(request(RequestBody::Query {
            query: Query::ResumeEvents(ResumeEventsInput {
                after: before.clone(),
                maximum_events: ProgressCount::new(128),
            }),
        }))
        .unwrap()
        .into_inner();
    let ReplyBody::Result {
        result: ResultPayload::Query { query },
    } = reply.body
    else {
        panic!("resume");
    };
    let QueryResult::ResumeEvents(batch) = *query else {
        panic!("events");
    };
    assert_eq!(batch.events.as_slice().len(), 2);
    assert!(matches!(
        batch.events.as_slice()[0].body,
        EventBody::OperationChanged { .. }
    ));
    assert!(matches!(
        batch.events.as_slice()[1].body,
        EventBody::DraftChanged { .. }
    ));
    assert_eq!(
        batch.events.as_slice()[1].cursor.sequence.get(),
        before.sequence.get() + 2
    );
}
#[test]
fn draft_event_capacity_refusal_retains_pending_completion_lease_and_native_result() {
    let (_fixture, mut engine, control, state, draft) = setup();
    let operation = execute(&mut engine, &draft);
    control.oversized.set(true);
    assert!(matches!(
        engine.advance(&operation.operation_id).unwrap().state,
        OperationState::RecoveryRequired { .. }
    ));
    assert_eq!(current(&mut engine, &draft), draft);
    assert_eq!(state.borrow().drops, 0);
    assert_eq!(control.published.get(), 0);
    assert_eq!(*control.order.borrow(), vec!["preflight"]);
    let steps = state.borrow().steps;
    let effects = state.borrow().effects;
    let reply = engine
        .dispatch(request(RequestBody::Command {
            command: Command::RequestHostClose(RequestHostCloseInput {
                expected_cursor: engine.cursor(),
            }),
        }))
        .unwrap()
        .into_inner();
    assert!(
        matches!(reply.body,ReplyBody::Result {result:ResultPayload::Command {command}} if matches!(*command,CommandResult::RequestHostClose(CloseDisposition::Deferred {..})))
    );
    control.force_safe_recovery.set(true);
    assert!(matches!(
        engine.recover(&operation.operation_id).unwrap().state,
        OperationState::RecoveryRequired { .. }
    ));
    assert_eq!(
        state.borrow().drops,
        0,
        "safe native boundary cannot release pending local publication"
    );
    control.force_safe_recovery.set(false);
    control.oversized.set(false);
    assert!(matches!(
        engine.recover(&operation.operation_id).unwrap().state,
        OperationState::Completed { .. }
    ));
    assert_eq!(state.borrow().steps, steps);
    assert_eq!(state.borrow().effects, effects);
    assert_eq!(state.borrow().acquisitions, 1);
    assert_eq!(state.borrow().drops, 1);
    assert_eq!(current(&mut engine, &draft).state, DraftState::Clean);
}
#[test]
fn single_event_replay_capacity_refusal_precedes_wal_and_local_publication() {
    let (_fixture, mut engine, control, state, draft) = setup();
    let operation = execute(&mut engine, &draft);
    control.replay_oversized.set(true);
    assert!(matches!(
        engine.advance(&operation.operation_id).unwrap().state,
        OperationState::RecoveryRequired { .. }
    ));
    assert_eq!(current(&mut engine, &draft), draft);
    assert_eq!(control.published.get(), 0);
    assert!(!control.terminal_written.get());
    assert_eq!(*control.order.borrow(), vec!["preflight"]);
    assert_eq!(state.borrow().drops, 0);
    let steps = state.borrow().steps;
    let effects = state.borrow().effects;
    control.replay_oversized.set(false);
    let before = engine.cursor();
    assert!(matches!(
        engine.recover(&operation.operation_id).unwrap().state,
        OperationState::Completed { .. }
    ));
    assert_eq!(state.borrow().steps, steps);
    assert_eq!(state.borrow().effects, effects);
    assert_eq!(state.borrow().drops, 1);
    let reply = engine
        .dispatch(request(RequestBody::Query {
            query: Query::ResumeEvents(ResumeEventsInput {
                after: before.clone(),
                maximum_events: ProgressCount::new(1),
            }),
        }))
        .unwrap()
        .into_inner();
    let ReplyBody::Result {
        result: ResultPayload::Query { query },
    } = reply.body
    else {
        panic!("replay reply");
    };
    let QueryResult::ResumeEvents(batch) = *query else {
        panic!("replay result");
    };
    assert_eq!(batch.events.as_slice().len(), 1);
    assert_eq!(batch.next.sequence.get(), before.sequence.get() + 1);
    let reply = engine
        .dispatch(request(RequestBody::Query {
            query: Query::ResumeEvents(ResumeEventsInput {
                after: batch.next,
                maximum_events: ProgressCount::new(1),
            }),
        }))
        .unwrap()
        .into_inner();
    let ReplyBody::Result {
        result: ResultPayload::Query { query },
    } = reply.body
    else {
        panic!("draft replay reply");
    };
    let QueryResult::ResumeEvents(batch) = *query else {
        panic!("draft replay result");
    };
    assert_eq!(batch.events.as_slice().len(), 1);
    assert!(matches!(
        batch.events.as_slice()[0].body,
        EventBody::DraftChanged { .. }
    ));
}
#[test]
fn terminal_wal_failure_keeps_dirty_draft_and_lease_without_local_publication() {
    for written in [false, true] {
        let (_fixture, mut engine, control, state, draft) = setup();
        let operation = execute(&mut engine, &draft);
        control.fail_terminal_wal.set(true);
        control.write_then_fail.set(written);
        let before = engine.cursor();
        assert_eq!(
            engine.advance(&operation.operation_id),
            Err(KernelFailure::Storage)
        );
        assert_eq!(engine.cursor(), before);
        assert_eq!(current(&mut engine, &draft), draft);
        assert_eq!(control.published.get(), 0);
        assert_eq!(control.terminal_written.get(), written);
        assert_eq!(state.borrow().drops, 0);
        assert_eq!(*control.order.borrow(), vec!["preflight", "terminal-wal"]);
        assert_eq!(
            engine.recover(&operation.operation_id),
            Err(KernelFailure::Poisoned)
        );
    }
}
#[test]
fn repeated_commit_or_post_commit_error_poison_host_and_retain_custody() {
    for double in [true, false] {
        let (_fixture, mut engine, control, state, draft) = setup();
        let operation = execute(&mut engine, &draft);
        control.double_commit.set(double);
        control.fail_after_commit.set(!double);
        assert_eq!(
            engine.advance(&operation.operation_id),
            Err(KernelFailure::InvalidPortResult)
        );
        assert_eq!(state.borrow().drops, 0);
        assert_eq!(
            engine.recover(&operation.operation_id),
            Err(KernelFailure::Poisoned)
        );
        assert_eq!(
            control
                .order
                .borrow()
                .iter()
                .filter(|&&s| s == "terminal-wal")
                .count(),
            1
        );
        assert_eq!(
            current(&mut engine, &draft).state,
            if double {
                DraftState::Dirty
            } else {
                DraftState::Clean
            }
        );
    }
}
