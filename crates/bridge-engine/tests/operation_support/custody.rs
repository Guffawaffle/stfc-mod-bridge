//! Opaque local state in the actual kernel path, with temporal exclusion/context
//! observations. Synthetic owners only; no configuration/native qualification.
use super::*;
use std::{
    cell::{Cell, RefCell},
    rc::Rc,
};

const PRIVATE: &str = "opaque-private-fixture-bytes-7ee3d7";

#[derive(Clone, Default)]
struct Audit {
    events: Rc<RefCell<Vec<String>>>,
    next: Rc<Cell<u64>>,
    mode: Rc<Cell<&'static str>>,
}
impl Audit {
    fn record(&self, event: String) {
        self.events.borrow_mut().push(event);
    }
    fn count(&self, prefix: &str) -> usize {
        self.events
            .borrow()
            .iter()
            .filter(|s| s.starts_with(prefix))
            .count()
    }
    fn token_drop(&self, id: u64, lease_live: bool) {
        assert!(
            self.events
                .borrow()
                .contains(&format!("token_drop:{id}:lease={lease_live}:owner=true"))
        );
    }
}

// Deliberately no Clone, Debug, Serialize or Send implementation.
struct Token {
    id: u64,
    audit: Audit,
    owner_alive: Rc<Cell<bool>>,
    lease_live: Rc<Cell<bool>>,
    semantics: PlanSemantics,
    recovery: RefCell<Option<RecoveryRef>>,
    private: &'static str,
    steps: usize,
}
impl Drop for Token {
    fn drop(&mut self) {
        self.audit.record(format!(
            "token_drop:{}:lease={}:owner={}",
            self.id,
            self.lease_live.get(),
            self.owner_alive.get()
        ));
    }
}
struct HeldLease {
    inner: Lease,
    token: u64,
    live: Rc<Cell<bool>>,
    audit: Audit,
}
impl ResourceLease for HeldLease {
    fn resources(&self) -> &[ResourceKey] {
        self.inner.resources()
    }
}
impl Drop for HeldLease {
    fn drop(&mut self) {
        self.audit.record(format!("lease_drop:{}", self.token));
        self.live.set(false);
    }
}
struct Ports {
    inner: Owner,
    audit: Audit,
    alive: Rc<Cell<bool>>,
}
impl Ports {
    fn new(fixture: &Fixture, audit: &Audit) -> Self {
        Self {
            inner: fixture.owner.clone(),
            audit: audit.clone(),
            alive: Rc::new(Cell::new(true)),
        }
    }
    fn token(&self, semantics: &PlanSemantics, recovery: Option<&RecoveryRef>) -> Token {
        let id = self.audit.next.get() + 1;
        self.audit.next.set(id);
        self.audit.record(format!(
            "{}:{id}",
            if recovery.is_some() {
                "recovery_token"
            } else {
                "capture"
            }
        ));
        Token {
            id,
            audit: self.audit.clone(),
            owner_alive: self.alive.clone(),
            lease_live: Rc::new(Cell::new(false)),
            semantics: semantics.clone(),
            recovery: RefCell::new(recovery.cloned()),
            private: PRIVATE,
            steps: 0,
        }
    }
    fn held(&self, custody: &Token, inner: Lease) -> HeldLease {
        custody.lease_live.set(true);
        HeldLease {
            inner,
            token: custody.id,
            live: custody.lease_live.clone(),
            audit: self.audit.clone(),
        }
    }
    fn check(&self, custody: &Token, lease: &HeldLease, semantics: &PlanSemantics, phase: &str) {
        assert_eq!(custody.private, PRIVATE);
        assert_eq!(&custody.semantics, semantics);
        assert_eq!(lease.token, custody.id);
        assert!(lease.live.get());
        assert!(custody.owner_alive.get());
        assert!(Rc::ptr_eq(&custody.lease_live, &lease.live));
        self.audit.record(format!("{phase}:{}", custody.id));
    }
}
impl Drop for Ports {
    fn drop(&mut self) {
        self.audit.record("owner_drop".into());
        self.alive.set(false);
    }
}
impl ApplicationServices for Ports {}
impl OperationPorts for Ports {
    type Custody = Token;
    type Lease = HeldLease;
    fn capture(
        &mut self,
        intent: &MutationIntent,
        host: &HostEpoch,
    ) -> Result<(CapturedOperation, Token), Box<BridgeError>> {
        let (mut captured, ()) = self.inner.capture(intent, host)?;
        let token = self.token(&captured.semantics, None);
        match self.audit.mode.get() {
            "bad_capture" => captured.semantics.action = ActionId::LaunchIsolated,
            "bad_resources" => captured.resources.clear(),
            _ => {}
        }
        Ok((captured, token))
    }
    fn acquire(
        &mut self,
        semantics: &PlanSemantics,
        custody: &Token,
        resources: &[ResourceKey],
    ) -> Result<HeldLease, Box<BridgeError>> {
        assert_eq!(semantics, &custody.semantics);
        self.audit.record(format!("acquire:{}", custody.id));
        if self.audit.mode.get() == "busy" {
            return Err(error(ErrorCode::OperationBusy));
        }
        let mut inner = self.inner.acquire(semantics, &(), resources)?;
        if self.audit.mode.get() == "bad_lease" {
            inner.resources.clear();
        }
        Ok(self.held(custody, inner))
    }
    fn acquire_recovery(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        resources: &[ResourceKey],
    ) -> Result<(Token, HeldLease), Box<BridgeError>> {
        assert_eq!(&operation.operation_id, &recovery.operation_id);
        self.audit.record(format!(
            "reacquire:{}:{}",
            operation.operation_id.as_str(),
            recovery.transaction.as_str()
        ));
        let token = self.token(&operation.semantics, Some(recovery));
        let ((), mut inner) = self
            .inner
            .acquire_recovery(operation, recovery, resources)?;
        if self.audit.mode.get() == "bad_recovery_lease" {
            inner.resources.clear();
        }
        let lease = self.held(&token, inner);
        Ok((token, lease))
    }
    fn revalidate(
        &mut self,
        semantics: &PlanSemantics,
        custody: &Token,
        lease: &HeldLease,
    ) -> Result<(), Box<BridgeError>> {
        self.check(custody, lease, semantics, "revalidate");
        if self.audit.mode.get() == "stale" {
            return Err(error(ErrorCode::StaleRevision));
        }
        self.inner.revalidate(semantics, &(), &lease.inner)
    }
    fn recovery_binding(
        &mut self,
        id: &OperationId,
        semantics: &PlanSemantics,
        custody: &Token,
        lease: &HeldLease,
    ) -> Result<RecoveryRef, Box<BridgeError>> {
        self.check(custody, lease, semantics, "binding");
        let mut recovery = self
            .inner
            .recovery_binding(id, semantics, &(), &lease.inner)?;
        if self.audit.mode.get() == "bad_binding" {
            recovery.operation_id = OperationId::new(uuid(777)).unwrap();
        }
        *custody.recovery.borrow_mut() = Some(recovery.clone());
        Ok(recovery)
    }
    fn advance(
        &mut self,
        op: &OperationSnapshot,
        recovery: &RecoveryRef,
        custody: &mut Token,
        lease: &HeldLease,
        cancel: bool,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        self.check(custody, lease, &op.semantics, "advance");
        assert_eq!(custody.recovery.borrow().as_ref(), Some(recovery));
        custody.steps += 1;
        if self.audit.mode.get() == "native_error" {
            return Err(error(ErrorCode::InternalFailure));
        }
        if self.audit.mode.get() == "unsafe" {
            return Ok(TransactionStep::RecoveryRequired {
                reason: RecoveryReason::InterruptedTransaction,
                safe_owner_boundary: false,
            });
        }
        self.inner
            .advance(op, recovery, &mut (), &lease.inner, cancel)
    }
    fn recover(
        &mut self,
        op: &OperationSnapshot,
        recovery: &RecoveryRef,
        custody: &mut Token,
        lease: &HeldLease,
    ) -> Result<TransactionStep, Box<BridgeError>> {
        self.check(custody, lease, &op.semantics, "recover");
        assert_eq!(custody.recovery.borrow().as_ref(), Some(recovery));
        custody.steps += 1;
        match self.audit.mode.get() {
            "unsafe" => Ok(TransactionStep::RecoveryRequired {
                reason: RecoveryReason::InterruptedTransaction,
                safe_owner_boundary: false,
            }),
            "safe" => Ok(TransactionStep::RecoveryRequired {
                reason: RecoveryReason::InterruptedTransaction,
                safe_owner_boundary: true,
            }),
            "recovery_progress" => Ok(TransactionStep::Progress {
                progress: Progress {
                    phase: PhaseId::new("pending").unwrap(),
                    measurement: Measurement::Unknown,
                },
                cancellable: false,
            }),
            _ => self.inner.recover(op, recovery, &mut (), &lease.inner),
        }
    }
    fn handoff_session(
        &mut self,
        session: &SessionBinding,
        custody: &mut Token,
        lease: &HeldLease,
    ) -> Result<bool, Box<BridgeError>> {
        self.check(custody, lease, &custody.semantics, "handoff");
        self.inner.handoff_session(session, &mut (), &lease.inner)
    }
}
fn open<J: DurableJournal>(
    f: &Fixture,
    a: &Audit,
    j: J,
    host: u64,
) -> Engine<Ports, J, Clock, Ids> {
    Engine::open(
        Ports::new(f, a),
        j,
        f.clock.clone(),
        Ids { next: host * 1000 },
        config(host),
    )
    .unwrap()
}
fn journal(f: &Fixture) -> FileJournal {
    FileJournal::open_existing(&f.root.join("engine.journal")).unwrap()
}
fn bytes(f: &Fixture) -> u64 {
    // The live Windows journal owns an exclusive byte-range lock. Observe its
    // length for no-append checks; read actual WAL bytes only after engine drop.
    std::fs::metadata(f.root.join("engine.journal"))
        .unwrap()
        .len()
}
fn absent_marker(encoded: &[u8]) {
    assert!(
        !encoded
            .windows(PRIVATE.len())
            .any(|window| window == PRIVATE.as_bytes())
    );
}

