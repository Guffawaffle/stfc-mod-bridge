//! Development source proof: actual local runner/kernel with deliberately
//! synthetic canonical owners. No native service, game, account or store proof.
use bridge_contracts::v1::*;
use bridge_engine::services::ApplicationServices;
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
impl ApplicationServices for Owner {}
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
    fn now(&self) -> Result<ClockReading, ProviderFailure> {
        Ok(ClockReading {
            monotonic_millis: 1000,
            utc: UtcTimestamp::new("2026-10-03T12:00:00Z").unwrap(),
        })
    }
    fn deadline(
        &self,
        sampled: &ClockReading,
        duration: u64,
    ) -> Result<ClockReading, ProviderFailure> {
        Ok(ClockReading {
            monotonic_millis: sampled
                .monotonic_millis
                .checked_add(duration)
                .ok_or(ProviderFailure::DeadlineOverflow)?,
            utc: UtcTimestamp::new("2026-10-03T12:01:00Z").unwrap(),
        })
    }
}
struct Ids(u64);
impl IdentitySource for Ids {
    fn plan_id(&mut self) -> Result<PlanId, ProviderFailure> {
        self.0 += 1;
        Ok(PlanId::new(uuid(self.0)).unwrap())
    }
    fn operation_id(&mut self) -> Result<OperationId, ProviderFailure> {
        self.0 += 1;
        Ok(OperationId::new(uuid(self.0)).unwrap())
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

#[test]
fn embedded_kernel_admission_loss_and_deferred_close_keep_real_dispatcher_custody() {
    let controls = Controls::default();
    let actor_controls = controls.clone();
    let (handle, inbox) = owner_channel();
    let owner =
        EmbeddedOwner::on_current_thread(inbox, || Ok(FixtureHost::new(actor_controls))).unwrap();
    let pending = handle
        .exchange(command(
            Command::Prepare(Box::new(PrepareInput {
                intent: preferences(),
            })),
            701,
        ))
        .unwrap();
    owner.turn(|_| Ok(DriveControl::Continue));
    let ReplyBody::Result {
        result: ResultPayload::Command { command: output },
    } = reply(&pending).body
    else {
        panic!("embedded preparation failed")
    };
    let CommandResult::Prepare(plan) = *output else {
        panic!("wrong embedded preparation result")
    };
    let input = commit_input(plan);
    // Admitted work remains with the actual kernel after its reply observer is
    // dropped. The synthetic owner still requires external progression consent.
    drop(
        handle
            .exchange(command(Command::Commit(input.clone()), 702))
            .unwrap(),
    );
    owner.turn(|_| Ok(DriveControl::Continue));
    assert_eq!(controls.audit.lock().unwrap().captures, 1);
    assert_eq!(controls.audit.lock().unwrap().mutations, 0);
    owner.signal_close();
    for _ in 0..3 {
        assert!(matches!(
            owner.turn(|_| Ok(DriveControl::Continue)),
            EmbeddedTurn::Retained { closing: true, .. }
        ));
        assert_eq!(controls.audit.lock().unwrap().lease_drops, 0);
        // A real shell must service a GUI turn here; this test only proves that
        // the portable wrapper returns while the real kernel keeps its lease.
    }
    // Exact replay under close retains the originally admitted operation; it
    // cannot recapture a plan or execute another mutation.
    let replay = handle
        .exchange(command(Command::Commit(input), 703))
        .unwrap();
    owner.turn(|_| Ok(DriveControl::Continue));
    committed(reply(&replay));
    assert_eq!(controls.audit.lock().unwrap().captures, 1);
    controls.release.store(true, Ordering::Release);
    let mut terminal = None;
    for _ in 0..10 {
        if let EmbeddedTurn::Closed(exit) = owner.turn(|_| Ok(DriveControl::Continue)) {
            terminal = Some(exit);
            break;
        }
    }
    assert_eq!(terminal.unwrap().disposition, CloseDisposition::Ready);
    let audit = controls.audit.lock().unwrap();
    assert_eq!(audit.mutations, 1);
    assert_eq!(audit.lease_drops, 1);
    assert!(audit.dropped.iter().all(|id| *id == thread::current().id()));
}

/// A controlled scheduling seam, not an alternate close/admission policy.
/// Every protocol, cursor and close method delegates to the actual LocalHost.
/// Pausing progression lets a real Snapshot cursor remain current until the
/// following versioned close is dispatched. No snapshot/cursor is fabricated.
struct EmbeddedSupplementHost<H: LocalHost> {
    inner: H,
    pause_progress: Arc<AtomicBool>,
    entries: Arc<AtomicUsize>,
}
impl<H: LocalHost> LocalHost for EmbeddedSupplementHost<H> {
    fn dispatch(&mut self, request: ValidatedRequest) -> Result<ValidatedReply, HostFailure> {
        self.entries.fetch_add(1, Ordering::AcqRel);
        self.inner.dispatch(request)
    }
    fn cursor(&self) -> Cursor {
        self.entries.fetch_add(1, Ordering::AcqRel);
        self.inner.cursor()
    }
    fn pump(&mut self) -> Result<(), HostFailure> {
        self.entries.fetch_add(1, Ordering::AcqRel);
        if self.pause_progress.load(Ordering::Acquire) {
            Ok(())
        } else {
            self.inner.pump()
        }
    }
    fn events_after(&mut self, after: &Cursor) -> Result<EventBatch, HostFailure> {
        self.entries.fetch_add(1, Ordering::AcqRel);
        self.inner.events_after(after)
    }
    fn request_close(&mut self) -> Result<CloseDisposition, HostFailure> {
        self.entries.fetch_add(1, Ordering::AcqRel);
        self.inner.request_close()
    }
    fn close_disposition(&self) -> Result<CloseDisposition, HostFailure> {
        self.entries.fetch_add(1, Ordering::AcqRel);
        self.inner.close_disposition()
    }
}

fn embedded_supplement_setup(
    controls: &Controls,
) -> (
    HostHandle,
    EmbeddedOwner<EmbeddedSupplementHost<FixtureHost>>,
    Arc<AtomicBool>,
    Arc<AtomicUsize>,
) {
    assert!(
        controls.root.is_none(),
        "supplement must remain an in-memory fixture"
    );
    let actor_controls = controls.clone();
    let pause = Arc::new(AtomicBool::new(false));
    let actor_pause = pause.clone();
    let entries = Arc::new(AtomicUsize::new(0));
    let actor_entries = entries.clone();
    let (handle, inbox) = owner_channel();
    let owner = EmbeddedOwner::on_current_thread(inbox, || {
        Ok(EmbeddedSupplementHost {
            inner: FixtureHost::new(actor_controls),
            pause_progress: actor_pause,
            entries: actor_entries,
        })
    })
    .unwrap();
    (handle, owner, pause, entries)
}

fn embedded_supplement_reply(pending: &PendingReply) -> Reply {
    // A missing one-turn reply fails immediately instead of blocking the owner.
    let frame = pending
        .try_recv()
        .expect("one bounded turn must dispatch the request")
        .expect("fixture dispatch must return a valid protocol reply");
    decode_reply(frame.as_bytes()).unwrap().into_inner()
}

fn embedded_supplement_snapshot(reply: Reply) -> Snapshot {
    let ReplyBody::Result {
        result: ResultPayload::Query { query },
    } = reply.body
    else {
        panic!("actual kernel Snapshot was rejected")
    };
    let QueryResult::Snapshot(snapshot) = *query else {
        panic!("wrong actual kernel Snapshot result")
    };
    snapshot
}

fn embedded_supplement_close(reply: Reply) -> CloseDisposition {
    let ReplyBody::Result {
        result: ResultPayload::Command { command },
    } = reply.body
    else {
        panic!("current-cursor versioned close was rejected")
    };
    let CommandResult::RequestHostClose(close) = *command else {
        panic!("wrong versioned close result")
    };
    close
}

fn embedded_supplement_admit<H: LocalHost + 'static>(
    owner: &EmbeddedOwner<H>,
    handle: &HostHandle,
    controls: &Controls,
    request_number: u64,
) -> OperationSnapshot {
    let before = controls.ticks.load(Ordering::Acquire);
    let prepared = handle
        .exchange(command(
            Command::Prepare(Box::new(PrepareInput {
                intent: preferences(),
            })),
            request_number,
        ))
        .unwrap();
    assert_eq!(
        owner.turn(|pump| {
            pump.tick();
            pump.tick();
            Ok(DriveControl::Continue)
        }),
        EmbeddedTurn::Retained {
            closing: false,
            failure: None
        }
    );
    assert_eq!(controls.ticks.load(Ordering::Acquire), before);
    let ReplyBody::Result {
        result: ResultPayload::Command { command: output },
    } = embedded_supplement_reply(&prepared).body
    else {
        panic!("real preparation failed")
    };
    let CommandResult::Prepare(plan) = *output else {
        panic!("wrong real preparation result")
    };
    let committed_reply = handle
        .exchange(command(
            Command::Commit(commit_input(plan)),
            request_number + 1,
        ))
        .unwrap();
    assert_eq!(
        owner.turn(|pump| {
            pump.tick();
            pump.tick();
            Ok(DriveControl::Continue)
        }),
        EmbeddedTurn::Retained {
            closing: false,
            failure: None
        }
    );
    // The committed reply precedes independent progression in the same turn.
    let admitted = committed(embedded_supplement_reply(&committed_reply));
    assert!(matches!(admitted.state, OperationState::Admitted));
    assert_eq!(controls.ticks.load(Ordering::Acquire), before + 1);
    let audit = controls.audit.lock().unwrap();
    assert_eq!(audit.captures, 1);
    assert_eq!(audit.mutations, 0);
    assert_eq!(audit.lease_drops, 0);
    admitted
}

fn embedded_supplement_assert_safe_drop(controls: &Controls, mutations: usize) {
    let audit = controls.audit.lock().unwrap();
    let thread = thread::current().id();
    assert_eq!(audit.constructed, vec![thread]);
    assert!(audit.invoked.iter().all(|id| *id == thread));
    assert_eq!(audit.mutations, mutations);
    assert_eq!(audit.lease_drops, 1);
    // One retained lease and its Owner are each destroyed on the owner thread.
    assert_eq!(audit.dropped.len(), 2);
    assert!(audit.dropped.iter().all(|id| *id == thread));
}

/// Count recovery entry without changing the existing fixture's native result.
/// A zero counter falsifies accidental automatic recovery during shutdown.
struct EmbeddedSupplementPorts {
    inner: Owner,
    recover_calls: Arc<AtomicUsize>,
}
impl ApplicationServices for EmbeddedSupplementPorts {}
impl OperationPorts for EmbeddedSupplementPorts {
    type Lease = LocalLease;
    fn capture(
        &mut self,
        intent: &MutationIntent,
        epoch: &HostEpoch,
    ) -> Result<CapturedOperation, Box<BridgeError>> {
        self.inner.capture(intent, epoch)
    }
    fn acquire(&mut self, keys: &[ResourceKey]) -> Result<Self::Lease, Box<BridgeError>> {
        self.inner.acquire(keys)
    }
    fn revalidate(
        &mut self,
        semantics: &PlanSemantics,
        lease: &Self::Lease,
    ) -> Result<(), Box<BridgeError>> {
        self.inner.revalidate(semantics, lease)
    }
    fn recovery_binding(
        &mut self,
        operation: &OperationId,
        semantics: &PlanSemantics,
        lease: &Self::Lease,
    ) -> Result<RecoveryRef, Box<BridgeError>> {
        self.inner.recovery_binding(operation, semantics, lease)
    }
    fn advance(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        lease: &Self::Lease,
        cancellation_requested: bool,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        self.inner
            .advance(operation, recovery, lease, cancellation_requested)
    }
    fn recover(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        lease: &Self::Lease,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        self.recover_calls.fetch_add(1, Ordering::AcqRel);
        self.inner.recover(operation, recovery, lease)
    }
    fn handoff_session(
        &mut self,
        session: &SessionBinding,
        lease: &Self::Lease,
    ) -> Result<bool, Box<BridgeError>> {
        self.inner.handoff_session(session, lease)
    }
}

fn embedded_supplement_kernel<J: DurableJournal>(
    controls: Controls,
    journal: J,
    recover_calls: Arc<AtomicUsize>,
) -> KernelHost<EmbeddedSupplementPorts, J, Clock, Ids> {
    assert!(
        controls.root.is_none(),
        "supplement must not provision a disk fixture"
    );
    let epoch = controls.epoch as u64;
    controls
        .audit
        .lock()
        .unwrap()
        .constructed
        .push(thread::current().id());
    let engine = Engine::open(
        EmbeddedSupplementPorts {
            inner: Owner {
                controls,
                _local: Rc::new(()),
            },
            recover_calls,
        },
        journal,
        Clock,
        Ids(10),
        HostConfiguration {
            epoch: HostEpoch::new(uuid(1 + epoch * 2)).unwrap(),
            stream: StreamId::new(uuid(2 + epoch * 2)).unwrap(),
            kind: HostKind::WindowsX64,
            preparation_lifetime_millis: 60000,
        },
    )
    .unwrap();
    KernelHost::new(engine)
}

#[test]
fn embedded_versioned_close_uses_real_snapshot_and_retires_driver_without_releasing_worker() {
    let controls = Controls::default();
    let (handle, owner, pause, entries) = embedded_supplement_setup(&controls);
    let admitted = embedded_supplement_admit(&owner, &handle, &controls, 10001);
    let drives = std::cell::Cell::new(0);

    // Negative control: Snapshot is authoritative when sampled, but the normal
    // following pump advances the real kernel cursor before a later request.
    let snapshot_reply = handle
        .exchange(query(Query::Snapshot(EmptyInput {}), 10003))
        .unwrap();
    assert_eq!(
        owner.turn(|pump| {
            drives.set(drives.get() + 1);
            pump.tick();
            pump.tick();
            Ok(DriveControl::Continue)
        }),
        EmbeddedTurn::Retained {
            closing: false,
            failure: None
        }
    );
    let stale = embedded_supplement_snapshot(embedded_supplement_reply(&snapshot_reply));
    let stale_close = handle
        .exchange(command(
            Command::RequestHostClose(RequestHostCloseInput {
                expected_cursor: stale.cursor.clone(),
            }),
            10004,
        ))
        .unwrap();
    assert_eq!(
        owner.turn(|pump| {
            drives.set(drives.get() + 1);
            pump.tick();
            pump.tick();
            Ok(DriveControl::Continue)
        }),
        EmbeddedTurn::Retained {
            closing: false,
            failure: None
        }
    );
    let ReplyBody::Rejected { error } = embedded_supplement_reply(&stale_close).body else {
        panic!("progressed cursor must refuse stale versioned close")
    };
    assert_eq!(error.code, ErrorCode::ResnapshotRequired);
    assert_eq!(drives.get(), 2, "rejected close must not retire the driver");
    assert_eq!(controls.audit.lock().unwrap().lease_drops, 0);

    // Pause only fixture progression for the actual snapshot/close exchange.
    pause.store(true, Ordering::Release);
    let before = controls.ticks.load(Ordering::Acquire);
    let snapshot_reply = handle
        .exchange(query(Query::Snapshot(EmptyInput {}), 10005))
        .unwrap();
    assert_eq!(
        owner.turn(|pump| {
            drives.set(drives.get() + 1);
            pump.tick();
            Ok(DriveControl::Continue)
        }),
        EmbeddedTurn::Retained {
            closing: false,
            failure: None
        }
    );
    let current = embedded_supplement_snapshot(embedded_supplement_reply(&snapshot_reply));
    assert_ne!(current.cursor, stale.cursor);
    let operation = current
        .operations
        .items
        .as_slice()
        .iter()
        .find(|op| op.operation_id == admitted.operation_id)
        .unwrap();
    assert!(matches!(operation.state, OperationState::Running { .. }));
    let close_reply = handle
        .exchange(command(
            Command::RequestHostClose(RequestHostCloseInput {
                expected_cursor: current.cursor.clone(),
            }),
            10006,
        ))
        .unwrap();
    assert_eq!(
        owner.turn(|pump| {
            drives.set(drives.get() + 1);
            pump.tick();
            pump.tick();
            Ok(DriveControl::Continue)
        }),
        EmbeddedTurn::Retained {
            closing: true,
            failure: None
        }
    );
    let CloseDisposition::Deferred { obligations } =
        embedded_supplement_close(embedded_supplement_reply(&close_reply))
    else {
        panic!("real admitted writer must defer versioned close")
    };
    assert_eq!(
        obligations.as_slice(),
        &[CloseObligation::Operation {
            operation_id: admitted.operation_id.clone(),
            operation_revision: operation.operation_revision,
        }]
    );
    assert_eq!(controls.ticks.load(Ordering::Acquire), before);
    assert_eq!(drives.get(), 4);

    pause.store(false, Ordering::Release);
    let external_services = std::cell::Cell::new(0);
    for completed_services in 0..3 {
        let before = controls.ticks.load(Ordering::Acquire);
        assert_eq!(
            owner.turn(|_| {
                drives.set(drives.get() + 1);
                panic!("observed versioned close permanently retires driver")
            }),
            EmbeddedTurn::Retained {
                closing: true,
                failure: None
            }
        );
        assert_eq!(drives.get(), 4);
        assert_eq!(controls.ticks.load(Ordering::Acquire), before + 1);
        assert_eq!(controls.audit.lock().unwrap().lease_drops, 0);
        assert_eq!(external_services.get(), completed_services);
        // Simulated caller service is possible only after turn() returned.
        // This counter does not qualify an actual GUI or native run loop.
        external_services.set(completed_services + 1);
    }
    controls.release.store(true, Ordering::Release);
    let EmbeddedTurn::Closed(exit) = owner.turn(|_| panic!("retired driver stays retired")) else {
        panic!("real terminal persistence permits owner-thread destruction")
    };
    assert_eq!(exit.disposition, CloseDisposition::Ready);
    assert_eq!(exit.failure, None);
    embedded_supplement_assert_safe_drop(&controls, 1);
    let terminal_entries = entries.load(Ordering::Acquire);
    assert_eq!(
        owner.turn(|_| panic!("terminal owner cannot run a driver")),
        EmbeddedTurn::Closed(exit)
    );
    assert_eq!(entries.load(Ordering::Acquire), terminal_entries);
    embedded_supplement_assert_safe_drop(&controls, 1);
}

#[test]
fn embedded_driver_error_after_admission_drains_real_kernel_work_to_safe_close() {
    let controls = Controls::default();
    let (handle, owner, _pause, _entries) = embedded_supplement_setup(&controls);
    embedded_supplement_admit(&owner, &handle, &controls, 10101);
    let before = controls.ticks.load(Ordering::Acquire);
    assert_eq!(
        owner.turn(|pump| {
            pump.tick();
            pump.tick();
            Err(HostFailure::UnavailableService)
        }),
        EmbeddedTurn::Retained {
            closing: true,
            failure: Some(HostFailure::UnavailableService)
        }
    );
    assert_eq!(controls.ticks.load(Ordering::Acquire), before + 1);
    for _ in 0..3 {
        let before = controls.ticks.load(Ordering::Acquire);
        assert_eq!(
            owner.turn(|_| panic!("errored driver must remain retired")),
            EmbeddedTurn::Retained {
                closing: true,
                failure: Some(HostFailure::UnavailableService)
            }
        );
        assert_eq!(controls.ticks.load(Ordering::Acquire), before + 1);
        assert_eq!(controls.audit.lock().unwrap().lease_drops, 0);
    }
    controls.release.store(true, Ordering::Release);
    let EmbeddedTurn::Closed(exit) = owner.turn(|_| panic!("error does not revive driver")) else {
        panic!("recoverable driver error must permit actual safe closure")
    };
    assert_eq!(exit.disposition, CloseDisposition::Ready);
    assert_eq!(exit.failure, Some(HostFailure::UnavailableService));
    embedded_supplement_assert_safe_drop(&controls, 1);
}

#[test]
fn embedded_driver_panic_after_real_admission_taints_custody_even_after_release() {
    let controls = Controls::default();
    let (handle, owner, _pause, entries) = embedded_supplement_setup(&controls);
    embedded_supplement_admit(&owner, &handle, &controls, 10201);
    let drives = std::cell::Cell::new(0);
    let before = controls.ticks.load(Ordering::Acquire);
    assert!(matches!(
        owner.turn(|pump| {
            drives.set(drives.get() + 1);
            pump.tick();
            panic!("controlled driver panic after real durable admission and progression")
        }),
        EmbeddedTurn::Retained {
            failure: Some(HostFailure::DriverPanicked),
            ..
        }
    ));
    assert_eq!(controls.ticks.load(Ordering::Acquire), before + 1);
    let tainted_entries = entries.load(Ordering::Acquire);
    let tainted_ticks = controls.ticks.load(Ordering::Acquire);
    controls.release.store(true, Ordering::Release);
    owner.signal_close();
    for _ in 0..3 {
        assert!(matches!(
            owner.turn(|_| {
                drives.set(drives.get() + 1);
                panic!("permanently tainted owner cannot revive driver")
            }),
            EmbeddedTurn::Retained {
                failure: Some(HostFailure::DriverPanicked),
                ..
            }
        ));
        assert_eq!(drives.get(), 1);
        assert_eq!(entries.load(Ordering::Acquire), tainted_entries);
        assert_eq!(controls.ticks.load(Ordering::Acquire), tainted_ticks);
        let audit = controls.audit.lock().unwrap();
        assert_eq!(audit.mutations, 0);
        assert_eq!(audit.lease_drops, 0);
        assert!(audit.dropped.is_empty());
    }
    drop(handle);
    drop(owner);
    // Deliberate bounded fixture leak: abandonment preserves the owned lease,
    // but does not keep servicing it or prove native call-local panic custody.
    let audit = controls.audit.lock().unwrap();
    assert_eq!(audit.mutations, 0);
    assert_eq!(audit.lease_drops, 0);
    assert!(audit.dropped.is_empty());
}

#[test]
fn embedded_real_kernel_safe_recovery_closes_without_running_recovery_or_mutation() {
    let controls = Controls::default();
    let actor_controls = controls.clone();
    let recover_calls = Arc::new(AtomicUsize::new(0));
    let actor_recover_calls = recover_calls.clone();
    let entries = Arc::new(AtomicUsize::new(0));
    let actor_entries = entries.clone();
    let (handle, inbox) = owner_channel();
    let owner = EmbeddedOwner::on_current_thread(inbox, || {
        Ok(EmbeddedSupplementHost {
            inner: embedded_supplement_kernel(
                actor_controls,
                Journal {
                    rows: vec![],
                    _local: Rc::new(()),
                    disk: None,
                },
                actor_recover_calls,
            ),
            pause_progress: Arc::new(AtomicBool::new(false)),
            entries: actor_entries,
        })
    })
    .unwrap();
    let admitted = embedded_supplement_admit(&owner, &handle, &controls, 10301);
    controls.safe_recovery.store(true, Ordering::Release);
    owner.signal_close();
    let EmbeddedTurn::Closed(exit) = owner.turn(|_| panic!("external close retires driver")) else {
        panic!("actual persisted safe recovery boundary must permit closure")
    };
    let CloseDisposition::RecoveryRequired { recoveries } = &exit.disposition else {
        panic!("safe recovery is not successful completion or Ready")
    };
    assert_eq!(recoveries.as_slice().len(), 1);
    assert_eq!(recoveries.as_slice()[0].operation_id, admitted.operation_id);
    assert_eq!(exit.failure, None);
    assert_eq!(recover_calls.load(Ordering::Acquire), 0);
    embedded_supplement_assert_safe_drop(&controls, 0);
    let terminal_entries = entries.load(Ordering::Acquire);
    let terminal_ticks = controls.ticks.load(Ordering::Acquire);
    assert_eq!(
        owner.turn(|_| panic!("terminal recovery does not auto-recover")),
        EmbeddedTurn::Closed(exit)
    );
    assert_eq!(entries.load(Ordering::Acquire), terminal_entries);
    assert_eq!(controls.ticks.load(Ordering::Acquire), terminal_ticks);
    assert_eq!(recover_calls.load(Ordering::Acquire), 0);
    embedded_supplement_assert_safe_drop(&controls, 0);
}

/// Mirrors operation_kernel.rs terminal-persistence failure at the journal port,
/// using its in-memory Journal seam instead of creating a FileJournal fixture.
/// Accepted records are observed separately; they prove ordering, not durability.
struct EmbeddedSupplementFaultJournal {
    inner: Journal,
    fail_append: Arc<AtomicBool>,
    accepted: Arc<Mutex<Vec<JournalRecord>>>,
    attempts: Arc<AtomicUsize>,
}
impl DurableJournal for EmbeddedSupplementFaultJournal {
    fn records(&self) -> &[JournalRecord] {
        self.inner.records()
    }
    fn append(&mut self, record: &JournalRecord) -> Result<(), KernelFailure> {
        self.attempts.fetch_add(1, Ordering::AcqRel);
        if self.fail_append.load(Ordering::Acquire) {
            return Err(KernelFailure::Storage);
        }
        self.inner.append(record)?;
        self.accepted.lock().unwrap().push(record.clone());
        Ok(())
    }
}

#[test]
fn embedded_real_kernel_terminal_journal_failure_retains_lease_and_poison_after_fault_clear() {
    let controls = Controls::default();
    assert!(controls.root.is_none());
    let actor_controls = controls.clone();
    let fail_append = Arc::new(AtomicBool::new(false));
    let actor_fail = fail_append.clone();
    let accepted = Arc::new(Mutex::new(Vec::new()));
    let actor_accepted = accepted.clone();
    let attempts = Arc::new(AtomicUsize::new(0));
    let actor_attempts = attempts.clone();
    let entries = Arc::new(AtomicUsize::new(0));
    let actor_entries = entries.clone();
    let recover_calls = Arc::new(AtomicUsize::new(0));
    let actor_recover_calls = recover_calls.clone();
    let (handle, inbox) = owner_channel();
    let owner = EmbeddedOwner::on_current_thread(inbox, || {
        Ok(EmbeddedSupplementHost {
            inner: embedded_supplement_kernel(
                actor_controls,
                EmbeddedSupplementFaultJournal {
                    inner: Journal {
                        rows: vec![],
                        _local: Rc::new(()),
                        disk: None,
                    },
                    fail_append: actor_fail,
                    accepted: actor_accepted,
                    attempts: actor_attempts,
                },
                actor_recover_calls,
            ),
            pause_progress: Arc::new(AtomicBool::new(false)),
            entries: actor_entries,
        })
    })
    .unwrap();
    let admitted = embedded_supplement_admit(&owner, &handle, &controls, 10401);
    let rows_before = accepted.lock().unwrap().len();
    let attempts_before = attempts.load(Ordering::Acquire);
    let ticks_before = controls.ticks.load(Ordering::Acquire);
    fail_append.store(true, Ordering::Release);
    controls.release.store(true, Ordering::Release);
    assert_eq!(
        owner.turn(|pump| {
            pump.tick();
            pump.tick();
            Ok(DriveControl::Continue)
        }),
        EmbeddedTurn::Retained {
            closing: true,
            failure: Some(HostFailure::Kernel(KernelFailure::Storage))
        }
    );
    assert_eq!(controls.ticks.load(Ordering::Acquire), ticks_before + 1);
    assert_eq!(controls.audit.lock().unwrap().mutations, 1);
    assert_eq!(controls.audit.lock().unwrap().lease_drops, 0);
    assert_eq!(accepted.lock().unwrap().len(), rows_before);
    assert_eq!(attempts.load(Ordering::Acquire), attempts_before + 1);
    let rows = accepted.lock().unwrap();
    let last_operation = rows
        .iter()
        .rev()
        .find_map(|row| match row {
            JournalRecord::Operation { value }
                if value.snapshot.operation_id == admitted.operation_id =>
            {
                Some(value)
            }
            _ => None,
        })
        .unwrap();
    assert_eq!(last_operation.phase, DurablePhase::Executing);
    assert!(matches!(
        last_operation.snapshot.state,
        OperationState::Running { .. }
    ));
    drop(rows);

    // A healed port is not recovery authority: Engine::advance remains poisoned.
    fail_append.store(false, Ordering::Release);
    owner.signal_close();
    let retained_entries = entries.load(Ordering::Acquire);
    for _ in 0..3 {
        assert_eq!(
            owner.turn(|_| panic!("kernel failure retires driver")),
            EmbeddedTurn::Retained {
                closing: true,
                failure: Some(HostFailure::Kernel(KernelFailure::Storage))
            }
        );
        assert_eq!(controls.ticks.load(Ordering::Acquire), ticks_before + 1);
        assert_eq!(attempts.load(Ordering::Acquire), attempts_before + 1);
        assert_eq!(accepted.lock().unwrap().len(), rows_before);
        let audit = controls.audit.lock().unwrap();
        assert_eq!(audit.mutations, 1);
        assert_eq!(audit.lease_drops, 0);
        assert!(audit.dropped.is_empty());
    }
    // Unlike panic taint, known kernel failure permits safe owner observations,
    // but cannot advance the effect or publish a safe destruction disposition.
    assert!(entries.load(Ordering::Acquire) > retained_entries);
    assert_eq!(recover_calls.load(Ordering::Acquire), 0);
    drop(handle);
    drop(owner);
    let audit = controls.audit.lock().unwrap();
    assert_eq!(audit.mutations, 1);
    assert_eq!(audit.lease_drops, 0);
    assert!(audit.dropped.is_empty());
}
