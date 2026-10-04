//! SC-14/SC-15 engine proof with synthetic native owners and actual persistent
//! filesystem state. These fixtures exercise no game, account or platform API.
use bridge_contracts::v1::*;
use bridge_engine::operations::*;
use std::{
    collections::BTreeMap,
    fs::{File, OpenOptions},
    io::Write,
    path::{Path, PathBuf},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU64, Ordering},
    },
    time::{SystemTime, UNIX_EPOCH},
};

type FixtureEngine = Engine<Owner, FileJournal, Clock, Ids>;
static NEXT: AtomicU64 = AtomicU64::new(1);

fn uuid(n: u64) -> String {
    format!("00000000-0000-4000-8000-{n:012x}")
}
fn target() -> ResolvedTarget {
    ResolvedTarget {
        installation: InstallationBinding::Registered {
            registration_id: InstallationId::new("00000000000000000000000000000001").unwrap(),
            registration_revision: OpaqueRevision::new("registration:1").unwrap(),
            physical_id: PhysicalInstallationId::new("fixture-installation:1").unwrap(),
            native_target_ref: NativeTargetRef::new("fixture-owner-target:1").unwrap(),
        },
        profile: ProfileBinding::Ordinary {
            ordinary_id: None,
            owner_scope: OwnerScope::new("fixture-user:1").unwrap(),
        },
    }
}
fn intent() -> MutationIntent {
    MutationIntent::LaunchOrdinary(OrdinaryLaunchInput {
        target: OrdinaryTargetSelector {
            installation: InstallationSelector::Registered {
                id: InstallationId::new("00000000000000000000000000000001").unwrap(),
                directory_assertion: None,
                revision_assertion: Some(OpaqueRevision::new("registration:1").unwrap()),
            },
            profile: OrdinaryProfileSelector::Ordinary {
                catalog_id_assertion: None,
            },
        },
        unrecognized_runtime_choice: UnrecognizedRuntimeChoice::Reject,
    })
}
fn directory_selector() -> InstallationSelector {
    InstallationSelector::Directory {
        directory: NativeAbsolutePath::Windows(
            WindowsAbsolutePath::new("D:\\Synthetic\\Game").unwrap(),
        ),
    }
}
fn directory_binding(binding: &InstallationBinding) -> InstallationBinding {
    let InstallationBinding::Registered {
        physical_id,
        native_target_ref,
        ..
    } = binding
    else {
        panic!("fixture capture must start with its registered binding");
    };
    InstallationBinding::Directory {
        physical_id: physical_id.clone(),
        native_target_ref: native_target_ref.clone(),
    }
}
fn semantics() -> PlanSemantics {
    PlanSemantics {
        hash_profile: HashProfile::BridgePlanSemanticJsonV1,
        action: ActionId::LaunchOrdinary,
        capture: PreparedCapture::LaunchOrdinary {
            target: target(),
            catalog_revision: OpaqueRevision::new("catalog:1").unwrap(),
            runtime: RuntimeExpectation::Absent,
            unrecognized_runtime_choice: UnrecognizedRuntimeChoice::Reject,
        },
        trust_domain: TrustDomain::Session,
        effects: BoundedList::new(vec![ProposedEffect::LaunchSession]).unwrap(),
    }
}
fn resources() -> Vec<ResourceKey> {
    vec![
        ResourceKey::Installation {
            physical_id: target().installation.physical_id().clone(),
        },
        ResourceKey::OrdinaryProfile {
            owner: OwnerScope::new("fixture-user:1").unwrap(),
        },
    ]
}
fn session() -> SessionBinding {
    SessionBinding {
        session_id: SessionId::new(uuid(800)).unwrap(),
        revision: OpaqueRevision::new("session:1").unwrap(),
        process: ProcessIdentity {
            pid: Pid::new(4242).unwrap(),
            start_identity: ProcessStartIdentity::Windows(
                ProcessGeneration::new("fixture-generation:1").unwrap(),
            ),
            executable_identity: ExecutableIdentity::new("fixture-executable:1").unwrap(),
            installation_physical_id: target().installation.physical_id().clone(),
            architecture: ProcessArchitecture::X86_64,
        },
    }
}
#[derive(Clone)]
struct Clock {
    now: Arc<AtomicU64>,
}
impl HostClock for Clock {
    fn now(&self) -> ClockReading {
        ClockReading {
            unix_millis: self.now.load(Ordering::SeqCst),
            utc: UtcTimestamp::new("2026-10-03T12:00:00Z").unwrap(),
        }
    }
    fn deadline(&self, lifetime_millis: u64) -> Result<ClockReading, Box<BridgeError>> {
        Ok(ClockReading {
            unix_millis: self.now.load(Ordering::SeqCst) + lifetime_millis,
            utc: UtcTimestamp::new("2026-10-03T12:01:00Z").unwrap(),
        })
    }
}
struct Ids {
    next: u64,
}
impl IdentitySource for Ids {
    fn plan_id(&mut self) -> PlanId {
        self.next += 1;
        PlanId::new(uuid(self.next)).unwrap()
    }
    fn operation_id(&mut self) -> OperationId {
        self.next += 1;
        OperationId::new(uuid(self.next)).unwrap()
    }
}
#[derive(Default, Clone, Debug, PartialEq, Eq)]
struct Counts {
    acquisition: usize,
    revalidation: usize,
    bindings: usize,
    download: usize,
    staging: usize,
    backup: usize,
    native_journal: usize,
    mutation: usize,
    recovery: usize,
    lease_drops: usize,
}
struct Lease {
    resources: Vec<ResourceKey>,
    _files: Vec<File>,
    counts: Arc<Mutex<Counts>>,
}
impl ResourceLease for Lease {
    fn resources(&self) -> &[ResourceKey] {
        &self.resources
    }
}
impl Drop for Lease {
    fn drop(&mut self) {
        self.counts.lock().unwrap().lease_drops += 1;
    }
}
#[derive(Clone)]
struct Owner {
    root: PathBuf,
    counts: Arc<Mutex<Counts>>,
    stale: Arc<AtomicBool>,
    cancellable: Arc<AtomicBool>,
    safe_recovery: Arc<AtomicBool>,
    recovery_rollback: Arc<AtomicBool>,
    session: Arc<AtomicBool>,
    handoff: Arc<AtomicBool>,
    wrong_binding: Arc<AtomicBool>,
    fail_native: Arc<AtomicBool>,
    repeat_progress: Arc<AtomicBool>,
    ignore_cancellation: Arc<AtomicBool>,
    include_session_receipt: Arc<AtomicBool>,
    wrong_session_custody: Arc<AtomicBool>,
    directory_capture: Arc<AtomicBool>,
}
fn durable_write(path: &Path, bytes: &[u8]) {
    let mut file = OpenOptions::new()
        .write(true)
        .create(true)
        .truncate(true)
        .open(path)
        .unwrap();
    file.write_all(bytes).unwrap();
    file.sync_all().unwrap();
}
impl Owner {
    fn new(root: &Path) -> Self {
        Self {
            root: root.to_owned(),
            counts: Arc::new(Mutex::new(Counts::default())),
            stale: Arc::new(AtomicBool::new(false)),
            cancellable: Arc::new(AtomicBool::new(true)),
            safe_recovery: Arc::new(AtomicBool::new(false)),
            recovery_rollback: Arc::new(AtomicBool::new(false)),
            session: Arc::new(AtomicBool::new(false)),
            handoff: Arc::new(AtomicBool::new(false)),
            wrong_binding: Arc::new(AtomicBool::new(false)),
            fail_native: Arc::new(AtomicBool::new(false)),
            repeat_progress: Arc::new(AtomicBool::new(false)),
            ignore_cancellation: Arc::new(AtomicBool::new(false)),
            include_session_receipt: Arc::new(AtomicBool::new(false)),
            wrong_session_custody: Arc::new(AtomicBool::new(false)),
            directory_capture: Arc::new(AtomicBool::new(false)),
        }
    }
    fn tx(&self, recovery: &RecoveryRef) -> PathBuf {
        self.root
            .join(format!("{}.native", recovery.operation_id.as_str()))
    }
    fn native_state(&self, recovery: &RecoveryRef) -> String {
        std::fs::read_to_string(self.tx(recovery)).unwrap_or_default()
    }
    fn completed(&self) -> TransactionStep {
        let receipt = self
            .include_session_receipt
            .load(Ordering::SeqCst)
            .then(|| {
                let evidence = Evidence {
                    observation_id: ObservationId::new(uuid(801)).unwrap(),
                    observed_at: UtcTimestamp::new("2026-10-03T12:00:00Z").unwrap(),
                    source: EvidenceSource::NativeLive,
                };
                Box::new(EffectReceipt::SessionSpawned {
                    session: Box::new(SessionProjection {
                        binding: session(),
                        target: Observation::Observed {
                            value: target(),
                            evidence: evidence.clone(),
                        },
                        live_identity: Observation::Observed {
                            value: true,
                            evidence: evidence.clone(),
                        },
                        readiness: Observation::Observed {
                            value: SessionReadiness::OrdinarySpawned,
                            evidence,
                        },
                    }),
                })
            });
        let custody = self.session.load(Ordering::SeqCst).then(|| {
            let mut session = session();
            if self.wrong_session_custody.load(Ordering::SeqCst) {
                session.session_id = SessionId::new(uuid(802)).unwrap();
                session.process.pid = Pid::new(5252).unwrap();
                session.process.start_identity = ProcessStartIdentity::Windows(
                    ProcessGeneration::new("different-generation:2").unwrap(),
                );
            }
            session
        });
        TransactionStep::Complete {
            outcome: CompletionOutcome::Changed {
                reason: CompletionReason::Applied,
                receipt,
            },
            session_custody: custody,
        }
    }
}
impl OperationPorts for Owner {
    type Lease = Lease;
    fn capture(
        &mut self,
        intent: &MutationIntent,
        _: &HostEpoch,
    ) -> Result<CapturedOperation, Box<BridgeError>> {
        if let MutationIntent::SaveApplicationPreferences(input) = intent {
            return Ok(CapturedOperation {
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
                    identity: ApplicationIdentity::new("fixture-application").unwrap(),
                }],
            });
        }
        let mut semantics = semantics();
        if self.directory_capture.load(Ordering::SeqCst) {
            let PreparedCapture::LaunchOrdinary { target, .. } = &mut semantics.capture else {
                unreachable!();
            };
            target.installation = directory_binding(&target.installation);
        }
        Ok(CapturedOperation {
            semantics,
            resources: resources(),
        })
    }
    fn acquire(&mut self, resources: &[ResourceKey]) -> Result<Lease, Box<BridgeError>> {
        self.counts.lock().unwrap().acquisition += 1;
        let mut files = Vec::new();
        for resource in resources {
            let name = match resource {
                ResourceKey::Installation { .. } => "installation.lease",
                ResourceKey::OrdinaryProfile { .. } => "profile.lease",
                ResourceKey::Application { .. } => "application.lease",
                _ => return Err(error(ErrorCode::UnsupportedCapability)),
            };
            let file = OpenOptions::new()
                .read(true)
                .write(true)
                .open(self.root.join(name))
                .unwrap();
            file.try_lock()
                .map_err(|_| error(ErrorCode::OperationBusy))?;
            files.push(file);
        }
        Ok(Lease {
            resources: resources.to_vec(),
            _files: files,
            counts: self.counts.clone(),
        })
    }
    fn revalidate(&mut self, _: &PlanSemantics, _: &Lease) -> Result<(), Box<BridgeError>> {
        self.counts.lock().unwrap().revalidation += 1;
        if self.stale.load(Ordering::SeqCst) {
            Err(error(ErrorCode::StaleRevision))
        } else {
            Ok(())
        }
    }
    fn recovery_binding(
        &mut self,
        operation: &OperationId,
        semantics: &PlanSemantics,
        _: &Lease,
    ) -> Result<RecoveryRef, Box<BridgeError>> {
        self.counts.lock().unwrap().bindings += 1;
        if let PreparedCapture::SaveApplicationPreferences { input } = &semantics.capture {
            return Ok(RecoveryRef {
                operation_id: operation.clone(),
                transaction: NativeTransactionRef::new(format!(
                    "fixture-owner:{}",
                    operation.as_str()
                ))
                .unwrap(),
                target: RecoveryTarget::ApplicationPreferences {
                    revision: input.expected_revision.clone(),
                },
            });
        }
        let mut captured = target();
        if self.wrong_binding.load(Ordering::SeqCst) {
            captured.installation = InstallationBinding::Directory {
                physical_id: PhysicalInstallationId::new("foreign-installation").unwrap(),
                native_target_ref: NativeTargetRef::new("foreign-owner").unwrap(),
            };
        }
        Ok(RecoveryRef {
            operation_id: operation.clone(),
            transaction: NativeTransactionRef::new(format!("fixture-owner:{}", operation.as_str()))
                .unwrap(),
            target: RecoveryTarget::Launch { target: captured },
        })
    }
    fn advance(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        _: &Lease,
        cancellation: bool,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        if let PreparedCapture::SaveApplicationPreferences { input } = &operation.semantics.capture
        {
            if self.native_state(recovery).is_empty() {
                durable_write(&self.tx(recovery), b"preferences-staging");
                durable_write(
                    &self.root.join("preferences.staged"),
                    &serde_json::to_vec(&input.values).unwrap(),
                );
                let mut counts = self.counts.lock().unwrap();
                counts.native_journal += 1;
                counts.staging += 1;
            }
            return Ok(TransactionStep::Progress {
                progress: Progress {
                    phase: PhaseId::new("preferences_staging").unwrap(),
                    measurement: Measurement::Unknown,
                },
                cancellable: true,
            });
        }
        if self.fail_native.load(Ordering::SeqCst) {
            return Err(error(ErrorCode::InternalFailure));
        }
        match self.native_state(recovery).as_str() {
            "" => {
                // A canonical fixture owner records intent before its own effects.
                durable_write(&self.tx(recovery), b"staging");
                let mut counts = self.counts.lock().unwrap();
                counts.native_journal += 1;
                let prior = std::fs::read(self.root.join("live.bytes")).unwrap();
                durable_write(&self.root.join("prior.backup"), &prior);
                counts.backup += 1;
                durable_write(&self.root.join("download.bytes"), b"reviewed-new-bytes");
                counts.download += 1;
                durable_write(&self.root.join("staged.bytes"), b"reviewed-new-bytes");
                counts.staging += 1;
                Ok(TransactionStep::Progress {
                    progress: Progress {
                        phase: PhaseId::new("staging").unwrap(),
                        measurement: Measurement::Unknown,
                    },
                    cancellable: self.cancellable.load(Ordering::SeqCst),
                })
            }
            "staging" if cancellation && !self.ignore_cancellation.load(Ordering::SeqCst) => {
                durable_write(&self.tx(recovery), b"rolled_back");
                Ok(TransactionStep::Complete {
                    outcome: CompletionOutcome::RolledBack {
                        reason: CompletionReason::RollbackCompleted,
                    },
                    session_custody: None,
                })
            }
            "staging" if self.repeat_progress.load(Ordering::SeqCst) => {
                Ok(TransactionStep::Progress {
                    progress: Progress {
                        phase: PhaseId::new("observed_native_work").unwrap(),
                        measurement: Measurement::Unknown,
                    },
                    cancellable: self.cancellable.load(Ordering::SeqCst),
                })
            }
            "staging" => {
                durable_write(&self.tx(recovery), b"publishing");
                durable_write(&self.root.join("live.bytes"), b"reviewed-new-bytes");
                self.counts.lock().unwrap().mutation += 1;
                durable_write(&self.tx(recovery), b"committed");
                Ok(self.completed())
            }
            "committed" => Ok(self.completed()),
            _ => Ok(TransactionStep::RecoveryRequired {
                reason: RecoveryReason::NativeCustodyUnresolved,
                safe_owner_boundary: self.safe_recovery.load(Ordering::SeqCst),
            }),
        }
    }
    fn recover(
        &mut self,
        _: &OperationSnapshot,
        recovery: &RecoveryRef,
        _: &Lease,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        self.counts.lock().unwrap().recovery += 1;
        let bytes = std::fs::read(self.root.join("live.bytes")).unwrap();
        match self.native_state(recovery).as_str() {
            "" => Ok(TransactionStep::Complete {
                outcome: CompletionOutcome::CancelledBeforeCommit {
                    reason: CompletionReason::CancellationAccepted,
                },
                session_custody: None,
            }),
            "committed" if bytes == b"reviewed-new-bytes" => Ok(self.completed()),
            "staging" | "publishing"
                if bytes == b"prior-owned-bytes" || bytes == b"reviewed-new-bytes" =>
            {
                if self.recovery_rollback.load(Ordering::SeqCst) {
                    let prior = std::fs::read(self.root.join("prior.backup")).unwrap();
                    durable_write(&self.root.join("live.bytes"), &prior);
                    durable_write(&self.tx(recovery), b"rolled_back");
                    Ok(TransactionStep::Complete {
                        outcome: CompletionOutcome::RolledBack {
                            reason: CompletionReason::RollbackCompleted,
                        },
                        session_custody: None,
                    })
                } else {
                    Ok(TransactionStep::RecoveryRequired {
                        reason: RecoveryReason::RollbackIncomplete,
                        safe_owner_boundary: self.safe_recovery.load(Ordering::SeqCst),
                    })
                }
            }
            "rolled_back" if bytes == b"prior-owned-bytes" => Ok(TransactionStep::Complete {
                outcome: CompletionOutcome::RolledBack {
                    reason: CompletionReason::RollbackCompleted,
                },
                session_custody: None,
            }),
            _ => Ok(TransactionStep::RecoveryRequired {
                reason: RecoveryReason::NativeCustodyUnresolved,
                safe_owner_boundary: self.safe_recovery.load(Ordering::SeqCst),
            }),
        }
    }
    fn handoff_session(&mut self, _: &SessionBinding, _: &Lease) -> Result<bool, Box<BridgeError>> {
        if !self.handoff.load(Ordering::SeqCst) {
            return Ok(false);
        }
        durable_write(
            &self.root.join("session-native-custody.receipt"),
            b"canonical-fixture-owner",
        );
        Ok(true)
    }
}