#[test]
fn capture_and_failed_admission_retain_or_destroy_only_the_exact_token() {
    for mode in [
        "bad_capture",
        "bad_resources",
        "clock",
        "deadline",
        "identity",
    ] {
        let f = Fixture::new();
        let a = Audit::default();
        a.mode.set(mode);
        if mode == "clock" {
            f.clock.controls.fail_at.store(2, Ordering::SeqCst);
        }
        if mode == "deadline" {
            f.clock.controls.fail_deadline.store(true, Ordering::SeqCst);
        }
        let controls = IdentityControls::default();
        controls
            .fail_plan
            .store(mode == "identity", Ordering::SeqCst);
        let mut engine = Engine::open(
            Ports::new(&f, &a),
            journal(&f),
            f.clock.clone(),
            FaultIds {
                ids: Ids { next: 1000 },
                controls,
            },
            config(1),
        )
        .unwrap();
        let before = bytes(&f);
        rejected(
            command(
                &mut engine,
                Command::Prepare(Box::new(PrepareInput { intent: intent() })),
            ),
            ErrorCode::InternalFailure,
        );
        a.token_drop(1, false);
        assert_eq!(a.count("acquire:"), 0);
        assert_eq!(bytes(&f), before);
    }
    for (mode, expected) in [
        ("busy", ErrorCode::OperationBusy),
        ("bad_lease", ErrorCode::InternalFailure),
        ("stale", ErrorCode::StaleRevision),
        ("bad_binding", ErrorCode::InternalFailure),
    ] {
        let f = Fixture::new();
        let a = Audit::default();
        let mut engine = open(&f, &a, journal(&f), 1);
        let input = commit_input(prepare(&mut engine), 900);
        let mut foreign_host = input.clone();
        foreign_host.plan_ref.host_epoch = HostEpoch::new(uuid(2)).unwrap();
        rejected(
            command(&mut engine, Command::Commit(foreign_host)),
            ErrorCode::PlanHostMismatch,
        );
        assert_eq!(a.count("token_drop:"), 0);
        let before = bytes(&f);
        a.mode.set(mode);
        rejected(
            command(&mut engine, Command::Commit(input.clone())),
            expected,
        );
        assert_eq!(a.count("token_drop:"), 0);
        assert_eq!(bytes(&f), before);
        assert_eq!(f.counts().native_journal, 0);
        a.mode.set("");
        let op = commit(&mut engine, input);
        assert_eq!(a.count("capture:"), 1);
        engine.advance(&op.operation_id).unwrap();
        engine.advance(&op.operation_id).unwrap();
        a.token_drop(1, true);
        drop(engine);
        absent_marker(&std::fs::read(f.root.join("engine.journal")).unwrap());
    }
}

