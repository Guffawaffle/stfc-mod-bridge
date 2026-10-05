//! Portable protocol work custody over one immutable host/document binding.
//!
//! Root retains this controller and independently services exchange jobs,
//! readiness/cleanup workers and expiry. Dropping a delivery ticket does not
//! cancel backend work. Dropping the last controller loses servicing and pending
//! observation; it is not safe host shutdown. Root separately retains its host
//! lifecycle handle and original-thread owner through actual safe disposal.
//!
//! Response callbacks run without a controller/registry mutex. Eligibility
//! checks are point-in-time: root must hold its real caller/document publication
//! barrier through the actual platform commit, coordinate retirement there, and
//! construct raw responses synchronously. This crate provides no executor,
//! timer, native authority, atomic native publication or private journal owner.
use crate::registration::{
    AckDto, CloseWait, FaultCode, FaultDto, MAX_FRAME_BYTES, PollLease, PrepareWork, PreparingWait,
    ReadyStep, Registry, RegistryClock, RegistrySnapshot, Subscribe, TrustedEpoch, Unsubscribe,
};
use bridge_engine::host::{
    HostHandle, OwnedFrame, PendingReply, SubmissionFailure, SubmissionFailureCode,
};
use std::{
    sync::{Arc, Mutex, MutexGuard},
    time::{Duration, Instant},
};

pub const MAX_EXCHANGES: usize = 32;
pub const MAX_REQUEST_BYTES: usize = 8 * 1024 * 1024;

/// Closed callback failure: paths, native errors and response text never enter
/// a refusal. An admitted response failure is always uncertain delivery.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ResponseFailure;

fn uncertain(code: FaultCode) -> FaultDto {
    FaultDto::uncertain(code)
}
fn submission(failure: SubmissionFailure) -> FaultDto {
    FaultDto::not_sent(match failure.code {
        SubmissionFailureCode::Disconnected => FaultCode::Disconnected,
        _ => FaultCode::DeliveryFailed,
    })
}