struct Fixture {
    root: PathBuf,
    owner: Owner,
    clock: Clock,
}
impl Fixture {
    fn new() -> Self {
        let root = std::env::temp_dir().join(format!(
            "bridge-engine-fixture-{}-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos(),
            NEXT.fetch_add(1, Ordering::SeqCst)
        ));
        std::fs::create_dir(&root).unwrap();
        for name in [
            "installation.lease",
            "profile.lease",
            "application.lease",
            "engine.journal",
            "other.journal",
        ] {
            File::create(root.join(name)).unwrap().sync_all().unwrap();
        }
        durable_write(&root.join("live.bytes"), b"prior-owned-bytes");
        Self {
            owner: Owner::new(&root),
            root,
            clock: Clock {
                now: Arc::new(AtomicU64::new(1_000)),
            },
        }
    }
    fn open(&self, host: u64) -> FixtureEngine {
        self.open_path(host, "engine.journal")
    }
    fn open_path(&self, host: u64, journal: &str) -> FixtureEngine {
        Engine::open(
            self.owner.clone(),
            FileJournal::open_existing(&self.root.join(journal)).unwrap(),
            self.clock.clone(),
            Ids { next: host * 1000 },
            config(host),
        )
        .unwrap()
    }
    fn counts(&self) -> Counts {
        self.owner.counts.lock().unwrap().clone()
    }
    fn live(&self) -> Vec<u8> {
        std::fs::read(self.root.join("live.bytes")).unwrap()
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let base = std::env::temp_dir().canonicalize().unwrap();
        if let Ok(root) = self.root.canonicalize() {
            assert!(
                root.starts_with(base)
                    && root
                        .file_name()
                        .unwrap()
                        .to_string_lossy()
                        .starts_with("bridge-engine-fixture-")
            );
            let _ = std::fs::remove_dir_all(root);
        }
    }
}
fn config(host: u64) -> HostConfiguration {
    HostConfiguration {
        epoch: HostEpoch::new(uuid(host)).unwrap(),
        stream: StreamId::new(uuid(host + 100)).unwrap(),
        kind: HostKind::WindowsX64,
        preparation_lifetime_millis: 60_000,
    }
}
fn request(body: RequestBody) -> ValidatedRequest {
    decode_request(
        &serde_json::to_vec(&Request {
            protocol_version: ProtocolVersion,
            request_id: RequestId::new(uuid(999)).unwrap(),
            body,
        })
        .unwrap(),
    )
    .unwrap()
}
fn command<P: OperationPorts, J: DurableJournal>(
    engine: &mut Engine<P, J, Clock, Ids>,
    command: Command,
) -> ReplyBody {
    engine
        .dispatch(request(RequestBody::Command { command }))
        .unwrap()
        .into_inner()
        .body
}
fn prepare<P: OperationPorts, J: DurableJournal>(
    engine: &mut Engine<P, J, Clock, Ids>,
) -> PreparedPlan {
    prepare_for(engine, intent())
}
fn prepare_for<P: OperationPorts, J: DurableJournal>(
    engine: &mut Engine<P, J, Clock, Ids>,
    intent: MutationIntent,
) -> PreparedPlan {
    match command(engine, Command::Prepare(Box::new(PrepareInput { intent }))) {
        ReplyBody::Result {
            result: ResultPayload::Command { command },
        } => match *command {
            CommandResult::Prepare(plan) => plan,
            _ => panic!("wrong command"),
        },
        _ => panic!("preparation rejected"),
    }
}
fn commit<P: OperationPorts, J: DurableJournal>(
    engine: &mut Engine<P, J, Clock, Ids>,
    input: CommitInput,
) -> OperationSnapshot {
    match command(engine, Command::Commit(input)) {
        ReplyBody::Result {
            result: ResultPayload::Command { command },
        } => match *command {
            CommandResult::Commit(operation) => operation,
            _ => panic!("wrong command"),
        },
        other => panic!("commit rejected: {other:?}"),
    }
}
fn commit_input(plan: PreparedPlan, key: u64) -> CommitInput {
    CommitInput {
        plan_ref: plan.plan_ref,
        idempotency_key: IdempotencyKey::new(uuid(key)).unwrap(),
    }
}
fn rejected(body: ReplyBody, expected: ErrorCode) {
    assert!(matches!(body, ReplyBody::Rejected { error } if error.code == expected));
}
fn admitted(engine: &mut FixtureEngine) -> (CommitInput, OperationSnapshot) {
    let input = commit_input(prepare(engine), 900);
    let snapshot = commit(engine, input.clone());
    (input, snapshot)
}