#[test]
fn exact_expiry_retires_custody_but_forged_refs_and_clock_failure_do_not() {
    for after_acquire in [false, true] {
        let f = Fixture::new();
        let a = Audit::default();
        let mut engine = open(&f, &a, journal(&f), 1);
        let input = commit_input(prepare(&mut engine), 900);
        let mut forged = input.clone();
        forged.plan_ref.review_digest = Sha256::new(format!("sha256:{}", "f".repeat(64))).unwrap();
        rejected(
            command(&mut engine, Command::Commit(forged)),
            ErrorCode::InvalidRequest,
        );
        assert_eq!(a.count("token_drop:"), 0);
        f.clock.controls.fail.store(true, Ordering::SeqCst);
        rejected(
            command(&mut engine, Command::Commit(input.clone())),
            ErrorCode::InternalFailure,
        );
        assert_eq!(a.count("token_drop:"), 0);
        f.clock.controls.fail.store(false, Ordering::SeqCst);
        if after_acquire {
            let calls = f.clock.controls.calls.load(Ordering::SeqCst);
            f.clock
                .controls
                .advance_at
                .store(calls + 2, Ordering::SeqCst);
            f.clock.controls.advance_to.store(61_000, Ordering::SeqCst);
        } else {
            f.clock.now.store(61_000, Ordering::SeqCst);
        }
        rejected(
            command(&mut engine, Command::Commit(input.clone())),
            ErrorCode::PlanExpired,
        );
        a.token_drop(1, after_acquire);
        let before = a.count("acquire:");
        rejected(
            command(&mut engine, Command::Commit(input)),
            ErrorCode::PlanExpired,
        );
        assert_eq!(a.count("acquire:"), before);
        assert_eq!(a.count("token_drop:"), 1);
    }
}

