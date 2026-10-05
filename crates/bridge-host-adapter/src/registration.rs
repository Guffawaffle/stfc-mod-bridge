//! Portable host observation registry; no Tauri or local owner.
//!
//! Root alone creates this object for its trusted document epoch, validates the
//! actual caller, and permanently revokes it under the same admission boundary.
//! Root must drive expiry independently of renderer polling. No method cancels
//! an operation, requests host closure, or transfers/drops the local host owner.
use bridge_engine::host::{HostHandle, HostObservation, Subscription};
use serde::{Deserialize, Serialize, ser::SerializeSeq};
use std::{
    sync::{
        Arc, Condvar, Mutex,
        atomic::{AtomicBool, AtomicUsize, Ordering},
    },
    time::{Duration, Instant},
};

pub const MAX_REGISTRATIONS: usize = 8;
pub const MAX_FRAME_BYTES: usize = 262_144;
pub const MAX_POLL_DTO_BYTES: usize = 2 * 1024 * 1024;
pub const MAX_KEY_BYTES: usize = 57;
pub const PREPARING_TIMEOUT: Duration = Duration::from_secs(5);
pub const IDLE_TIMEOUT: Duration = Duration::from_secs(30);
const READY_WAIT_QUANTUM: Duration = Duration::from_millis(100);
const MAX_CLOSE_WAITERS: usize = 16;
const MAX_KEY_CLOSE_WAITERS: usize = 2;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum FaultCode {
    UnavailableBinding,
    Disconnected,
    DeliveryFailed,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Delivery {
    NotSent,
    MayHaveReachedBackend,
}

/// This closed DTO is the ONLY native refusal which may assert NotSent. Root
/// must never convert an arbitrary invoke error into a pre-enqueue refusal.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FaultDto {
    schema_version: u8,
    code: FaultCode,
    delivery: Delivery,
}
impl FaultDto {
    pub(crate) fn not_sent(code: FaultCode) -> Self {
        Self {
            schema_version: 1,
            code,
            delivery: Delivery::NotSent,
        }
    }
    pub(crate) fn uncertain(code: FaultCode) -> Self {
        Self {
            schema_version: 1,
            code,
            delivery: Delivery::MayHaveReachedBackend,
        }
    }
    pub fn code(self) -> FaultCode {
        self.code
    }
    pub fn delivery(self) -> Delivery {
        self.delivery
    }
}

/// Tauri's framework parses its IPC payload before this type is validated. This
/// type does not claim to bound memory allocated by the framework's JSON parser.
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct KeyArgs {
    registration_key: String,
}
impl KeyArgs {
    pub fn registration_key(&self) -> &str {
        &self.registration_key
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AckDto {
    schema_version: u8,
    registration_key: String,
}
impl AckDto {
    fn new(key: &str) -> Self {
        Self {
            schema_version: 1,
            registration_key: key.into(),
        }
    }
}

struct OneFrame(Option<String>);
impl Serialize for OneFrame {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut seq = serializer.serialize_seq(Some(usize::from(self.0.is_some())))?;
        if let Some(frame) = &self.0 {
            seq.serialize_element(frame)?;
        }
        seq.end()
    }
}
/// Constructed only by a single-use poll lease. It has zero or one frame and no
/// second queue, lookahead, native handle, path, error text, or invented cursor.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct PollDto {
    schema_version: u8,
    registration_key: String,
    frames: OneFrame,
}

