// Portable reservation controls, not native producer qualification.
use super::*;
use bridge_engine::host::{KernelHost, LocalHost};

fn fixture() -> Fixture {
    let f = Fixture::new();
    f.owner.settlement_required.store(true, Ordering::SeqCst);
    f
}
fn completed(engine: &mut FixtureEngine) -> (CommitInput, OperationSnapshot) {
    let (input, admitted) = admitted(engine);
    engine.advance(&admitted.operation_id).unwrap();
    let terminal = engine.advance(&admitted.operation_id).unwrap();
    assert!(matches!(terminal.state, OperationState::Completed { .. }));
    assert_eq!(engine.pending_settlements(), vec![terminal.operation_id.clone()]);
    (input, terminal)
}
fn deferred(engine: &FixtureEngine, operation: &OperationSnapshot) {
    assert!(matches!(engine.close_disposition().unwrap(), CloseDisposition::Deferred { obligations }
        if obligations.as_slice().contains(&CloseObligation::Operation {
            operation_id: operation.operation_id.clone(),
            operation_revision: operation.operation_revision,
        })));
}

#[test]
fn refusal_retains_terminal_custody_and_conflicts_without_republishing_or_reexecuting() {
    let f = fixture();
    f.owner.settlement_fail.store(true, Ordering::SeqCst);
    let mut engine = f.open(1);
    let (input, terminal) = completed(&mut engine);
    let cursor = engine.cursor();
    for pending in [false, true] {
        f.owner.settlement_fail.store(!pending, Ordering::SeqCst);
        f.owner.settlement_pending.store(pending, Ordering::SeqCst);
        assert_eq!(engine.advance(&terminal.operation_id).unwrap(), terminal);
        assert_eq!(engine.cursor(), cursor);
        deferred(&engine, &terminal);
        assert_eq!(commit(&mut engine, input.clone()), terminal);
        let plan = prepare(&mut engine);
        rejected(command(&mut engine, Command::Commit(commit_input(plan, 8000))), ErrorCode::OperationBusy);
        assert_eq!(f.counts().mutation, 1);
        assert_eq!(f.counts().lease_drops, 0);
    }
    f.owner.settlement_pending.store(false, Ordering::SeqCst);
    // Admission policy remains immutable even if the current composition changes.
    f.owner.settlement_required.store(false, Ordering::SeqCst);
    assert_eq!(engine.advance(&terminal.operation_id).unwrap(), terminal);
    assert!(engine.pending_settlements().is_empty());
    assert_eq!(engine.cursor(), cursor);
    assert_eq!(f.counts().lease_drops, 1);
    assert_eq!(engine.close_disposition().unwrap(), CloseDisposition::Ready);
    assert_eq!(engine.advance(&terminal.operation_id).unwrap(), terminal);
    assert_eq!(f.counts().settlement, 3);
    assert_eq!(f.counts().mutation, 1);
}

#[test]
fn terminal_restart_reacquires_settlement_without_native_recovery_or_completion_replay() {
    let f = fixture();
    let mut engine = f.open(1);
    let (input, terminal) = completed(&mut engine);
    drop(engine);
    let before = f.counts();
    f.owner.settlement_required.store(false, Ordering::SeqCst);
    let mut restarted = f.open(2);
    deferred(&restarted, &terminal);
    assert_eq!(commit(&mut restarted, input), terminal);
    assert_eq!(restarted.advance(&terminal.operation_id).unwrap(), terminal);
    assert_eq!(restarted.cursor().sequence.get(), 0);
    assert_eq!(f.counts().acquisition, before.acquisition + 1);
    assert_eq!(f.counts().mutation, 1);
    assert_eq!(f.counts().recovery, 0);
    assert_eq!(restarted.close_disposition().unwrap(), CloseDisposition::Ready);
    drop(restarted);
    let again = f.open(3);
    assert!(again.pending_settlements().is_empty());
    assert_eq!(again.operation(&terminal.operation_id), Some(&terminal));
    assert_eq!(f.counts().settlement, 1);
}

