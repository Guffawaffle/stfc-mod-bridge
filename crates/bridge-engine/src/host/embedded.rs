//! A local owner whose bounded turns are driven by an external event loop.
//!
//! This portable custody boundary does not establish native GUI responsiveness.
//! Production native owners require an aborting panic policy before construction;
//! catching a panic cannot preserve call-local native guards during unwinding.
use super::*;
use std::cell::{Cell, RefCell};

/// One external event-loop turn. Retained is never authority to destroy the
/// shell. Reentered performed no driver or owner call. Closed is published only
/// after a fresh safe disposition and successful owner-thread destruction.
/// A destruction panic permanently denies Closed; Retained then describes the
/// shell refusal and does not prove that a partially destroyed owner survived.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum EmbeddedTurn {
    Retained {
        closing: bool,
        failure: Option<HostFailure>,
    },
    Reentered,
    Closed(HostExit),
}

/// Construct, progress and safely dispose the owner on its original thread.
/// The host must own its retained state: a borrowed wrapper cannot extend the
/// lifetime of an external native guard when this shell is abandoned.
/// No owner reference or extraction operation is exposed. Abandoning a retained
/// shell leaks its boxed runtime instead of releasing unresolved custody; that
/// abandons servicing and is not a normal shutdown or a recovery qualification.
///
/// ```compile_fail
/// use bridge_engine::host::{EmbeddedOwner, LocalHost};
/// fn transfer<H: LocalHost + Send + 'static>(owner: EmbeddedOwner<H>) {
///     std::thread::spawn(move || drop(owner));
/// }
/// ```
///
/// A Send host still cannot make this local shell Sync.
///
/// ```compile_fail
/// use bridge_engine::host::{EmbeddedOwner, LocalHost};
/// fn share<H: LocalHost + Send + Sync + 'static>(owner: &EmbeddedOwner<H>) {
///     std::thread::scope(|scope| { scope.spawn(move || owner.signal_close()); });
/// }
/// ```
///
/// A factory may borrow setup configuration, but its returned host cannot borrow
/// the external owner whose lifetime the retained wrapper is meant to preserve.
///
/// ```compile_fail
/// use bridge_contracts::v1::*;
/// use bridge_engine::host::{EmbeddedOwner, LocalHost, HostFailure, OwnerInbox};
/// struct Borrowed<'a, H: LocalHost>(&'a mut H);
/// impl<H: LocalHost> LocalHost for Borrowed<'_, H> {
///     fn dispatch(&mut self, request: ValidatedRequest) -> Result<ValidatedReply, HostFailure> { self.0.dispatch(request) }
///     fn cursor(&self) -> Cursor { self.0.cursor() }
///     fn pump(&mut self) -> Result<(), HostFailure> { self.0.pump() }
///     fn events_after(&mut self, after: &Cursor) -> Result<EventBatch, HostFailure> { self.0.events_after(after) }
///     fn request_close(&mut self) -> Result<CloseDisposition, HostFailure> { self.0.request_close() }
///     fn close_disposition(&self) -> Result<CloseDisposition, HostFailure> { self.0.close_disposition() }
/// }
/// fn borrow_owner<'a, H: LocalHost + 'a>(inbox: OwnerInbox, host: &'a mut H) {
///     let _ = EmbeddedOwner::on_current_thread(inbox, || Ok(Borrowed(host)));
/// }
/// ```
pub struct EmbeddedOwner<H: LocalHost + 'static> {
    retained: RefCell<Option<Box<transport::Runtime<H>>>>,
    terminal: RefCell<Option<HostExit>>,
    close_signal: Cell<bool>,
    closing_observed: Cell<bool>,
    driver_live: Cell<bool>,
    tainted: Cell<bool>,
    failure: Cell<Option<HostFailure>>,
    _local: PhantomData<Rc<()>>,
}