#[test]
fn admission_is_persistent_before_reply_and_replay_precedes_old_host_lookup() {
    let fixture = Fixture::new();
    let mut engine = fixture.open(1);
    let (input, operation) = admitted(&mut engine);
    assert!(matches!(operation.state, OperationState::Admitted));
    assert_eq!(fixture.counts().native_journal, 0);
    assert_eq!(fixture.counts().mutation, 0);
    assert_eq!(commit(&mut engine, input.clone()), operation);
    assert_eq!(fixture.counts().acquisition, 1);
    assert_eq!(fixture.counts().bindings, 1);
    let mut conflicting = input.clone();
    conflicting.plan_ref.review_digest = Sha256::new(format!("sha256:{}", "f".repeat(64))).unwrap();
    rejected(
        command(&mut engine, Command::Commit(conflicting)),
        ErrorCode::IdempotencyConflict,
    );
    drop(engine);
    let mut restarted = fixture.open(2);
    let replay = commit(&mut restarted, input.clone());
    assert_eq!(replay.operation_id, operation.operation_id);
    assert!(matches!(
        replay.state,
        OperationState::Completed {
            outcome: CompletionOutcome::CancelledBeforeCommit { .. }
        }
    ));
    let mut fresh = input;
    fresh.idempotency_key = IdempotencyKey::new(uuid(901)).unwrap();
    rejected(
        command(&mut restarted, Command::Commit(fresh)),
        ErrorCode::PlanHostMismatch,
    );
    assert_eq!(fixture.counts().acquisition, 1);
    assert_eq!(fixture.live(), b"prior-owned-bytes");
}

#[test]
fn losing_writer_has_no_download_staging_backup_or_journal() {
    let fixture = Fixture::new();
    let mut first = fixture.open(1);
    let mut second = fixture.open_path(2, "other.journal");
    let (_, operation) = admitted(&mut first);
    let losing = commit_input(prepare(&mut second), 901);
    let journal_before = std::fs::metadata(fixture.root.join("other.journal"))
        .unwrap()
        .len();
    rejected(
        command(&mut second, Command::Commit(losing)),
        ErrorCode::OperationBusy,
    );
    assert_eq!(
        std::fs::metadata(fixture.root.join("other.journal"))
            .unwrap()
            .len(),
        journal_before
    );
    let counts = fixture.counts();
    assert_eq!(
        (
            counts.download,
            counts.staging,
            counts.backup,
            counts.native_journal,
            counts.mutation
        ),
        (0, 0, 0, 0, 0)
    );
    first.advance(&operation.operation_id).unwrap();
    let counts = fixture.counts();
    assert_eq!(
        (
            counts.download,
            counts.staging,
            counts.backup,
            counts.native_journal
        ),
        (1, 1, 1, 1)
    );
}

#[test]
fn disconnect_observation_keeps_worker_lease_and_exact_replay_mutates_once() {
    let fixture = Fixture::new();
    let mut engine = fixture.open(1);
    let mut competitor = fixture.open_path(2, "other.journal");
    let (input, operation) = admitted(&mut engine);
    drop(operation.clone());
    let running = engine.advance(&operation.operation_id).unwrap();
    assert!(matches!(running.state, OperationState::Running { .. }));
    let losing = commit_input(prepare(&mut competitor), 901);
    rejected(
        command(&mut competitor, Command::Commit(losing)),
        ErrorCode::OperationBusy,
    );
    assert_eq!(fixture.counts().lease_drops, 0);
    let completed = engine.advance(&operation.operation_id).unwrap();
    assert!(matches!(
        completed.state,
        OperationState::Completed {
            outcome: CompletionOutcome::Changed { .. }
        }
    ));
    assert_eq!(commit(&mut engine, input.clone()), completed);
    assert_eq!(engine.advance(&operation.operation_id).unwrap(), completed);
    assert_eq!(fixture.counts().mutation, 1);
    assert_eq!(fixture.counts().lease_drops, 1);
    drop(engine);
    let mut restarted = fixture.open(3);
    assert_eq!(commit(&mut restarted, input), completed);
    assert_eq!(fixture.counts().mutation, 1);
}

#[test]
fn revision_revalidation_occurs_under_exclusion_before_recovery_or_admission() {
    let fixture = Fixture::new();
    let mut engine = fixture.open(1);
    let input = commit_input(prepare(&mut engine), 900);
    fixture.owner.stale.store(true, Ordering::SeqCst);
    let before = std::fs::metadata(fixture.root.join("engine.journal"))
        .unwrap()
        .len();
    rejected(
        command(&mut engine, Command::Commit(input)),
        ErrorCode::StaleRevision,
    );
    assert_eq!(fixture.counts().revalidation, 1);
    assert_eq!(fixture.counts().bindings, 0);
    assert_eq!(fixture.counts().lease_drops, 1);
    assert_eq!(
        std::fs::metadata(fixture.root.join("engine.journal"))
            .unwrap()
            .len(),
        before
    );
    assert_eq!(fixture.counts().native_journal, 0);
}

#[test]
fn prepared_scope_cannot_retarget_or_be_committed_after_expiry() {
    let fixture = Fixture::new();
    let mut engine = fixture.open(1);
    let input = commit_input(prepare(&mut engine), 900);
    fixture.clock.now.store(61_000, Ordering::SeqCst);
    rejected(
        command(&mut engine, Command::Commit(input)),
        ErrorCode::PlanExpired,
    );
    assert_eq!(fixture.counts().acquisition, 0);
    let mut other = intent();
    if let MutationIntent::LaunchOrdinary(i) = &mut other {
        i.target.installation = InstallationSelector::Registered {
            id: InstallationId::new("00000000000000000000000000000002").unwrap(),
            directory_assertion: None,
            revision_assertion: None,
        };
    }
    rejected(
        command(
            &mut engine,
            Command::Prepare(Box::new(PrepareInput { intent: other })),
        ),
        ErrorCode::InternalFailure,
    );
}

#[test]
fn ordinary_directory_prepare_accepts_owner_resolved_registered_capture_without_effects() {
    let fixture = Fixture::new();
    let mut engine = fixture.open(1);
    let journal_before = std::fs::metadata(fixture.root.join("engine.journal"))
        .unwrap()
        .len();
    let mut input = intent();
    let MutationIntent::LaunchOrdinary(ordinary) = &mut input else {
        unreachable!();
    };
    ordinary.target.installation = directory_selector();
    let plan = prepare_for(&mut engine, input);
    let PreparedCapture::LaunchOrdinary {
        target: captured, ..
    } = plan.semantics.capture
    else {
        panic!("ordinary capture missing");
    };
    assert_eq!(captured, target());
    assert_eq!(plan.grants_lock, FalseFlag);
    assert_eq!(plan.grants_permission, FalseFlag);
    assert_eq!(fixture.counts(), Counts::default());
    assert_eq!(fixture.live(), b"prior-owned-bytes");
    assert_eq!(
        std::fs::metadata(fixture.root.join("engine.journal"))
            .unwrap()
            .len(),
        journal_before
    );
    assert!(!fixture.root.join("prior.backup").exists());
}

#[test]
fn ordinary_registered_prepare_refuses_changed_kind_id_and_revision_before_effects() {
    let fixture = Fixture::new();
    let mut engine = fixture.open(1);
    let journal_before = std::fs::metadata(fixture.root.join("engine.journal"))
        .unwrap()
        .len();
    for (id, revision) in [
        ("00000000000000000000000000000002", "registration:1"),
        ("00000000000000000000000000000001", "registration:2"),
    ] {
        let mut input = intent();
        let MutationIntent::LaunchOrdinary(ordinary) = &mut input else {
            unreachable!();
        };
        ordinary.target.installation = InstallationSelector::Registered {
            id: InstallationId::new(id).unwrap(),
            directory_assertion: None,
            revision_assertion: Some(OpaqueRevision::new(revision).unwrap()),
        };
        rejected(
            command(
                &mut engine,
                Command::Prepare(Box::new(PrepareInput { intent: input })),
            ),
            ErrorCode::InternalFailure,
        );
    }
    fixture
        .owner
        .directory_capture
        .store(true, Ordering::SeqCst);
    rejected(
        command(
            &mut engine,
            Command::Prepare(Box::new(PrepareInput { intent: intent() })),
        ),
        ErrorCode::InternalFailure,
    );
    assert_eq!(fixture.counts(), Counts::default());
    assert_eq!(fixture.live(), b"prior-owned-bytes");
    assert_eq!(
        std::fs::metadata(fixture.root.join("engine.journal"))
            .unwrap()
            .len(),
        journal_before
    );
}

#[test]
fn foreign_native_recovery_binding_is_refused_without_a_journal() {
    let fixture = Fixture::new();
    let mut engine = fixture.open(1);
    fixture.owner.wrong_binding.store(true, Ordering::SeqCst);
    let input = commit_input(prepare(&mut engine), 900);
    let before = std::fs::metadata(fixture.root.join("engine.journal"))
        .unwrap()
        .len();
    rejected(
        command(&mut engine, Command::Commit(input)),
        ErrorCode::InternalFailure,
    );
    assert_eq!(
        std::fs::metadata(fixture.root.join("engine.journal"))
            .unwrap()
            .len(),
        before
    );
    assert_eq!(fixture.counts().native_journal, 0);
}

#[test]
fn cancellation_distinguishes_precommit_requested_too_late_and_terminal() {
    let fixture = Fixture::new();
    let mut engine = fixture.open(1);
    let (_, op) = admitted(&mut engine);
    let cancel = |o: &OperationSnapshot| {
        Command::CancelOperation(CancelOperationInput {
            operation_id: o.operation_id.clone(),
            expected_operation_revision: o.operation_revision,
        })
    };
    assert!(
        matches!(command(&mut engine, cancel(&op)), ReplyBody::Result { result: ResultPayload::Command { command } } if matches!(*command, CommandResult::CancelOperation(CancelDisposition::CancelledBeforeCommit { .. })))
    );
    assert_eq!(fixture.counts().native_journal, 0);
    let next = commit_input(prepare(&mut engine), 901);
    let op = commit(&mut engine, next);
    let running = engine.advance(&op.operation_id).unwrap();
    rejected(command(&mut engine, cancel(&op)), ErrorCode::StaleRevision);
    assert!(
        matches!(command(&mut engine, cancel(&running)), ReplyBody::Result { result: ResultPayload::Command { command } } if matches!(*command, CommandResult::CancelOperation(CancelDisposition::Requested { .. })))
    );
    assert_eq!(fixture.counts().lease_drops, 1);
    let rolled_back = engine.advance(&op.operation_id).unwrap();
    assert!(matches!(
        rolled_back.state,
        OperationState::Completed {
            outcome: CompletionOutcome::RolledBack { .. }
        }
    ));
    assert!(
        matches!(command(&mut engine, cancel(&rolled_back)), ReplyBody::Result { result: ResultPayload::Command { command } } if matches!(*command, CommandResult::CancelOperation(CancelDisposition::AlreadyTerminal { .. })))
    );
    let next = commit_input(prepare(&mut engine), 902);
    let op = commit(&mut engine, next);
    fixture.owner.cancellable.store(false, Ordering::SeqCst);
    let running = engine.advance(&op.operation_id).unwrap();
    assert!(
        matches!(command(&mut engine, cancel(&running)), ReplyBody::Result { result: ResultPayload::Command { command } } if matches!(*command, CommandResult::CancelOperation(CancelDisposition::TooLate { .. })))
    );
}