#[test]
fn cancellation_and_admission_restart_settle_without_beginning_native_work() {
    for restart in [false, true] {
        let f = fixture();
        let mut engine = f.open(1);
        let (_, operation) = admitted(&mut engine);
        if restart {
            drop(engine);
            engine = f.open(2);
        } else {
            command(&mut engine, Command::CancelOperation(CancelOperationInput {
                operation_id: operation.operation_id.clone(),
                expected_operation_revision: operation.operation_revision,
            }));
        }
        let terminal = engine.operation(&operation.operation_id).unwrap().clone();
        assert!(matches!(terminal.state, OperationState::Completed {
            outcome: CompletionOutcome::CancelledBeforeCommit { .. }
        }));
        deferred(&engine, &terminal);
        assert_eq!(engine.advance(&operation.operation_id).unwrap(), terminal);
        assert_eq!(f.counts().staging, 0);
        assert_eq!(f.counts().mutation, 0);
        assert_eq!(f.counts().recovery, 0);
        assert_eq!(f.counts().settlement, 1);
        assert_eq!(engine.close_disposition().unwrap(), CloseDisposition::Ready);
    }
}

#[test]
fn busy_restart_reacquisition_keeps_completed_observation_available() {
    let f = fixture();
    let mut engine = f.open(1);
    let (_, terminal) = completed(&mut engine);
    drop(engine);
    let records = FileJournal::open_existing(&f.root.join("engine.journal")).unwrap().records().to_vec();
    let Some(JournalRecord::Operation { value }) = records.last() else { panic!("terminal") };
    let mut competing_owner = f.owner.clone();
    let competing_lease = competing_owner.acquire(&terminal.semantics, &(), &value.resources).unwrap();
    let mut restarted = f.open(2);
    assert_eq!(restarted.advance(&terminal.operation_id).unwrap(), terminal);
    deferred(&restarted, &terminal);
    assert_eq!(restarted.cursor().sequence.get(), 0);
    assert_eq!(f.counts().settlement, 0);
    drop(competing_lease);
    assert_eq!(restarted.advance(&terminal.operation_id).unwrap(), terminal);
    assert_eq!(restarted.close_disposition().unwrap(), CloseDisposition::Ready);
    assert_eq!(f.counts().mutation, 1);
    assert_eq!(f.counts().recovery, 0);
}

#[test]
fn session_handoff_and_safe_recovery_cannot_discard_an_unsettled_reservation() {
    let f = fixture();
    f.owner.session.store(true, Ordering::SeqCst);
    f.owner.handoff.store(true, Ordering::SeqCst);
    let mut engine = f.open(1);
    let (_, terminal) = completed(&mut engine);
    assert!(engine.handoff_session(&terminal.operation_id).unwrap());
    let terminal = engine.operation(&terminal.operation_id).unwrap().clone();
    deferred(&engine, &terminal);
    assert_eq!(f.counts().lease_drops, 0);
    engine.advance(&terminal.operation_id).unwrap();
    assert_eq!(f.counts().lease_drops, 1);
    assert_eq!(engine.close_disposition().unwrap(), CloseDisposition::Ready);
    drop(engine);

    let f = fixture();
    f.owner.safe_recovery.store(true, Ordering::SeqCst);
    let mut engine = f.open(1);
    let (_, operation) = admitted(&mut engine);
    engine.advance(&operation.operation_id).unwrap();
    let recovery = RecoveryRef {
        operation_id: operation.operation_id.clone(),
        transaction: NativeTransactionRef::new(format!("fixture-owner:{}", operation.operation_id.as_str())).unwrap(),
        target: RecoveryTarget::Launch { target: target() },
    };
    durable_write(&f.owner.tx(&recovery), b"foreign");
    let unresolved = engine.advance(&operation.operation_id).unwrap();
    deferred(&engine, &unresolved);
    assert_eq!(f.counts().lease_drops, 0);
}

#[test]
fn host_pump_drives_terminal_settlement_even_after_the_observer_disconnects() {
    let f = fixture();
    let mut engine = f.open(1);
    let (_, terminal) = completed(&mut engine);
    let cursor = engine.cursor();
    let mut host = KernelHost::new(engine);
    host.pump().unwrap();
    assert!(host.engine_mut().pending_settlements().is_empty());
    assert_eq!(host.cursor(), cursor);
    assert_eq!(host.engine_mut().operation(&terminal.operation_id), Some(&terminal));
    assert_eq!(host.close_disposition().unwrap(), CloseDisposition::Ready);
    assert_eq!(f.counts().mutation, 1);
}

