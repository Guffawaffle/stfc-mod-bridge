//! Development source proof: actual local runner/kernel with deliberately
//! synthetic canonical owners. No native service, game, account or store proof.
use bridge_contracts::v1::*;
use bridge_engine::{host::*, operations::*};
use std::{
    fs::{self, OpenOptions},
    io::Write,
    marker::PhantomData,
    path::{Path, PathBuf},
    process::{Command as ProcessCommand, Stdio},
    rc::Rc,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicUsize, Ordering},
    },
    thread::{self, ThreadId},
    time::{Duration, Instant},
};

const WAIT: Duration = Duration::from_secs(10);
fn uuid(n: u64) -> String {
    format!("00000000-0000-4000-8000-{n:012x}")
}
fn request(body: RequestBody, n: u64) -> OwnedFrame {
    OwnedFrame::new(
        serde_json::to_vec(&Request {
            protocol_version: ProtocolVersion,
            request_id: RequestId::new(uuid(n)).unwrap(),
            body,
        })
        .unwrap(),
    )
    .unwrap()
}
fn query(query: Query, n: u64) -> OwnedFrame {
    request(RequestBody::Query { query }, n)
}
fn command(command: Command, n: u64) -> OwnedFrame {
    request(RequestBody::Command { command }, n)
}
fn reply(pending: &PendingReply) -> Reply {
    decode_reply(pending.recv_timeout(WAIT).unwrap().unwrap().as_bytes())
        .unwrap()
        .into_inner()
}
fn exchange(handle: &HostHandle, frame: OwnedFrame) -> Reply {
    reply(&handle.exchange(frame).unwrap())
}
fn wait_until(mut predicate: impl FnMut() -> bool) {
    let deadline = Instant::now() + WAIT;
    while !predicate() {
        assert!(
            Instant::now() < deadline,
            "bounded source fixture timed out"
        );
        thread::sleep(Duration::from_millis(1));
    }
}
fn preferences() -> MutationIntent {
    MutationIntent::SaveApplicationPreferences(SaveApplicationPreferencesInput {
        expected_revision: OpaqueRevision::new("fixture-preferences:1").unwrap(),
        values: ApplicationPreferences {
            theme: ThemePreference::Dark,
            motion: MotionPreference::Reduced,
            last_target: None,
            provider: None,
        },
    })
}
#[derive(Default)]
struct Audit {
    constructed: Vec<ThreadId>,
    invoked: Vec<ThreadId>,
    dropped: Vec<ThreadId>,
    captures: usize,
    lease_drops: usize,
    mutations: usize,
}
#[derive(Clone, Default)]
struct Controls {
    audit: Arc<Mutex<Audit>>,
    release: Arc<AtomicBool>,
    ticks: Arc<AtomicUsize>,
    close_error: Arc<AtomicBool>,
    safe_recovery: Arc<AtomicBool>,
    blocked: Arc<AtomicBool>,
    owner_panic: Arc<AtomicBool>,
    epoch: usize,
    root: Option<PathBuf>,
    wrong_reply_id: bool,
    wrong_reply_kind: bool,
    future_event: bool,
}
fn marker(controls: &Controls, leaf: &str) {
    if let Some(root) = &controls.root {
        let mut file = OpenOptions::new()
            .write(true)
            .create(true)
            .truncate(true)
            .open(root.join(leaf))
            .unwrap();
        file.write_all(b"synthetic-local-host-fixture").unwrap();
        file.sync_all().unwrap();
    }
}
struct LocalLease {
    keys: Vec<ResourceKey>,
    controls: Controls,
    _local: Rc<()>,
}
impl ResourceLease for LocalLease {
    fn resources(&self) -> &[ResourceKey] {
        &self.keys
    }
}
impl Drop for LocalLease {
    fn drop(&mut self) {
        marker(&self.controls, "retained-lease-dropped");
        let mut audit = self.controls.audit.lock().unwrap();
        audit.lease_drops += 1;
        audit.dropped.push(thread::current().id());
    }
}
struct Owner {
    controls: Controls,
    _local: Rc<()>,
}
impl Drop for Owner {
    fn drop(&mut self) {
        marker(&self.controls, "retained-owner-dropped");
        self.controls
            .audit
            .lock()
            .unwrap()
            .dropped
            .push(thread::current().id());
    }
}
impl OperationPorts for Owner {
    type Lease = LocalLease;
    fn capture(
        &mut self,
        intent: &MutationIntent,
        _: &HostEpoch,
    ) -> Result<CapturedOperation, Box<BridgeError>> {
        let MutationIntent::SaveApplicationPreferences(input) = intent else {
            return Err(error(ErrorCode::UnsupportedCapability));
        };
        let mut audit = self.controls.audit.lock().unwrap();
        audit.captures += 1;
        audit.invoked.push(thread::current().id());
        Ok(CapturedOperation {
            semantics: PlanSemantics {
                hash_profile: HashProfile::BridgePlanSemanticJsonV1,
                action: ActionId::SaveApplicationPreferences,
                capture: PreparedCapture::SaveApplicationPreferences {
                    input: input.clone(),
                },
                trust_domain: TrustDomain::ApplicationState,
                effects: BoundedList::new(vec![ProposedEffect::SaveApplicationPreferences])
                    .unwrap(),
            },
            resources: vec![ResourceKey::Application {
                identity: ApplicationIdentity::new("synthetic-host-fixture").unwrap(),
            }],
        })
    }
    fn acquire(&mut self, keys: &[ResourceKey]) -> Result<Self::Lease, Box<BridgeError>> {
        Ok(LocalLease {
            keys: keys.to_vec(),
            controls: self.controls.clone(),
            _local: Rc::new(()),
        })
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
        let PreparedCapture::SaveApplicationPreferences { input } = &semantics.capture else {
            unreachable!()
        };
        Ok(RecoveryRef {
            operation_id: operation.clone(),
            transaction: NativeTransactionRef::new("synthetic-host-transaction").unwrap(),
            target: RecoveryTarget::ApplicationPreferences {
                revision: input.expected_revision.clone(),
            },
        })
    }
    fn advance(
        &mut self,
        operation: &OperationSnapshot,
        _: &RecoveryRef,
        _: &Self::Lease,
        _: bool,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        if self.controls.owner_panic.load(Ordering::Acquire) {
            struct CallLocal(Controls);
            impl Drop for CallLocal {
                fn drop(&mut self) {
                    marker(&self.0, "call-local-dropped");
                }
            }
            marker(&self.controls, "native-staging");
            let _call_local = CallLocal(self.controls.clone());
            panic!("synthetic owner-method panic; call-local custody unknown");
        }
        self.controls
            .audit
            .lock()
            .unwrap()
            .invoked
            .push(thread::current().id());
        self.controls.ticks.fetch_add(1, Ordering::AcqRel);
        if self.controls.safe_recovery.load(Ordering::Acquire) {
            return Ok(TransactionStep::RecoveryRequired {
                reason: RecoveryReason::InterruptedTransaction,
                safe_owner_boundary: true,
            });
        }
        if self.controls.release.load(Ordering::Acquire) {
            self.controls.audit.lock().unwrap().mutations += 1;
            Ok(TransactionStep::Complete {
                outcome: CompletionOutcome::Changed {
                    reason: CompletionReason::Applied,
                    receipt: Some(Box::new(EffectReceipt::ApplicationPreferencesSaved {
                        snapshot: ApplicationPreferencesSnapshot {
                            revision: OpaqueRevision::new("fixture-preferences:2").unwrap(),
                            values: match &operation.semantics.capture {
                                PreparedCapture::SaveApplicationPreferences { input } => {
                                    input.values.clone()
                                }
                                _ => unreachable!(),
                            },
                        },
                    })),
                },
                session_custody: None,
            })
        } else {
            Ok(TransactionStep::Progress {
                progress: Progress {
                    phase: PhaseId::new("synthetic_work").unwrap(),
                    measurement: Measurement::Unknown,
                },
                cancellable: true,
            })
        }
    }
    fn recover(
        &mut self,
        _: &OperationSnapshot,
        _: &RecoveryRef,
        _: &Self::Lease,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        if self
            .controls
            .root
            .as_ref()
            .is_some_and(|root| root.join("native-staging").exists())
        {
            marker(&self.controls, "native-rolled-back");
            return Ok(TransactionStep::Complete {
                outcome: CompletionOutcome::RolledBack {
                    reason: CompletionReason::RollbackCompleted,
                },
                session_custody: None,
            });
        }
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
struct Journal {
    rows: Vec<JournalRecord>,
    _local: Rc<()>,
    disk: Option<FileJournal>,
}
impl DurableJournal for Journal {
    fn records(&self) -> &[JournalRecord] {
        self.disk.as_ref().map_or(&self.rows, FileJournal::records)
    }
    fn append(&mut self, row: &JournalRecord) -> Result<(), KernelFailure> {
        if let Some(disk) = &mut self.disk {
            return disk.append(row);
        }
        self.rows.push(row.clone());
        Ok(())
    }
}
struct Clock;
impl HostClock for Clock {
    fn now(&self) -> ClockReading {
        ClockReading {
            unix_millis: 1000,
            utc: UtcTimestamp::new("2026-10-03T12:00:00Z").unwrap(),
        }
    }
    fn deadline(&self, duration: u64) -> Result<ClockReading, Box<BridgeError>> {
        Ok(ClockReading {
            unix_millis: 1000 + duration,
            utc: UtcTimestamp::new("2026-10-03T12:01:00Z").unwrap(),
        })
    }
}
struct Ids(u64);
impl IdentitySource for Ids {
    fn plan_id(&mut self) -> PlanId {
        self.0 += 1;
        PlanId::new(uuid(self.0)).unwrap()
    }
    fn operation_id(&mut self) -> OperationId {
        self.0 += 1;
        OperationId::new(uuid(self.0)).unwrap()
    }
}
type Kernel = KernelHost<Owner, Journal, Clock, Ids>;
struct FixtureHost {
    kernel: Kernel,
    controls: Controls,
    _local: PhantomData<Rc<()>>,
}
impl FixtureHost {
    fn new(controls: Controls) -> Self {
        controls
            .audit
            .lock()
            .unwrap()
            .constructed
            .push(thread::current().id());
        let engine = Engine::open(
            Owner {
                controls: controls.clone(),
                _local: Rc::new(()),
            },
            Journal {
                rows: vec![],
                _local: Rc::new(()),
                disk: controls
                    .root
                    .as_ref()
                    .map(|root| FileJournal::open_existing(&root.join("journal.bin")).unwrap()),
            },
            Clock,
            Ids(10),
            HostConfiguration {
                epoch: HostEpoch::new(uuid(1 + controls.epoch as u64 * 2)).unwrap(),
                stream: StreamId::new(uuid(2 + controls.epoch as u64 * 2)).unwrap(),
                kind: HostKind::WindowsX64,
                preparation_lifetime_millis: 60000,
            },
        )
        .unwrap();
        Self {
            kernel: KernelHost::new(engine),
            controls,
            _local: PhantomData,
        }
    }
}
impl LocalHost for FixtureHost {
    fn dispatch(&mut self, request: ValidatedRequest) -> Result<ValidatedReply, HostFailure> {
        while self.controls.blocked.load(Ordering::Acquire) {
            thread::sleep(Duration::from_millis(1));
        }
        if self.controls.wrong_reply_kind {
            let request = Request {
                protocol_version: ProtocolVersion,
                request_id: request.as_inner().request_id.clone(),
                body: RequestBody::Query {
                    query: Query::Snapshot(EmptyInput {}),
                },
            };
            return self
                .kernel
                .dispatch(decode_request(&serde_json::to_vec(&request).unwrap()).unwrap());
        }
        let reply = self.kernel.dispatch(request)?;
        if self.controls.wrong_reply_id {
            let mut reply = reply.into_inner();
            reply.request_id = ReplyRequestId::new(Some(RequestId::new(uuid(9999)).unwrap()));
            return Ok(decode_reply(&serde_json::to_vec(&reply).unwrap()).unwrap());
        }
        Ok(reply)
    }
    fn cursor(&self) -> Cursor {
        self.kernel.cursor()
    }
    fn pump(&mut self) -> Result<(), HostFailure> {
        self.kernel.pump()
    }
    fn events_after(&mut self, after: &Cursor) -> Result<EventBatch, HostFailure> {
        if self.controls.future_event {
            let mut future = after.clone();
            future.sequence = Sequence::new(future.sequence.get() + 1);
            return Ok(EventBatch {
                after: after.clone(),
                next: future.clone(),
                events: BoundedList::new(vec![Event {
                    protocol_version: ProtocolVersion,
                    cursor: future,
                    body: EventBody::SnapshotInvalidated {
                        reason: SnapshotInvalidationReason::OperationChanged,
                    },
                }])
                .unwrap(),
            });
        }
        self.kernel.events_after(after)
    }
    fn request_close(&mut self) -> Result<CloseDisposition, HostFailure> {
        let close = self.kernel.request_close()?;
        if self.controls.close_error.load(Ordering::Acquire) {
            Err(HostFailure::CloseUnavailable)
        } else {
            Ok(close)
        }
    }
    fn close_disposition(&self) -> Result<CloseDisposition, HostFailure> {
        if self.controls.close_error.load(Ordering::Acquire) {
            Err(HostFailure::CloseUnavailable)
        } else {
            self.kernel.close_disposition()
        }
    }
}
fn prepare(handle: &HostHandle, n: u64) -> PreparedPlan {
    let ReplyBody::Result {
        result: ResultPayload::Command { command },
    } = exchange(
        handle,
        command(
            Command::Prepare(Box::new(PrepareInput {
                intent: preferences(),
            })),
            n,
        ),
    )
    .body
    else {
        panic!("fixture preparation failed")
    };
    let CommandResult::Prepare(plan) = *command else {
        panic!("wrong fixture reply")
    };
    plan
}
fn commit_input(plan: PreparedPlan) -> CommitInput {
    CommitInput {
        plan_ref: plan.plan_ref,
        idempotency_key: IdempotencyKey::new(uuid(900)).unwrap(),
    }
}
fn committed(reply: Reply) -> OperationSnapshot {
    let ReplyBody::Result {
        result: ResultPayload::Command { command },
    } = reply.body
    else {
        panic!("fixture commit failed")
    };
    let CommandResult::Commit(operation) = *command else {
        panic!("wrong fixture reply")
    };
    operation
}
fn spawn(
    controls: &Controls,
) -> (
    HostHandle,
    thread::JoinHandle<Result<HostExit, HostFailure>>,
) {
    let controls = controls.clone();
    spawn_local_host(move || Ok(FixtureHost::new(controls))).unwrap()
}

#[test]
fn non_send_owner_lease_and_journal_construct_invoke_and_drop_on_actor_thread() {
    fn send<T: Send>() {}
    send::<HostHandle>();
    send::<OwnedFrame>();
    send::<PendingReply>();
    let controls = Controls::default();
    controls.release.store(true, Ordering::Release);
    let (handle, worker) = spawn(&controls);
    let plan = prepare(&handle, 100);
    committed(exchange(
        &handle,
        command(Command::Commit(commit_input(plan)), 101),
    ));
    wait_until(|| controls.audit.lock().unwrap().mutations == 1);
    drop(handle);
    let exit = worker.join().unwrap().unwrap();
    assert_eq!(exit.disposition, CloseDisposition::Ready);
    let audit = controls.audit.lock().unwrap();
    let owner = audit.constructed[0];
    assert_ne!(owner, thread::current().id());
    assert!(!audit.invoked.is_empty());
    assert!(!audit.dropped.is_empty());
    assert!(
        audit
            .invoked
            .iter()
            .chain(&audit.dropped)
            .all(|id| *id == owner)
    );
    assert_eq!(audit.lease_drops, 1);
}

#[test]
fn caller_thread_factory_and_borrowed_pump_drain_work_on_that_same_thread() {
    let controls = Controls::default();
    let actor_controls = controls.clone();
    let (handle, inbox) = owner_channel();
    let pending = handle
        .exchange(command(
            Command::Prepare(Box::new(PrepareInput {
                intent: preferences(),
            })),
            100,
        ))
        .unwrap();
    let current = thread::current().id();
    let mut step = 0;
    let mut admission = None;
    let exit = run_on_current_thread(
        inbox,
        || Ok(FixtureHost::new(actor_controls)),
        |pump| {
            assert_eq!(thread::current().id(), current);
            pump.tick();
            if step == 0 {
                let ReplyBody::Result {
                    result: ResultPayload::Command { command: output },
                } = reply(&pending).body
                else {
                    panic!()
                };
                let CommandResult::Prepare(plan) = *output else {
                    panic!()
                };
                admission = Some(
                    handle
                        .exchange(command(Command::Commit(commit_input(plan)), 101))
                        .unwrap(),
                );
                step = 1;
            } else if step == 1 {
                committed(reply(admission.as_ref().unwrap()));
                controls.release.store(true, Ordering::Release);
                return DriveControl::RequestClose;
            }
            DriveControl::Continue
        },
    )
    .unwrap();
    assert_eq!(exit.disposition, CloseDisposition::Ready);
    assert_eq!(controls.audit.lock().unwrap().constructed, vec![current]);
    assert_eq!(controls.audit.lock().unwrap().mutations, 1);
    assert!(
        controls
            .audit
            .lock()
            .unwrap()
            .dropped
            .iter()
            .all(|id| *id == current)
    );
}

#[test]
fn strict_framing_rejects_duplicates_invalid_utf8_and_oversize_before_owner_capture() {
    let controls = Controls::default();
    let (handle, worker) = spawn(&controls);
    for bytes in [
        b"{\"protocolVersion\":1,\"protocolVersion\":1}".to_vec(),
        vec![0xff],
        vec![],
    ] {
        let output = exchange(&handle, OwnedFrame::new(bytes).unwrap());
        assert!(
            matches!(output.body, ReplyBody::Rejected { error } if error.code == ErrorCode::InvalidRequest)
        );
    }
    assert_eq!(
        OwnedFrame::new(vec![b'x'; MAX_MESSAGE_BYTES + 1])
            .unwrap_err()
            .code,
        SubmissionFailureCode::FrameTooLarge
    );
    let maximum = OwnedFrame::new(vec![b' '; MAX_MESSAGE_BYTES]).unwrap();
    assert_eq!(maximum.as_bytes().len(), MAX_MESSAGE_BYTES);
    assert_eq!(controls.audit.lock().unwrap().captures, 0);
    assert!(
        !format!("{:?}", OwnedFrame::new(b"private-canary".to_vec()).unwrap())
            .contains("private-canary")
    );
    drop(handle);
    worker.join().unwrap().unwrap();
}

#[test]
fn queued_requests_are_bounded_and_queue_refusal_is_not_sent() {
    let (handle, inbox) = owner_channel();
    let mut requests = Vec::new();
    for n in 0..MAX_QUEUED_REQUESTS {
        requests.push(
            handle
                .exchange(query(Query::Hello(EmptyInput {}), n as u64 + 100))
                .unwrap(),
        );
    }
    assert!(matches!(
        handle.exchange(query(Query::Hello(EmptyInput {}), 999)),
        Err(SubmissionFailure {
            code: SubmissionFailureCode::QueueFull
        })
    ));
    drop(inbox);
    assert!(requests[0].recv_timeout(WAIT).is_err());
    assert!(matches!(
        handle.request_close(),
        Err(SubmissionFailure {
            code: SubmissionFailureCode::Disconnected
        })
    ));
}

#[test]
fn completed_unread_replies_and_subscriptions_have_independent_count_limits() {
    let controls = Controls::default();
    let (handle, worker) = spawn(&controls);
    let mut replies = vec![];
    for n in 0..MAX_PENDING_REPLIES {
        let pending = handle
            .exchange(query(Query::Hello(EmptyInput {}), n as u64 + 100))
            .unwrap();
        reply(&pending);
        replies.push(pending);
    }
    assert!(matches!(
        handle.request_close(),
        Err(SubmissionFailure {
            code: SubmissionFailureCode::PendingLimit
        })
    ));
    let mut observations = vec![];
    for _ in 0..MAX_OBSERVERS {
        let subscription = handle.subscribe(None).unwrap();
        subscription.ready_timeout(WAIT).unwrap().unwrap();
        observations.push(subscription);
    }
    assert!(matches!(
        handle.subscribe(None),
        Err(SubmissionFailure {
            code: SubmissionFailureCode::ObserverLimit
        })
    ));
    replies.clear();
    observations.clear();
    let replacement = handle.subscribe(None).unwrap();
    replacement.ready_timeout(WAIT).unwrap().unwrap();
    drop(replacement);
    drop(handle);
    worker.join().unwrap().unwrap();
}

#[test]
fn lost_commit_reply_and_dropped_subscription_replay_original_operation_without_duplicate_effects()
{
    let controls = Controls::default();
    let (handle, worker) = spawn(&controls);
    let subscription = handle.subscribe(None).unwrap();
    subscription.ready_timeout(WAIT).unwrap().unwrap();
    let input = commit_input(prepare(&handle, 100));
    drop(
        handle
            .exchange(command(Command::Commit(input.clone()), 101))
            .unwrap(),
    );
    drop(subscription);
    wait_until(|| controls.ticks.load(Ordering::Acquire) > 2);
    assert_eq!(controls.audit.lock().unwrap().lease_drops, 0);
    let replayed = committed(exchange(
        &handle,
        command(Command::Commit(input.clone()), 102),
    ));
    controls.release.store(true, Ordering::Release);
    wait_until(|| controls.audit.lock().unwrap().mutations == 1);
    let again = committed(exchange(&handle, command(Command::Commit(input), 103)));
    assert_eq!(replayed.operation_id, again.operation_id);
    assert!(matches!(again.state, OperationState::Completed { .. }));
    assert_eq!(controls.audit.lock().unwrap().mutations, 1);
    assert_eq!(controls.audit.lock().unwrap().captures, 1);
    drop(handle);
    worker.join().unwrap().unwrap();
}

#[test]
fn last_handle_disconnect_defers_drop_until_worker_reaches_real_fixture_boundary() {
    let controls = Controls::default();
    let (handle, worker) = spawn(&controls);
    let input = commit_input(prepare(&handle, 100));
    committed(exchange(&handle, command(Command::Commit(input), 101)));
    drop(handle);
    wait_until(|| controls.ticks.load(Ordering::Acquire) > 3);
    assert!(!worker.is_finished());
    assert_eq!(controls.audit.lock().unwrap().lease_drops, 0);
    assert!(controls.audit.lock().unwrap().dropped.is_empty());
    controls.release.store(true, Ordering::Release);
    let exit = worker.join().unwrap().unwrap();
    assert_eq!(exit.disposition, CloseDisposition::Ready);
    assert_eq!(controls.audit.lock().unwrap().lease_drops, 1);
}

#[test]
fn serialized_normal_close_refuses_later_admission_and_keeps_progressing() {
    let controls = Controls::default();
    let (handle, worker) = spawn(&controls);
    let input = commit_input(prepare(&handle, 100));
    committed(exchange(&handle, command(Command::Commit(input), 101)));
    let close = handle
        .request_close()
        .unwrap()
        .recv_timeout(WAIT)
        .unwrap()
        .unwrap();
    assert!(matches!(close, CloseDisposition::Deferred { .. }));
    let rejected = exchange(
        &handle,
        command(
            Command::Prepare(Box::new(PrepareInput {
                intent: preferences(),
            })),
            102,
        ),
    );
    assert!(
        matches!(rejected.body, ReplyBody::Rejected { error } if error.code == ErrorCode::OperationBusy)
    );
    assert_eq!(controls.audit.lock().unwrap().lease_drops, 0);
    controls.release.store(true, Ordering::Release);
    worker.join().unwrap().unwrap();
}

#[test]
fn observer_overflow_is_explicit_resnapshot_and_does_not_block_worker_completion() {
    let controls = Controls::default();
    let (handle, worker) = spawn(&controls);
    let subscription = handle.subscribe(None).unwrap();
    let watermark = subscription.ready_timeout(WAIT).unwrap().unwrap();
    assert_eq!(watermark.sequence.get(), 0);
    let input = commit_input(prepare(&handle, 100));
    committed(exchange(&handle, command(Command::Commit(input), 101)));
    wait_until(|| controls.ticks.load(Ordering::Acquire) > MAX_OBSERVER_EVENTS + 4);
    assert!(matches!(
        subscription.try_recv(),
        Ok(HostObservation::Fault(
            ObservationFailure::OverflowResnapshotRequired
        ))
    ));
    controls.release.store(true, Ordering::Release);
    wait_until(|| controls.audit.lock().unwrap().mutations == 1);
    assert!(matches!(
        subscription.try_recv(),
        Ok(HostObservation::Fault(
            ObservationFailure::OverflowResnapshotRequired
        ))
    ));
    let renewed = handle.subscribe(None).unwrap();
    renewed.ready_timeout(WAIT).unwrap().unwrap();
    let snapshot = exchange(&handle, query(Query::Snapshot(EmptyInput {}), 103));
    assert!(
        matches!(snapshot.body, ReplyBody::Result { result: ResultPayload::Query { query } } if matches!(*query, QueryResult::Snapshot(_)))
    );
    drop(renewed);
    drop(subscription);
    drop(handle);
    worker.join().unwrap().unwrap();
}

#[test]
fn foreign_and_expired_event_watermarks_refuse_without_reexecution() {
    let controls = Controls::default();
    let (handle, worker) = spawn(&controls);
    let subscription = handle.subscribe(None).unwrap();
    let watermark = subscription.ready_timeout(WAIT).unwrap().unwrap();
    drop(subscription);
    let mut foreign = watermark.clone();
    foreign.host_epoch = HostEpoch::new(uuid(90)).unwrap();
    let refused = handle.subscribe(Some(foreign)).unwrap();
    assert_eq!(
        refused.ready_timeout(WAIT).unwrap(),
        Err(ObservationFailure::RetentionGapResnapshotRequired)
    );
    drop(refused);
    let input = commit_input(prepare(&handle, 100));
    committed(exchange(&handle, command(Command::Commit(input), 101)));
    wait_until(|| controls.ticks.load(Ordering::Acquire) > 135);
    let stale = handle.subscribe(Some(watermark)).unwrap();
    assert_eq!(
        stale.ready_timeout(WAIT).unwrap(),
        Err(ObservationFailure::RetentionGapResnapshotRequired)
    );
    assert_eq!(controls.audit.lock().unwrap().captures, 1);
    controls.release.store(true, Ordering::Release);
    drop(stale);
    drop(handle);
    worker.join().unwrap().unwrap();
}

#[test]
fn close_errors_keep_host_until_actual_close_observation_becomes_available() {
    let controls = Controls::default();
    controls.close_error.store(true, Ordering::Release);
    let (handle, worker) = spawn(&controls);
    assert_eq!(
        handle.request_close().unwrap().recv_timeout(WAIT).unwrap(),
        Err(HostFailure::CloseUnavailable)
    );
    drop(handle);
    thread::sleep(Duration::from_millis(20));
    assert!(!worker.is_finished());
    assert!(controls.audit.lock().unwrap().dropped.is_empty());
    controls.close_error.store(false, Ordering::Release);
    let exit = worker.join().unwrap().unwrap();
    assert_eq!(exit.disposition, CloseDisposition::Ready);
    assert_eq!(exit.failure, Some(HostFailure::CloseUnavailable));
}

#[test]
fn safe_recovery_exit_stays_recovery_required_and_does_not_auto_recover() {
    let controls = Controls::default();
    controls.safe_recovery.store(true, Ordering::Release);
    let (handle, worker) = spawn(&controls);
    let input = commit_input(prepare(&handle, 100));
    committed(exchange(&handle, command(Command::Commit(input), 101)));
    drop(handle);
    let exit = worker.join().unwrap().unwrap();
    assert!(matches!(
        exit.disposition,
        CloseDisposition::RecoveryRequired { .. }
    ));
    assert_eq!(controls.audit.lock().unwrap().mutations, 0);
}

#[test]
fn observer_driver_panic_is_contained_and_drains_owned_work_before_drop() {
    let controls = Controls::default();
    let (handle, inbox) = owner_channel();
    let producer = handle.clone();
    let other_controls = controls.clone();
    let requester = thread::spawn(move || {
        let input = commit_input(prepare(&producer, 100));
        committed(exchange(&producer, command(Command::Commit(input), 101)));
        wait_until(|| other_controls.ticks.load(Ordering::Acquire) > 1);
        other_controls.release.store(true, Ordering::Release);
    });
    let local_controls = controls.clone();
    let exit = run_on_current_thread(
        inbox,
        || Ok(FixtureHost::new(local_controls)),
        |_| {
            if controls.ticks.load(Ordering::Acquire) > 0 {
                panic!("synthetic observer callback panic");
            }
            DriveControl::Continue
        },
    )
    .unwrap();
    requester.join().unwrap();
    assert_eq!(exit.failure, Some(HostFailure::DriverPanicked));
    assert_eq!(exit.disposition, CloseDisposition::Ready);
    assert_eq!(controls.audit.lock().unwrap().mutations, 1);
    assert_eq!(controls.audit.lock().unwrap().lease_drops, 1);
}

fn fixture_parent() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../artifacts/next/preparation/local-host-fixtures")
}

#[test]
fn owner_panic_child_fixture() {
    let Some(requested) = std::env::var_os("BRIDGE_LOCAL_HOST_CHILD_DIR") else {
        assert!(
            std::env::var_os("BRIDGE_LOCAL_HOST_CHILD_MODE").is_none(),
            "orphaned child routing must refuse"
        );
        return;
    };
    assert_eq!(
        std::env::var("BRIDGE_LOCAL_HOST_CHILD_MODE").unwrap(),
        "owner-panic"
    );
    let root = PathBuf::from(requested).canonicalize().unwrap();
    let parent = fixture_parent().canonicalize().unwrap();
    assert!(
        root.starts_with(&parent) && root.parent() == Some(parent.as_path()),
        "child must use a direct owned source fixture"
    );
    assert!(
        fs::symlink_metadata(&root).unwrap().is_dir()
            && !fs::symlink_metadata(&root).unwrap().is_symlink()
    );
    let controls = Controls {
        root: Some(root.clone()),
        ..Controls::default()
    };
    controls.owner_panic.store(true, Ordering::Release);
    let (handle, worker) = spawn(&controls);
    let subscription = handle.subscribe(None).unwrap();
    subscription.ready_timeout(WAIT).unwrap().unwrap();
    let input = commit_input(prepare(&handle, 100));
    fs::write(
        root.join("commit.json"),
        serde_json::to_vec(&input).unwrap(),
    )
    .unwrap();
    committed(exchange(&handle, command(Command::Commit(input), 101)));
    wait_until(|| root.join("call-local-dropped").exists());
    wait_until(|| {
        matches!(
            subscription.try_recv(),
            Ok(HostObservation::Fault(ObservationFailure::HostFailed(
                HostFailure::OwnerPanicked
            )))
        )
    });
    assert_eq!(
        handle.request_close().unwrap().recv_timeout(WAIT).unwrap(),
        Err(HostFailure::OwnerPanicked)
    );
    assert!(!worker.is_finished());
    marker(&controls, "panic-reported-alive");
    drop(subscription);
    drop(handle);
    // No safe completion/join exists for owner panic. The parent must terminate
    // this exact fixture process and inspect its journal in a fresh host.
    loop {
        thread::sleep(Duration::from_millis(10));
    }
}

#[test]
fn owner_thread_panic_is_unknown_custody_until_actual_process_death_and_explicit_restart_recovery()
{
    assert!(
        std::env::var_os("BRIDGE_LOCAL_HOST_CHILD_DIR").is_none()
            && std::env::var_os("BRIDGE_LOCAL_HOST_CHILD_MODE").is_none(),
        "caller child overrides forbidden"
    );
    let parent = fixture_parent();
    fs::create_dir_all(&parent).unwrap();
    let parent = parent.canonicalize().unwrap();
    let root = parent.join(format!(
        "{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ));
    fs::create_dir(&root).unwrap();
    fs::write(root.join("journal.bin"), []).unwrap();
    let output = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(root.join("child.stdout"))
        .unwrap();
    let errors = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(root.join("child.stderr"))
        .unwrap();
    let mut child = ProcessCommand::new(std::env::current_exe().unwrap())
        .args([
            "--exact",
            "owner_panic_child_fixture",
            "--nocapture",
            "--test-threads=1",
        ])
        .env("BRIDGE_LOCAL_HOST_CHILD_DIR", &root)
        .env("BRIDGE_LOCAL_HOST_CHILD_MODE", "owner-panic")
        .stdout(Stdio::from(output))
        .stderr(Stdio::from(errors))
        .spawn()
        .unwrap();
    let deadline = Instant::now() + WAIT;
    while !root.join("panic-reported-alive").exists() {
        if child.try_wait().unwrap().is_some() || Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            panic!(
                "owned child failed: {}",
                fs::read_to_string(root.join("child.stderr")).unwrap()
            );
        }
        thread::sleep(Duration::from_millis(1));
    }
    assert!(
        child.try_wait().unwrap().is_none(),
        "actor panic did not kill this process"
    );
    assert!(
        root.join("call-local-dropped").exists(),
        "catching unwind cannot preserve call-local custody"
    );
    assert!(
        !root.join("retained-owner-dropped").exists()
            && !root.join("retained-lease-dropped").exists()
    );
    assert!(matches!(
        FileJournal::open_existing(&root.join("journal.bin")),
        Err(KernelFailure::JournalBusy)
    ));
    let killed_pid = child.id();
    child.kill().unwrap();
    let status = child.wait().unwrap();
    assert!(!status.success());
    let controls = Controls {
        epoch: 1,
        root: Some(root.clone()),
        ..Controls::default()
    };
    let mut restarted = FixtureHost::new(controls.clone());
    let input: CommitInput =
        serde_json::from_slice(&fs::read(root.join("commit.json")).unwrap()).unwrap();
    let original = committed(
        restarted
            .kernel
            .dispatch(
                decode_request(command(Command::Commit(input.clone()), 102).as_bytes()).unwrap(),
            )
            .unwrap()
            .into_inner(),
    );
    assert!(
        matches!(original.state, OperationState::RecoveryRequired { .. }),
        "new host must report forced-death interruption"
    );
    let recovered = restarted
        .kernel
        .engine_mut()
        .recover(&original.operation_id)
        .unwrap();
    assert!(matches!(
        recovered.state,
        OperationState::Completed {
            outcome: CompletionOutcome::RolledBack { .. }
        }
    ));
    assert_eq!(controls.audit.lock().unwrap().mutations, 0);
    assert!(root.join("native-rolled-back").exists());
    let replayed = committed(
        restarted
            .kernel
            .dispatch(decode_request(command(Command::Commit(input), 103).as_bytes()).unwrap())
            .unwrap()
            .into_inner(),
    );
    assert_eq!(replayed.operation_id, original.operation_id);
    assert_eq!(replayed.state, recovered.state);
    fs::write(root.join("process-observation.json"), serde_json::to_vec_pretty(&serde_json::json!({
        "schemaVersion":"bridge-local-host-process-fixture/v1", "childPid":killed_pid,
        "ownerPanicProcessAlive":true, "callLocalUnwindObserved":true, "retainedOwnerBeforeKill":true,
        "actualOwnedProcessKilled":true, "recovery":"explicit_synthetic_rollback", "operationId":original.operation_id,
        "nativeRuntimeQualified":false, "releaseQualified":false,
    })).unwrap()).unwrap();
}

#[test]
fn foreign_reply_request_id_is_refused_even_when_its_closed_schema_is_valid() {
    let controls = Controls {
        wrong_reply_id: true,
        ..Controls::default()
    };
    let (handle, worker) = spawn(&controls);
    assert_eq!(
        handle
            .exchange(query(Query::Hello(EmptyInput {}), 100))
            .unwrap()
            .recv_timeout(WAIT)
            .unwrap(),
        Err(HostFailure::InvalidReply)
    );
    let exit = worker.join().unwrap().unwrap();
    assert_eq!(exit.failure, Some(HostFailure::InvalidReply));
    assert_eq!(exit.disposition, CloseDisposition::Ready);
}

#[test]
fn another_reply_kind_cannot_answer_an_exact_request_even_with_its_matching_id() {
    let controls = Controls {
        wrong_reply_kind: true,
        ..Controls::default()
    };
    let (handle, worker) = spawn(&controls);
    assert_eq!(
        handle
            .exchange(query(Query::Hello(EmptyInput {}), 100))
            .unwrap()
            .recv_timeout(WAIT)
            .unwrap(),
        Err(HostFailure::InvalidReply)
    );
    assert_eq!(
        worker.join().unwrap().unwrap().failure,
        Some(HostFailure::InvalidReply)
    );
}

#[test]
fn validly_framed_events_cannot_advance_beyond_actual_owner_watermark() {
    let controls = Controls {
        future_event: true,
        ..Controls::default()
    };
    let (handle, worker) = spawn(&controls);
    let observation = handle.subscribe(None).unwrap();
    assert_eq!(
        observation.ready_timeout(WAIT).unwrap(),
        Err(ObservationFailure::HostFailed(
            HostFailure::InvalidObservation
        ))
    );
    assert_eq!(
        worker.join().unwrap().unwrap().failure,
        Some(HostFailure::InvalidObservation)
    );
    assert_eq!(controls.audit.lock().unwrap().captures, 0);
}

#[test]
fn unimplemented_application_services_reject_and_do_not_bootstrap_synthetic_observations() {
    let controls = Controls::default();
    let (handle, worker) = spawn(&controls);
    let response = exchange(&handle, query(Query::ListInstallations(EmptyInput {}), 100));
    assert!(
        matches!(response.body, ReplyBody::Rejected { error } if error.code == ErrorCode::UnsupportedCapability)
    );
    let snapshot = exchange(&handle, query(Query::Snapshot(EmptyInput {}), 101));
    assert!(
        matches!(snapshot.body, ReplyBody::Result { result: ResultPayload::Query { query } } if matches!(&*query,
        QueryResult::Snapshot(Snapshot { installations: bridge_contracts::v1::Observation::Unavailable { reason: ObservationReason::NativeUnavailable }, .. })))
    );
    assert_eq!(controls.audit.lock().unwrap().captures, 0);
    drop(handle);
    worker.join().unwrap().unwrap();
}

#[test]
fn ready_subscription_delivers_actual_consecutive_kernel_events_before_snapshot_watermark() {
    let controls = Controls::default();
    let (handle, worker) = spawn(&controls);
    let subscription = handle.subscribe(None).unwrap();
    let ready = subscription.ready_timeout(WAIT).unwrap().unwrap();
    let input = commit_input(prepare(&handle, 100));
    let admitted = committed(exchange(&handle, command(Command::Commit(input), 101)));
    let mut cursor = ready;
    let mut received = 0;
    let deadline = Instant::now() + WAIT;
    while received < 3 {
        match subscription.try_recv() {
            Ok(HostObservation::Event(frame)) => {
                let event = decode_event(frame.as_bytes()).unwrap().into_inner();
                assert_eq!(event.cursor.host_epoch, cursor.host_epoch);
                assert_eq!(event.cursor.stream_id, cursor.stream_id);
                assert_eq!(event.cursor.sequence.get(), cursor.sequence.get() + 1);
                assert!(
                    matches!(&event.body, EventBody::OperationChanged { operation } if operation.operation_id == admitted.operation_id && operation.semantics == admitted.semantics)
                );
                cursor = event.cursor;
                received += 1;
            }
            Ok(HostObservation::Fault(fault)) => {
                panic!("unexpected source observation fault {fault:?}")
            }
            Err(_) => {
                assert!(Instant::now() < deadline);
                thread::sleep(Duration::from_millis(1));
            }
        }
    }
    let snapshot = exchange(&handle, query(Query::Snapshot(EmptyInput {}), 103));
    assert!(
        matches!(snapshot.body, ReplyBody::Result { result: ResultPayload::Query { query } } if matches!(*query,
        QueryResult::Snapshot(ref snapshot) if snapshot.cursor.host_epoch == cursor.host_epoch && snapshot.cursor.stream_id == cursor.stream_id
            && snapshot.cursor.sequence >= cursor.sequence && snapshot.operations.items.as_slice().iter().any(|operation| operation.operation_id == admitted.operation_id)))
    );
    controls.release.store(true, Ordering::Release);
    drop(subscription);
    drop(handle);
    worker.join().unwrap().unwrap();
}