enum JobPhase {
    Reserved,
    Pending(PendingReply),
    Settled(Result<OwnedFrame, FaultDto>),
    Building,
}
struct Job {
    id: u64,
    bytes: usize,
    abandoned: bool,
    phase: JobPhase,
}
struct State {
    document_live: bool,
    submission_live: bool,
    next_id: u64,
    bytes: usize,
    jobs: [Option<Job>; MAX_EXCHANGES],
}
impl State {
    fn index(&self, id: u64) -> Option<usize> {
        self.jobs
            .iter()
            .position(|job| job.as_ref().is_some_and(|job| job.id == id))
    }
    fn remove(&mut self, index: usize) {
        if let Some(job) = self.jobs[index].take() {
            self.bytes -= job.bytes;
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ControllerSnapshot {
    pub document_live: bool,
    pub submission_live: bool,
    pub pending: usize,
    pub settled: usize,
    pub building: usize,
    pub original_request_bytes: usize,
}

/// Contains only bounded transport observations. No registry/handle/epoch
/// extraction is exposed. Replacing a document requires a new controller.
pub struct TransportController {
    host: HostHandle,
    registry: Arc<Registry>,
    clock: Arc<dyn RegistryClock>,
    state: Mutex<State>,
}
impl TransportController {
    pub fn new(host: HostHandle, epoch: TrustedEpoch, clock: Arc<dyn RegistryClock>) -> Arc<Self> {
        Arc::new(Self {
            host,
            registry: Registry::new(epoch),
            clock,
            state: Mutex::new(State {
                document_live: true,
                submission_live: true,
                next_id: 1,
                bytes: 0,
                jobs: std::array::from_fn(|_| None),
            }),
        })
    }
    fn lock(&self) -> Result<MutexGuard<'_, State>, FaultDto> {
        // Poison is permanent uncertainty. Never recover counters or invent a
        // successful response/cleanup by taking the poisoned inner value.
        self.state
            .lock()
            .map_err(|_| uncertain(FaultCode::UnavailableBinding))
    }
    fn check_document(&self) -> Result<(), FaultDto> {
        if !self.lock()?.document_live {
            return Err(uncertain(FaultCode::UnavailableBinding));
        }
        Ok(())
    }
    fn admit(state: &State) -> Result<(), FaultDto> {
        if !state.document_live || !state.submission_live {
            return Err(FaultDto::not_sent(FaultCode::UnavailableBinding));
        }
        Ok(())
    }
    pub fn snapshot(&self) -> Result<ControllerSnapshot, FaultDto> {
        let state = self.lock()?;
        let mut result = ControllerSnapshot {
            document_live: state.document_live,
            submission_live: state.submission_live,
            pending: 0,
            settled: 0,
            building: 0,
            original_request_bytes: state.bytes,
        };
        for job in state.jobs.iter().flatten() {
            match job.phase {
                JobPhase::Reserved | JobPhase::Pending(_) => result.pending += 1,
                JobPhase::Settled(_) => result.settled += 1,
                JobPhase::Building => result.building += 1,
            }
        }
        Ok(result)
    }
    /// Separate point-in-time observation; not an atomic composite snapshot.
    pub fn observation_snapshot(&self) -> Result<RegistrySnapshot, FaultDto> {
        self.registry.snapshot()
    }
    pub fn submit_exchange(self: &Arc<Self>, bytes: Vec<u8>) -> Result<ExchangeTicket, FaultDto> {
        let length = bytes.len();
        if length > MAX_FRAME_BYTES {
            return Err(FaultDto::not_sent(FaultCode::DeliveryFailed));
        }
        let mut state = self.lock()?;
        Self::admit(&state)?;
        let aggregate = state
            .bytes
            .checked_add(length)
            .filter(|bytes| *bytes <= MAX_REQUEST_BYTES)
            .ok_or_else(|| FaultDto::not_sent(FaultCode::DeliveryFailed))?;
        let index = state
            .jobs
            .iter()
            .position(Option::is_none)
            .ok_or_else(|| FaultDto::not_sent(FaultCode::DeliveryFailed))?;
        let id = state.next_id;
        state.next_id = id
            .checked_add(1)
            .ok_or_else(|| FaultDto::not_sent(FaultCode::UnavailableBinding))?;
        // Reserve both limits before constructing/enqueueing the owned frame.
        state.bytes = aggregate;
        state.jobs[index] = Some(Job {
            id,
            bytes: length,
            abandoned: false,
            phase: JobPhase::Reserved,
        });
        let frame = match OwnedFrame::new(bytes) {
            Ok(frame) => frame,
            Err(failure) => {
                state.remove(index);
                return Err(submission(failure));
            }
        };
        // This enqueue is nonblocking and performs no owner/caller callback.
        match self.host.exchange(frame) {
            Ok(pending) => {
                state.jobs[index]
                    .as_mut()
                    .expect("reserved bounded job")
                    .phase = JobPhase::Pending(pending);
                Ok(ExchangeTicket {
                    controller: self.clone(),
                    id,
                    active: true,
                })
            }
            Err(failure) => {
                state.remove(index);
                Err(submission(failure))
            }
        }
    }
    /// One try_recv per pending job, at most 32. Root calls this independently
    /// of requester lifetime. Pending jobs remain retained after ticket loss,
    /// document revocation or presentation of a timeout to the requester.
    pub fn service_exchanges(&self) -> Result<usize, FaultDto> {
        let mut state = self.lock()?;
        let mut completed = 0;
        for index in 0..MAX_EXCHANGES {
            let Some(job) = state.jobs[index].as_mut() else {
                continue;
            };
            let JobPhase::Pending(pending) = &job.phase else {
                continue;
            };
            let result = match pending.try_recv() {
                Err(std::sync::mpsc::TryRecvError::Empty) => continue,
                Err(std::sync::mpsc::TryRecvError::Disconnected) => {
                    Err(uncertain(FaultCode::Disconnected))
                }
                Ok(Err(_)) => Err(uncertain(FaultCode::DeliveryFailed)),
                Ok(Ok(frame))
                    if frame.as_bytes().is_empty() || frame.as_bytes().len() > MAX_FRAME_BYTES =>
                {
                    Err(uncertain(FaultCode::DeliveryFailed))
                }
                Ok(Ok(frame)) => Ok(frame),
            };
            completed += 1;
            if job.abandoned {
                state.remove(index);
            } else {
                job.phase = JobPhase::Settled(result);
            }
        }
        Ok(completed)
    }
    fn abandon_ticket(&self, id: u64) {
        if let Ok(mut state) = self.lock()
            && let Some(index) = state.index(id)
        {
            let job = state.jobs[index].as_mut().expect("existing bounded job");
            job.abandoned = true;
            if matches!(job.phase, JobPhase::Settled(_)) {
                state.remove(index);
            }
        }
    }
    fn release_response(&self, id: u64) {
        if let Ok(mut state) = self.lock()
            && let Some(index) = state.index(id)
            && matches!(
                state.jobs[index]
                    .as_ref()
                    .expect("existing bounded job")
                    .phase,
                JobPhase::Building
            )
        {
            state.remove(index);
        }
    }
    fn check_response(&self, id: u64) -> Result<(), FaultDto> {
        let state = self.lock()?;
        if !state.document_live {
            return Err(uncertain(FaultCode::UnavailableBinding));
        }
        if !state.index(id).is_some_and(|index| {
            matches!(
                state.jobs[index].as_ref().expect("existing job").phase,
                JobPhase::Building
            )
        }) {
            return Err(uncertain(FaultCode::Disconnected));
        }
        Ok(())
    }
    pub fn begin_subscribe(self: &Arc<Self>, key: &str) -> Result<SubscribeStart, FaultDto> {
        let now = self.clock.now(); // No caller-defined clock runs under state mutex.
        let state = self.lock()?;
        Self::admit(&state)?;
        let result = self.registry.begin_subscribe(key, now)?;
        drop(state);
        Ok(match result {
            Subscribe::AlreadyReady(ack) => SubscribeStart::Ready(ReadyCandidate {
                controller: self.clone(),
                ack,
            }),
            Subscribe::Prepare(work) => SubscribeStart::Prepare(SubscribeWork {
                controller: self.clone(),
                work,
            }),
        })
    }
    pub fn begin_poll(self: &Arc<Self>, key: &str) -> Result<PollResponseWork, FaultDto> {
        self.check_document()?;
        let lease = self.registry.begin_poll(key, self.clock.now())?;
        Ok(PollResponseWork {
            controller: self.clone(),
            guard: PollGuard {
                lease,
                committed: false,
            },
        })
    }
    pub fn unsubscribe(self: &Arc<Self>, key: &str) -> Result<CloseStart, FaultDto> {
        Ok(match self.registry.unsubscribe(key)? {
            Unsubscribe::Closed(ack) => CloseStart::Closed(ack),
            Unsubscribe::Waiting(wait) => CloseStart::Waiting(CleanupWork {
                _controller: self.clone(),
                wait,
            }),
        })
    }
    pub fn next_deadline(&self) -> Result<Option<Instant>, FaultDto> {
        self.registry.next_deadline()
    }
    pub fn expire(&self) -> Result<usize, FaultDto> {
        self.registry.expire(self.clock.now())
    }
    /// Synchronously closes new protocol submission. Existing replies,
    /// readiness/cleanup custody and poll observations remain available.
    pub fn stop_submission(&self) -> Result<(), FaultDto> {
        self.lock()?.submission_live = false;
        Ok(())
    }
    /// Irreversible document loss. Does not cancel queued exchanges or close
    /// the owner; root keeps independently servicing actual settlement/cleanup.
    pub fn revoke_document(&self) -> Result<usize, FaultDto> {
        let state_result = self.lock().map(|mut state| {
            state.document_live = false;
            state.submission_live = false;
        });
        let registry_result = self.registry.revoke(); // Try actual registry revocation even on controller poison.
        state_result?;
        registry_result
    }
}

/// Delivery observer only. No clone/retry/receiver extraction is available.
pub struct ExchangeTicket {
    controller: Arc<TransportController>,
    id: u64,
    active: bool,
}
impl ExchangeTicket {
    /// None means still pending; it is not a not-sent refusal. Root may show an
    /// uncertain timeout while retaining servicing of this ticket's actual job.
    pub fn take_response(&mut self) -> Result<Option<ExchangeResponseWork>, FaultDto> {
        if !self.active {
            return Err(uncertain(FaultCode::Disconnected));
        }
        let mut state = self.controller.lock()?;
        if !state.document_live {
            return Err(uncertain(FaultCode::UnavailableBinding));
        }
        let index = state
            .index(self.id)
            .ok_or_else(|| uncertain(FaultCode::Disconnected))?;
        let job = state.jobs[index].as_mut().expect("existing bounded job");
        if matches!(job.phase, JobPhase::Reserved | JobPhase::Pending(_)) {
            return Ok(None);
        }
        if !matches!(job.phase, JobPhase::Settled(_)) {
            return Err(uncertain(FaultCode::Disconnected));
        }
        let JobPhase::Settled(result) = std::mem::replace(&mut job.phase, JobPhase::Building)
        else {
            unreachable!()
        };
        self.active = false;
        match result {
            Ok(frame) => Ok(Some(ExchangeResponseWork {
                frame,
                reservation: ResponseReservation {
                    controller: self.controller.clone(),
                    id: self.id,
                },
            })),
            Err(fault) => {
                state.remove(index);
                Err(fault)
            }
        }
    }
}
impl Drop for ExchangeTicket {
    fn drop(&mut self) {
        if self.active {
            self.controller.abandon_ticket(self.id);
        }
    }
}
struct ResponseReservation {
    controller: Arc<TransportController>,
    id: u64,
}
impl Drop for ResponseReservation {
    fn drop(&mut self) {
        self.controller.release_response(self.id);
    }
}
pub struct ExchangeResponseWork {
    frame: OwnedFrame,
    reservation: ResponseReservation,
}
impl ExchangeResponseWork {
    /// Build the actual raw platform response synchronously, outside locks.
    /// R must already own the constructed response, not defer serialization.
    pub fn build<R>(
        self,
        builder: impl FnOnce(&[u8]) -> Result<R, ResponseFailure>,
    ) -> Result<BuiltExchangeResponse<R>, FaultDto> {
        self.reservation
            .controller
            .check_response(self.reservation.id)?;
        let response =
            builder(self.frame.as_bytes()).map_err(|_| uncertain(FaultCode::DeliveryFailed))?;
        Ok(BuiltExchangeResponse {
            response: Some(response),
            reservation: self.reservation,
        })
    }
}
pub struct BuiltExchangeResponse<R> {
    response: Option<R>,
    reservation: ResponseReservation,
}
impl<R> BuiltExchangeResponse<R> {
    /// Point-in-time recheck, followed by a lock-free commit callback. Root
    /// supplies its actual native admission/publication barrier around this call.
    pub fn commit<T>(
        mut self,
        commit: impl FnOnce(R) -> Result<T, ResponseFailure>,
    ) -> Result<T, FaultDto> {
        self.reservation
            .controller
            .check_response(self.reservation.id)?;
        commit(self.response.take().expect("one constructed response"))
            .map_err(|_| uncertain(FaultCode::DeliveryFailed))
    }
}

pub enum SubscribeStart {
    Ready(ReadyCandidate),
    Prepare(SubscribeWork),
}
pub struct SubscribeWork {
    controller: Arc<TransportController>,
    work: PrepareWork,
}
impl SubscribeWork {
    pub fn deadline(&self) -> Instant {
        self.work.deadline()
    }
    pub fn attach(self) -> Result<ReadinessWork, FaultDto> {
        // No engine subscribe has been sent by this work yet.
        if !self.controller.lock()?.document_live {
            return Err(FaultDto::not_sent(FaultCode::UnavailableBinding));
        }
        let wait = self
            .work
            .attach(&self.controller.host, self.controller.clock.now())?;
        Ok(ReadinessWork {
            controller: self.controller,
            wait,
        })
    }
}
pub enum ReadinessStep {
    Pending,
    Ready(ReadyCandidate),
}
pub struct ReadinessWork {
    controller: Arc<TransportController>,
    wait: PreparingWait,
}
impl ReadinessWork {
    pub fn step(&mut self) -> Result<ReadinessStep, FaultDto> {
        self.controller.check_document()?;
        Ok(match self.wait.step(self.controller.clock.now())? {
            ReadyStep::Pending => ReadinessStep::Pending,
            ReadyStep::Ready(ack) => ReadinessStep::Ready(ReadyCandidate {
                controller: self.controller.clone(),
                ack,
            }),
        })
    }
    /// Root runs only on its bounded off-main worker, using the original
    /// reservation deadline and the registry's actual readiness/disposal rules.
    pub fn wait_to_ready(self) -> Result<ReadyCandidate, FaultDto> {
        self.controller.check_document()?;
        let ack = self.wait.wait_to_ready(self.controller.clock.as_ref())?;
        Ok(ReadyCandidate {
            controller: self.controller,
            ack,
        })
    }
}
/// A candidate ACK, not caller authority or an atomic publication permit.
pub struct ReadyCandidate {
    controller: Arc<TransportController>,
    ack: AckDto,
}
impl ReadyCandidate {
    pub fn ack(&self) -> &AckDto {
        &self.ack
    }
    pub fn recheck(&self) -> Result<(), FaultDto> {
        self.controller.check_document()?;
        self.controller
            .registry
            .confirm_ready_ack(&self.ack, self.controller.clock.now())
    }
}
pub enum CloseStart {
    Closed(AckDto),
    Waiting(CleanupWork),
}
pub struct CleanupWork {
    _controller: Arc<TransportController>,
    wait: CloseWait,
}
impl CleanupWork {
    pub fn poll_closed(&self) -> Option<AckDto> {
        self.wait.poll_closed()
    }
    pub fn wait_timeout(&self, timeout: Duration) -> Result<AckDto, FaultDto> {
        self.wait.wait_timeout(timeout)
    }
}

struct PollGuard {
    lease: PollLease,
    committed: bool,
}
impl Drop for PollGuard {
    fn drop(&mut self) {
        if !self.committed {
            self.lease.abandon_response();
        }
    }
}
pub struct PollResponseWork {
    controller: Arc<TransportController>,
    guard: PollGuard,
}
impl PollResponseWork {
    pub fn build<R>(
        mut self,
        builder: impl FnOnce(&str) -> Result<R, ResponseFailure>,
    ) -> Result<BuiltPollResponse<R>, FaultDto> {
        self.controller.check_document()?;
        self.guard.lease.read_one(self.controller.clock.now())?;
        let encoded = self
            .guard
            .lease
            .serialize_json(self.controller.clock.now())?;
        // Guard remains armed across caller builder errors/unwind, including
        // the interval after registry serialization set response_built=true.
        let response = builder(&encoded).map_err(|_| uncertain(FaultCode::DeliveryFailed))?;
        Ok(BuiltPollResponse {
            controller: self.controller,
            guard: self.guard,
            response: Some(response),
        })
    }
}
// Dispose an abandoned constructed platform response before releasing its
// actual poll lease. A response destructor may itself perform a root callback.
pub struct BuiltPollResponse<R> {
    response: Option<R>,
    controller: Arc<TransportController>,
    guard: PollGuard,
}
impl<R> BuiltPollResponse<R> {
    pub fn commit<T>(
        mut self,
        commit: impl FnOnce(R) -> Result<T, ResponseFailure>,
    ) -> Result<T, FaultDto> {
        self.controller.check_document()?;
        self.guard
            .lease
            .check_deliverable(self.controller.clock.now())?;
        let result = commit(self.response.take().expect("one constructed raw response"))
            .map_err(|_| uncertain(FaultCode::DeliveryFailed))?;
        self.guard.committed = true;
        Ok(result)
    }
}
