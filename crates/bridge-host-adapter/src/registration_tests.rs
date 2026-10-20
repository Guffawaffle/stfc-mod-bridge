//! Controlled actual engine channel/embedded turns with a test-only !Send host.
//! No Tauri, native service, journal, filesystem/process mutation or game proof.
use super::*;
use bridge_contracts::v1::*;
use bridge_engine::host::{
    DriveControl, EmbeddedOwner, EmbeddedTurn, HostFailure, LocalHost, OwnedFrame, owner_channel,
};
use std::{
    cell::{Cell, RefCell},
    rc::Rc,
};

const EPOCH: &str = "11111111-1111-4111-8111-111111111111";
fn key(n: u64) -> String {
    format!("{EPOCH}:{n}")
}
fn fixture(name: &str) -> &'static [u8] {
    match name {
        "event" => include_bytes!(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../contracts/fixtures/sc-03-working-readiness-event.json"
        )),
        "request" => include_bytes!(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../contracts/fixtures/checkpoint-hello-request.json"
        )),
        "reply" => include_bytes!(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../contracts/fixtures/checkpoint-hello-reply.json"
        )),
        _ => panic!("closed fixture name"),
    }
}
fn base_cursor() -> Cursor {
    let mut cursor = decode_event(fixture("event")).unwrap().into_inner().cursor;
    cursor.sequence = Sequence::new(0);
    cursor
}
#[derive(Default)]
struct Audit {
    pumps: Cell<usize>,
    dispatches: Cell<usize>,
    closes: Cell<usize>,
    drops: Cell<usize>,
    remaining_work: Cell<usize>,
    events: RefCell<Vec<Event>>,
}
struct TestHost {
    audit: Rc<Audit>,
    owner_thread: std::thread::ThreadId,
}
impl TestHost {
    fn assert_owner_thread(&self) {
        assert_eq!(
            std::thread::current().id(),
            self.owner_thread,
            "test owner must remain on its original thread"
        );
    }
}
impl Drop for TestHost {
    fn drop(&mut self) {
        self.assert_owner_thread();
        self.audit.drops.set(self.audit.drops.get() + 1);
    }
}
impl LocalHost for TestHost {
    fn dispatch(&mut self, request: ValidatedRequest) -> Result<ValidatedReply, HostFailure> {
        self.assert_owner_thread();
        self.audit.dispatches.set(self.audit.dispatches.get() + 1);
        self.audit.remaining_work.set(3); // Deliberately synthetic test-only custody.
        let mut reply = decode_reply(fixture("reply")).unwrap().into_inner();
        reply.request_id = ReplyRequestId::new(Some(request.as_inner().request_id.clone()));
        Ok(decode_reply(&serde_json::to_vec(&reply).unwrap()).unwrap())
    }
    fn cursor(&self) -> Cursor {
        self.assert_owner_thread();
        self.audit
            .events
            .borrow()
            .last()
            .map(|event| event.cursor.clone())
            .unwrap_or_else(base_cursor)
    }
    fn pump(&mut self) -> Result<(), HostFailure> {
        self.assert_owner_thread();
        self.audit.pumps.set(self.audit.pumps.get() + 1);
        self.audit
            .remaining_work
            .set(self.audit.remaining_work.get().saturating_sub(1));
        Ok(())
    }
    fn events_after(&mut self, after: &Cursor) -> Result<EventBatch, HostFailure> {
        self.assert_owner_thread();
        let events: Vec<_> = self
            .audit
            .events
            .borrow()
            .iter()
            .filter(|event| event.cursor.sequence > after.sequence)
            .cloned()
            .collect();
        let next = events
            .last()
            .map(|event| event.cursor.clone())
            .unwrap_or_else(|| after.clone());
        Ok(EventBatch {
            after: after.clone(),
            events: BoundedList::new(events).unwrap(),
            next,
        })
    }
    fn request_close(&mut self) -> Result<CloseDisposition, HostFailure> {
        self.assert_owner_thread();
        self.audit.closes.set(self.audit.closes.get() + 1);
        self.close_disposition()
    }
    fn close_disposition(&self) -> Result<CloseDisposition, HostFailure> {
        self.assert_owner_thread();
        if self.audit.remaining_work.get() != 0 {
            return Ok(CloseDisposition::Deferred {
                obligations: BoundedList::new(vec![CloseObligation::Operation {
                    operation_id: OperationId::new("33333333-3333-4333-8333-333333333333").unwrap(),
                    operation_revision: RevisionCounter::new(1),
                }])
                .unwrap(),
            });
        }
        Ok(CloseDisposition::Ready)
    }
}
fn setup() -> (
    Arc<Registry>,
    HostHandle,
    Rc<Audit>,
    EmbeddedOwner<TestHost>,
    Instant,
) {
    let registry = Registry::new(TrustedEpoch::from_root(EPOCH).unwrap());
    let (handle, inbox) = owner_channel();
    let audit = Rc::new(Audit::default());
    let local = audit.clone();
    let owner = EmbeddedOwner::on_current_thread(inbox, || {
        Ok(TestHost {
            audit: local,
            owner_thread: std::thread::current().id(),
        })
    })
    .unwrap();
    (registry, handle, audit, owner, Instant::now())
}
fn progress(owner: &EmbeddedOwner<TestHost>) {
    assert!(matches!(
        owner.turn(|_| Ok(DriveControl::Continue)),
        EmbeddedTurn::Retained { .. }
    ));
}
fn finish(owner: &EmbeddedOwner<TestHost>, audit: &Audit) {
    owner.signal_close();
    let mut closed = false;
    for _ in 0..8 {
        if matches!(
            owner.turn(|_| Ok(DriveControl::Continue)),
            EmbeddedTurn::Closed(_)
        ) {
            closed = true;
            break;
        }
    }
    assert!(closed, "test-only owner must close safely");
    assert_eq!(audit.drops.get(), 1);
}
fn work(registry: &Arc<Registry>, n: u64, now: Instant) -> PrepareWork {
    match registry.begin_subscribe(&key(n), now).unwrap() {
        Subscribe::Prepare(work) => work,
        Subscribe::AlreadyReady(_) => panic!("new ordinal"),
    }
}
fn ready(
    registry: &Arc<Registry>,
    handle: &HostHandle,
    owner: &EmbeddedOwner<TestHost>,
    n: u64,
    now: Instant,
) -> AckDto {
    let mut wait = work(registry, n, now).attach(handle, now).unwrap();
    assert!(matches!(wait.step(now).unwrap(), ReadyStep::Pending));
    progress(owner);
    match wait.step(now).unwrap() {
        ReadyStep::Ready(ack) => ack,
        ReadyStep::Pending => panic!("owner processed readiness"),
    }
}
fn append(audit: &Audit, count: u64) {
    for _ in 0..count {
        let mut event = decode_event(fixture("event")).unwrap().into_inner();
        event.cursor.sequence = Sequence::new(audit.events.borrow().len() as u64 + 1);
        // Every event still traverses the actual engine event codec.
        let event = decode_event(&serde_json::to_vec(&event).unwrap())
            .unwrap()
            .into_inner();
        audit.events.borrow_mut().push(event);
    }
}
fn waiting(value: Unsubscribe) -> CloseWait {
    match value {
        Unsubscribe::Waiting(wait) => wait,
        Unsubscribe::Closed(_) => panic!("custody has not ended"),
    }
}
fn closed(value: Unsubscribe) -> AckDto {
    match value {
        Unsubscribe::Closed(ack) => ack,
        Unsubscribe::Waiting(_) => panic!("custody has ended"),
    }
}
fn poll_value(registry: &Arc<Registry>, n: u64, now: Instant) -> serde_json::Value {
    let mut poll = registry.begin_poll(&key(n), now).unwrap();
    poll.read_one(now).unwrap();
    serde_json::from_str(&poll.serialize_json(now).unwrap()).unwrap()
}