#[test]
fn accepted_cancellation_remains_observable_after_native_cancellable_boundary() {
    let fixture = Fixture::new();
    fixture.owner.repeat_progress.store(true, Ordering::SeqCst);
    fixture
        .owner
        .ignore_cancellation
        .store(true, Ordering::SeqCst);
    let mut engine = fixture.open(1);
    let (_, op) = admitted(&mut engine);
    let running = engine.advance(&op.operation_id).unwrap();
    let cancel = |o: &OperationSnapshot| {
        Command::CancelOperation(CancelOperationInput {
            operation_id: o.operation_id.clone(),
            expected_operation_revision: o.operation_revision,
        })
    };
    assert!(
        matches!(command(&mut engine, cancel(&running)), ReplyBody::Result { result: ResultPayload::Command { command } }
        if matches!(*command, CommandResult::CancelOperation(CancelDisposition::Requested { .. })))
    );
    fixture.owner.cancellable.store(false, Ordering::SeqCst);
    let crossing = engine.advance(&op.operation_id).unwrap();
    assert!(matches!(
        crossing.state,
        OperationState::CancellationRequested { .. }
    ));
    let before = engine.cursor();
    assert!(
        matches!(command(&mut engine, cancel(&crossing)), ReplyBody::Result { result: ResultPayload::Command { command } }
        if matches!(&*command, CommandResult::CancelOperation(CancelDisposition::Requested { operation }) if operation == &crossing))
    );
    assert_eq!(engine.cursor(), before);
    assert_eq!(fixture.counts().lease_drops, 0);
    fixture.owner.repeat_progress.store(false, Ordering::SeqCst);
    let committed = engine.advance(&op.operation_id).unwrap();
    assert!(matches!(
        committed.state,
        OperationState::Completed {
            outcome: CompletionOutcome::Changed { .. }
        }
    ));
    assert!(
        matches!(command(&mut engine, cancel(&committed)), ReplyBody::Result { result: ResultPayload::Command { command } }
        if matches!(&*command, CommandResult::CancelOperation(CancelDisposition::AlreadyTerminal { operation }) if operation == &committed))
    );
    assert_eq!(fixture.counts().mutation, 1);
}

#[test]
fn native_receipt_and_session_custody_require_the_same_exact_process_binding() {
    let fixture = Fixture::new();
    fixture
        .owner
        .include_session_receipt
        .store(true, Ordering::SeqCst);
    fixture.owner.session.store(true, Ordering::SeqCst);
    fixture
        .owner
        .wrong_session_custody
        .store(true, Ordering::SeqCst);
    let mut engine = fixture.open(1);
    let (_, op) = admitted(&mut engine);
    engine.advance(&op.operation_id).unwrap();
    assert_eq!(
        engine.advance(&op.operation_id),
        Err(KernelFailure::InvalidPortResult)
    );
    assert_eq!(fixture.counts().mutation, 1);
    assert_eq!(fixture.counts().lease_drops, 0);
    assert!(
        matches!(engine.close_disposition().unwrap(), CloseDisposition::Deferred { obligations }
        if matches!(obligations.as_slice(), [CloseObligation::Operation { .. }]))
    );
    assert_eq!(
        engine.handoff_session(&op.operation_id),
        Err(KernelFailure::InvalidPortResult)
    );
    assert!(!fixture.root.join("session-native-custody.receipt").exists());
    fixture
        .owner
        .wrong_session_custody
        .store(false, Ordering::SeqCst);
    let complete = engine.advance(&op.operation_id).unwrap();
    assert!(matches!(
        complete.state,
        OperationState::Completed {
            outcome: CompletionOutcome::Changed { .. }
        }
    ));
    assert_eq!(fixture.counts().mutation, 1);
    assert!(
        matches!(engine.close_disposition().unwrap(), CloseDisposition::Deferred { obligations }
        if matches!(obligations.as_slice(), [CloseObligation::SessionCustody { session: bound }] if bound == &session()))
    );
}

fn snapshot_with_reserved_recovery(engine: &mut FixtureEngine, op: &OperationSnapshot) -> usize {
    let mut reply = engine
        .dispatch(request(RequestBody::Query {
            query: Query::Snapshot(EmptyInput {}),
        }))
        .unwrap()
        .into_inner();
    let ReplyBody::Result {
        result: ResultPayload::Query { query },
    } = &mut reply.body
    else {
        panic!("snapshot missing");
    };
    let QueryResult::Snapshot(snapshot) = query.as_mut() else {
        panic!("wrong query");
    };
    let recovery = RecoveryRef {
        operation_id: op.operation_id.clone(),
        transaction: NativeTransactionRef::new(format!(
            "fixture-owner:{}",
            op.operation_id.as_str()
        ))
        .unwrap(),
        target: RecoveryTarget::Launch { target: target() },
    };
    let mut reserved = op.clone();
    reserved.state = OperationState::RecoveryRequired {
        recovery: Box::new(recovery),
        reason: RecoveryReason::NativeCustodyUnresolved,
    };
    snapshot.operations.items = BoundedList::new(vec![reserved]).unwrap();
    let bytes = serde_json::to_vec(&reply).unwrap();
    decode_reply(&bytes).unwrap();
    bytes.len()
}

#[test]
fn native_terminal_observation_capacity_failure_persists_exact_recovery() {
    let fixture = Fixture::new();
    let mut sizing = fixture.open(1);
    let (_, sized_op) = admitted(&mut sizing);
    let limit = snapshot_with_reserved_recovery(&mut sizing, &sized_op) + 64;
    drop(sizing);
    fixture
        .owner
        .include_session_receipt
        .store(true, Ordering::SeqCst);
    let mut engine = Engine::open_with_observation_budget(
        fixture.owner.clone(),
        FileJournal::open_existing(&fixture.root.join("other.journal")).unwrap(),
        fixture.clock.clone(),
        Ids { next: 2000 },
        config(2),
        limit,
    )
    .unwrap();
    let (input, op) = admitted(&mut engine);
    engine.advance(&op.operation_id).unwrap();
    let recovery = engine.advance(&op.operation_id).unwrap();
    assert!(matches!(
        recovery.state,
        OperationState::RecoveryRequired {
            reason: RecoveryReason::NativeCustodyUnresolved,
            ..
        }
    ));
    assert_eq!(fixture.counts().mutation, 1);
    assert_eq!(fixture.live(), b"reviewed-new-bytes");
    let replay = commit(&mut engine, input.clone());
    assert_eq!(replay, recovery);
    assert_eq!(fixture.counts().lease_drops, 2);
    assert!(matches!(
        engine.close_disposition().unwrap(),
        CloseDisposition::RecoveryRequired { .. }
    ));
    let next = commit_input(prepare(&mut engine), 901);
    rejected(
        command(&mut engine, Command::Commit(next)),
        ErrorCode::RecoveryRequired,
    );
    drop(engine);
    let mut normal_budget = fixture.open_path(3, "other.journal");
    let recovered = normal_budget.recover(&op.operation_id).unwrap();
    assert!(matches!(
        recovered.state,
        OperationState::Completed {
            outcome: CompletionOutcome::Changed { .. }
        }
    ));
    assert_eq!(commit(&mut normal_budget, input), recovered);
    assert_eq!(fixture.counts().mutation, 1);
}

#[test]
fn terminal_observation_recovery_preserves_exact_session_custody_until_handoff() {
    let fixture = Fixture::new();
    let mut sizing = fixture.open(1);
    let (_, sized_op) = admitted(&mut sizing);
    let limit = snapshot_with_reserved_recovery(&mut sizing, &sized_op) + 64;
    drop(sizing);
    fixture
        .owner
        .include_session_receipt
        .store(true, Ordering::SeqCst);
    fixture.owner.session.store(true, Ordering::SeqCst);
    let mut engine = Engine::open_with_observation_budget(
        fixture.owner.clone(),
        FileJournal::open_existing(&fixture.root.join("other.journal")).unwrap(),
        fixture.clock.clone(),
        Ids { next: 2000 },
        config(2),
        limit,
    )
    .unwrap();
    let (_, op) = admitted(&mut engine);
    engine.advance(&op.operation_id).unwrap();
    let recovery = engine.advance(&op.operation_id).unwrap();
    assert!(matches!(
        recovery.state,
        OperationState::RecoveryRequired { .. }
    ));
    assert_eq!(fixture.counts().mutation, 1);
    assert_eq!(fixture.counts().lease_drops, 1);
    assert!(
        matches!(engine.close_disposition().unwrap(), CloseDisposition::Deferred { obligations }
        if obligations.as_slice().len() == 2
            && obligations.as_slice().iter().any(|o| matches!(o, CloseObligation::SessionCustody { session: bound } if bound == &session()))
            && obligations.as_slice().iter().any(|o| matches!(o, CloseObligation::Operation { operation_id, operation_revision }
                if operation_id == &op.operation_id && operation_revision == &recovery.operation_revision)))
    );
    assert!(!engine.handoff_session(&op.operation_id).unwrap());
    assert_eq!(fixture.counts().lease_drops, 1);
    drop(engine);
    let mut engine = Engine::open_with_observation_budget(
        fixture.owner.clone(),
        FileJournal::open_existing(&fixture.root.join("other.journal")).unwrap(),
        fixture.clock.clone(),
        Ids { next: 3000 },
        config(3),
        limit,
    )
    .unwrap();
    assert_eq!(fixture.counts().lease_drops, 2);
    assert!(
        matches!(engine.close_disposition().unwrap(), CloseDisposition::Deferred { obligations }
        if obligations.as_slice().iter().any(|o| matches!(o, CloseObligation::SessionCustody { session: bound } if bound == &session()))
            && obligations.as_slice().iter().any(|o| matches!(o, CloseObligation::Operation { operation_id, .. } if operation_id == &op.operation_id)))
    );
    let resumed = engine.recover(&op.operation_id).unwrap();
    assert!(matches!(
        resumed.state,
        OperationState::RecoveryRequired { .. }
    ));
    assert_eq!(fixture.counts().mutation, 1);
    assert_eq!(fixture.counts().lease_drops, 2);
    fixture.owner.handoff.store(true, Ordering::SeqCst);
    assert!(engine.handoff_session(&op.operation_id).unwrap());
    assert_eq!(fixture.counts().lease_drops, 3);
    assert!(matches!(
        engine.close_disposition().unwrap(),
        CloseDisposition::RecoveryRequired { .. }
    ));
    assert_eq!(fixture.counts().mutation, 1);
}

#[test]
fn deferred_close_includes_safe_recovery_and_independent_running_operation() {
    let fixture = Fixture::new();
    let mut engine = fixture.open(1);
    let (_, op) = admitted(&mut engine);
    engine.advance(&op.operation_id).unwrap();
    fixture.owner.fail_native.store(true, Ordering::SeqCst);
    engine.advance(&op.operation_id).unwrap();
    fixture.owner.fail_native.store(false, Ordering::SeqCst);
    fixture.owner.safe_recovery.store(true, Ordering::SeqCst);
    let recovery = engine.recover(&op.operation_id).unwrap();
    assert!(matches!(
        recovery.state,
        OperationState::RecoveryRequired { .. }
    ));
    assert!(matches!(
        engine.close_disposition().unwrap(),
        CloseDisposition::RecoveryRequired { .. }
    ));
    let preferences = MutationIntent::SaveApplicationPreferences(SaveApplicationPreferencesInput {
        expected_revision: OpaqueRevision::new("preferences:1").unwrap(),
        values: ApplicationPreferences {
            theme: ThemePreference::Dark,
            motion: MotionPreference::Reduced,
            last_target: None,
            provider: None,
        },
    });
    let plan = prepare_for(&mut engine, preferences);
    let admitted_preferences = commit(&mut engine, commit_input(plan, 901));
    let running = engine.advance(&admitted_preferences.operation_id).unwrap();
    assert!(matches!(running.state, OperationState::Running { .. }));
    let expected_cursor = engine.cursor();
    let result = command(
        &mut engine,
        Command::RequestHostClose(RequestHostCloseInput { expected_cursor }),
    );
    assert!(
        matches!(result, ReplyBody::Result { result: ResultPayload::Command { command } }
        if matches!(*command, CommandResult::RequestHostClose(CloseDisposition::Deferred { ref obligations })
            if obligations.as_slice().len() == 2
                && obligations.as_slice().iter().any(|o| matches!(o, CloseObligation::Operation { operation_id, operation_revision }
                    if operation_id == &recovery.operation_id && operation_revision == &recovery.operation_revision))
                && obligations.as_slice().iter().any(|o| matches!(o, CloseObligation::Operation { operation_id, operation_revision }
                    if operation_id == &running.operation_id && operation_revision == &running.operation_revision))))
    );
    assert_eq!(fixture.live(), b"prior-owned-bytes");
    assert!(fixture.root.join("preferences.staged").is_file());
}