#[test]
fn admission_is_single_use_after_cancel_or_completion_but_exact_replay_survives() {
    for cancel in [true, false] {
        let f = Fixture::new();
        let a = Audit::default();
        let mut engine = open(&f, &a, journal(&f), 1);
        let plan = prepare(&mut engine);
        let input = commit_input(plan.clone(), 900);
        let op = commit(&mut engine, input.clone());
        if cancel {
            command(
                &mut engine,
                Command::CancelOperation(CancelOperationInput {
                    operation_id: op.operation_id.clone(),
                    expected_operation_revision: op.operation_revision,
                }),
            );
        } else {
            engine.advance(&op.operation_id).unwrap();
            engine.advance(&op.operation_id).unwrap();
        }
        a.token_drop(1, true);
        let before = bytes(&f);
        rejected(
            command(&mut engine, Command::Commit(commit_input(plan, 901))),
            ErrorCode::PlanExpired,
        );
        assert_eq!(bytes(&f), before);
        assert_eq!(a.count("acquire:"), 1);
        f.clock.now.store(99_000, Ordering::SeqCst);
        f.clock.controls.fail.store(true, Ordering::SeqCst);
        let replay = commit(&mut engine, input.clone());
        assert!(matches!(replay.state, OperationState::Completed { .. }));
        let expected_cursor = engine.cursor();
        command(
            &mut engine,
            Command::RequestHostClose(RequestHostCloseInput { expected_cursor }),
        );
        assert_eq!(commit(&mut engine, input.clone()), replay);
        drop(engine);
        let mut restarted = open(&f, &a, journal(&f), 2);
        assert_eq!(commit(&mut restarted, input), replay);
        assert_eq!(a.count("capture:"), 1);
        assert_eq!(a.count("recovery_token:"), 0);
    }
}