impl<H: LocalHost + 'static> EmbeddedOwner<H> {
    pub fn on_current_thread<F>(inbox: OwnerInbox, factory: F) -> Result<Self, HostFailure>
    where
        F: FnOnce() -> Result<H, HostFailure>,
    {
        let host =
            catch_unwind(AssertUnwindSafe(factory)).map_err(|_| HostFailure::FactoryPanicked)??;
        Ok(Self {
            retained: RefCell::new(Some(Box::new(transport::Runtime::new(host, inbox.inner)))),
            terminal: RefCell::new(None),
            close_signal: Cell::new(false),
            closing_observed: Cell::new(false),
            driver_live: Cell::new(true),
            tainted: Cell::new(false),
            failure: Cell::new(None),
            _local: PhantomData,
        })
    }

    /// Latch intent even during a recursive platform callback. This performs no
    /// owner call and is not an acknowledgement of the owner's close disposition.
    /// The external shell must also synchronously close its own submission gate.
    pub fn signal_close(&self) {
        self.close_signal.set(true);
    }

    /// At most one inbox request, owner progression and bounded event delivery.
    /// Multiple driver ticks cannot consume additional turns. Missing driver
    /// ticks receive one fallback tick. After a driver error or close request,
    /// later turns progress the retained owner without invoking another driver.
    /// No sleep or drain loop occurs here; service the OS loop between calls.
    ///
    /// The callback cannot retain its borrowed pump beyond this call.
    ///
    /// ```compile_fail
    /// use bridge_engine::host::{EmbeddedOwner, LocalHost, DriveControl};
    /// fn escape<H: LocalHost + 'static>(owner: &EmbeddedOwner<H>) {
    ///     let mut saved = None;
    ///     owner.turn(|pump| { saved = Some(pump); Ok(DriveControl::Continue) });
    ///     drop(saved);
    /// }
    /// ```
    pub fn turn<D>(&self, driver: D) -> EmbeddedTurn
    where
        D: FnOnce(&mut OwnerThreadPump<'_, H>) -> Result<DriveControl, HostFailure>,
    {
        let Ok(mut retained) = self.retained.try_borrow_mut() else {
            return EmbeddedTurn::Reentered;
        };
        if let Some(exit) = self.terminal.borrow().as_ref() {
            return EmbeddedTurn::Closed(exit.clone());
        }
        if self.tainted.get() {
            return self.observation(&retained);
        }
        let result = catch_unwind(AssertUnwindSafe(|| {
            let runtime = retained.as_mut().expect("retained embedded runtime");
            if self.close_signal.get() {
                runtime.arm_close();
            }
            self.observe_closing(runtime);
            let mut used_turn = false;
            if self.driver_live.get() && !runtime.custody_unknown() {
                let mut pump = OwnerThreadPump {
                    runtime,
                    tick_budget: Some(&mut used_turn),
                    close_signal: Some(&self.close_signal),
                    _local: PhantomData,
                };
                match driver(&mut pump) {
                    Ok(DriveControl::Continue) => {}
                    Ok(DriveControl::RequestClose) => {
                        self.driver_live.set(false);
                        self.close_signal.set(true);
                    }
                    Err(failure) => {
                        self.driver_live.set(false);
                        self.close_signal.set(true);
                        pump.runtime.fail(failure);
                    }
                }
            }
            if self.close_signal.get() {
                runtime.arm_close();
            }
            self.observe_closing(runtime);
            if !used_turn && !runtime.custody_unknown() {
                runtime.tick_with_close_signal(Some(&self.close_signal));
            }
            if self.close_signal.get() {
                runtime.arm_close();
            }
            self.observe_closing(runtime);
            if runtime.custody_unknown() {
                self.tainted.set(true);
                return None;
            }
            runtime.exit_when_safe()
        }));
        match result {
            Ok(Some(exit)) => {
                // Keep the borrow during destruction, so native destructor
                // callbacks cannot reenter an empty/replaced owner shell.
                let runtime = retained.take().expect("safe embedded runtime");
                match catch_unwind(AssertUnwindSafe(|| drop(runtime))) {
                    Ok(()) => {
                        *self.terminal.borrow_mut() = Some(exit.clone());
                        EmbeddedTurn::Closed(exit)
                    }
                    Err(_) => {
                        self.tainted.set(true);
                        self.failure.set(Some(HostFailure::OwnerPanicked));
                        self.observation(&retained)
                    }
                }
            }
            Ok(None) => self.observation(&retained),
            Err(_) => {
                self.tainted.set(true);
                self.driver_live.set(false);
                self.close_signal.set(true);
                let mut failure = HostFailure::DriverPanicked;
                if let Some(runtime) = retained.as_mut() {
                    runtime.taint(HostFailure::DriverPanicked);
                    failure = runtime.failure.unwrap_or(failure);
                }
                self.failure.set(self.failure.get().or(Some(failure)));
                self.observation(&retained)
            }
        }
    }

    fn observe_closing(&self, runtime: &transport::Runtime<H>) {
        if runtime.closing {
            self.closing_observed.set(true);
            self.driver_live.set(false);
        }
    }

    fn observation(&self, retained: &Option<Box<transport::Runtime<H>>>) -> EmbeddedTurn {
        if let Some(runtime) = retained.as_ref() {
            self.observe_closing(runtime);
        }
        EmbeddedTurn::Retained {
            closing: self.closing_observed.get(),
            failure: self
                .failure
                .get()
                .or_else(|| retained.as_ref().and_then(|runtime| runtime.failure)),
        }
    }
}

impl<H: LocalHost + 'static> Drop for EmbeddedOwner<H> {
    fn drop(&mut self) {
        if let Some(runtime) = self.retained.get_mut().take() {
            // No owner calls, native destruction, progress or shutdown claims.
            let _ = Box::leak(runtime);
        }
    }
}