#[test]
fn restart_session_handoff_keeps_unsafe_recovery_exclusion_until_owner_recovery() {
    let fixture = Fixture::new();
    let mut sizing = fixture.open(1);
    let (_, sized_op) = admitted(&mut sizing);
    let limit = snapshot_with_reserved_recovery(&mut sizing, &sized_op) + 64;
    drop(sizing);
    fixture
        .owner
        .include_session_receipt
        .store(true, Ordering::SeqCst);
    fixture.owner.session.store(true, Ordering::SeqCst);
    let mut before_restart = Engine::open_with_observation_budget(
        fixture.owner.clone(),
        FileJournal::open_existing(&fixture.root.join("other.journal")).unwrap(),
        fixture.clock.clone(),
        Ids { next: 2000 },
        config(2),
        limit,
    )
    .unwrap();
    let (_, op) = admitted(&mut before_restart);
    before_restart.advance(&op.operation_id).unwrap();
    let recovery = before_restart.advance(&op.operation_id).unwrap();
    assert!(matches!(
        recovery.state,
        OperationState::RecoveryRequired { .. }
    ));
    drop(before_restart);
    let mut restarted = Engine::open_with_observation_budget(
        fixture.owner.clone(),
        FileJournal::open_existing(&fixture.root.join("other.journal")).unwrap(),
        fixture.clock.clone(),
        Ids { next: 3000 },
        config(3),
        limit,
    )
    .unwrap();
    let before_handoff = fixture.counts().lease_drops;
    fixture.owner.handoff.store(true, Ordering::SeqCst);
    assert!(restarted.handoff_session(&op.operation_id).unwrap());
    assert_eq!(fixture.counts().lease_drops, before_handoff);
    assert_eq!(
        restarted.handoff_session(&op.operation_id),
        Err(KernelFailure::InvalidPortResult)
    );
    assert_eq!(fixture.counts().lease_drops, before_handoff);
    let pending = restarted.operation(&op.operation_id).unwrap().clone();
    assert!(
        matches!(restarted.close_disposition().unwrap(), CloseDisposition::Deferred { obligations }
        if matches!(obligations.as_slice(), [CloseObligation::Operation { operation_id, operation_revision }]
            if operation_id == &op.operation_id && operation_revision == &pending.operation_revision))
    );
    let mut competitor = fixture.open(4);
    let input = commit_input(prepare(&mut competitor), 902);
    rejected(
        command(&mut competitor, Command::Commit(input.clone())),
        ErrorCode::OperationBusy,
    );
    assert_eq!(fixture.counts().lease_drops, before_handoff);
    // The native fixture owner's verified handoff has settled session lifetime.
    // Recovery therefore returns the same durable transaction without another
    // host session obligation; it does not publish a second launch/mutation.
    fixture.owner.session.store(false, Ordering::SeqCst);
    let resolved_boundary = restarted.recover(&op.operation_id).unwrap();
    assert!(matches!(
        resolved_boundary.state,
        OperationState::RecoveryRequired { .. }
    ));
    assert_eq!(fixture.counts().lease_drops, before_handoff + 1);
    assert!(matches!(
        restarted.close_disposition().unwrap(),
        CloseDisposition::RecoveryRequired { .. }
    ));
    assert_eq!(fixture.counts().mutation, 1);
    let admitted_competitor = commit(&mut competitor, input);
    assert!(matches!(
        admitted_competitor.state,
        OperationState::Admitted
    ));
    assert_eq!(fixture.counts().mutation, 1);
}

#[test]
fn normal_close_defers_until_worker_and_session_custody_reach_safe_owner_boundaries() {
    let fixture = Fixture::new();
    fixture.owner.session.store(true, Ordering::SeqCst);
    let mut engine = fixture.open(1);
    let (_, op) = admitted(&mut engine);
    let cursor = engine.cursor();
    assert!(
        matches!(command(&mut engine, Command::RequestHostClose(RequestHostCloseInput { expected_cursor: cursor })), ReplyBody::Result { result: ResultPayload::Command { command } } if matches!(*command, CommandResult::RequestHostClose(CloseDisposition::Deferred { .. })))
    );
    rejected(
        command(
            &mut engine,
            Command::Prepare(Box::new(PrepareInput { intent: intent() })),
        ),
        ErrorCode::OperationBusy,
    );
    engine.advance(&op.operation_id).unwrap();
    engine.advance(&op.operation_id).unwrap();
    assert!(
        matches!(engine.close_disposition().unwrap(), CloseDisposition::Deferred { obligations } if matches!(obligations.as_slice(), [CloseObligation::SessionCustody { .. }]))
    );
    assert!(!engine.handoff_session(&op.operation_id).unwrap());
    assert_eq!(fixture.counts().lease_drops, 0);
    fixture.owner.handoff.store(true, Ordering::SeqCst);
    assert!(engine.handoff_session(&op.operation_id).unwrap());
    assert_eq!(engine.close_disposition().unwrap(), CloseDisposition::Ready);
    assert_eq!(fixture.counts().lease_drops, 1);
    assert!(
        fixture
            .root
            .join("session-native-custody.receipt")
            .is_file()
    );
}

#[test]
fn interrupted_work_blocks_conflicts_and_foreign_bytes_remain_unresolved() {
    let fixture = Fixture::new();
    let mut engine = fixture.open(1);
    let (input, op) = admitted(&mut engine);
    engine.advance(&op.operation_id).unwrap();
    drop(engine);
    durable_write(
        &fixture.root.join("live.bytes"),
        b"externally-replaced-bytes",
    );
    let mut restarted = fixture.open(2);
    let snapshot = commit(&mut restarted, input);
    assert!(matches!(
        snapshot.state,
        OperationState::RecoveryRequired { .. }
    ));
    let next = commit_input(prepare(&mut restarted), 901);
    rejected(
        command(&mut restarted, Command::Commit(next)),
        ErrorCode::RecoveryRequired,
    );
    fixture.owner.safe_recovery.store(true, Ordering::SeqCst);
    let recovered = restarted.recover(&op.operation_id).unwrap();
    assert!(matches!(
        recovered.state,
        OperationState::RecoveryRequired {
            reason: RecoveryReason::NativeCustodyUnresolved,
            ..
        }
    ));
    assert_eq!(fixture.live(), b"externally-replaced-bytes");
    assert!(matches!(
        restarted.close_disposition().unwrap(),
        CloseDisposition::RecoveryRequired { .. }
    ));
}

#[test]
fn interruption_at_staging_reconciles_to_durable_rollback() {
    let fixture = Fixture::new();
    let mut engine = fixture.open(1);
    let (_, op) = admitted(&mut engine);
    engine.advance(&op.operation_id).unwrap();
    drop(engine);
    fixture
        .owner
        .recovery_rollback
        .store(true, Ordering::SeqCst);
    let mut restarted = fixture.open(2);
    let recovered = restarted.recover(&op.operation_id).unwrap();
    assert!(matches!(
        recovered.state,
        OperationState::Completed {
            outcome: CompletionOutcome::RolledBack { .. }
        }
    ));
    assert_eq!(fixture.live(), b"prior-owned-bytes");
    drop(restarted);
    assert!(matches!(
        fixture.open(3).operation(&op.operation_id).unwrap().state,
        OperationState::Completed {
            outcome: CompletionOutcome::RolledBack { .. }
        }
    ));
}

#[test]
fn snapshots_are_complete_and_events_are_scoped_consecutive_and_replay_free() {
    let fixture = Fixture::new();
    let mut engine = fixture.open(1);
    let initial = engine.cursor();
    let (input, op) = admitted(&mut engine);
    engine.advance(&op.operation_id).unwrap();
    let before = engine.cursor();
    commit(&mut engine, input);
    assert_eq!(engine.cursor(), before);
    let reply = engine
        .dispatch(request(RequestBody::Query {
            query: Query::Snapshot(EmptyInput {}),
        }))
        .unwrap()
        .into_inner();
    assert!(
        matches!(reply.body, ReplyBody::Result { result: ResultPayload::Query { query } } if matches!(*query, QueryResult::Snapshot(Snapshot { operations: Inventory { completeness: Completeness::Complete, ref items, .. }, .. }) if items.as_slice().len() == 1 && items.as_slice()[0].operation_id == op.operation_id))
    );
    let reply = engine
        .dispatch(request(RequestBody::Query {
            query: Query::ResumeEvents(ResumeEventsInput {
                after: initial,
                maximum_events: ProgressCount::new(128),
            }),
        }))
        .unwrap()
        .into_inner();
    assert!(
        matches!(reply.body, ReplyBody::Result { result: ResultPayload::Query { query } } if matches!(*query, QueryResult::ResumeEvents(ref batch) if batch.events.as_slice().len() == 3 && batch.next == engine.cursor()))
    );
    let foreign = Cursor {
        host_epoch: HostEpoch::new(uuid(8)).unwrap(),
        ..engine.cursor()
    };
    let reply = engine
        .dispatch(request(RequestBody::Query {
            query: Query::ResumeEvents(ResumeEventsInput {
                after: foreign,
                maximum_events: ProgressCount::new(128),
            }),
        }))
        .unwrap()
        .into_inner();
    rejected(reply.body, ErrorCode::ResnapshotRequired);
}

#[test]
fn disk_journal_is_exclusive_and_complete_corruption_never_becomes_success() {
    let fixture = Fixture::new();
    let journal = FileJournal::open_existing(&fixture.root.join("engine.journal")).unwrap();
    assert!(matches!(
        FileJournal::open_existing(&fixture.root.join("engine.journal")),
        Err(KernelFailure::JournalBusy)
    ));
    drop(journal);
    let mut engine = fixture.open(1);
    admitted(&mut engine);
    drop(engine);
    let path = fixture.root.join("engine.journal");
    let original = std::fs::read(&path).unwrap();
    let mut torn = OpenOptions::new().append(true).open(&path).unwrap();
    torn.write_all(&[20, 0, 0, 0, 1, 2, 3]).unwrap();
    torn.sync_all().unwrap();
    drop(torn);
    drop(FileJournal::open_existing(&path).unwrap());
    assert_eq!(std::fs::read(&path).unwrap(), original);
    let mut bad_length = original.clone();
    bad_length[8] ^= 1;
    durable_write(&path, &bad_length);
    assert!(matches!(
        FileJournal::open_existing(&path),
        Err(KernelFailure::CorruptJournal)
    ));
    let mut damaged = original;
    let last = damaged.len() - 1;
    damaged[last] ^= 1;
    durable_write(&path, &damaged);
    assert!(matches!(
        FileJournal::open_existing(&path),
        Err(KernelFailure::CorruptJournal)
    ));
}

struct FaultJournal {
    inner: FileJournal,
    remaining: Arc<AtomicU64>,
}
impl DurableJournal for FaultJournal {
    fn records(&self) -> &[JournalRecord] {
        self.inner.records()
    }
    fn append(&mut self, record: &JournalRecord) -> Result<(), KernelFailure> {
        if self.remaining.load(Ordering::SeqCst) == 0 {
            return Err(KernelFailure::Storage);
        }
        self.remaining.fetch_sub(1, Ordering::SeqCst);
        self.inner.append(record)
    }
}
#[test]
fn admission_persistence_failure_never_acknowledges_or_starts_effects_and_keeps_custody() {
    let fixture = Fixture::new();
    let remaining = Arc::new(AtomicU64::new(1));
    let journal = FaultJournal {
        inner: FileJournal::open_existing(&fixture.root.join("engine.journal")).unwrap(),
        remaining,
    };
    let mut engine = Engine::open(
        fixture.owner.clone(),
        journal,
        fixture.clock.clone(),
        Ids { next: 1000 },
        config(1),
    )
    .unwrap();
    let input = commit_input(prepare(&mut engine), 900);
    rejected(
        command(&mut engine, Command::Commit(input)),
        ErrorCode::PersistenceFailed,
    );
    assert_eq!(fixture.counts().native_journal, 0);
    assert_eq!(fixture.counts().lease_drops, 0);
    assert!(matches!(
        engine.close_disposition().unwrap(),
        CloseDisposition::Deferred { .. }
    ));
    drop(engine);
    let restarted = fixture.open(2);
    assert!(
        restarted
            .operation(&OperationId::new(uuid(1002)).unwrap())
            .is_none()
    );
    assert_eq!(fixture.live(), b"prior-owned-bytes");
}

