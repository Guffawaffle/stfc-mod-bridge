use super::*;
use std::{
    fmt,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicUsize, Ordering},
        mpsc::{self, Receiver, SyncSender, TryRecvError, TrySendError},
    },
    time::Duration,
};

/// An owned bounded frame. Debug/logging deliberately exposes only its length.
#[derive(Clone, PartialEq, Eq)]
pub struct OwnedFrame(Vec<u8>);
impl OwnedFrame {
    pub fn new(bytes: Vec<u8>) -> Result<Self, SubmissionFailure> {
        if bytes.len() > MAX_MESSAGE_BYTES {
            return Err(SubmissionFailure::not_sent(
                SubmissionFailureCode::FrameTooLarge,
            ));
        }
        Ok(Self(bytes))
    }
    pub fn as_bytes(&self) -> &[u8] {
        &self.0
    }
    pub fn into_bytes(self) -> Vec<u8> {
        self.0
    }
}
impl fmt::Debug for OwnedFrame {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("OwnedFrame")
            .field("bytes", &self.0.len())
            .finish()
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SubmissionFailureCode {
    FrameTooLarge,
    QueueFull,
    PendingLimit,
    ObserverLimit,
    Disconnected,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SubmissionFailure {
    pub code: SubmissionFailureCode,
}
impl SubmissionFailure {
    fn not_sent(code: SubmissionFailureCode) -> Self {
        Self { code }
    }
}
impl fmt::Display for SubmissionFailure {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("request not sent")
    }
}
impl std::error::Error for SubmissionFailure {}

/// Receiving failure/timeout after successful enqueue cannot prove not-sent.
/// Dropping this receiver never sends CancelOperation or releases worker custody.
pub struct PendingReply {
    receiver: Receiver<Result<OwnedFrame, HostFailure>>,
    _slot: Slot,
}
impl PendingReply {
    pub fn recv_timeout(
        &self,
        timeout: Duration,
    ) -> Result<Result<OwnedFrame, HostFailure>, mpsc::RecvTimeoutError> {
        self.receiver.recv_timeout(timeout)
    }
    pub fn try_recv(&self) -> Result<Result<OwnedFrame, HostFailure>, TryRecvError> {
        self.receiver.try_recv()
    }
}
pub struct PendingClose {
    receiver: Receiver<Result<CloseDisposition, HostFailure>>,
    _slot: Slot,
}
impl PendingClose {
    pub fn recv_timeout(
        &self,
        timeout: Duration,
    ) -> Result<Result<CloseDisposition, HostFailure>, mpsc::RecvTimeoutError> {
        self.receiver.recv_timeout(timeout)
    }
    pub fn try_recv(&self) -> Result<Result<CloseDisposition, HostFailure>, TryRecvError> {
        self.receiver.try_recv()
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ObservationFailure {
    OverflowResnapshotRequired,
    RetentionGapResnapshotRequired,
    HostFailed(HostFailure),
    HostClosed,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum HostObservation {
    Event(OwnedFrame),
    Fault(ObservationFailure),
}
struct ObservationState {
    alive: AtomicBool,
    fault: Mutex<Option<ObservationFailure>>,
}
impl ObservationState {
    fn fault(&self) -> Option<ObservationFailure> {
        *self.fault.lock().unwrap_or_else(|e| e.into_inner())
    }
    fn fail(&self, fault: ObservationFailure) {
        let mut slot = self.fault.lock().unwrap_or_else(|e| e.into_inner());
        if slot.is_none() {
            *slot = Some(fault);
        }
    }
}

/// Readiness is an actor acknowledgement of the subscription watermark. An
/// adapter must await it before its initial Snapshot exchange. Terminal faults
/// have a separate bounded cell so a full event queue cannot hide a gap.
pub struct Subscription {
    ready: Receiver<Result<Cursor, ObservationFailure>>,
    receiver: Receiver<OwnedFrame>,
    state: Arc<ObservationState>,
    _slot: Slot,
}
impl Subscription {
    pub fn ready_timeout(
        &self,
        timeout: Duration,
    ) -> Result<Result<Cursor, ObservationFailure>, mpsc::RecvTimeoutError> {
        self.ready.recv_timeout(timeout)
    }
    pub fn try_recv(&self) -> Result<HostObservation, TryRecvError> {
        if let Some(fault) = self.state.fault() {
            return Ok(HostObservation::Fault(fault));
        }
        let result = self.receiver.try_recv().map(HostObservation::Event);
        if let Some(fault) = self.state.fault() {
            return Ok(HostObservation::Fault(fault));
        }
        result
    }
}
impl Drop for Subscription {
    fn drop(&mut self) {
        self.state.alive.store(false, Ordering::Release);
    }
}

struct Slot(Arc<AtomicUsize>);
impl Slot {
    fn acquire(
        count: &Arc<AtomicUsize>,
        maximum: usize,
        code: SubmissionFailureCode,
    ) -> Result<Self, SubmissionFailure> {
        count
            .try_update(Ordering::AcqRel, Ordering::Acquire, |n| {
                (n < maximum).then_some(n + 1)
            })
            .map_err(|_| SubmissionFailure::not_sent(code))?;
        Ok(Self(count.clone()))
    }
}
impl Drop for Slot {
    fn drop(&mut self) {
        self.0.fetch_sub(1, Ordering::AcqRel);
    }
}

#[derive(Clone)]
pub struct HostHandle {
    sender: SyncSender<Job>,
    pending: Arc<AtomicUsize>,
    observers: Arc<AtomicUsize>,
}
impl HostHandle {
    pub fn exchange(&self, frame: OwnedFrame) -> Result<PendingReply, SubmissionFailure> {
        let slot = Slot::acquire(
            &self.pending,
            MAX_PENDING_REPLIES,
            SubmissionFailureCode::PendingLimit,
        )?;
        let (sender, receiver) = mpsc::sync_channel(1);
        self.send(Job::Exchange { frame, sender })?;
        Ok(PendingReply {
            receiver,
            _slot: slot,
        })
    }
    /// Trusted host/window lifecycle control, serialized in the same inbox as
    /// requests. Renderer close still uses versioned RequestHostClose input.
    pub fn request_close(&self) -> Result<PendingClose, SubmissionFailure> {
        let slot = Slot::acquire(
            &self.pending,
            MAX_PENDING_REPLIES,
            SubmissionFailureCode::PendingLimit,
        )?;
        let (sender, receiver) = mpsc::sync_channel(1);
        self.send(Job::Close { sender })?;
        Ok(PendingClose {
            receiver,
            _slot: slot,
        })
    }
    pub fn subscribe(&self, after: Option<Cursor>) -> Result<Subscription, SubmissionFailure> {
        let slot = Slot::acquire(
            &self.observers,
            MAX_OBSERVERS,
            SubmissionFailureCode::ObserverLimit,
        )?;
        let state = Arc::new(ObservationState {
            alive: AtomicBool::new(true),
            fault: Mutex::new(None),
        });
        let (events, receiver) = mpsc::sync_channel(MAX_OBSERVER_EVENTS);
        let (ready, acknowledgement) = mpsc::sync_channel(1);
        self.send(Job::Subscribe {
            after,
            events,
            ready,
            state: state.clone(),
        })?;
        Ok(Subscription {
            ready: acknowledgement,
            receiver,
            state,
            _slot: slot,
        })
    }
    fn send(&self, job: Job) -> Result<(), SubmissionFailure> {
        self.sender.try_send(job).map_err(|error| {
            SubmissionFailure::not_sent(match error {
                TrySendError::Full(_) => SubmissionFailureCode::QueueFull,
                TrySendError::Disconnected(_) => SubmissionFailureCode::Disconnected,
            })
        })
    }
}

pub fn owner_channel() -> (HostHandle, OwnerInbox) {
    let (sender, receiver) = mpsc::sync_channel(MAX_QUEUED_REQUESTS);
    (
        HostHandle {
            sender,
            pending: Arc::new(AtomicUsize::new(0)),
            observers: Arc::new(AtomicUsize::new(0)),
        },
        OwnerInbox {
            inner: Inbox { receiver },
        },
    )
}
pub(crate) struct Inbox {
    receiver: Receiver<Job>,
}
enum Job {
    Exchange {
        frame: OwnedFrame,
        sender: SyncSender<Result<OwnedFrame, HostFailure>>,
    },
    Close {
        sender: SyncSender<Result<CloseDisposition, HostFailure>>,
    },
    Subscribe {
        after: Option<Cursor>,
        events: SyncSender<OwnedFrame>,
        ready: SyncSender<Result<Cursor, ObservationFailure>>,
        state: Arc<ObservationState>,
    },
}
struct Observer {
    cursor: Cursor,
    sender: SyncSender<OwnedFrame>,
    state: Arc<ObservationState>,
}

pub(crate) struct Runtime<H: LocalHost> {
    host: H,
    inbox: Inbox,
    observers: Vec<Observer>,
    pub(crate) closing: bool,
    pub(crate) failure: Option<HostFailure>,
    owner_panicked: bool,
}
impl<H: LocalHost> Runtime<H> {
    pub(crate) fn new(host: H, inbox: Inbox) -> Self {
        Self {
            host,
            inbox,
            observers: vec![],
            closing: false,
            failure: None,
            owner_panicked: false,
        }
    }
    fn owner_call<T>(
        &mut self,
        call: impl FnOnce(&mut H) -> Result<T, HostFailure>,
    ) -> Result<T, HostFailure> {
        if self.owner_panicked {
            return Err(HostFailure::OwnerPanicked);
        }
        match catch_unwind(AssertUnwindSafe(|| call(&mut self.host))) {
            Ok(result) => result,
            Err(_) => {
                self.owner_panicked = true;
                self.fail(HostFailure::OwnerPanicked);
                Err(HostFailure::OwnerPanicked)
            }
        }
    }
    pub(crate) fn fail(&mut self, failure: HostFailure) {
        if self.failure.is_none() {
            self.failure = Some(failure);
        }
        for observer in &self.observers {
            observer.state.fail(ObservationFailure::HostFailed(failure));
        }
        self.observers.clear();
    }
    pub(crate) fn custody_unknown(&self) -> bool {
        self.owner_panicked
    }
    pub(crate) fn taint(&mut self, failure: HostFailure) {
        self.owner_panicked = true;
        self.fail(failure);
    }
    pub(crate) fn arm_close(&mut self) {
        if self.closing {
            return;
        }
        self.closing = true;
        if let Err(failure) = self.owner_call(|host| host.request_close()) {
            self.fail(failure);
        }
    }
    pub(crate) fn tick(&mut self) {
        self.tick_with_close_signal(None);
    }
    pub(crate) fn tick_with_close_signal(&mut self, close_signal: Option<&std::cell::Cell<bool>>) {
        // The blocking runner continues answering queued failure observations
        // after taint; owner_call still refuses every actual owner method.
        // An embedded tainted shell performs no later turn at all.
        let embedded = close_signal.is_some();
        if self.owner_panicked && embedded {
            return;
        }
        if close_signal.is_some_and(std::cell::Cell::get) {
            self.arm_close();
        }
        if self.owner_panicked && embedded {
            return;
        }
        self.observers
            .retain(|observer| observer.state.alive.load(Ordering::Acquire));
        match self.inbox.receiver.try_recv() {
            Ok(job) => self.receive(job),
            Err(TryRecvError::Empty) => {}
            Err(TryRecvError::Disconnected) => self.arm_close(),
        }
        // Dispatch may invoke a recursive platform callback. Observe its close
        // intent before further progression or another externally driven turn.
        if close_signal.is_some_and(std::cell::Cell::get) {
            self.arm_close();
        }
        if self.owner_panicked && embedded {
            return;
        }
        // Request/reply/subscriber loss never prevents independent advancement.
        if let Err(failure) = self.owner_call(|host| host.pump()) {
            self.fail(failure);
            self.arm_close();
        }
        if close_signal.is_some_and(std::cell::Cell::get) {
            self.arm_close();
        }
        if self.owner_panicked && embedded {
            return;
        }
        self.deliver_events();
    }
    fn receive(&mut self, job: Job) {
        match job {
            Job::Exchange { frame, sender } => {
                let response = match decode_request(frame.as_bytes()) {
                    Ok(request) => {
                        let request_id = request.as_inner().request_id.clone();
                        let expected = request.as_inner().body.clone();
                        let closing = matches!(
                            request.as_inner().body,
                            RequestBody::Command {
                                command: Command::RequestHostClose(_)
                            }
                        );
                        let reply = self.owner_call(|host| host.dispatch(request));
                        reply.and_then(|reply| {
                            if reply.as_inner().request_id.as_ref() != Some(&request_id) {
                                return Err(HostFailure::InvalidReply);
                            }
                            if !reply_kind_matches(&expected, &reply.as_inner().body) {
                                return Err(HostFailure::InvalidReply);
                            }
                            if closing
                                && matches!(
                                    reply.as_inner().body,
                                    ReplyBody::Result {
                                        result: ResultPayload::Command { .. }
                                    }
                                )
                            {
                                self.closing = true;
                            }
                            encode_reply(reply.as_inner())
                        })
                    }
                    Err(failure) => encode_reply(&Reply {
                        protocol_version: ProtocolVersion,
                        request_id: ReplyRequestId::new(failure.request_id),
                        body: ReplyBody::Rejected {
                            error: failure.error,
                        },
                    }),
                };
                if let Err(failure) = response {
                    self.fail(failure);
                    self.arm_close();
                }
                // Receiver abandonment cannot block or undo durable admission.
                let _ = sender.try_send(response);
            }
            Job::Close { sender } => {
                self.closing = true;
                let response = self
                    .owner_call(|host| host.request_close())
                    .and_then(validate_close);
                if let Err(failure) = response {
                    self.fail(failure);
                }
                let _ = sender.try_send(response);
            }
            Job::Subscribe {
                after,
                events,
                ready,
                state,
            } => {
                if !state.alive.load(Ordering::Acquire) {
                    return;
                }
                if let Some(failure) = self.failure {
                    state.fail(ObservationFailure::HostFailed(failure));
                    let _ = ready.try_send(Err(ObservationFailure::HostFailed(failure)));
                    return;
                }
                let cursor = self.owner_call(|host| Ok(host.cursor()));
                match cursor {
                    Ok(current) => {
                        let cursor = after.unwrap_or(current);
                        // Validate old/foreign cursors before acknowledging readiness.
                        match self.read_batch(&cursor) {
                            Ok(_) => {
                                let _ = ready.try_send(Ok(cursor.clone()));
                                self.observers.push(Observer {
                                    cursor,
                                    sender: events,
                                    state,
                                });
                            }
                            Err(HostFailure::ResnapshotRequired) => {
                                state.fail(ObservationFailure::RetentionGapResnapshotRequired);
                                let _ = ready.try_send(Err(
                                    ObservationFailure::RetentionGapResnapshotRequired,
                                ));
                            }
                            Err(failure) => {
                                state.fail(ObservationFailure::HostFailed(failure));
                                let _ =
                                    ready.try_send(Err(ObservationFailure::HostFailed(failure)));
                                self.fail(failure);
                                self.arm_close();
                            }
                        }
                    }
                    Err(failure) => {
                        state.fail(ObservationFailure::HostFailed(failure));
                        let _ = ready.try_send(Err(ObservationFailure::HostFailed(failure)));
                    }
                }
            }
        }
    }
    fn deliver_events(&mut self) {
        let observers = std::mem::take(&mut self.observers);
        for mut observer in observers {
            if let Some(failure) = self.failure {
                observer.state.fail(ObservationFailure::HostFailed(failure));
                continue;
            }
            if !observer.state.alive.load(Ordering::Acquire) {
                continue;
            }
            match self.read_batch(&observer.cursor) {
                Ok(batch) => {
                    let mut live = true;
                    for event in batch.events.as_slice() {
                        let frame = match encode_event(event) {
                            Ok(frame) => frame,
                            Err(failure) => {
                                observer.state.fail(ObservationFailure::HostFailed(failure));
                                self.fail(failure);
                                self.arm_close();
                                live = false;
                                break;
                            }
                        };
                        match observer.sender.try_send(frame) {
                            Ok(()) => observer.cursor = event.cursor.clone(),
                            Err(TrySendError::Full(_)) => {
                                observer
                                    .state
                                    .fail(ObservationFailure::OverflowResnapshotRequired);
                                live = false;
                                break;
                            }
                            Err(TrySendError::Disconnected(_)) => {
                                live = false;
                                break;
                            }
                        }
                    }
                    if live {
                        self.observers.push(observer);
                    }
                }
                Err(HostFailure::ResnapshotRequired) => observer
                    .state
                    .fail(ObservationFailure::RetentionGapResnapshotRequired),
                Err(failure) => {
                    observer.state.fail(ObservationFailure::HostFailed(failure));
                    self.fail(failure);
                    self.arm_close();
                }
            }
        }
    }
    pub(crate) fn exit_when_safe(&mut self) -> Option<HostExit> {
        if !self.closing || self.owner_panicked {
            return None;
        }
        let disposition = match self
            .owner_call(|host| host.close_disposition())
            .and_then(validate_close)
        {
            Ok(close) => close,
            Err(failure) => {
                self.fail(failure);
                return None;
            }
        };
        if matches!(disposition, CloseDisposition::Deferred { .. }) {
            return None;
        }
        let cursor = match self.owner_call(|host| Ok(host.cursor())) {
            Ok(cursor) => cursor,
            Err(_) => return None,
        };
        for observer in &self.observers {
            observer.state.fail(ObservationFailure::HostClosed);
        }
        Some(HostExit {
            disposition,
            cursor,
            failure: self.failure,
        })
    }
    fn read_batch(&mut self, after: &Cursor) -> Result<EventBatch, HostFailure> {
        self.owner_call(|host| {
            let batch = host.events_after(after)?;
            validate_batch(&batch, after, &host.cursor())?;
            Ok(batch)
        })
    }
}

fn encode_reply(reply: &Reply) -> Result<OwnedFrame, HostFailure> {
    let bytes = serde_json::to_vec(reply).map_err(|_| HostFailure::InvalidReply)?;
    decode_reply(&bytes).map_err(|_| HostFailure::InvalidReply)?;
    OwnedFrame::new(bytes).map_err(|_| HostFailure::InvalidReply)
}
fn encode_event(event: &Event) -> Result<OwnedFrame, HostFailure> {
    let bytes = serde_json::to_vec(event).map_err(|_| HostFailure::InvalidObservation)?;
    decode_event(&bytes).map_err(|_| HostFailure::InvalidObservation)?;
    OwnedFrame::new(bytes).map_err(|_| HostFailure::InvalidObservation)
}
fn validate_batch(batch: &EventBatch, after: &Cursor, current: &Cursor) -> Result<(), HostFailure> {
    if &batch.after != after
        || current.host_epoch != after.host_epoch
        || current.stream_id != after.stream_id
        || batch.next.sequence > current.sequence
    {
        return Err(HostFailure::InvalidObservation);
    }
    // The reply codec validates scope, strictly consecutive sequence and next.
    encode_reply(&Reply {
        protocol_version: ProtocolVersion,
        request_id: ReplyRequestId::new(Some(
            RequestId::new("ffffffff-ffff-4fff-8fff-ffffffffffff")
                .map_err(|_| HostFailure::InvalidObservation)?,
        )),
        body: ReplyBody::Result {
            result: ResultPayload::Query {
                query: Box::new(QueryResult::ResumeEvents(batch.clone())),
            },
        },
    })
    .map(|_| ())
    .map_err(|_| HostFailure::InvalidObservation)
}
fn validate_close(close: CloseDisposition) -> Result<CloseDisposition, HostFailure> {
    encode_reply(&Reply {
        protocol_version: ProtocolVersion,
        request_id: ReplyRequestId::new(Some(
            RequestId::new("ffffffff-ffff-4fff-8fff-ffffffffffff")
                .map_err(|_| HostFailure::CloseUnavailable)?,
        )),
        body: ReplyBody::Result {
            result: ResultPayload::Command {
                command: Box::new(CommandResult::RequestHostClose(close.clone())),
            },
        },
    })
    .map(|_| close)
    .map_err(|_| HostFailure::CloseUnavailable)
}

fn reply_kind_matches(request: &RequestBody, reply: &ReplyBody) -> bool {
    match (request, reply) {
        (_, ReplyBody::Rejected { .. }) => true,
        (
            RequestBody::Query {
                query: Query::GetDraft(input),
            },
            ReplyBody::Result {
                result: ResultPayload::Query { query: output },
            },
        ) => match output.as_ref() {
            QueryResult::GetDraft(result) => {
                result.cursor.host_epoch == input.host_epoch
                    && match &result.draft {
                        Observation::Observed { value, .. } => {
                            value.draft.host_epoch == input.host_epoch
                                && value.draft.draft_id == input.draft_id
                        }
                        _ => true,
                    }
            }
            _ => false,
        },
        (
            RequestBody::Query { query: input },
            ReplyBody::Result {
                result: ResultPayload::Query { query: output },
            },
        ) => matches!(
            (input, output.as_ref()),
            (Query::Hello(_), QueryResult::Hello(_))
                | (Query::ResolveTarget(_), QueryResult::ResolveTarget(_))
                | (Query::GetOperation(_), QueryResult::GetOperation(_))
                | (Query::Snapshot(_), QueryResult::Snapshot(_))
                | (Query::ListProfiles(_), QueryResult::ListProfiles(_))
                | (
                    Query::ListInstallations(_),
                    QueryResult::ListInstallations(_)
                )
                | (Query::ListSessions(_), QueryResult::ListSessions(_))
                | (
                    Query::ListImportSources(_),
                    QueryResult::ListImportSources(_)
                )
                | (Query::GetActions(_), QueryResult::GetActions(_))
                | (
                    Query::ReadConfiguration(_),
                    QueryResult::ReadConfiguration(_)
                )
                | (
                    Query::ConfigurationHistory(_),
                    QueryResult::ConfigurationHistory(_)
                )
                | (
                    Query::CheckRuntimeRelease(_),
                    QueryResult::CheckRuntimeRelease(_)
                )
                | (Query::CheckGameUpdate(_), QueryResult::CheckGameUpdate(_))
                | (
                    Query::CheckBridgeUpdate(_),
                    QueryResult::CheckBridgeUpdate(_)
                )
                | (Query::ResumeEvents(_), QueryResult::ResumeEvents(_))
                | (
                    Query::DiagnosticPreview(_),
                    QueryResult::DiagnosticPreview(_)
                )
        ),
        (
            RequestBody::Command { command: input },
            ReplyBody::Result {
                result: ResultPayload::Command { command: output },
            },
        ) => matches!(
            (input, output.as_ref()),
            (Command::Prepare(_), CommandResult::Prepare(_))
                | (Command::Commit(_), CommandResult::Commit(_))
                | (
                    Command::CancelOperation(_),
                    CommandResult::CancelOperation(_)
                )
                | (
                    Command::RequestHostClose(_),
                    CommandResult::RequestHostClose(_)
                )
                | (Command::OpenDraft(_), CommandResult::OpenDraft(_))
                | (
                    Command::SetDraftChanges(_),
                    CommandResult::SetDraftChanges(_)
                )
                | (Command::DiscardDraft(_), CommandResult::DiscardDraft(_))
                | (
                    Command::RequestImportDiscovery(_),
                    CommandResult::RequestImportDiscovery(_)
                )
                | (
                    Command::RequestSensitiveInput(_),
                    CommandResult::RequestSensitiveInput(_)
                )
                | (
                    Command::RequestExportDestination(_),
                    CommandResult::RequestExportDestination(_)
                )
        ),
        _ => false,
    }
}

#[cfg(test)]
mod draft_reply_tests {
    use super::*;

    #[test]
    fn current_draft_reply_refuses_codec_valid_selector_substitution() {
        let request = decode_request(include_bytes!(
            "../../../../contracts/fixtures/sc08-get-current-dirty-draft-request.json"
        ))
        .unwrap()
        .into_inner();
        let original = decode_reply(include_bytes!(
            "../../../../contracts/fixtures/sc08-get-current-dirty-draft-reply.json"
        ))
        .unwrap()
        .into_inner();
        assert!(reply_kind_matches(&request.body, &original.body));
        for foreign_host in [false, true] {
            let mut forged = original.clone();
            let ReplyBody::Result {
                result: ResultPayload::Query { query },
            } = &mut forged.body
            else {
                panic!()
            };
            let QueryResult::GetDraft(result) = query.as_mut() else {
                panic!()
            };
            let Observation::Observed { value, .. } = &mut result.draft else {
                panic!()
            };
            if foreign_host {
                let host = HostEpoch::new("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee").unwrap();
                value.draft.host_epoch = host.clone();
                result.cursor.host_epoch = host;
            } else {
                value.draft.draft_id =
                    DraftId::new("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee").unwrap();
            }
            let validated = decode_reply(&serde_json::to_vec(&forged).unwrap()).unwrap();
            assert!(!reply_kind_matches(
                &request.body,
                &validated.as_inner().body
            ));
        }
    }

    #[test]
    fn missing_draft_reply_still_requires_the_requested_host() {
        let request = decode_request(include_bytes!(
            "../../../../contracts/fixtures/sc08-get-missing-draft-request.json"
        ))
        .unwrap()
        .into_inner();
        let mut reply = decode_reply(include_bytes!(
            "../../../../contracts/fixtures/sc08-get-missing-draft-reply.json"
        ))
        .unwrap()
        .into_inner();
        assert!(reply_kind_matches(&request.body, &reply.body));
        let ReplyBody::Result {
            result: ResultPayload::Query { query },
        } = &mut reply.body
        else {
            panic!()
        };
        let QueryResult::GetDraft(result) = query.as_mut() else {
            panic!()
        };
        result.cursor.host_epoch = HostEpoch::new("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee").unwrap();
        let validated = decode_reply(&serde_json::to_vec(&reply).unwrap()).unwrap();
        assert!(!reply_kind_matches(
            &request.body,
            &validated.as_inner().body
        ));
    }
}