/// Canonical lowercase UUID versions/variant exactly match the frontend key
/// allocator. Root issues the UUID; renderer knowledge is never authorization.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TrustedEpoch(String);
impl TrustedEpoch {
    pub fn from_root(value: &str) -> Result<Self, FaultDto> {
        let bytes = value.as_bytes();
        let valid = bytes.len() == 36
            && bytes.iter().enumerate().all(|(i, b)| match i {
                8 | 13 | 18 | 23 => *b == b'-',
                14 => (b'1'..=b'8').contains(b),
                19 => matches!(*b, b'8' | b'9' | b'a' | b'b'),
                _ => b.is_ascii_digit() || (b'a'..=b'f').contains(b),
            });
        if !valid {
            return Err(FaultDto::not_sent(FaultCode::UnavailableBinding));
        }
        Ok(Self(value.into()))
    }
    fn ordinal(&self, key: &str) -> Result<u64, FaultDto> {
        if key.len() < 38
            || key.len() > MAX_KEY_BYTES
            || !key.is_ascii()
            || key.get(..36) != Some(self.0.as_str())
            || key.as_bytes()[36] != b':'
        {
            return Err(FaultDto::not_sent(FaultCode::UnavailableBinding));
        }
        let digits = &key[37..];
        if digits.starts_with('0') || !digits.bytes().all(|b| b.is_ascii_digit()) {
            return Err(FaultDto::not_sent(FaultCode::DeliveryFailed));
        }
        digits
            .parse::<u64>()
            .ok()
            .filter(|n| *n != 0)
            .ok_or_else(|| FaultDto::not_sent(FaultCode::DeliveryFailed))
    }
}

