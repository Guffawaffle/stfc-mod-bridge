//! Owned original-thread host, actual engine channels and controlled time only.
//! No production mock, native service, journal, store, process or GUI authority.
use bridge_contracts::v1::*;
use bridge_engine::host::*;
use bridge_host_adapter::{controller::*, registration::*};
use std::{
    cell::{Cell, RefCell},
    rc::Rc,
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};

pub const EPOCH: &str = "11111111-1111-4111-8111-111111111111";
pub fn key(n: u64) -> String {
    format!("{EPOCH}:{n}")
}
pub fn fixture(name: &str) -> &'static [u8] {
    match name {
        "request" => include_bytes!(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../contracts/fixtures/checkpoint-hello-request.json"
        )),
        "reply" => include_bytes!(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../contracts/fixtures/checkpoint-hello-reply.json"
        )),
        "event" => include_bytes!(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../contracts/fixtures/sc-03-working-readiness-event.json"
        )),
        _ => panic!("closed fixture name"),
    }
}
pub fn request() -> Vec<u8> {
    fixture("request").to_vec()
}
pub fn padded_request() -> Vec<u8> {
    let mut bytes = request();
    bytes.resize(MAX_FRAME_BYTES, b' ');
    bytes
}
pub fn base_cursor() -> Cursor {
    let mut cursor = decode_event(fixture("event")).unwrap().into_inner().cursor;
    cursor.sequence = Sequence::new(0);
    cursor
}
pub struct Clock(pub Mutex<Instant>);
impl RegistryClock for Clock {
    fn now(&self) -> Instant {
        *self.0.lock().unwrap()
    }
}
impl Clock {
    pub fn advance(&self, duration: Duration) {
        let mut now = self.0.lock().unwrap();
        *now += duration;
    }
}
#[derive(Default)]
pub struct Audit {
    pub dispatches: Cell<usize>,
    pub closes: Cell<usize>,
    pub drops: Cell<usize>,
    pub remaining: Cell<usize>,
    pub fail_dispatch: Cell<bool>,
    pub events: RefCell<Vec<Event>>,
}
pub struct TestHost {
    audit: Rc<Audit>,
    thread: std::thread::ThreadId,
}
impl TestHost {
    fn on_owner(&self) {
        assert_eq!(std::thread::current().id(), self.thread);
    }
}
impl Drop for TestHost {
    fn drop(&mut self) {
        self.on_owner();
        self.audit.drops.set(self.audit.drops.get() + 1);
    }
}
impl LocalHost for TestHost {
    fn dispatch(&mut self, request: ValidatedRequest) -> Result<ValidatedReply, HostFailure> {
        self.on_owner();
        self.audit.dispatches.set(self.audit.dispatches.get() + 1);
        self.audit.remaining.set(3);
        if self.audit.fail_dispatch.get() {
            return Err(HostFailure::UnavailableService);
        }
        let mut reply = decode_reply(fixture("reply")).unwrap().into_inner();
        reply.request_id = ReplyRequestId::new(Some(request.as_inner().request_id.clone()));
        Ok(decode_reply(&serde_json::to_vec(&reply).unwrap()).unwrap())
    }
    fn cursor(&self) -> Cursor {
        self.on_owner();
        self.audit
            .events
            .borrow()
            .last()
            .map(|event| event.cursor.clone())
            .unwrap_or_else(base_cursor)
    }
    fn pump(&mut self) -> Result<(), HostFailure> {
        self.on_owner();
        self.audit
            .remaining
            .set(self.audit.remaining.get().saturating_sub(1));
        Ok(())
    }
    fn events_after(&mut self, after: &Cursor) -> Result<EventBatch, HostFailure> {
        self.on_owner();
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
            next,
            events: BoundedList::new(events).unwrap(),
        })
    }
    fn request_close(&mut self) -> Result<CloseDisposition, HostFailure> {
        self.on_owner();
        self.audit.closes.set(self.audit.closes.get() + 1);
        self.close_disposition()
    }
    fn close_disposition(&self) -> Result<CloseDisposition, HostFailure> {
        self.on_owner();
        if self.audit.remaining.get() != 0 {
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
pub struct Fixture {
    pub controller: Arc<TransportController>,
    pub handle: HostHandle,
    pub audit: Rc<Audit>,
    pub owner: EmbeddedOwner<TestHost>,
    pub clock: Arc<Clock>,
}
pub fn setup() -> Fixture {
    let (handle, inbox) = owner_channel();
    let clock = Arc::new(Clock(Mutex::new(Instant::now())));
    let controller = TransportController::new(
        handle.clone(),
        TrustedEpoch::from_root(EPOCH).unwrap(),
        clock.clone(),
    );
    let audit = Rc::new(Audit::default());
    let local = audit.clone();
    let owner = EmbeddedOwner::on_current_thread(inbox, || {
        Ok(TestHost {
            audit: local,
            thread: std::thread::current().id(),
        })
    })
    .unwrap();
    Fixture {
        controller,
        handle,
        audit,
        owner,
        clock,
    }
}
pub fn progress(owner: &EmbeddedOwner<TestHost>) -> EmbeddedTurn {
    owner.turn(|_| Ok(DriveControl::Continue))
}
pub fn finish(owner: &EmbeddedOwner<TestHost>, audit: &Audit) {
    owner.signal_close();
    let mut closed = false;
    for _ in 0..64 {
        if matches!(progress(owner), EmbeddedTurn::Closed(_)) {
            closed = true;
            break;
        }
    }
    assert!(
        closed,
        "owned controlled fixture closes on its original thread"
    );
    assert_eq!(audit.drops.get(), 1);
}
pub fn prepare(controller: &Arc<TransportController>, n: u64) -> SubscribeWork {
    match controller.begin_subscribe(&key(n)).unwrap() {
        SubscribeStart::Prepare(work) => work,
        SubscribeStart::Ready(_) => panic!("new registration"),
    }
}
pub fn ready(fixture: &Fixture, n: u64) -> ReadyCandidate {
    let mut wait = prepare(&fixture.controller, n).attach().unwrap();
    assert!(matches!(wait.step().unwrap(), ReadinessStep::Pending));
    progress(&fixture.owner);
    match wait.step().unwrap() {
        ReadinessStep::Ready(candidate) => candidate,
        ReadinessStep::Pending => panic!("actual owner processed subscription ACK"),
    }
}
pub fn append(audit: &Audit, count: u64) {
    for _ in 0..count {
        let mut event = decode_event(fixture("event")).unwrap().into_inner();
        event.cursor.sequence = Sequence::new(audit.events.borrow().len() as u64 + 1);
        let event = decode_event(&serde_json::to_vec(&event).unwrap())
            .unwrap()
            .into_inner();
        audit.events.borrow_mut().push(event);
    }
}
pub fn waiting(value: CloseStart) -> CleanupWork {
    match value {
        CloseStart::Waiting(work) => work,
        CloseStart::Closed(_) => panic!("actual work is retained"),
    }
}
pub fn response(ticket: &mut ExchangeTicket) -> ExchangeResponseWork {
    ticket
        .take_response()
        .unwrap()
        .expect("actual settled response")
}
pub fn assert_fault(fault: FaultDto, code: FaultCode, delivery: Delivery) {
    assert_eq!(fault.code(), code);
    assert_eq!(fault.delivery(), delivery);
}