#[test]
fn registration_state_is_send_sync_without_a_local_owner() {
    fn send<T: Send>() {}
    fn send_sync<T: Send + Sync>() {}
    send_sync::<Registry>();
    send::<PrepareWork>();
    send::<PreparingWait>();
    send::<PollLease>();
    send::<CloseWait>();
}

#[test]
fn key_parser_matches_canonical_frontend_epoch_and_nonzero_u64() {
    let epoch = TrustedEpoch::from_root(EPOCH).unwrap();
    assert_eq!(epoch.ordinal(&key(1)).unwrap(), 1);
    assert_eq!(epoch.ordinal(&key(u64::MAX)).unwrap(), u64::MAX);
    for suffix in [
        "",
        "0",
        "00",
        "01",
        "+1",
        "-1",
        "1.0",
        "1 ",
        "1\n",
        "18446744073709551616",
        "１２",
    ] {
        assert!(
            epoch.ordinal(&format!("{EPOCH}:{suffix}")).is_err(),
            "{suffix}"
        );
    }
    assert!(
        epoch
            .ordinal("11111111-1111-4111-8111-111111111112:1")
            .is_err()
    );
    for value in [
        "11111111-1111-0111-8111-111111111111",
        "11111111-1111-9111-8111-111111111111",
        "11111111-1111-4111-7111-111111111111",
        "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA",
    ] {
        assert!(TrustedEpoch::from_root(value).is_err());
    }
}