pub trait RegistryClock: Send + Sync {
    fn now(&self) -> Instant;
}
pub struct SystemClock;
impl RegistryClock for SystemClock {
    fn now(&self) -> Instant {
        Instant::now()
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Phase {
    Preparing,
    Ready,
    Closing,
}
struct Lifecycle {
    cancelled: AtomicBool,
    closed: AtomicBool,
    close_waiters: AtomicUsize,
}
impl Lifecycle {
    fn new() -> Self {
        Self {
            cancelled: AtomicBool::new(false),
            closed: AtomicBool::new(false),
            close_waiters: AtomicUsize::new(0),
        }
    }
}
struct Entry {
    ordinal: u64,
    phase: Phase,
    deadline: Instant,
    lifecycle: Arc<Lifecycle>,
    worker: bool,
    polling: bool,
    cleanup: bool,
    subscription: Option<Subscription>,
}
struct State {
    live: bool,
    high_water: u64,
    entries: [Option<Entry>; MAX_REGISTRATIONS],
    poll: Option<u64>,
    close_waiters: usize,
}
impl State {
    fn index(&self, ordinal: u64) -> Option<usize> {
        self.entries
            .iter()
            .position(|e| e.as_ref().is_some_and(|e| e.ordinal == ordinal))
    }
    fn retire(entry: &mut Entry) {
        entry.phase = Phase::Closing;
        entry.lifecycle.cancelled.store(true, Ordering::Release);
    }
    fn finish_if_gone(&mut self, ordinal: u64) -> bool {
        let Some(index) = self.index(ordinal) else {
            return false;
        };
        let entry = self.entries[index].as_ref().expect("existing bounded slot");
        if entry.phase == Phase::Closing
            && !entry.worker
            && !entry.polling
            && !entry.cleanup
            && entry.subscription.is_none()
        {
            let entry = self.entries[index].take().expect("existing bounded slot");
            entry.lifecycle.closed.store(true, Ordering::Release);
            return true;
        }
        false
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct RegistrySnapshot {
    pub live: bool,
    pub preparing: usize,
    pub ready: usize,
    pub closing: usize,
    pub poll_reserved: bool,
    pub close_waiters: usize,
    pub high_water: u64,
}

/// Only safe DTOs, atomics and Subscription receivers live here. In particular,
/// no LocalHost, native service, lease, journal or owner reference is accepted.
pub struct Registry {
    epoch: TrustedEpoch,
    state: Mutex<State>,
    changed: Condvar,
}
impl Registry {
    pub fn new(epoch: TrustedEpoch) -> Arc<Self> {
        Arc::new(Self {
            epoch,
            state: Mutex::new(State {
                live: true,
                high_water: 0,
                entries: std::array::from_fn(|_| None),
                poll: None,
                close_waiters: 0,
            }),
            changed: Condvar::new(),
        })
    }
    fn lock(&self) -> Result<std::sync::MutexGuard<'_, State>, FaultDto> {
        // Poison is permanent unavailability, never an invented cleanup ACK.
        self.state
            .lock()
            .map_err(|_| FaultDto::uncertain(FaultCode::UnavailableBinding))
    }
    pub fn snapshot(&self) -> Result<RegistrySnapshot, FaultDto> {
        let state = self.lock()?;
        let mut result = RegistrySnapshot {
            live: state.live,
            preparing: 0,
            ready: 0,
            closing: 0,
            poll_reserved: state.poll.is_some(),
            close_waiters: state.close_waiters,
            high_water: state.high_water,
        };
        for entry in state.entries.iter().flatten() {
            match entry.phase {
                Phase::Preparing => result.preparing += 1,
                Phase::Ready => result.ready += 1,
                Phase::Closing => result.closing += 1,
            }
        }
        Ok(result)
    }
    /// Native timer planning only. Renderer activity is neither necessary nor
    /// sufficient to call expire; Closing has no fictional cleanup deadline.
    pub fn next_deadline(&self) -> Result<Option<Instant>, FaultDto> {
        let state = self.lock()?;
        Ok(state
            .entries
            .iter()
            .flatten()
            .filter(|entry| entry.phase != Phase::Closing)
            .map(|entry| entry.deadline)
            .min())
    }
    pub fn begin_subscribe(
        self: &Arc<Self>,
        key: &str,
        now: Instant,
    ) -> Result<Subscribe, FaultDto> {
        let ordinal = self.epoch.ordinal(key)?;
        let mut state = self.lock()?;
        if !state.live {
            return Err(FaultDto::not_sent(FaultCode::UnavailableBinding));
        }
        if let Some(index) = state.index(ordinal) {
            let entry = state.entries[index]
                .as_ref()
                .expect("existing bounded slot");
            if entry.phase == Phase::Ready
                && now < entry.deadline
                && !entry.lifecycle.cancelled.load(Ordering::Acquire)
            {
                return Ok(Subscribe::AlreadyReady(AckDto::new(key)));
            }
            // No second observer and no unbounded readiness waiter fanout.
            return Err(FaultDto::not_sent(FaultCode::Disconnected));
        }
        if ordinal <= state.high_water {
            return Err(FaultDto::not_sent(FaultCode::Disconnected));
        }
        // Even a capacity-refused observed key cannot later resurrect via retry.
        state.high_water = ordinal;
        let index = state
            .entries
            .iter()
            .position(Option::is_none)
            .ok_or_else(|| FaultDto::not_sent(FaultCode::DeliveryFailed))?;
        let deadline = now
            .checked_add(PREPARING_TIMEOUT)
            .ok_or_else(|| FaultDto::not_sent(FaultCode::UnavailableBinding))?;
        let lifecycle = Arc::new(Lifecycle::new());
        state.entries[index] = Some(Entry {
            ordinal,
            phase: Phase::Preparing,
            deadline,
            lifecycle: lifecycle.clone(),
            worker: true,
            polling: false,
            cleanup: false,
            subscription: None,
        });
        Ok(Subscribe::Prepare(PrepareWork {
            registry: self.clone(),
            ordinal,
            key: key.into(),
            deadline,
            lifecycle,
            armed: true,
        }))
    }
    /// ACK is produced only after actual Subscription/work disposal. Preparing
    /// and in-flight poll cases return a bounded waiter, never a premature ACK.
    pub fn unsubscribe(self: &Arc<Self>, key: &str) -> Result<Unsubscribe, FaultDto> {
        let ordinal = self.epoch.ordinal(key)?;
        let detached = {
            let mut state = self.lock()?;
            state.high_water = state.high_water.max(ordinal);
            let Some(index) = state.index(ordinal) else {
                return Ok(Unsubscribe::Closed(AckDto::new(key)));
            };
            let entry = state.entries[index]
                .as_mut()
                .expect("existing bounded slot");
            State::retire(entry);
            if let Some(subscription) = entry.subscription.take() {
                entry.cleanup = true;
                Some(subscription)
            } else {
                None
            }
        };
        if let Some(subscription) = detached {
            drop(subscription); // Never hold the registry mutex while dropping a receiver.
            self.finish_cleanup(ordinal);
        }
        let mut state = self.lock()?;
        let Some(index) = state.index(ordinal) else {
            return Ok(Unsubscribe::Closed(AckDto::new(key)));
        };
        let lifecycle = state.entries[index]
            .as_ref()
            .expect("existing bounded slot")
            .lifecycle
            .clone();
        if state.close_waiters >= MAX_CLOSE_WAITERS
            || lifecycle.close_waiters.load(Ordering::Acquire) >= MAX_KEY_CLOSE_WAITERS
        {
            return Err(FaultDto::uncertain(FaultCode::Disconnected));
        }
        state.close_waiters += 1;
        lifecycle.close_waiters.fetch_add(1, Ordering::AcqRel);
        Ok(Unsubscribe::Waiting(CloseWait {
            registry: self.clone(),
            lifecycle,
            key: key.into(),
        }))
    }
    pub fn begin_poll(self: &Arc<Self>, key: &str, now: Instant) -> Result<PollLease, FaultDto> {
        let ordinal = self.epoch.ordinal(key)?;
        let mut state = self.lock()?;
        if !state.live {
            return Err(FaultDto::not_sent(FaultCode::UnavailableBinding));
        }
        if state.poll.is_some() {
            return Err(FaultDto::not_sent(FaultCode::Disconnected));
        }
        let index = state
            .index(ordinal)
            .ok_or_else(|| FaultDto::not_sent(FaultCode::Disconnected))?;
        let entry = state.entries[index]
            .as_mut()
            .expect("existing bounded slot");
        if entry.phase != Phase::Ready
            || now >= entry.deadline
            || entry.lifecycle.cancelled.load(Ordering::Acquire)
        {
            return Err(FaultDto::not_sent(FaultCode::Disconnected));
        }
        let deadline = now
            .checked_add(IDLE_TIMEOUT)
            .ok_or_else(|| FaultDto::not_sent(FaultCode::UnavailableBinding))?;
        let subscription = entry
            .subscription
            .take()
            .ok_or_else(|| FaultDto::not_sent(FaultCode::Disconnected))?;
        entry.deadline = deadline; // Only a valid admitted poll renews the lease.
        entry.polling = true;
        state.poll = Some(ordinal);
        Ok(PollLease {
            registry: self.clone(),
            ordinal,
            key: key.into(),
            subscription: Some(subscription),
            read: false,
            serialized: false,
            response_built: false,
            frame: None,
        })
    }
    /// Root repeats actual-caller authorization and this exact-key check before
    /// publishing a delayed readiness response. A previously computed ACK alone
    /// cannot grant readiness after revocation/expiry/retirement.
    pub fn confirm_ready_ack(&self, ack: &AckDto, now: Instant) -> Result<(), FaultDto> {
        let ordinal = self.epoch.ordinal(&ack.registration_key)?;
        let state = self.lock()?;
        let valid = state.live
            && state.index(ordinal).is_some_and(|index| {
                let entry = state.entries[index]
                    .as_ref()
                    .expect("existing bounded slot");
                entry.phase == Phase::Ready
                    && now < entry.deadline
                    && !entry.lifecycle.cancelled.load(Ordering::Acquire)
            });
        if valid {
            Ok(())
        } else {
            Err(FaultDto::uncertain(FaultCode::Disconnected))
        }
    }
    /// A root-owned independent timer calls this even when the renderer has no
    /// polling timer or never receives a control reply. At most eight transitions.
    pub fn expire(&self, now: Instant) -> Result<usize, FaultDto> {
        self.retire_matching(Some(now))
    }
    /// Irreversible. Root calls this synchronously on document/instance loss or
    /// timer/worker failure; normal Deferred close alone does not revoke it.
    pub fn revoke(&self) -> Result<usize, FaultDto> {
        self.retire_matching(None)
    }
    fn retire_matching(&self, now: Option<Instant>) -> Result<usize, FaultDto> {
        let mut detached: [Option<(u64, Subscription)>; MAX_REGISTRATIONS] =
            std::array::from_fn(|_| None);
        let mut retired = 0;
        {
            let mut state = self.lock()?;
            if now.is_none() {
                state.live = false;
            }
            for (index, slot) in state.entries.iter_mut().enumerate() {
                let Some(entry) = slot.as_mut() else {
                    continue;
                };
                if entry.phase != Phase::Closing && now.is_none_or(|now| now >= entry.deadline) {
                    State::retire(entry);
                    retired += 1;
                    if let Some(subscription) = entry.subscription.take() {
                        entry.cleanup = true;
                        detached[index] = Some((entry.ordinal, subscription));
                    }
                }
            }
        }
        for (ordinal, subscription) in detached.into_iter().flatten() {
            drop(subscription);
            self.finish_cleanup(ordinal);
        }
        Ok(retired)
    }
    fn finish_cleanup(&self, ordinal: u64) {
        if let Ok(mut state) = self.lock()
            && let Some(index) = state.index(ordinal)
        {
            state.entries[index]
                .as_mut()
                .expect("existing bounded slot")
                .cleanup = false;
            if state.finish_if_gone(ordinal) {
                self.changed.notify_all();
            }
        }
    }
    fn finish_worker(&self, ordinal: u64) {
        if let Ok(mut state) = self.lock()
            && let Some(index) = state.index(ordinal)
        {
            let entry = state.entries[index]
                .as_mut()
                .expect("existing bounded slot");
            State::retire(entry);
            entry.worker = false;
            if state.finish_if_gone(ordinal) {
                self.changed.notify_all();
            }
        }
    }
    fn deliverable(&self, ordinal: u64, now: Instant) -> Result<(), FaultDto> {
        let state = self.lock()?;
        let valid = state.live
            && state.index(ordinal).is_some_and(|index| {
                let entry = state.entries[index]
                    .as_ref()
                    .expect("existing bounded slot");
                entry.phase == Phase::Ready
                    && entry.polling
                    && now < entry.deadline
                    && !entry.lifecycle.cancelled.load(Ordering::Acquire)
            });
        if valid {
            Ok(())
        } else {
            Err(FaultDto::uncertain(FaultCode::Disconnected))
        }
    }
    fn fault_poll(&self, ordinal: u64) {
        if let Ok(mut state) = self.lock()
            && let Some(index) = state.index(ordinal)
        {
            State::retire(
                state.entries[index]
                    .as_mut()
                    .expect("existing bounded slot"),
            );
        }
    }
}

pub enum Subscribe {
    AlreadyReady(AckDto),
    Prepare(PrepareWork),
}
/// Returned before scheduling. A delayed or abandoned worker retains the
/// Preparing/Closing reservation until this actual token is dropped.
pub struct PrepareWork {
    registry: Arc<Registry>,
    ordinal: u64,
    key: String,
    deadline: Instant,
    lifecycle: Arc<Lifecycle>,
    armed: bool,
}
impl PrepareWork {
    /// Root bootstrap waiting shares this original admission deadline; it must
    /// not start a separate fresh five-second interval before attach.
    pub fn deadline(&self) -> Instant {
        self.deadline
    }
    pub fn attach(mut self, host: &HostHandle, now: Instant) -> Result<PreparingWait, FaultDto> {
        {
            let state = self.registry.lock()?;
            if !state.live
                || self.lifecycle.cancelled.load(Ordering::Acquire)
                || now >= self.deadline
            {
                return Err(FaultDto::not_sent(FaultCode::Disconnected));
            }
        }
        // Nonblocking enqueue, no registry lock/native owner crosses this call.
        let subscription = host
            .subscribe(None)
            .map_err(|_| FaultDto::not_sent(FaultCode::Disconnected))?;
        self.armed = false;
        Ok(PreparingWait {
            registry: self.registry.clone(),
            ordinal: self.ordinal,
            key: self.key.clone(),
            deadline: self.deadline,
            lifecycle: self.lifecycle.clone(),
            subscription: Some(subscription),
            armed: true,
        })
    }
}
impl Drop for PrepareWork {
    fn drop(&mut self) {
        if self.armed {
            self.registry.finish_worker(self.ordinal);
        }
    }
}

pub enum ReadyStep {
    Pending,
    Ready(AckDto),
}
/// Move to one bounded off-GUI readiness task. No thread is created here. Tests
/// can call step with zero wait before/after controlled actual owner turns.
pub struct PreparingWait {
    registry: Arc<Registry>,
    ordinal: u64,
    key: String,
    deadline: Instant,
    lifecycle: Arc<Lifecycle>,
    subscription: Option<Subscription>,
    armed: bool,
}
impl PreparingWait {
    pub fn step(&mut self, now: Instant) -> Result<ReadyStep, FaultDto> {
        self.receive(now, Duration::ZERO)
    }
    fn receive(&mut self, now: Instant, wait: Duration) -> Result<ReadyStep, FaultDto> {
        if !self.armed || self.lifecycle.cancelled.load(Ordering::Acquire) || now >= self.deadline {
            return Err(FaultDto::uncertain(FaultCode::Disconnected));
        }
        let ready = self
            .subscription
            .as_ref()
            .expect("unsettled readiness receiver")
            .ready_timeout(wait);
        match ready {
            Err(std::sync::mpsc::RecvTimeoutError::Timeout) => Ok(ReadyStep::Pending),
            Err(std::sync::mpsc::RecvTimeoutError::Disconnected) | Ok(Err(_)) => {
                Err(FaultDto::uncertain(FaultCode::Disconnected))
            }
            // This is the actual engine watermark ACK. No synthetic success.
            Ok(Ok(_real_cursor)) => self.install_ack(now).map(ReadyStep::Ready),
        }
    }
    /// Root invokes ONLY from its bounded off-main worker. Deadline began when
    /// begin_subscribe admitted the reservation, not when this task started.
    pub fn wait_to_ready(mut self, clock: &dyn RegistryClock) -> Result<AckDto, FaultDto> {
        loop {
            let now = clock.now();
            if now >= self.deadline || self.lifecycle.cancelled.load(Ordering::Acquire) {
                return Err(FaultDto::uncertain(FaultCode::Disconnected));
            }
            let remaining = self
                .deadline
                .checked_duration_since(now)
                .ok_or_else(|| FaultDto::uncertain(FaultCode::Disconnected))?;
            let wait = remaining.min(READY_WAIT_QUANTUM);
            // Use a new trusted time after a blocking wait for the final install.
            match self
                .subscription
                .as_ref()
                .expect("unsettled readiness receiver")
                .ready_timeout(wait)
            {
                Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {
                    if self.lifecycle.cancelled.load(Ordering::Acquire) {
                        return Err(FaultDto::uncertain(FaultCode::Disconnected));
                    }
                }
                Err(std::sync::mpsc::RecvTimeoutError::Disconnected) | Ok(Err(_)) => {
                    return Err(FaultDto::uncertain(FaultCode::Disconnected));
                }
                Ok(Ok(_real_cursor)) => return self.install_ack(clock.now()),
            }
        }
    }
    fn install_ack(&mut self, now: Instant) -> Result<AckDto, FaultDto> {
        let mut state = self.registry.lock()?;
        let index = state
            .index(self.ordinal)
            .ok_or_else(|| FaultDto::uncertain(FaultCode::Disconnected))?;
        if !state.live {
            return Err(FaultDto::uncertain(FaultCode::UnavailableBinding));
        }
        let entry = state.entries[index]
            .as_mut()
            .expect("existing bounded slot");
        if !self.armed
            || entry.phase != Phase::Preparing
            || !entry.worker
            || self.lifecycle.cancelled.load(Ordering::Acquire)
            || now >= entry.deadline
        {
            return Err(FaultDto::uncertain(FaultCode::Disconnected));
        }
        entry.deadline = now
            .checked_add(IDLE_TIMEOUT)
            .ok_or_else(|| FaultDto::uncertain(FaultCode::UnavailableBinding))?;
        entry.subscription = self.subscription.take();
        entry.worker = false;
        entry.phase = Phase::Ready;
        self.armed = false;
        Ok(AckDto::new(&self.key))
    }
}
impl Drop for PreparingWait {
    fn drop(&mut self) {
        if self.armed {
            drop(self.subscription.take()); // Actual disposal precedes slot release.
            self.registry.finish_worker(self.ordinal);
        }
    }
}

pub enum Unsubscribe {
    Closed(AckDto),
    Waiting(CloseWait),
}
/// Bounded at two per key and sixteen per registry until actual token drop,
/// including already closed keys whose off-main task has not returned yet.
pub struct CloseWait {
    registry: Arc<Registry>,
    lifecycle: Arc<Lifecycle>,
    key: String,
}
impl CloseWait {
    pub fn poll_closed(&self) -> Option<AckDto> {
        self.lifecycle
            .closed
            .load(Ordering::Acquire)
            .then(|| AckDto::new(&self.key))
    }
    pub fn wait_timeout(&self, timeout: Duration) -> Result<AckDto, FaultDto> {
        let state = self.registry.lock()?;
        let (_state, _timeout) = self
            .registry
            .changed
            .wait_timeout_while(state, timeout, |_| {
                !self.lifecycle.closed.load(Ordering::Acquire)
            })
            .map_err(|_| FaultDto::uncertain(FaultCode::UnavailableBinding))?;
        self.poll_closed()
            .ok_or_else(|| FaultDto::uncertain(FaultCode::Disconnected))
    }
}
impl Drop for CloseWait {
    fn drop(&mut self) {
        // Unknown poison leaves quota reserved; do not silently recover state.
        if let Ok(mut state) = self.registry.lock() {
            state.close_waiters -= 1;
            self.lifecycle.close_waiters.fetch_sub(1, Ordering::AcqRel);
        }
    }
}

/// One global reservation remains held across read/JSON serialization and local
/// retirement. It ends only on actual lease drop, never on an idle expiry flag.
/// It does NOT wait for or claim browser receipt/SDK promise settlement.
pub struct PollLease {
    registry: Arc<Registry>,
    ordinal: u64,
    key: String,
    subscription: Option<Subscription>,
    read: bool,
    serialized: bool,
    response_built: bool,
    frame: Option<String>,
}
impl PollLease {
    /// Point-in-time eligibility only; root still supplies the caller and
    /// document publication boundary through its actual native commit.
    pub(crate) fn check_deliverable(&self, now: Instant) -> Result<(), FaultDto> {
        self.registry.deliverable(self.ordinal, now)
    }

    pub fn read_one(&mut self, now: Instant) -> Result<(), FaultDto> {
        if self.read {
            return Err(FaultDto::uncertain(FaultCode::DeliveryFailed));
        }
        self.read = true;
        self.registry.deliverable(self.ordinal, now)?;
        let observation = self
            .subscription
            .as_ref()
            .expect("poll owns subscription")
            .try_recv();
        self.registry.deliverable(self.ordinal, now)?;
        match observation {
            Err(std::sync::mpsc::TryRecvError::Empty) => Ok(()),
            Err(std::sync::mpsc::TryRecvError::Disconnected) | Ok(HostObservation::Fault(_)) => {
                self.registry.fault_poll(self.ordinal);
                Err(FaultDto::uncertain(FaultCode::Disconnected))
            }
            Ok(HostObservation::Event(frame)) => {
                let bytes = frame.into_bytes();
                if bytes.is_empty() || bytes.len() > MAX_FRAME_BYTES {
                    self.registry.fault_poll(self.ordinal);
                    return Err(FaultDto::uncertain(FaultCode::DeliveryFailed));
                }
                let frame = String::from_utf8(bytes).map_err(|_| {
                    self.registry.fault_poll(self.ordinal);
                    FaultDto::uncertain(FaultCode::DeliveryFailed)
                })?;
                // The engine already encoded/validated this event. Retain the
                // exact raw frame for the common frontend decoding path.
                self.frame = Some(frame);
                Ok(())
            }
        }
    }
    /// Root builds its raw JSON IPC response while this lease is STILL alive,
    /// then drops it. Returning a serde DTO first would release the reservation
    /// before Tauri's later serialization; that shortcut is not this interface.
    pub fn serialize_json(&mut self, now: Instant) -> Result<String, FaultDto> {
        if !self.read || self.serialized {
            return Err(FaultDto::uncertain(FaultCode::DeliveryFailed));
        }
        self.registry.deliverable(self.ordinal, now)?;
        self.serialized = true;
        let dto = PollDto {
            schema_version: 1,
            registration_key: self.key.clone(),
            frames: OneFrame(self.frame.take()),
        };
        // One <=256KiB UTF-8 frame needs at most six JSON bytes per raw byte;
        // fixed ASCII metadata is <256 bytes. No unbounded serialization input.
        let encoded = serde_json::to_string(&dto)
            .map_err(|_| FaultDto::uncertain(FaultCode::DeliveryFailed))?;
        if encoded.len() > MAX_POLL_DTO_BYTES {
            return Err(FaultDto::uncertain(FaultCode::DeliveryFailed));
        }
        self.registry.deliverable(self.ordinal, now)?;
        self.response_built = true;
        Ok(encoded)
    }
    /// Root calls this if its response construction fails after serialization.
    /// It is never an acknowledgement of framework or browser delivery.
    pub fn abandon_response(&self) {
        self.registry.fault_poll(self.ordinal);
    }
}
impl Drop for PollLease {
    fn drop(&mut self) {
        // Cancellation/panic/early-return before a complete bounded native
        // response could lose a popped event. Continuing would conceal a gap.
        if !self.response_built {
            self.registry.fault_poll(self.ordinal);
        }
        if let Ok(mut state) = self.registry.lock()
            && let Some(index) = state.index(self.ordinal)
        {
            let live = state.live;
            let entry = state.entries[index]
                .as_mut()
                .expect("existing bounded slot");
            if live
                && entry.phase == Phase::Ready
                && !entry.lifecycle.cancelled.load(Ordering::Acquire)
            {
                entry.subscription = self.subscription.take();
                entry.polling = false;
                state.poll = None;
                return;
            }
        }
        drop(self.subscription.take()); // Real disposal before global poll release.
        if let Ok(mut state) = self.registry.lock() {
            if let Some(index) = state.index(self.ordinal) {
                state.entries[index]
                    .as_mut()
                    .expect("existing bounded slot")
                    .polling = false;
                if state.finish_if_gone(self.ordinal) {
                    self.registry.changed.notify_all();
                }
            }
            if state.poll == Some(self.ordinal) {
                state.poll = None;
            }
        }
    }
}

#[cfg(test)]
#[path = "registration_tests.rs"]
mod tests;