struct AckFaultJournal {
    disk: FileJournal,
    after_write: bool,
}
impl DurableJournal for AckFaultJournal {
    fn records(&self) -> &[JournalRecord] { self.disk.records() }
    fn append(&mut self, record: &JournalRecord) -> Result<(), KernelFailure> {
        if matches!(record, JournalRecord::Settlement { .. }) {
            if self.after_write { self.disk.append(record)?; }
            return Err(KernelFailure::Storage);
        }
        self.disk.append(record)
    }
}

#[test]
fn settlement_ack_failure_retains_custody_and_restart_inspects_the_owner_tombstone() {
    for after_write in [false, true] {
        let f = fixture();
        let mut engine = Engine::open(f.owner.clone(), AckFaultJournal {
            disk: FileJournal::open_existing(&f.root.join("engine.journal")).unwrap(), after_write
        }, f.clock.clone(), Ids { next: 1000 }, config(1)).unwrap();
        let plan = prepare(&mut engine);
        let operation = commit(&mut engine, commit_input(plan, 9000));
        engine.advance(&operation.operation_id).unwrap();
        let terminal = engine.advance(&operation.operation_id).unwrap();
        let cursor = engine.cursor();
        assert_eq!(engine.advance(&operation.operation_id), Err(KernelFailure::Storage));
        assert_eq!(engine.operation(&operation.operation_id), Some(&terminal));
        assert_eq!(engine.cursor(), cursor);
        assert_eq!(engine.advance(&operation.operation_id), Err(KernelFailure::Poisoned));
        assert_eq!(f.counts().lease_drops, 0);
        assert!(matches!(engine.close_disposition().unwrap(), CloseDisposition::Deferred { .. }));
        drop(engine);
        let mut restarted = f.open(2);
        if after_write {
            assert!(restarted.pending_settlements().is_empty());
        } else {
            restarted.advance(&operation.operation_id).unwrap();
        }
        assert_eq!(f.counts().settlement, if after_write { 1 } else { 2 });
        assert_eq!(f.counts().mutation, 1);
        assert_eq!(f.counts().recovery, 0);
        assert_eq!(restarted.operation(&operation.operation_id), Some(&terminal));
        assert_eq!(restarted.close_disposition().unwrap(), CloseDisposition::Ready);
    }
}

struct Records(Vec<JournalRecord>);
impl DurableJournal for Records {
    fn records(&self) -> &[JournalRecord] { &self.0 }
    fn append(&mut self, record: &JournalRecord) -> Result<(), KernelFailure> { self.0.push(record.clone()); Ok(()) }
}
#[test]
fn invalid_or_duplicate_settlement_records_and_changed_admission_policy_fail_closed() {
    let f = fixture();
    let mut engine = f.open(1);
    let (_, terminal) = completed(&mut engine);
    drop(engine);
    let records = FileJournal::open_existing(&f.root.join("engine.journal")).unwrap().records().to_vec();
    for mode in ["wrong_revision", "duplicate", "nonterminal", "unknown", "changed_policy"] {
        let mut damaged = records.clone();
        let mut ack = JournalRecord::Settlement {
            operation_id: terminal.operation_id.clone(), operation_revision: terminal.operation_revision,
        };
        match mode {
            "wrong_revision" => { if let JournalRecord::Settlement { operation_revision, .. } = &mut ack { *operation_revision = RevisionCounter::new(1); } }
            "unknown" => { if let JournalRecord::Settlement { operation_id, .. } = &mut ack { *operation_id = OperationId::new(uuid(99999)).unwrap(); } }
            "nonterminal" => { damaged.truncate(2); if let JournalRecord::Settlement { operation_revision, .. } = &mut ack { *operation_revision = RevisionCounter::new(1); } }
            "duplicate" => damaged.push(ack.clone()),
            "changed_policy" => { if let Some(JournalRecord::Operation { value }) = damaged.last_mut() { value.settlement_required = false; } }
            _ => unreachable!(),
        }
        damaged.push(ack);
        assert!(matches!(Engine::open(f.owner.clone(), Records(damaged), f.clock.clone(), Ids { next: 2000 }, config(2)), Err(KernelFailure::CorruptJournal)), "{mode}");
    }
    let JournalRecord::Operation { value } = &records[1] else { panic!("admission") };
    let mut legacy = serde_json::to_value(value).unwrap();
    legacy.as_object_mut().unwrap().remove("settlement_required");
    assert!(serde_json::from_value::<DurableOperation>(legacy).is_err());
}