#[test]
fn terminal_persistence_failure_keeps_lease_and_restart_observes_native_commit_once() {
    let fixture = Fixture::new();
    let remaining = Arc::new(AtomicU64::new(u64::MAX));
    let journal = FaultJournal {
        inner: FileJournal::open_existing(&fixture.root.join("engine.journal")).unwrap(),
        remaining: remaining.clone(),
    };
    let mut engine = Engine::open(
        fixture.owner.clone(),
        journal,
        fixture.clock.clone(),
        Ids { next: 1000 },
        config(1),
    )
    .unwrap();
    let input = commit_input(prepare(&mut engine), 900);
    let op = commit(&mut engine, input.clone());
    engine.advance(&op.operation_id).unwrap();
    remaining.store(0, Ordering::SeqCst);
    assert_eq!(
        engine.advance(&op.operation_id),
        Err(KernelFailure::Storage)
    );
    assert_eq!(fixture.counts().mutation, 1);
    assert_eq!(fixture.counts().lease_drops, 0);
    assert!(matches!(
        engine.close_disposition().unwrap(),
        CloseDisposition::Deferred { .. }
    ));
    drop(engine);
    let mut restarted = fixture.open(2);
    let recovered = restarted.recover(&op.operation_id).unwrap();
    assert!(matches!(
        recovered.state,
        OperationState::Completed {
            outcome: CompletionOutcome::Changed { .. }
        }
    ));
    assert_eq!(commit(&mut restarted, input), recovered);
    assert_eq!(fixture.counts().mutation, 1);
}

#[test]
fn arbitrary_native_error_does_not_claim_rollback_or_release_lease() {
    let fixture = Fixture::new();
    let mut engine = fixture.open(1);
    let (_, op) = admitted(&mut engine);
    fixture.owner.fail_native.store(true, Ordering::SeqCst);
    let recovery = engine.advance(&op.operation_id).unwrap();
    assert!(matches!(
        recovery.state,
        OperationState::RecoveryRequired {
            reason: RecoveryReason::NativeCustodyUnresolved,
            ..
        }
    ));
    assert_eq!(fixture.counts().lease_drops, 0);
    assert!(matches!(
        engine.close_disposition().unwrap(),
        CloseDisposition::Deferred { .. }
    ));
}

#[test]
fn host_epoch_and_stream_reuse_are_refused_after_restart() {
    let fixture = Fixture::new();
    drop(fixture.open(1));
    let journal = FileJournal::open_existing(&fixture.root.join("engine.journal")).unwrap();
    assert!(matches!(
        Engine::open(
            fixture.owner.clone(),
            journal,
            fixture.clock.clone(),
            Ids { next: 2000 },
            config(1)
        ),
        Err(KernelFailure::ReusedHostIdentity)
    ));
}

#[test]
fn retention_gap_requires_resnapshot_and_current_cursor_never_reexecutes() {
    let fixture = Fixture::new();
    fixture.owner.repeat_progress.store(true, Ordering::SeqCst);
    let mut engine = fixture.open(1);
    let initial = engine.cursor();
    let (input, op) = admitted(&mut engine);
    for _ in 0..140 {
        engine.advance(&op.operation_id).unwrap();
    }
    let query = |after| {
        request(RequestBody::Query {
            query: Query::ResumeEvents(ResumeEventsInput {
                after,
                maximum_events: ProgressCount::new(128),
            }),
        })
    };
    rejected(
        engine.dispatch(query(initial)).unwrap().into_inner().body,
        ErrorCode::ResnapshotRequired,
    );
    let current = engine.cursor();
    let result = engine
        .dispatch(query(current.clone()))
        .unwrap()
        .into_inner();
    assert!(
        matches!(result.body, ReplyBody::Result { result: ResultPayload::Query { query } } if matches!(*query, QueryResult::ResumeEvents(ref batch) if batch.events.as_slice().is_empty() && batch.next == current))
    );
    commit(&mut engine, input);
    assert_eq!(engine.cursor(), current);
    assert_eq!(fixture.counts().native_journal, 1);
    assert_eq!(fixture.counts().mutation, 0);
}

#[test]
fn retained_history_capacity_refuses_before_another_exclusion_or_journal() {
    let fixture = Fixture::new();
    let mut engine = fixture.open(1);
    for index in 0..64 {
        let plan = prepare(&mut engine);
        let op = commit(&mut engine, commit_input(plan, 2000 + index));
        command(
            &mut engine,
            Command::CancelOperation(CancelOperationInput {
                operation_id: op.operation_id,
                expected_operation_revision: op.operation_revision,
            }),
        );
    }
    let input = commit_input(prepare(&mut engine), 3000);
    let before = std::fs::metadata(fixture.root.join("engine.journal"))
        .unwrap()
        .len();
    rejected(
        command(&mut engine, Command::Commit(input)),
        ErrorCode::OperationBusy,
    );
    assert_eq!(fixture.counts().acquisition, 64);
    assert_eq!(fixture.counts().bindings, 64);
    assert_eq!(fixture.counts().native_journal, 0);
    assert_eq!(
        std::fs::metadata(fixture.root.join("engine.journal"))
            .unwrap()
            .len(),
        before
    );
    let result = engine
        .dispatch(request(RequestBody::Query {
            query: Query::Snapshot(EmptyInput {}),
        }))
        .unwrap()
        .into_inner();
    assert!(
        matches!(result.body, ReplyBody::Result { result: ResultPayload::Query { query } } if matches!(*query, QueryResult::Snapshot(ref snapshot) if snapshot.operations.items.as_slice().len() == 64 && snapshot.operations.completeness == Completeness::Complete))
    );
}

#[test]
fn complete_validly_framed_journal_with_conflicting_replay_binding_blocks_open() {
    let fixture = Fixture::new();
    let mut engine = fixture.open(1);
    let (_, op) = admitted(&mut engine);
    drop(engine);
    let mut journal = FileJournal::open_existing(&fixture.root.join("engine.journal")).unwrap();
    let mut last = journal
        .records()
        .iter()
        .filter_map(|record| match record {
            JournalRecord::Operation { value } => Some(*value.clone()),
            _ => None,
        })
        .next_back()
        .unwrap();
    assert_eq!(last.snapshot.operation_id, op.operation_id);
    last.commit.idempotency_key = IdempotencyKey::new(uuid(901)).unwrap();
    last.snapshot.operation_revision =
        RevisionCounter::new(last.snapshot.operation_revision.get() + 1);
    journal
        .append(&JournalRecord::Operation {
            value: Box::new(last),
        })
        .unwrap();
    drop(journal);
    let journal = FileJournal::open_existing(&fixture.root.join("engine.journal")).unwrap();
    assert!(matches!(
        Engine::open(
            fixture.owner.clone(),
            journal,
            fixture.clock.clone(),
            Ids { next: 2000 },
            config(2)
        ),
        Err(KernelFailure::CorruptJournal)
    ));
}

#[test]
fn forced_death_recovery_across_actual_process_boundaries() {
    use std::process::{Command, Stdio};
    use std::time::{Duration, Instant};
    if let Some(root) = std::env::var_os("BRIDGE_FIXTURE_CHILD_ROOT") {
        let root = PathBuf::from(root);
        let mode = std::env::var("BRIDGE_FIXTURE_CHILD_MODE").unwrap();
        let remaining = Arc::new(AtomicU64::new(u64::MAX));
        let journal = FaultJournal {
            inner: FileJournal::open_existing(&root.join("engine.journal")).unwrap(),
            remaining: remaining.clone(),
        };
        let mut engine = Engine::open(
            Owner::new(&root),
            journal,
            Clock {
                now: Arc::new(AtomicU64::new(1000)),
            },
            Ids { next: 501_000 },
            config(501),
        )
        .unwrap();
        let input = commit_input(prepare(&mut engine), 900);
        let op = commit(&mut engine, input.clone());
        if mode != "admitted" {
            engine.advance(&op.operation_id).unwrap();
        }
        if mode == "committed" {
            remaining.store(0, Ordering::SeqCst);
            assert_eq!(
                engine.advance(&op.operation_id),
                Err(KernelFailure::Storage)
            );
        }
        durable_write(
            &root.join("child-exact-commit.json"),
            &serde_json::to_vec(&(input, op.operation_id)).unwrap(),
        );
        durable_write(
            &root.join("child-ready"),
            b"retained-worker-and-journal-lock",
        );
        // The parent kills this process, bypassing every Rust destructor. A
        // surviving in-process worker is therefore impossible in these tests.
        loop {
            std::thread::sleep(Duration::from_millis(50));
        }
    }
    for mode in ["admitted", "staging", "committed"] {
        let fixture = Fixture::new();
        let mut command = Command::new(std::env::current_exe().unwrap());
        command
            .args([
                "--exact",
                "forced_death_recovery_across_actual_process_boundaries",
                "--nocapture",
            ])
            .env("BRIDGE_FIXTURE_CHILD_ROOT", &fixture.root)
            .env("BRIDGE_FIXTURE_CHILD_MODE", mode)
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            command.creation_flags(0x0800_0000);
        }
        let mut child = command.spawn().unwrap();
        let start = Instant::now();
        while !fixture.root.join("child-ready").is_file() {
            if let Some(status) = child.try_wait().unwrap() {
                let output = child.wait_with_output().unwrap();
                panic!(
                    "fixture child ended before boundary {mode}: {status}, {}",
                    String::from_utf8_lossy(&output.stderr)
                );
            }
            if start.elapsed() > Duration::from_secs(20) {
                child.kill().unwrap();
                let output = child.wait_with_output().unwrap();
                panic!(
                    "fixture child timed out at {mode}: {}",
                    String::from_utf8_lossy(&output.stderr)
                );
            }
            std::thread::sleep(Duration::from_millis(10));
        }
        // The lock must still belong to the child before actual forced death.
        assert!(matches!(
            FileJournal::open_existing(&fixture.root.join("engine.journal")),
            Err(KernelFailure::JournalBusy)
        ));
        child.kill().unwrap();
        let status = child.wait().unwrap();
        assert!(!status.success());
        let (input, id): (CommitInput, OperationId) = serde_json::from_slice(
            &std::fs::read(fixture.root.join("child-exact-commit.json")).unwrap(),
        )
        .unwrap();
        fixture
            .owner
            .recovery_rollback
            .store(true, Ordering::SeqCst);
        let mut restarted = fixture.open(502);
        let replay = commit(&mut restarted, input.clone());
        assert_eq!(replay.operation_id, id);
        match mode {
            "admitted" => {
                assert!(matches!(
                    replay.state,
                    OperationState::Completed {
                        outcome: CompletionOutcome::CancelledBeforeCommit { .. }
                    }
                ));
                assert_eq!(fixture.live(), b"prior-owned-bytes");
                assert!(!fixture.root.join("prior.backup").exists());
            }
            "staging" => {
                assert!(matches!(
                    replay.state,
                    OperationState::RecoveryRequired { .. }
                ));
                let recovered = restarted.recover(&id).unwrap();
                assert!(matches!(
                    recovered.state,
                    OperationState::Completed {
                        outcome: CompletionOutcome::RolledBack { .. }
                    }
                ));
                assert_eq!(fixture.live(), b"prior-owned-bytes");
            }
            "committed" => {
                assert!(matches!(
                    replay.state,
                    OperationState::RecoveryRequired { .. }
                ));
                let recovered = restarted.recover(&id).unwrap();
                assert!(matches!(
                    recovered.state,
                    OperationState::Completed {
                        outcome: CompletionOutcome::Changed { .. }
                    }
                ));
                assert_eq!(fixture.live(), b"reviewed-new-bytes");
                assert_eq!(fixture.counts().mutation, 0);
            }
            _ => unreachable!(),
        }
        let before = std::fs::metadata(fixture.root.join("engine.journal"))
            .unwrap()
            .len();
        commit(&mut restarted, input);
        assert_eq!(
            std::fs::metadata(fixture.root.join("engine.journal"))
                .unwrap()
                .len(),
            before
        );
    }
}

