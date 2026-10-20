//! Bounded owned-frame transport with thread-local engine custody.
//!
//! Only factories/configuration and protocol observations cross threads. A
//! factory constructs its possibly !Send host inside the owning run boundary.
//! The private runner retains that host through disconnect and deferred close.
//! No native loader, store, game, desktop API or synthetic production host is
//! supplied here. Actual platform provisioning and service adoption are pending.
mod embedded;
mod kernel;
mod transport;

use crate::operations::KernelFailure;
use bridge_contracts::v1::*;
pub use embedded::{EmbeddedOwner, EmbeddedTurn};
pub use kernel::KernelHost;
use std::{
    marker::PhantomData,
    panic::{AssertUnwindSafe, catch_unwind},
    rc::Rc,
    thread,
    time::Duration,
};
pub use transport::{
    HostHandle, HostObservation, ObservationFailure, OwnedFrame, PendingClose, PendingReply,
    SubmissionFailure, SubmissionFailureCode, Subscription, owner_channel,
};

pub const MAX_QUEUED_REQUESTS: usize = 16;
pub const MAX_PENDING_REPLIES: usize = 32;
pub const MAX_OBSERVERS: usize = 8;
pub const MAX_OBSERVER_EVENTS: usize = 16;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum HostFailure {
    Kernel(KernelFailure),
    InvalidReply,
    InvalidObservation,
    ResnapshotRequired,
    CloseUnavailable,
    UnavailableService,
    SpawnFailed,
    FactoryPanicked,
    OwnerPanicked,
    DriverPanicked,
}
impl std::fmt::Display for HostFailure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("local host unavailable")
    }
}
impl std::error::Error for HostFailure {}

/// Construct, invoke and drop this object on one owning thread. Methods must
/// remain bounded by their real owner boundary. No implementation may fabricate
/// safe closure, progress, recovery or native availability. Panicking owner
/// methods permanently retain the runner because custody is then unknown.
pub trait LocalHost {
    fn dispatch(&mut self, request: ValidatedRequest) -> Result<ValidatedReply, HostFailure>;
    fn cursor(&self) -> Cursor;
    fn pump(&mut self) -> Result<(), HostFailure>;
    fn events_after(&mut self, after: &Cursor) -> Result<EventBatch, HostFailure>;
    /// Arm refusal of new admission before reporting a close disposition.
    fn request_close(&mut self) -> Result<CloseDisposition, HostFailure>;
    fn close_disposition(&self) -> Result<CloseDisposition, HostFailure>;
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DriveControl {
    Continue,
    RequestClose,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct HostExit {
    pub disposition: CloseDisposition,
    pub cursor: Cursor,
    pub failure: Option<HostFailure>,
}

/// Protocol inbox only. It contains no engine or retained native owner and may
/// move to the selected thread before its factory is invoked.
pub struct OwnerInbox {
    pub(crate) inner: transport::Inbox,
}

/// A borrowed owner-thread pump cannot be moved out of the run boundary or used
/// to drop its host. The local marker also prevents transfer for Send test hosts.
///
/// ```compile_fail
/// use bridge_engine::host::{OwnerThreadPump, LocalHost};
/// fn transfer<H: LocalHost + Send>(pump: OwnerThreadPump<'_, H>) {
///     std::thread::scope(|scope| { scope.spawn(move || drop(pump)); });
/// }
/// ```
pub struct OwnerThreadPump<'a, H: LocalHost> {
    runtime: &'a mut transport::Runtime<H>,
    tick_budget: Option<&'a mut bool>,
    close_signal: Option<&'a std::cell::Cell<bool>>,
    _local: PhantomData<Rc<()>>,
}
impl<H: LocalHost> OwnerThreadPump<'_, H> {
    /// One request, one owner progression, then bounded event delivery.
    pub fn tick(&mut self) {
        if let Some(used) = self.tick_budget.as_mut() {
            if **used {
                return;
            }
            // Reserve before any callback can signal close or reenter.
            **used = true;
        }
        self.runtime.tick_with_close_signal(self.close_signal);
    }
    pub fn request_close(&mut self) {
        self.runtime.arm_close();
    }
    pub fn is_closing(&self) -> bool {
        self.runtime.closing
    }
    pub fn failure(&self) -> Option<HostFailure> {
        self.runtime.failure
    }
}

/// Run on the current thread, including a platform main thread where required.
/// The callback performs one bounded host/run-loop turn. Returning RequestClose
/// or panicking stops observation, then the runner keeps pumping until actual
/// Ready/safe RecoveryRequired. Deferred/error/owner panic cannot return/drop H.
/// The callback does not receive ownership of H or any retained native handle.
/// Native GUI embedding must separately prove run-loop progress during draining.
pub fn run_on_current_thread<F, H, D>(
    inbox: OwnerInbox,
    factory: F,
    mut driver: D,
) -> Result<HostExit, HostFailure>
where
    F: FnOnce() -> Result<H, HostFailure>,
    H: LocalHost,
    D: FnMut(&mut OwnerThreadPump<'_, H>) -> DriveControl,
{
    let host =
        catch_unwind(AssertUnwindSafe(factory)).map_err(|_| HostFailure::FactoryPanicked)??;
    let mut runtime = transport::Runtime::new(host, inbox.inner);
    let mut driver_live = true;
    loop {
        if driver_live {
            let mut pump = OwnerThreadPump {
                runtime: &mut runtime,
                tick_budget: None,
                close_signal: None,
                _local: PhantomData,
            };
            match catch_unwind(AssertUnwindSafe(|| driver(&mut pump))) {
                Ok(DriveControl::Continue) => {}
                Ok(DriveControl::RequestClose) => {
                    driver_live = false;
                    runtime.arm_close();
                }
                Err(_) => {
                    driver_live = false;
                    runtime.fail(HostFailure::DriverPanicked);
                    runtime.arm_close();
                }
            }
        }
        runtime.tick();
        if let Some(exit) = runtime.exit_when_safe() {
            return Ok(exit);
        }
        thread::sleep(Duration::from_millis(1));
    }
}

/// The factory itself crosses threads; its result deliberately need not Send.
/// Native modules, consumers, leases and journal wrappers must be constructed
/// inside this factory, never captured from the caller. Dropping the JoinHandle
/// detaches observation and does not terminate the worker thread.
///
/// ```compile_fail
/// use bridge_engine::host::{LocalHost, spawn_local_host};
/// fn transfer_existing<H: LocalHost + 'static>(host: H) {
///     let _ = spawn_local_host(move || Ok(host));
/// }
/// ```
pub fn spawn_local_host<F, H>(
    factory: F,
) -> Result<
    (
        HostHandle,
        thread::JoinHandle<Result<HostExit, HostFailure>>,
    ),
    HostFailure,
>
where
    F: FnOnce() -> Result<H, HostFailure> + Send + 'static,
    H: LocalHost + 'static,
{
    let (handle, inbox) = owner_channel();
    let worker = thread::Builder::new()
        .name("bridge-local-host".into())
        .spawn(move || run_on_current_thread(inbox, factory, |_| DriveControl::Continue))
        .map_err(|_| HostFailure::SpawnFailed)?;
    Ok((handle, worker))
}