#[test]
fn control_arguments_and_faults_have_the_exact_frontend_closed_shape() {
    let valid = format!(r#"{{"registrationKey":"{}"}}"#, key(1));
    assert_eq!(
        serde_json::from_str::<KeyArgs>(&valid)
            .unwrap()
            .registration_key(),
        key(1)
    );
    for invalid in [
        r#"{}"#,
        r#"{"registrationKey":1}"#,
        r#"{"registrationKey":"x","path":"y"}"#,
        r#"{"registrationKey":"a","registrationKey":"b"}"#,
    ] {
        assert!(serde_json::from_str::<KeyArgs>(invalid).is_err());
    }
    for code in [
        FaultCode::UnavailableBinding,
        FaultCode::Disconnected,
        FaultCode::DeliveryFailed,
    ] {
        for delivery in [Delivery::NotSent, Delivery::MayHaveReachedBackend] {
            let dto = FaultDto {
                schema_version: 1,
                code,
                delivery,
            };
            let bytes = serde_json::to_vec(&dto).unwrap();
            assert!(bytes.len() < 128);
            let value: serde_json::Value = serde_json::from_slice(&bytes).unwrap();
            assert_eq!(value.as_object().unwrap().len(), 3);
            assert_eq!(value["schemaVersion"], 1);
        }
    }
}

#[test]
fn readiness_requires_the_actual_engine_watermark_ack() {
    let (registry, handle, audit, owner, now) = setup();
    let mut wait = work(&registry, 1, now).attach(&handle, now).unwrap();
    assert!(matches!(wait.step(now).unwrap(), ReadyStep::Pending));
    assert!(registry.begin_poll(&key(1), now).is_err());
    assert_eq!(registry.snapshot().unwrap().preparing, 1);
    progress(&owner);
    let ack = match wait.step(now).unwrap() {
        ReadyStep::Ready(ack) => ack,
        _ => panic!("real ACK"),
    };
    registry.confirm_ready_ack(&ack, now).unwrap();
    assert_eq!(
        serde_json::to_value(&ack).unwrap(),
        serde_json::json!({"schemaVersion":1,"registrationKey":key(1)})
    );
    assert!(wait.step(now).is_err());
    registry.revoke().unwrap();
    finish(&owner, &audit);
}

#[test]
fn unsubscribe_before_subscribe_fences_unknown_keys_but_keeps_earlier_active_keys() {
    let (registry, handle, audit, owner, now) = setup();
    ready(&registry, &handle, &owner, 1, now);
    closed(registry.unsubscribe(&key(5)).unwrap());
    for ordinal in [2, 3, 4, 5] {
        assert!(registry.begin_subscribe(&key(ordinal), now).is_err());
    }
    assert_eq!(
        poll_value(&registry, 1, now)["frames"],
        serde_json::json!([])
    );
    ready(&registry, &handle, &owner, 6, now);
    registry.revoke().unwrap();
    finish(&owner, &audit);
}

#[test]
fn preparing_duplicate_is_refused_and_ready_duplicate_never_adds_an_observer_or_renews_idle() {
    let (registry, handle, audit, owner, now) = setup();
    let first = work(&registry, 1, now);
    assert!(registry.begin_subscribe(&key(1), now).is_err());
    let mut wait = first.attach(&handle, now).unwrap();
    progress(&owner);
    assert!(matches!(wait.step(now).unwrap(), ReadyStep::Ready(_)));
    assert!(matches!(
        registry
            .begin_subscribe(&key(1), now + Duration::from_secs(29))
            .unwrap(),
        Subscribe::AlreadyReady(_)
    ));
    assert_eq!(registry.snapshot().unwrap().ready, 1);
    assert_eq!(registry.expire(now + IDLE_TIMEOUT).unwrap(), 1);
    assert!(registry.begin_subscribe(&key(1), now).is_err());
    finish(&owner, &audit);
}

#[test]
fn eight_actual_ready_subscriptions_refuse_ninth_and_real_cleanup_releases_capacity() {
    let (registry, handle, audit, owner, now) = setup();
    for ordinal in 1..=8 {
        ready(&registry, &handle, &owner, ordinal, now);
    }
    assert_eq!(registry.snapshot().unwrap().ready, 8);
    assert!(registry.begin_subscribe(&key(9), now).is_err());
    closed(registry.unsubscribe(&key(1)).unwrap());
    assert_eq!(registry.snapshot().unwrap().ready, 7);
    assert!(registry.begin_subscribe(&key(9), now).is_err());
    ready(&registry, &handle, &owner, 10, now);
    assert_eq!(registry.snapshot().unwrap().ready, 8);
    registry.revoke().unwrap();
    finish(&owner, &audit);
}

#[test]
fn preparing_deadline_begins_at_admission_and_closing_waits_for_actual_unscheduled_work_drop() {
    let (registry, _handle, audit, owner, now) = setup();
    let mut works = Vec::new();
    for n in 1..=8 {
        works.push(work(&registry, n, now));
    }
    assert!(registry.begin_subscribe(&key(9), now).is_err());
    assert_eq!(registry.snapshot().unwrap().preparing, 8);
    assert_eq!(registry.expire(now + PREPARING_TIMEOUT).unwrap(), 8);
    assert_eq!(registry.snapshot().unwrap().closing, 8);
    assert!(
        registry
            .begin_subscribe(&key(10), now + PREPARING_TIMEOUT)
            .is_err()
    );
    drop(works.pop());
    assert_eq!(registry.snapshot().unwrap().closing, 7);
    let replacement = work(&registry, 11, now + PREPARING_TIMEOUT);
    assert_eq!(registry.snapshot().unwrap().preparing, 1);
    drop(replacement);
    drop(works);
    assert_eq!(registry.snapshot().unwrap().closing, 0);
    assert!(
        registry
            .begin_subscribe(&key(9), now + PREPARING_TIMEOUT)
            .is_err()
    );
    finish(&owner, &audit);
}

#[test]
fn delayed_worker_cannot_reset_its_five_second_deadline_or_enqueue_after_expiry() {
    let (registry, handle, audit, owner, now) = setup();
    let prepare = work(&registry, 1, now);
    assert!(prepare.attach(&handle, now + PREPARING_TIMEOUT).is_err());
    assert_eq!(registry.snapshot().unwrap().preparing, 0);
    assert_eq!(registry.snapshot().unwrap().closing, 0);
    progress(&owner);
    assert_eq!(audit.dispatches.get(), 0);
    finish(&owner, &audit);
}

#[test]
fn preparing_unsubscribe_and_late_actual_ack_cannot_resurrect_the_key() {
    let (registry, handle, audit, owner, now) = setup();
    let mut prepare = work(&registry, 1, now).attach(&handle, now).unwrap();
    let close = waiting(registry.unsubscribe(&key(1)).unwrap());
    assert!(close.poll_closed().is_none());
    assert_eq!(registry.snapshot().unwrap().closing, 1);
    progress(&owner);
    assert!(prepare.step(now).is_err());
    assert!(close.poll_closed().is_none());
    drop(prepare);
    assert!(close.poll_closed().is_some());
    assert_eq!(registry.snapshot().unwrap().closing, 0);
    assert!(registry.begin_subscribe(&key(1), now).is_err());
    ready(&registry, &handle, &owner, 2, now);
    drop(close);
    registry.revoke().unwrap();
    finish(&owner, &audit);
}

#[test]
fn ready_response_must_recheck_exact_key_and_document_liveness_before_root_publication() {
    let (registry, handle, audit, owner, now) = setup();
    let ack = ready(&registry, &handle, &owner, 1, now);
    registry.revoke().unwrap();
    assert!(registry.confirm_ready_ack(&ack, now).is_err());
    assert!(registry.begin_subscribe(&key(2), now).is_err());
    assert!(registry.begin_poll(&key(1), now).is_err());
    assert_eq!(registry.snapshot().unwrap().ready, 0);
    finish(&owner, &audit);
}

#[test]
fn close_waiters_are_bounded_until_actual_waiter_drop_even_after_observer_cleanup() {
    let (registry, handle, audit, owner, now) = setup();
    let mut waits = Vec::new();
    let mut prepares = Vec::new();
    for n in 1..=8 {
        prepares.push(work(&registry, n, now).attach(&handle, now).unwrap());
        waits.push(waiting(registry.unsubscribe(&key(n)).unwrap()));
        waits.push(waiting(registry.unsubscribe(&key(n)).unwrap()));
        assert!(registry.unsubscribe(&key(n)).is_err());
    }
    assert_eq!(registry.snapshot().unwrap().close_waiters, 16);
    drop(prepares);
    assert!(waits.iter().all(|w| w.poll_closed().is_some()));
    assert_eq!(registry.snapshot().unwrap().closing, 0);
    assert_eq!(registry.snapshot().unwrap().close_waiters, 16);
    let extra = work(&registry, 9, now);
    assert!(registry.unsubscribe(&key(9)).is_err());
    drop(extra);
    drop(waits);
    assert_eq!(registry.snapshot().unwrap().close_waiters, 0);
    finish(&owner, &audit);
}

#[test]
fn cleanup_timeout_is_uncertain_and_cannot_publish_a_success_before_real_drop() {
    let (registry, handle, audit, owner, now) = setup();
    let prepare = work(&registry, 1, now).attach(&handle, now).unwrap();
    let close = waiting(registry.unsubscribe(&key(1)).unwrap());
    assert_eq!(
        close.wait_timeout(Duration::ZERO).unwrap_err().delivery(),
        Delivery::MayHaveReachedBackend
    );
    assert_eq!(registry.snapshot().unwrap().closing, 1);
    drop(prepare);
    close.wait_timeout(Duration::ZERO).unwrap();
    drop(close);
    finish(&owner, &audit);
}

#[test]
fn only_a_valid_poll_renews_ready_idle_and_expiry_needs_no_renderer_call() {
    let (registry, handle, audit, owner, now) = setup();
    ready(&registry, &handle, &owner, 1, now);
    assert_eq!(registry.next_deadline().unwrap(), Some(now + IDLE_TIMEOUT));
    assert!(
        registry
            .begin_poll(&key(99), now + Duration::from_secs(29))
            .is_err()
    );
    assert_eq!(registry.next_deadline().unwrap(), Some(now + IDLE_TIMEOUT));
    poll_value(&registry, 1, now + Duration::from_secs(29));
    assert_eq!(
        registry.next_deadline().unwrap(),
        Some(now + Duration::from_secs(59))
    );
    assert_eq!(registry.expire(now + Duration::from_secs(30)).unwrap(), 0);
    assert_eq!(registry.expire(now + Duration::from_secs(59)).unwrap(), 1);
    assert_eq!(registry.next_deadline().unwrap(), None);
    finish(&owner, &audit);
}

#[test]
fn global_poll_survives_expiry_and_retirement_until_actual_native_lease_drop() {
    let (registry, handle, audit, owner, now) = setup();
    ready(&registry, &handle, &owner, 1, now);
    let mut poll = registry.begin_poll(&key(1), now).unwrap();
    poll.read_one(now).unwrap();
    poll.serialize_json(now).unwrap();
    ready(&registry, &handle, &owner, 2, now + Duration::from_secs(20));
    assert!(
        registry
            .begin_poll(&key(2), now + Duration::from_secs(20))
            .is_err()
    );
    assert_eq!(registry.expire(now + Duration::from_secs(31)).unwrap(), 1);
    let close = waiting(registry.unsubscribe(&key(1)).unwrap());
    assert!(close.poll_closed().is_none());
    assert!(registry.snapshot().unwrap().poll_reserved);
    assert!(
        registry
            .begin_poll(&key(2), now + Duration::from_secs(31))
            .is_err()
    );
    drop(poll);
    assert!(close.poll_closed().is_some());
    assert!(!registry.snapshot().unwrap().poll_reserved);
    assert_eq!(
        poll_value(&registry, 2, now + Duration::from_secs(31))["frames"],
        serde_json::json!([])
    );
    drop(close);
    registry.revoke().unwrap();
    finish(&owner, &audit);
}

#[test]
fn one_frame_poll_retains_order_and_uses_the_shared_codec_without_lookahead() {
    let (registry, handle, audit, owner, now) = setup();
    ready(&registry, &handle, &owner, 1, now);
    append(&audit, 2);
    progress(&owner);
    for expected in 1..=2 {
        let value = poll_value(&registry, 1, now);
        assert_eq!(value.as_object().unwrap().len(), 3);
        let frames = value["frames"].as_array().unwrap();
        assert_eq!(frames.len(), 1);
        let event = decode_event(frames[0].as_str().unwrap().as_bytes()).unwrap();
        assert_eq!(event.as_inner().cursor.sequence.get(), expected);
    }
    assert_eq!(
        poll_value(&registry, 1, now)["frames"],
        serde_json::json!([])
    );
    registry.revoke().unwrap();
    finish(&owner, &audit);
}

#[test]
fn sticky_engine_overflow_fault_precedes_queued_frames_and_closes_the_generation() {
    let (registry, handle, audit, owner, now) = setup();
    ready(&registry, &handle, &owner, 1, now);
    append(&audit, 17);
    progress(&owner);
    let mut poll = registry.begin_poll(&key(1), now).unwrap();
    assert_eq!(
        poll.read_one(now).unwrap_err().delivery(),
        Delivery::MayHaveReachedBackend
    );
    assert_eq!(registry.snapshot().unwrap().closing, 1);
    assert!(poll.serialize_json(now).is_err());
    drop(poll);
    assert_eq!(registry.snapshot().unwrap().closing, 0);
    assert!(registry.begin_poll(&key(1), now).is_err());
    ready(&registry, &handle, &owner, 2, now);
    registry.revoke().unwrap();
    finish(&owner, &audit);
}

#[test]
fn poll_abandoned_after_pop_retires_instead_of_concealing_the_consumed_event() {
    let (registry, handle, audit, owner, now) = setup();
    ready(&registry, &handle, &owner, 1, now);
    append(&audit, 1);
    progress(&owner);
    let mut poll = registry.begin_poll(&key(1), now).unwrap();
    poll.read_one(now).unwrap();
    drop(poll);
    assert!(registry.begin_poll(&key(1), now).is_err());
    assert!(registry.begin_subscribe(&key(1), now).is_err());
    assert!(!registry.snapshot().unwrap().poll_reserved);
    ready(&registry, &handle, &owner, 2, now);
    registry.revoke().unwrap();
    finish(&owner, &audit);
}

#[test]
fn revoke_during_a_poll_suppresses_serialization_and_preserves_reservation_until_drop() {
    let (registry, handle, audit, owner, now) = setup();
    ready(&registry, &handle, &owner, 1, now);
    let mut poll = registry.begin_poll(&key(1), now).unwrap();
    poll.read_one(now).unwrap();
    registry.revoke().unwrap();
    assert!(poll.serialize_json(now).is_err());
    assert!(registry.snapshot().unwrap().poll_reserved);
    assert_eq!(registry.snapshot().unwrap().closing, 1);
    drop(poll);
    assert!(!registry.snapshot().unwrap().poll_reserved);
    assert_eq!(registry.snapshot().unwrap().closing, 0);
    finish(&owner, &audit);
}

#[test]
fn maximal_scalar_frame_json_escaping_stays_inside_the_declared_native_dto_budget() {
    for frame in [
        "\0".repeat(MAX_FRAME_BYTES),
        "🖖".repeat(MAX_FRAME_BYTES / 4),
    ] {
        assert_eq!(frame.len(), MAX_FRAME_BYTES);
        let dto = PollDto {
            schema_version: 1,
            registration_key: key(u64::MAX),
            frames: OneFrame(Some(frame.clone())),
        };
        let json = serde_json::to_string(&dto).unwrap();
        assert!(json.len() < MAX_POLL_DTO_BYTES);
        assert_eq!(
            serde_json::from_str::<serde_json::Value>(&json).unwrap()["frames"][0],
            frame
        );
    }
}

#[test]
fn independent_observer_expiry_never_cancels_admitted_work_or_drops_the_owner() {
    let (registry, handle, audit, owner, now) = setup();
    ready(&registry, &handle, &owner, 1, now);
    let reply = handle
        .exchange(OwnedFrame::new(fixture("request").to_vec()).unwrap())
        .unwrap();
    progress(&owner);
    assert!(reply.try_recv().unwrap().is_ok());
    assert_eq!(audit.dispatches.get(), 1);
    assert_eq!(audit.remaining_work.get(), 2);
    registry.expire(now + IDLE_TIMEOUT).unwrap();
    assert_eq!(registry.snapshot().unwrap().ready, 0);
    assert_eq!(audit.closes.get(), 0);
    assert_eq!(audit.drops.get(), 0);
    progress(&owner);
    progress(&owner);
    assert_eq!(audit.remaining_work.get(), 0);
    assert_eq!(audit.dispatches.get(), 1);
    assert_eq!(audit.closes.get(), 0);
    assert_eq!(audit.drops.get(), 0);
    finish(&owner, &audit);
}

#[test]
fn lost_poll_response_requires_frontend_retirement_and_never_replays_backend_exchange() {
    let (registry, handle, audit, owner, now) = setup();
    ready(&registry, &handle, &owner, 1, now);
    append(&audit, 1);
    progress(&owner);
    let _unreceived_response = poll_value(&registry, 1, now); // Native serialization is not JS receipt.
    closed(registry.unsubscribe(&key(1)).unwrap());
    ready(&registry, &handle, &owner, 2, now);
    assert_eq!(audit.dispatches.get(), 0);
    assert!(registry.begin_poll(&key(1), now).is_err());
    assert_eq!(
        poll_value(&registry, 2, now)["frames"],
        serde_json::json!([])
    );
    registry.revoke().unwrap();
    finish(&owner, &audit);
}

#[test]
fn a_fixed_clock_at_the_preparing_deadline_refuses_without_a_zero_wait_spin() {
    struct Fixed(Instant);
    impl RegistryClock for Fixed {
        fn now(&self) -> Instant {
            self.0
        }
    }
    let (registry, handle, audit, owner, now) = setup();
    let wait = work(&registry, 1, now).attach(&handle, now).unwrap();
    assert!(wait.wait_to_ready(&Fixed(now + PREPARING_TIMEOUT)).is_err());
    assert_eq!(registry.snapshot().unwrap().preparing, 0);
    finish(&owner, &audit);
}

#[test]
fn blocking_readiness_rechecks_elapsed_time_after_the_actual_ack_before_installing_ready() {
    struct Crossing {
        now: Instant,
        reads: AtomicUsize,
    }
    impl RegistryClock for Crossing {
        fn now(&self) -> Instant {
            if self.reads.fetch_add(1, Ordering::AcqRel) == 0 {
                self.now
            } else {
                self.now + PREPARING_TIMEOUT
            }
        }
    }
    let (registry, handle, audit, owner, now) = setup();
    let wait = work(&registry, 1, now).attach(&handle, now).unwrap();
    progress(&owner);
    let clock = Crossing {
        now,
        reads: AtomicUsize::new(0),
    };
    assert!(wait.wait_to_ready(&clock).is_err());
    assert_eq!(registry.snapshot().unwrap().ready, 0);
    assert_eq!(registry.snapshot().unwrap().closing, 0);
    assert!(registry.begin_subscribe(&key(1), now).is_err());
    finish(&owner, &audit);
}

// Additional source-only controls. No implementation hooks or native services.
const CONTROL_TIMEOUT: Duration = Duration::from_secs(2);

/// Own and join each worker, including panic/failure paths. Every worker used
/// below has only bounded channel/condvar waits or the real five-second
/// readiness deadline. Drop first revokes observation and releases its gate;
/// no worker receives LocalHost, EmbeddedOwner, Audit, service or native custody.
struct OwnedControlThread<T> {
    worker: Option<std::thread::JoinHandle<T>>,
    registry: Arc<Registry>,
    release: Option<std::sync::mpsc::SyncSender<()>>,
}
impl<T> OwnedControlThread<T> {
    fn spawn<F>(
        registry: Arc<Registry>,
        release: Option<std::sync::mpsc::SyncSender<()>>,
        worker: F,
    ) -> Self
    where
        T: Send + 'static,
        F: FnOnce() -> T + Send + 'static,
    {
        Self {
            worker: Some(std::thread::spawn(worker)),
            registry,
            release,
        }
    }
    fn release(&mut self) {
        if let Some(sender) = self.release.take() {
            let _ = sender.try_send(());
        }
    }
    fn join(mut self) -> std::thread::Result<T> {
        self.release();
        self.worker.take().expect("one owned worker").join()
    }
}
impl<T> Drop for OwnedControlThread<T> {
    fn drop(&mut self) {
        if self.worker.is_some() {
            let _ = self.registry.revoke();
            self.release();
            if let Some(worker) = self.worker.take() {
                let _ = worker.join();
            }
        }
    }
}

/// Checkpoint1 is before any engine ACK can be received. Checkpoint2 is reached
/// only after wait_to_ready received the actual ACK and before install_ack.
/// After its finite test gate, this clock ALWAYS reads real monotonic time;
/// it cannot freeze a readiness deadline and create an unbounded wait loop.
struct ReadinessCheckpoint {
    call: usize,
    calls: AtomicUsize,
    entered: std::sync::mpsc::SyncSender<()>,
    release: Mutex<std::sync::mpsc::Receiver<()>>,
}
impl RegistryClock for ReadinessCheckpoint {
    fn now(&self) -> Instant {
        let call = self.calls.fetch_add(1, Ordering::AcqRel) + 1;
        if call == self.call {
            let _ = self.entered.try_send(());
            let _ = self
                .release
                .lock()
                .expect("test clock mutex")
                .recv_timeout(CONTROL_TIMEOUT);
        }
        Instant::now()
    }
}
#[derive(Clone, Copy)]
enum Retirement {
    Unsubscribe,
    Revoke,
}

fn readiness_crossing_control(call: usize, retirement: Retirement) {
    let (registry, handle, audit, owner, now) = setup();
    let owner_thread = std::thread::current().id();
    let wait = work(&registry, 1, now).attach(&handle, now).unwrap();
    let (entered_tx, entered_rx) = std::sync::mpsc::sync_channel(1);
    let (release_tx, release_rx) = std::sync::mpsc::sync_channel(1);
    let clock = ReadinessCheckpoint {
        call,
        calls: AtomicUsize::new(0),
        entered: entered_tx,
        release: Mutex::new(release_rx),
    };
    if call == 2 {
        // Queue the actual owner ACK BEFORE starting the worker. Thus its first
        // receive cannot timeout and turn checkpoint2 into a later loop-top
        // clock read. Reaching checkpoint2 proves this genuine ACK was consumed.
        progress(&owner);
    }
    let worker = OwnedControlThread::spawn(registry.clone(), Some(release_tx), move || {
        (std::thread::current().id(), wait.wait_to_ready(&clock))
    });
    entered_rx
        .recv_timeout(CONTROL_TIMEOUT)
        .expect("bounded readiness checkpoint");
    if matches!(retirement, Retirement::Revoke) {
        assert_eq!(registry.revoke().unwrap(), 1);
    }
    let close = waiting(registry.unsubscribe(&key(1)).unwrap());
    assert!(close.poll_closed().is_none());
    assert_eq!(registry.snapshot().unwrap().closing, 1);
    assert_eq!(registry.snapshot().unwrap().ready, 0);
    if call == 1 {
        // Retirement happened BEFORE this actual engine subscribe turn/ACK.
        // Worker still retains the actual receiver at its clock checkpoint.
        progress(&owner);
    }
    assert!(
        close.poll_closed().is_none(),
        "a phase flag cannot replace receiver disposal"
    );
    let (worker_thread, result) = worker.join().expect("owned bounded readiness worker");
    assert_ne!(worker_thread, owner_thread);
    let failure = result.expect_err("retired readiness cannot install Ready");
    assert_eq!(failure.delivery(), Delivery::MayHaveReachedBackend);
    if matches!(retirement, Retirement::Revoke) && call == 2 {
        assert_eq!(failure.code(), FaultCode::UnavailableBinding);
    }
    assert!(close.poll_closed().is_some());
    assert_eq!(registry.snapshot().unwrap().closing, 0);
    assert_eq!(registry.snapshot().unwrap().ready, 0);
    assert!(registry.begin_subscribe(&key(1), Instant::now()).is_err());
    assert_eq!(audit.dispatches.get(), 0);
    assert_eq!(audit.closes.get(), 0);
    assert_eq!(audit.drops.get(), 0);
    drop(close);
    assert_eq!(registry.snapshot().unwrap().close_waiters, 0);
    registry.revoke().unwrap();
    finish(&owner, &audit);
}

#[test]
fn explicit_abandon_after_serialization_retires_consumed_stream_with_global_poll_retained() {
    let (registry, handle, audit, owner, now) = setup();
    ready(&registry, &handle, &owner, 1, now);
    ready(&registry, &handle, &owner, 2, now);
    append(&audit, 1);
    progress(&owner);
    let mut poll = registry.begin_poll(&key(1), now).unwrap();
    poll.read_one(now).unwrap();
    let response: serde_json::Value =
        serde_json::from_str(&poll.serialize_json(now).unwrap()).unwrap();
    let frames = response["frames"].as_array().unwrap();
    assert_eq!(frames.len(), 1);
    assert_eq!(
        decode_event(frames[0].as_str().unwrap().as_bytes())
            .unwrap()
            .as_inner()
            .cursor
            .sequence
            .get(),
        1
    );
    poll.abandon_response(); // Explicit failure AFTER successful native serialization.
    let snapshot = registry.snapshot().unwrap();
    assert!(snapshot.poll_reserved);
    assert_eq!(snapshot.closing, 1);
    assert_eq!(snapshot.ready, 1);
    assert!(registry.begin_poll(&key(2), now).is_err());
    let close = waiting(registry.unsubscribe(&key(1)).unwrap());
    assert!(close.poll_closed().is_none());
    assert!(poll.serialize_json(now).is_err());
    drop(poll);
    assert!(close.poll_closed().is_some());
    assert!(!registry.snapshot().unwrap().poll_reserved);
    assert!(registry.begin_poll(&key(1), now).is_err());
    assert!(registry.begin_subscribe(&key(1), now).is_err());
    let next = poll_value(&registry, 2, now);
    assert_eq!(next["frames"].as_array().unwrap().len(), 1);
    // A different already-ready subscription keeps its own engine observation.
    assert_eq!(audit.dispatches.get(), 0);
    assert_eq!(audit.closes.get(), 0);
    assert_eq!(audit.drops.get(), 0);
    drop(close);
    registry.revoke().unwrap();
    finish(&owner, &audit);
}

#[test]
fn mutex_poison_is_permanent_and_never_fabricates_subscription_cleanup_ack() {
    let (registry, handle, audit, owner, now) = setup();
    let ack = ready(&registry, &handle, &owner, 1, now);
    let mut poll = registry.begin_poll(&key(1), now).unwrap();
    poll.read_one(now).unwrap();
    poll.serialize_json(now).unwrap();
    let prepare = work(&registry, 2, now).attach(&handle, now).unwrap();
    let close = waiting(registry.unsubscribe(&key(2)).unwrap());
    assert!(close.poll_closed().is_none());
    let poisoned = registry.clone();
    let worker: OwnedControlThread<()> =
        OwnedControlThread::spawn(registry.clone(), None, move || {
            let _held = poisoned.state.lock().expect("unpoisoned test registry");
            panic!("controlled registry mutex poison");
        });
    assert!(worker.join().is_err());
    for attempt in 0..2 {
        assert_eq!(
            registry.snapshot().unwrap_err().code(),
            FaultCode::UnavailableBinding
        );
        assert_eq!(
            registry.next_deadline().unwrap_err().code(),
            FaultCode::UnavailableBinding
        );
        assert!(registry.begin_subscribe(&key(3 + attempt), now).is_err());
        assert!(registry.begin_poll(&key(1), now).is_err());
        assert!(registry.confirm_ready_ack(&ack, now).is_err());
        assert!(registry.unsubscribe(&key(1)).is_err());
        assert!(
            registry.unsubscribe(&key(99)).is_err(),
            "unknown key cannot fabricate a Closed ACK under poison"
        );
        assert!(registry.expire(now + IDLE_TIMEOUT).is_err());
        assert!(registry.revoke().is_err());
        let failure = close.wait_timeout(Duration::ZERO).unwrap_err();
        assert_eq!(failure.code(), FaultCode::UnavailableBinding);
        assert_eq!(failure.delivery(), Delivery::MayHaveReachedBackend);
    }
    drop(prepare);
    drop(poll); // Actual receivers dispose, but poison cannot prove/ACK the reservation state.
    assert!(close.poll_closed().is_none());
    assert!(close.wait_timeout(Duration::ZERO).is_err());
    assert!(registry.snapshot().is_err());
    assert!(registry.begin_subscribe(&key(100), now).is_err());
    assert_eq!(audit.dispatches.get(), 0);
    assert_eq!(audit.closes.get(), 0);
    assert_eq!(audit.drops.get(), 0);
    drop(close);
    finish(&owner, &audit);
}

#[test]
fn off_owner_readiness_unsubscribe_before_engine_ack_retains_closing_until_real_drop() {
    readiness_crossing_control(1, Retirement::Unsubscribe);
}
#[test]
fn off_owner_readiness_revoke_before_engine_ack_cannot_restore_document() {
    readiness_crossing_control(1, Retirement::Revoke);
}
#[test]
fn off_owner_readiness_unsubscribe_after_engine_ack_before_install_is_fenced() {
    readiness_crossing_control(2, Retirement::Unsubscribe);
}
#[test]
fn off_owner_readiness_revoke_after_engine_ack_before_install_is_fenced() {
    readiness_crossing_control(2, Retirement::Revoke);
}

#[test]
fn close_wait_real_notification_and_drop_follow_actual_global_poll_retirement() {
    let (registry, handle, audit, owner, now) = setup();
    ready(&registry, &handle, &owner, 1, now);
    ready(&registry, &handle, &owner, 2, now);
    append(&audit, 1);
    progress(&owner);
    let mut poll = registry.begin_poll(&key(1), now).unwrap();
    poll.read_one(now).unwrap();
    let response: serde_json::Value =
        serde_json::from_str(&poll.serialize_json(now).unwrap()).unwrap();
    assert_eq!(response["frames"].as_array().unwrap().len(), 1);
    let (poll_entered_tx, poll_entered_rx) = std::sync::mpsc::sync_channel(1);
    let (release_tx, release_rx) = std::sync::mpsc::sync_channel(1);
    let poll_worker = OwnedControlThread::spawn(registry.clone(), Some(release_tx), move || {
        let _ = poll_entered_tx.try_send(());
        let _ = release_rx.recv_timeout(CONTROL_TIMEOUT);
        drop(poll); // Real receiver disposition/global quota release, off-owner.
    });
    poll_entered_rx
        .recv_timeout(CONTROL_TIMEOUT)
        .expect("bounded retained poll checkpoint");
    let close = waiting(registry.unsubscribe(&key(1)).unwrap());
    assert!(close.poll_closed().is_none());
    assert!(registry.snapshot().unwrap().poll_reserved);
    assert!(registry.begin_poll(&key(2), now).is_err());
    let (waiting_tx, waiting_rx) = std::sync::mpsc::sync_channel(1);
    let waiter = OwnedControlThread::spawn(registry.clone(), None, move || {
        // Signal WHILE holding the same mutex used by actual PollLease drop.
        // The drop cannot complete until wait_timeout_while atomically releases
        // this mutex and registers the condition wait: notification is tested,
        // rather than permitting cleanup to win before the waiter starts.
        let state = close
            .registry
            .state
            .lock()
            .expect("unpoisoned close registry");
        let _ = waiting_tx.try_send(());
        let (state, timeout) = close
            .registry
            .changed
            .wait_timeout_while(state, CONTROL_TIMEOUT, |_| {
                !close.lifecycle.closed.load(Ordering::Acquire)
            })
            .expect("bounded real cleanup notification");
        let notified = !timeout.timed_out();
        drop(state);
        let ack = close.wait_timeout(Duration::ZERO);
        drop(close); // Actual global close-waiter reservation disposal.
        (notified, ack)
    });
    waiting_rx
        .recv_timeout(CONTROL_TIMEOUT)
        .expect("bounded close waiter checkpoint");
    poll_worker.join().expect("owned poll cleanup worker");
    let (notified, ack) = waiter.join().expect("owned close notification worker");
    assert!(
        notified,
        "a missing notify must not pass by waiting for the timeout"
    );
    assert_eq!(
        serde_json::to_value(ack.unwrap()).unwrap(),
        serde_json::json!({"schemaVersion":1,"registrationKey":key(1)})
    );
    let snapshot = registry.snapshot().unwrap();
    assert!(!snapshot.poll_reserved);
    assert_eq!(snapshot.closing, 0);
    assert_eq!(snapshot.close_waiters, 0);
    assert_eq!(snapshot.ready, 1);
    assert_eq!(
        poll_value(&registry, 2, now)["frames"]
            .as_array()
            .unwrap()
            .len(),
        1
    );
    assert_eq!(audit.dispatches.get(), 0);
    assert_eq!(audit.closes.get(), 0);
    assert_eq!(audit.drops.get(), 0);
    registry.revoke().unwrap();
    finish(&owner, &audit);
}