/// Distinct synthetic installations/profiles use distinct actual filesystem
/// exclusions and native journals. This owner is only a controlled kernel port;
/// its synthetic sessions are not live game/process qualification evidence.
#[derive(Clone)]
struct IndependentLaunchOwner {
    root: PathBuf,
    counts: Arc<Mutex<Counts>>,
    bindings: Arc<Mutex<BTreeMap<OperationId, RecoveryRef>>>,
    directory_capture: bool,
}

#[derive(serde::Serialize, serde::Deserialize)]
struct IndependentNativeRecord {
    operation: OperationId,
    target: ResolvedTarget,
    session: SessionBinding,
    state: String,
}

fn independent_profile(index: u64) -> ProfileId {
    ProfileId::new(format!("{index:032x}")).unwrap()
}
fn independent_target(index: u64) -> ResolvedTarget {
    ResolvedTarget {
        installation: InstallationBinding::Registered {
            registration_id: InstallationId::new(format!("{index:032x}")).unwrap(),
            registration_revision: OpaqueRevision::new("registration:1").unwrap(),
            physical_id: PhysicalInstallationId::new(format!("fixture-many-installation:{index}"))
                .unwrap(),
            native_target_ref: NativeTargetRef::new(format!("fixture-many-target:{index}"))
                .unwrap(),
        },
        profile: ProfileBinding::Isolated {
            id: independent_profile(index),
            revision: OpaqueRevision::new("profile:1").unwrap(),
        },
    }
}
fn independent_intent(index: u64) -> MutationIntent {
    MutationIntent::LaunchIsolated(IsolatedLaunchInput {
        target: IsolatedTargetSelector {
            installation: InstallationSelector::Registered {
                id: InstallationId::new(format!("{index:032x}")).unwrap(),
                directory_assertion: None,
                revision_assertion: Some(OpaqueRevision::new("registration:1").unwrap()),
            },
            profile: IsolatedProfileSelector::Isolated {
                id: independent_profile(index),
                revision_assertion: Some(OpaqueRevision::new("profile:1").unwrap()),
            },
        },
        store_mode: StoreMode::Existing,
        unrecognized_runtime_choice: UnrecognizedRuntimeChoice::Reject,
    })
}
fn independent_session(index: u64) -> SessionBinding {
    SessionBinding {
        session_id: SessionId::new(uuid(10_000 + index)).unwrap(),
        revision: OpaqueRevision::new("session:1").unwrap(),
        process: ProcessIdentity {
            pid: Pid::new((10_000 + index) as u32).unwrap(),
            start_identity: ProcessStartIdentity::Windows(
                ProcessGeneration::new(format!("fixture-many-start:{index}")).unwrap(),
            ),
            executable_identity: ExecutableIdentity::new(format!(
                "fixture-many-executable:{index}"
            ))
            .unwrap(),
            installation_physical_id: independent_target(index).installation.physical_id().clone(),
            architecture: ProcessArchitecture::X86_64,
        },
    }
}
fn independent_index(capture: &PreparedCapture) -> u64 {
    let PreparedCapture::LaunchIsolated { target, .. } = capture else {
        panic!("wrong fixture operation");
    };
    let ProfileBinding::Isolated { id, .. } = &target.profile else {
        panic!("wrong fixture profile");
    };
    let index = u64::from_str_radix(id.as_str(), 16).unwrap();
    assert!((1..=65).contains(&index));
    assert_eq!(target, &independent_target(index));
    index
}
fn independent_resources(index: u64) -> Vec<ResourceKey> {
    vec![
        ResourceKey::Installation {
            physical_id: independent_target(index).installation.physical_id().clone(),
        },
        ResourceKey::IsolatedProfile {
            id: independent_profile(index),
        },
    ]
}

#[test]
fn isolated_directory_prepare_accepts_owner_resolved_registered_capture_without_effects() {
    let fixture = Fixture::new();
    let owner = IndependentLaunchOwner::provision(&fixture.root);
    let mut engine = Engine::open(
        owner.clone(),
        FileJournal::open_existing(&fixture.root.join("engine.journal")).unwrap(),
        fixture.clock.clone(),
        Ids { next: 100 },
        config(1),
    )
    .unwrap();
    let journal_before = std::fs::metadata(fixture.root.join("engine.journal"))
        .unwrap()
        .len();
    let mut input = independent_intent(1);
    let MutationIntent::LaunchIsolated(isolated) = &mut input else {
        unreachable!();
    };
    isolated.target.installation = directory_selector();
    let plan = prepare_for(&mut engine, input);
    let PreparedCapture::LaunchIsolated { target, .. } = plan.semantics.capture else {
        panic!("isolated capture missing");
    };
    assert_eq!(target, independent_target(1));
    assert_eq!(plan.grants_lock, FalseFlag);
    assert_eq!(plan.grants_permission, FalseFlag);
    assert_eq!(*owner.counts.lock().unwrap(), Counts::default());
    assert!(owner.bindings.lock().unwrap().is_empty());
    assert_eq!(
        std::fs::read(owner.directory(1).join("live.bytes")).unwrap(),
        b"before-profile:1"
    );
    assert_eq!(
        std::fs::metadata(fixture.root.join("engine.journal"))
            .unwrap()
            .len(),
        journal_before
    );
}

#[test]
fn isolated_registered_prepare_refuses_changed_kind_id_and_revision_before_effects() {
    let fixture = Fixture::new();
    let mut owner = IndependentLaunchOwner::provision(&fixture.root);
    owner.directory_capture = true;
    let mut wrong_kind = Engine::open(
        owner.clone(),
        FileJournal::open_existing(&fixture.root.join("engine.journal")).unwrap(),
        fixture.clock.clone(),
        Ids { next: 100 },
        config(1),
    )
    .unwrap();
    let journal_before = std::fs::metadata(fixture.root.join("engine.journal"))
        .unwrap()
        .len();
    rejected(
        command(
            &mut wrong_kind,
            Command::Prepare(Box::new(PrepareInput {
                intent: independent_intent(1),
            })),
        ),
        ErrorCode::InternalFailure,
    );
    assert_eq!(
        std::fs::metadata(fixture.root.join("engine.journal"))
            .unwrap()
            .len(),
        journal_before
    );
    drop(wrong_kind);
    owner.directory_capture = false;
    let mut engine = Engine::open(
        owner.clone(),
        FileJournal::open_existing(&fixture.root.join("other.journal")).unwrap(),
        fixture.clock.clone(),
        Ids { next: 200 },
        config(2),
    )
    .unwrap();
    let journal_before = std::fs::metadata(fixture.root.join("other.journal"))
        .unwrap()
        .len();
    for (id, revision) in [
        ("00000000000000000000000000000002", "registration:1"),
        ("00000000000000000000000000000001", "registration:2"),
    ] {
        let mut input = independent_intent(1);
        let MutationIntent::LaunchIsolated(isolated) = &mut input else {
            unreachable!();
        };
        isolated.target.installation = InstallationSelector::Registered {
            id: InstallationId::new(id).unwrap(),
            directory_assertion: None,
            revision_assertion: Some(OpaqueRevision::new(revision).unwrap()),
        };
        rejected(
            command(
                &mut engine,
                Command::Prepare(Box::new(PrepareInput { intent: input })),
            ),
            ErrorCode::InternalFailure,
        );
    }
    assert_eq!(*owner.counts.lock().unwrap(), Counts::default());
    assert!(owner.bindings.lock().unwrap().is_empty());
    assert_eq!(
        std::fs::read(owner.directory(1).join("live.bytes")).unwrap(),
        b"before-profile:1"
    );
    assert_eq!(
        std::fs::metadata(fixture.root.join("other.journal"))
            .unwrap()
            .len(),
        journal_before
    );
}