#[test]
fn unsafe_work_and_unknown_wal_disposition_keep_token_lease_and_context_together() {
    for fail_after in [0, 1, 3] {
        let f = Fixture::new();
        let a = Audit::default();
        let remaining = Arc::new(AtomicU64::new(10));
        let mut engine = open(
            &f,
            &a,
            FaultJournal {
                inner: journal(&f),
                remaining: remaining.clone(),
            },
            1,
        );
        let input = commit_input(prepare(&mut engine), 900);
        remaining.store(fail_after, Ordering::SeqCst);
        if fail_after == 0 {
            rejected(
                command(&mut engine, Command::Commit(input.clone())),
                ErrorCode::PersistenceFailed,
            );
        } else {
            let op = commit(&mut engine, input.clone());
            if fail_after == 3 {
                engine.advance(&op.operation_id).unwrap();
            }
            assert_eq!(
                engine.advance(&op.operation_id),
                Err(KernelFailure::Storage)
            );
        }
        assert_eq!(a.count("token_drop:"), 0);
        assert_eq!(a.count("lease_drop:"), 0);
        if fail_after <= 1 {
            assert_eq!(a.count("advance:"), 0);
            assert_eq!(f.counts().native_journal, 0);
        }
        rejected(
            command(&mut engine, Command::Commit(input)),
            ErrorCode::PersistenceFailed,
        );
        drop(engine);
        absent_marker(&std::fs::read(f.root.join("engine.journal")).unwrap());
        a.token_drop(1, true);
        let events = a.events.borrow();
        let token = events
            .iter()
            .position(|s| s.starts_with("token_drop:"))
            .unwrap();
        let lease = events
            .iter()
            .position(|s| s.starts_with("lease_drop:"))
            .unwrap();
        let owner = events.iter().position(|s| s == "owner_drop").unwrap();
        assert!(token < lease && lease < owner);
    }
    let f = Fixture::new();
    let a = Audit::default();
    let mut engine = open(&f, &a, journal(&f), 1);
    let plan = prepare(&mut engine);
    let op = commit(&mut engine, commit_input(plan, 900));
    a.mode.set("native_error");
    engine.advance(&op.operation_id).unwrap();
    a.mode.set("recovery_progress");
    assert_eq!(
        engine.recover(&op.operation_id),
        Err(KernelFailure::InvalidPortResult)
    );
    a.mode.set("unsafe");
    engine.recover(&op.operation_id).unwrap();
    assert_eq!(a.count("token_drop:"), 0);
    assert_eq!(a.count("recovery_token:"), 0);
    a.mode.set("safe");
    engine.recover(&op.operation_id).unwrap();
    a.token_drop(1, true);
}

#[test]
fn restart_reacquires_exact_recovery_custody_once_and_invalid_pairs_drop_in_order() {
    let f = Fixture::new();
    let a = Audit::default();
    let mut engine = open(&f, &a, journal(&f), 1);
    let plan = prepare(&mut engine);
    let op = commit(&mut engine, commit_input(plan, 900));
    a.mode.set("unsafe");
    engine.advance(&op.operation_id).unwrap();
    drop(engine);
    let mut restarted = open(&f, &a, journal(&f), 2);
    a.mode.set("bad_recovery_lease");
    assert_eq!(
        restarted.recover(&op.operation_id),
        Err(KernelFailure::InvalidPortResult)
    );
    a.token_drop(2, true);
    a.mode.set("unsafe");
    restarted.recover(&op.operation_id).unwrap();
    restarted.recover(&op.operation_id).unwrap();
    assert_eq!(a.count("capture:"), 1);
    assert_eq!(a.count("recovery_token:"), 2);
    a.mode.set("safe");
    restarted.recover(&op.operation_id).unwrap();
    a.token_drop(3, true);
    assert!(
        a.events
            .borrow()
            .iter()
            .any(|s| s.starts_with(&format!("reacquire:{}:", op.operation_id.as_str())))
    );
    drop(restarted);
    absent_marker(&std::fs::read(f.root.join("engine.journal")).unwrap());
}

#[test]
fn session_handoff_reacquires_custody_and_keeps_it_on_failed_persistence() {
    let f = Fixture::new();
    let a = Audit::default();
    f.owner.session.store(true, Ordering::SeqCst);
    let mut engine = open(&f, &a, journal(&f), 1);
    let plan = prepare(&mut engine);
    let op = commit(&mut engine, commit_input(plan, 900));
    engine.advance(&op.operation_id).unwrap();
    engine.advance(&op.operation_id).unwrap();
    assert_eq!(a.count("token_drop:"), 0);
    drop(engine);
    let remaining = Arc::new(AtomicU64::new(10));
    let mut restarted = open(
        &f,
        &a,
        FaultJournal {
            inner: journal(&f),
            remaining: remaining.clone(),
        },
        2,
    );
    assert!(!restarted.handoff_session(&op.operation_id).unwrap());
    assert_eq!(a.count("recovery_token:"), 1);
    assert_eq!(a.count("capture:"), 1);
    f.owner.handoff.store(true, Ordering::SeqCst);
    remaining.store(0, Ordering::SeqCst);
    assert_eq!(
        restarted.handoff_session(&op.operation_id),
        Err(KernelFailure::Storage)
    );
    assert_eq!(a.count("token_drop:"), 1);
    drop(restarted);
    a.token_drop(2, true);
}

#[test]
fn equal_preparations_keep_distinct_tokens_and_busy_refusal_retains_second() {
    let f = Fixture::new();
    let a = Audit::default();
    let mut engine = open(&f, &a, journal(&f), 1);
    let first = prepare(&mut engine);
    let second = prepare(&mut engine);
    assert_eq!(first.semantics, second.semantics);
    assert_ne!(first.plan_ref.plan_id, second.plan_ref.plan_id);
    let first_input = commit_input(first, 900);
    let second_input = commit_input(second, 901);
    let first_op = commit(&mut engine, first_input.clone());
    rejected(
        command(&mut engine, Command::Commit(second_input.clone())),
        ErrorCode::OperationBusy,
    );
    assert_eq!(a.count("acquire:"), 1);
    assert_eq!(a.count("token_drop:"), 0);
    command(
        &mut engine,
        Command::CancelOperation(CancelOperationInput {
            operation_id: first_op.operation_id.clone(),
            expected_operation_revision: first_op.operation_revision,
        }),
    );
    a.token_drop(1, true);
    let second_op = commit(&mut engine, second_input);
    assert_ne!(first_op.operation_id, second_op.operation_id);
    engine.advance(&second_op.operation_id).unwrap();
    engine.advance(&second_op.operation_id).unwrap();
    a.token_drop(2, true);
    assert_eq!(a.count("capture:"), 2);
    assert_eq!(a.count("acquire:"), 2);
    assert_eq!(
        commit(&mut engine, first_input).operation_id,
        first_op.operation_id
    );
}

#[test]
fn expiry_pruning_drops_only_expired_tokens_and_never_recycles_plan_identity() {
    let f = Fixture::new();
    let a = Audit::default();
    let controls = IdentityControls::default();
    let mut engine = Engine::open(
        Ports::new(&f, &a),
        journal(&f),
        f.clock.clone(),
        FaultIds {
            ids: Ids { next: 1000 },
            controls: controls.clone(),
        },
        config(1),
    )
    .unwrap();
    let first = prepare(&mut engine);
    f.clock.now.store(61_000, Ordering::SeqCst);
    *controls.plan.lock().unwrap() = Some(first.plan_ref.plan_id);
    rejected(
        command(
            &mut engine,
            Command::Prepare(Box::new(PrepareInput { intent: intent() })),
        ),
        ErrorCode::InternalFailure,
    );
    a.token_drop(1, false);
    a.token_drop(2, false);
    assert_eq!(a.count("acquire:"), 0);
    *controls.plan.lock().unwrap() = None;
    let live = prepare(&mut engine);
    let input = commit_input(live, 900);
    controls.fail_operation.store(true, Ordering::SeqCst);
    rejected(
        command(&mut engine, Command::Commit(input.clone())),
        ErrorCode::InternalFailure,
    );
    assert_eq!(a.count("token_drop:"), 2);
    assert_eq!(a.count("acquire:"), 0);
    controls.fail_operation.store(false, Ordering::SeqCst);
    commit(&mut engine, input);
    assert_eq!(a.count("capture:"), 3);
    drop(engine);
    a.token_drop(3, true);
}

#[test]
fn observation_reservation_refusal_keeps_prepared_token_until_exact_expiry() {
    let f = Fixture::new();
    let mut sizing = f.open(1);
    let (_, op) = admitted(&mut sizing);
    let budget = snapshot_with_reserved_recovery(&mut sizing, &op) - 1;
    drop(sizing);
    let a = Audit::default();
    let mut engine = Engine::open_with_observation_budget(
        Ports::new(&f, &a),
        FileJournal::open_existing(&f.root.join("other.journal")).unwrap(),
        f.clock.clone(),
        Ids { next: 2000 },
        config(2),
        budget,
    )
    .unwrap();
    let input = commit_input(prepare(&mut engine), 900);
    let before = std::fs::metadata(f.root.join("other.journal"))
        .unwrap()
        .len();
    rejected(
        command(&mut engine, Command::Commit(input.clone())),
        ErrorCode::OperationBusy,
    );
    assert_eq!(
        std::fs::metadata(f.root.join("other.journal"))
            .unwrap()
            .len(),
        before
    );
    assert_eq!(a.count("token_drop:"), 0);
    assert_eq!(a.count("lease_drop:"), 1);
    assert_eq!(a.count("advance:"), 0);
    f.clock.now.store(61_000, Ordering::SeqCst);
    rejected(
        command(&mut engine, Command::Commit(input)),
        ErrorCode::PlanExpired,
    );
    a.token_drop(1, false);
}