impl IndependentLaunchOwner {
    fn provision(root: &Path) -> Self {
        let root = root.join("independent-native-owner");
        std::fs::create_dir(&root).unwrap();
        for index in 1..=65 {
            let directory = root.join(format!("{index:032x}"));
            std::fs::create_dir(&directory).unwrap();
            for name in ["installation.lease", "profile.lease"] {
                File::create(directory.join(name))
                    .unwrap()
                    .sync_all()
                    .unwrap();
            }
            durable_write(
                &directory.join("live.bytes"),
                format!("before-profile:{index}").as_bytes(),
            );
        }
        Self {
            root,
            counts: Arc::new(Mutex::new(Counts::default())),
            bindings: Arc::new(Mutex::new(BTreeMap::new())),
            directory_capture: false,
        }
    }
    fn directory(&self, index: u64) -> PathBuf {
        self.root.join(format!("{index:032x}"))
    }
    fn journal(&self, index: u64, operation: &OperationId) -> PathBuf {
        self.directory(index)
            .join(format!("{}.native", operation.as_str()))
    }
    fn complete(&self, record: &IndependentNativeRecord) -> TransactionStep {
        let evidence = Evidence {
            observation_id: ObservationId::new(record.session.session_id.as_str()).unwrap(),
            observed_at: UtcTimestamp::new("2026-10-03T12:00:00Z").unwrap(),
            source: EvidenceSource::NativeLive,
        };
        TransactionStep::Complete {
            outcome: CompletionOutcome::Changed {
                reason: CompletionReason::Applied,
                receipt: Some(Box::new(EffectReceipt::SessionSpawned {
                    session: Box::new(SessionProjection {
                        binding: record.session.clone(),
                        target: Observation::Observed {
                            value: record.target.clone(),
                            evidence: evidence.clone(),
                        },
                        live_identity: Observation::Observed {
                            value: true,
                            evidence: evidence.clone(),
                        },
                        readiness: Observation::Observed {
                            value: SessionReadiness::IsolatedReady,
                            evidence,
                        },
                    }),
                })),
            },
            session_custody: Some(record.session.clone()),
        }
    }
}
impl OperationPorts for IndependentLaunchOwner {
    type Lease = Lease;
    fn capture(
        &mut self,
        intent: &MutationIntent,
        _: &HostEpoch,
    ) -> Result<CapturedOperation, Box<BridgeError>> {
        let MutationIntent::LaunchIsolated(input) = intent else {
            return Err(error(ErrorCode::UnsupportedCapability));
        };
        let IsolatedProfileSelector::Isolated { id, .. } = &input.target.profile;
        let index = u64::from_str_radix(id.as_str(), 16).unwrap();
        if !(1..=65).contains(&index) {
            return Err(error(ErrorCode::TargetMissing));
        }
        let mut target = independent_target(index);
        if self.directory_capture {
            target.installation = directory_binding(&target.installation);
        }
        Ok(CapturedOperation {
            semantics: PlanSemantics {
                hash_profile: HashProfile::BridgePlanSemanticJsonV1,
                action: ActionId::LaunchIsolated,
                capture: PreparedCapture::LaunchIsolated {
                    target,
                    catalog_revision: OpaqueRevision::new("catalog:1").unwrap(),
                    runtime: RuntimeExpectation::Absent,
                    store_mode: StoreMode::Existing,
                    unrecognized_runtime_choice: UnrecognizedRuntimeChoice::Reject,
                },
                trust_domain: TrustDomain::Session,
                effects: BoundedList::new(vec![ProposedEffect::LaunchSession]).unwrap(),
            },
            resources: independent_resources(index),
        })
    }
    fn acquire(&mut self, resources: &[ResourceKey]) -> Result<Lease, Box<BridgeError>> {
        self.counts.lock().unwrap().acquisition += 1;
        let mut files = Vec::new();
        for resource in resources {
            let (index, name) = match resource {
                ResourceKey::Installation { physical_id } => (
                    physical_id
                        .as_str()
                        .strip_prefix("fixture-many-installation:")
                        .unwrap()
                        .parse::<u64>()
                        .unwrap(),
                    "installation.lease",
                ),
                ResourceKey::IsolatedProfile { id } => (
                    u64::from_str_radix(id.as_str(), 16).unwrap(),
                    "profile.lease",
                ),
                _ => return Err(error(ErrorCode::UnsupportedCapability)),
            };
            assert!((1..=65).contains(&index));
            let file = OpenOptions::new()
                .read(true)
                .write(true)
                .open(self.directory(index).join(name))
                .unwrap();
            file.try_lock()
                .map_err(|_| error(ErrorCode::OperationBusy))?;
            files.push(file);
        }
        Ok(Lease {
            resources: resources.to_vec(),
            _files: files,
            counts: self.counts.clone(),
        })
    }
    fn revalidate(
        &mut self,
        semantics: &PlanSemantics,
        lease: &Lease,
    ) -> Result<(), Box<BridgeError>> {
        self.counts.lock().unwrap().revalidation += 1;
        let index = independent_index(&semantics.capture);
        assert_eq!(lease.resources(), independent_resources(index));
        if !self.directory(index).join("live.bytes").is_file() {
            return Err(error(ErrorCode::StaleRevision));
        }
        Ok(())
    }
    fn recovery_binding(
        &mut self,
        operation: &OperationId,
        semantics: &PlanSemantics,
        _: &Lease,
    ) -> Result<RecoveryRef, Box<BridgeError>> {
        let index = independent_index(&semantics.capture);
        let recovery = RecoveryRef {
            operation_id: operation.clone(),
            transaction: NativeTransactionRef::new(format!(
                "fixture-many-owner:{}",
                operation.as_str()
            ))
            .unwrap(),
            target: RecoveryTarget::Launch {
                target: independent_target(index),
            },
        };
        self.counts.lock().unwrap().bindings += 1;
        self.bindings
            .lock()
            .unwrap()
            .insert(operation.clone(), recovery.clone());
        Ok(recovery)
    }
    fn advance(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        lease: &Lease,
        _: bool,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        let index = independent_index(&operation.semantics.capture);
        assert_eq!(lease.resources(), independent_resources(index));
        assert_eq!(
            self.bindings.lock().unwrap().get(&operation.operation_id),
            Some(recovery)
        );
        let journal = self.journal(index, &operation.operation_id);
        if !journal.exists() {
            let record = IndependentNativeRecord {
                operation: operation.operation_id.clone(),
                target: independent_target(index),
                session: independent_session(index),
                state: "staged".into(),
            };
            durable_write(&journal, &serde_json::to_vec(&record).unwrap());
            durable_write(
                &self.directory(index).join("staged.bytes"),
                format!("after-profile:{index}").as_bytes(),
            );
            let mut counts = self.counts.lock().unwrap();
            counts.native_journal += 1;
            counts.staging += 1;
            return Ok(TransactionStep::Progress {
                progress: Progress {
                    phase: PhaseId::new("staging").unwrap(),
                    measurement: Measurement::Unknown,
                },
                cancellable: true,
            });
        }
        let mut record: IndependentNativeRecord =
            serde_json::from_slice(&std::fs::read(&journal).unwrap()).unwrap();
        assert_eq!(record.operation, operation.operation_id);
        assert_eq!(record.target, independent_target(index));
        assert_eq!(record.session, independent_session(index));
        if record.state == "staged" {
            record.state = "publishing".into();
            durable_write(&journal, &serde_json::to_vec(&record).unwrap());
            durable_write(
                &self.directory(index).join("live.bytes"),
                format!("after-profile:{index}").as_bytes(),
            );
            self.counts.lock().unwrap().mutation += 1;
            record.state = "committed".into();
            durable_write(&journal, &serde_json::to_vec(&record).unwrap());
        }
        assert_eq!(record.state, "committed");
        Ok(self.complete(&record))
    }
    fn recover(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        lease: &Lease,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        self.counts.lock().unwrap().recovery += 1;
        let index = independent_index(&operation.semantics.capture);
        assert_eq!(lease.resources(), independent_resources(index));
        let record: IndependentNativeRecord = serde_json::from_slice(
            &std::fs::read(self.journal(index, &operation.operation_id)).unwrap(),
        )
        .unwrap();
        assert_eq!(record.operation, recovery.operation_id);
        assert_eq!(
            recovery.target,
            RecoveryTarget::Launch {
                target: record.target.clone()
            }
        );
        assert_eq!(record.state, "committed");
        assert_eq!(
            std::fs::read(self.directory(index).join("live.bytes")).unwrap(),
            format!("after-profile:{index}").as_bytes()
        );
        Ok(self.complete(&record))
    }
    fn handoff_session(&mut self, _: &SessionBinding, _: &Lease) -> Result<bool, Box<BridgeError>> {
        Ok(false)
    }
}

fn assert_complete_close_boundary<P: OperationPorts>(
    engine: &mut Engine<P, FileJournal, Clock, Ids>,
    expected: &[(OperationId, SessionBinding)],
) {
    let reply = engine
        .dispatch(request(RequestBody::Command {
            command: Command::RequestHostClose(RequestHostCloseInput {
                expected_cursor: engine.cursor(),
            }),
        }))
        .unwrap()
        .into_inner();
    let bytes = serde_json::to_vec(&reply).unwrap();
    decode_reply(&bytes).unwrap();
    let ReplyBody::Result {
        result: ResultPayload::Command { command },
    } = reply.body
    else {
        panic!("close rejected");
    };
    let CommandResult::RequestHostClose(CloseDisposition::Deferred { obligations }) = *command
    else {
        panic!("close not deferred");
    };
    assert_eq!(obligations.as_slice().len(), 128);
    for (id, session) in expected {
        let revision = engine.operation(id).unwrap().operation_revision;
        assert!(matches!(
            engine.operation(id).unwrap().state,
            OperationState::RecoveryRequired { .. }
        ));
        assert_eq!(obligations.as_slice().iter().filter(|o| matches!(o, CloseObligation::Operation { operation_id, operation_revision } if operation_id == id && operation_revision == &revision)).count(), 1);
        assert_eq!(obligations.as_slice().iter().filter(|o| matches!(o, CloseObligation::SessionCustody { session: bound } if bound == session)).count(), 1);
    }
    let event = Event {
        protocol_version: ProtocolVersion,
        cursor: engine.cursor(),
        body: EventBody::HostCloseDeferred { obligations },
    };
    decode_event(&serde_json::to_vec(&event).unwrap()).unwrap();
}

#[test]
fn sixty_four_independent_recoveries_keep_all_128_close_obligations_across_restart() {
    let fixture = Fixture::new();
    let owner = IndependentLaunchOwner::provision(&fixture.root);
    let mut sizing = Engine::open(
        owner.clone(),
        FileJournal::open_existing(&fixture.root.join("engine.journal")).unwrap(),
        fixture.clock.clone(),
        Ids { next: 1000 },
        config(1),
    )
    .unwrap();
    for index in 1..=64 {
        let plan = prepare_for(&mut sizing, independent_intent(index));
        commit(&mut sizing, commit_input(plan, 3000 + index));
    }
    let mut reservation = sizing
        .dispatch(request(RequestBody::Query {
            query: Query::Snapshot(EmptyInput {}),
        }))
        .unwrap()
        .into_inner();
    let ReplyBody::Result {
        result: ResultPayload::Query { query },
    } = &mut reservation.body
    else {
        panic!("missing snapshot");
    };
    let QueryResult::Snapshot(snapshot) = query.as_mut() else {
        panic!("wrong query");
    };
    let mut operations = snapshot.operations.items.as_slice().to_vec();
    for operation in &mut operations {
        let recovery = owner.bindings.lock().unwrap()[&operation.operation_id].clone();
        operation.state = OperationState::RecoveryRequired {
            recovery: Box::new(recovery),
            reason: RecoveryReason::NativeCustodyUnresolved,
        };
    }
    snapshot.operations.items = BoundedList::new(operations).unwrap();
    let reservation_bytes = serde_json::to_vec(&reservation).unwrap();
    decode_reply(&reservation_bytes).unwrap();
    let budget = reservation_bytes.len() + 16;
    assert!(budget < MAX_MESSAGE_BYTES);
    assert_eq!(owner.counts.lock().unwrap().native_journal, 0);
    drop(sizing);
    let mut engine = Engine::open_with_observation_budget(
        owner.clone(),
        FileJournal::open_existing(&fixture.root.join("other.journal")).unwrap(),
        fixture.clock.clone(),
        Ids { next: 2000 },
        config(2),
        budget,
    )
    .unwrap();
    let mut expected = Vec::new();
    for index in 1..=64 {
        let plan = prepare_for(&mut engine, independent_intent(index));
        let operation = commit(&mut engine, commit_input(plan, 4000 + index));
        expected.push((operation.operation_id, independent_session(index)));
    }
    for (id, _) in &expected {
        engine.advance(id).unwrap();
    }
    for (id, _) in &expected {
        let operation = engine.advance(id).unwrap();
        assert!(matches!(
            operation.state,
            OperationState::RecoveryRequired { .. }
        ));
    }
    let next = commit_input(prepare_for(&mut engine, independent_intent(65)), 4065);
    let before = owner.counts.lock().unwrap().clone();
    let journal_before = std::fs::metadata(fixture.root.join("other.journal"))
        .unwrap()
        .len();
    rejected(
        command(&mut engine, Command::Commit(next)),
        ErrorCode::OperationBusy,
    );
    assert_eq!(*owner.counts.lock().unwrap(), before);
    assert_eq!(
        std::fs::metadata(fixture.root.join("other.journal"))
            .unwrap()
            .len(),
        journal_before
    );
    assert_eq!(before.native_journal, 64);
    assert_eq!(before.mutation, 64);
    assert_complete_close_boundary(&mut engine, &expected);
    drop(engine);
    // Discard the preparation/binding registry when the host restarts. Recovery
    // must inspect the engine journal and each exact retained native file, not
    // derive continuity from a surviving in-memory reference map.
    let restart_owner = IndependentLaunchOwner {
        root: owner.root.clone(),
        counts: owner.counts.clone(),
        bindings: Arc::new(Mutex::new(BTreeMap::new())),
        directory_capture: false,
    };
    let mut restarted = Engine::open_with_observation_budget(
        restart_owner.clone(),
        FileJournal::open_existing(&fixture.root.join("other.journal")).unwrap(),
        fixture.clock.clone(),
        Ids { next: 5000 },
        config(5),
        budget,
    )
    .unwrap();
    assert_complete_close_boundary(&mut restarted, &expected);
    for (id, _) in &expected {
        assert!(matches!(
            restarted.recover(id).unwrap().state,
            OperationState::RecoveryRequired { .. }
        ));
    }
    assert_complete_close_boundary(&mut restarted, &expected);
    assert_eq!(owner.counts.lock().unwrap().mutation, 64);
    assert_eq!(owner.counts.lock().unwrap().native_journal, 64);
    assert!(restart_owner.bindings.lock().unwrap().is_empty());
}
