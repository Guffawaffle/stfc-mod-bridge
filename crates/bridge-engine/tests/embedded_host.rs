//! Portable event-loop custody falsification with a deliberately synthetic
//! !Send host. No native service, GUI, private journal or installed game proof.
use bridge_contracts::v1::*;
use bridge_engine::host::*;
use std::{
    cell::{Cell, RefCell},
    rc::Rc,
    sync::mpsc::TryRecvError,
    thread::{self, ThreadId},
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum OwnerEntry {
    Dispatch,
    Cursor,
    Pump,
    Events,
    RequestClose,
    CloseDisposition,
}

#[derive(Default)]
struct Audit {
    threads: RefCell<Vec<ThreadId>>,
    pumps: Cell<usize>,
    effects: Cell<usize>,
    closes: Cell<usize>,
    drops: Cell<usize>,
    released: Cell<bool>,
    recovery: Cell<bool>,
    close_error: Cell<bool>,
    pump_panic: Cell<bool>,
    drop_panic: Cell<bool>,
    on_dispatch: RefCell<Option<Rc<dyn Fn()>>>,
    on_drop: RefCell<Option<Rc<dyn Fn()>>>,
    panic_at: Cell<Option<OwnerEntry>>,
    requires_external_service: Cell<bool>,
    external_services: Cell<usize>,
}
impl Audit {
    fn call(&self) {
        self.threads.borrow_mut().push(thread::current().id());
    }
    fn entry(&self, point: OwnerEntry) {
        self.call();
        assert_ne!(
            self.panic_at.get(),
            Some(point),
            "controlled owner entry panic"
        );
    }
}
struct Host {
    audit: Rc<Audit>,
    closing: bool,
}
impl Drop for Host {
    fn drop(&mut self) {
        self.audit.call();
        self.audit.drops.set(self.audit.drops.get() + 1);
        if let Some(callback) = self.audit.on_drop.borrow().as_ref() {
            callback();
        }
        assert!(!self.audit.drop_panic.get(), "controlled destructor panic");
    }
}
fn cursor() -> Cursor {
    Cursor {
        host_epoch: HostEpoch::new("11111111-1111-4111-8111-111111111111").unwrap(),
        stream_id: StreamId::new("22222222-2222-4222-8222-222222222222").unwrap(),
        sequence: Sequence::new(0),
    }
}
impl LocalHost for Host {
    fn dispatch(&mut self, request: ValidatedRequest) -> Result<ValidatedReply, HostFailure> {
        self.audit.entry(OwnerEntry::Dispatch);
        if self.closing {
            return Err(HostFailure::UnavailableService);
        }
        self.audit.effects.set(self.audit.effects.get() + 1);
        if let Some(callback) = self.audit.on_dispatch.borrow().as_ref() {
            callback();
        }
        let mut reply = decode_reply(include_bytes!(
            "../../../contracts/fixtures/checkpoint-hello-reply.json"
        ))
        .unwrap()
        .into_inner();
        reply.request_id = ReplyRequestId::new(Some(request.as_inner().request_id.clone()));
        Ok(decode_reply(&serde_json::to_vec(&reply).unwrap()).unwrap())
    }
    fn cursor(&self) -> Cursor {
        self.audit.entry(OwnerEntry::Cursor);
        cursor()
    }
    fn pump(&mut self) -> Result<(), HostFailure> {
        self.audit.entry(OwnerEntry::Pump);
        if self.audit.requires_external_service.get() {
            assert_eq!(
                self.audit.external_services.get(),
                self.audit.pumps.get(),
                "external event-loop service must occur between owner turns"
            );
        }
        self.audit.pumps.set(self.audit.pumps.get() + 1);
        assert!(!self.audit.pump_panic.get(), "controlled owner panic");
        Ok(())
    }
    fn events_after(&mut self, after: &Cursor) -> Result<EventBatch, HostFailure> {
        self.audit.entry(OwnerEntry::Events);
        Ok(EventBatch {
            after: after.clone(),
            next: after.clone(),
            events: BoundedList::new(vec![]).unwrap(),
        })
    }
    fn request_close(&mut self) -> Result<CloseDisposition, HostFailure> {
        self.audit.entry(OwnerEntry::RequestClose);
        self.audit.closes.set(self.audit.closes.get() + 1);
        self.closing = true;
        self.close_disposition()
    }
    fn close_disposition(&self) -> Result<CloseDisposition, HostFailure> {
        self.audit.entry(OwnerEntry::CloseDisposition);
        if self.audit.close_error.get() {
            return Err(HostFailure::CloseUnavailable);
        }
        if !self.audit.released.get() {
            return Ok(CloseDisposition::Deferred {
                obligations: BoundedList::new(vec![CloseObligation::Operation {
                    operation_id: OperationId::new("33333333-3333-4333-8333-333333333333").unwrap(),
                    operation_revision: RevisionCounter::new(1),
                }])
                .unwrap(),
            });
        }
        if self.audit.recovery.get() {
            return Ok(CloseDisposition::RecoveryRequired {
                recoveries: BoundedList::new(vec![RecoveryRef {
                    operation_id: OperationId::new("33333333-3333-4333-8333-333333333333").unwrap(),
                    transaction: NativeTransactionRef::new("controlled-embedded-owner").unwrap(),
                    target: RecoveryTarget::ApplicationPreferences {
                        revision: OpaqueRevision::new("fixture:1").unwrap(),
                    },
                }])
                .unwrap(),
            });
        }
        Ok(CloseDisposition::Ready)
    }
}
fn setup() -> (Rc<Audit>, HostHandle, Rc<EmbeddedOwner<Host>>) {
    let audit = Rc::new(Audit::default());
    let (handle, inbox) = owner_channel();
    let local = audit.clone();
    let owner = EmbeddedOwner::on_current_thread(inbox, || {
        local.call();
        Ok(Host {
            audit: local,
            closing: false,
        })
    })
    .unwrap();
    (audit, handle, Rc::new(owner))
}
fn frame() -> OwnedFrame {
    OwnedFrame::new(
        include_bytes!("../../../contracts/fixtures/checkpoint-hello-request.json").to_vec(),
    )
    .unwrap()
}
fn progress(owner: &EmbeddedOwner<Host>) -> EmbeddedTurn {
    owner.turn(|_| Ok(DriveControl::Continue))
}

#[test]
fn embedded_deferred_close_services_external_turns_before_exact_once_drop() {
    let (audit, _handle, owner) = setup();
    owner.signal_close();
    for _ in 0..5 {
        assert!(matches!(
            progress(&owner),
            EmbeddedTurn::Retained { closing: true, .. }
        ));
        assert_eq!(audit.drops.get(), 0);
        // An external platform turn is available here, outside the engine call.
        assert_eq!(audit.closes.get(), 1);
    }
    audit.released.set(true);
    let closed = progress(&owner);
    assert!(
        matches!(&closed, EmbeddedTurn::Closed(exit) if exit.disposition == CloseDisposition::Ready)
    );
    assert_eq!(audit.drops.get(), 1);
    assert_eq!(progress(&owner), closed);
    assert_eq!(audit.pumps.get(), 6);
    assert!(
        audit
            .threads
            .borrow()
            .iter()
            .all(|id| *id == thread::current().id())
    );
}

#[test]
fn embedded_multiple_driver_ticks_and_fallback_have_one_turn_budget() {
    let (audit, handle, owner) = setup();
    let first = handle.exchange(frame()).unwrap();
    let second = handle.exchange(frame()).unwrap();
    owner.turn(|pump| {
        pump.tick();
        pump.tick();
        Ok(DriveControl::Continue)
    });
    assert!(first.try_recv().unwrap().is_ok());
    assert!(matches!(second.try_recv(), Err(TryRecvError::Empty)));
    assert_eq!(audit.pumps.get(), 1);
    progress(&owner);
    assert!(second.try_recv().unwrap().is_ok());
    assert_eq!(audit.pumps.get(), 2);
}

#[test]
fn embedded_driver_recursion_refuses_before_another_owner_call() {
    let (audit, _handle, owner) = setup();
    owner.turn(|pump| {
        assert_eq!(progress(&owner), EmbeddedTurn::Reentered);
        owner.signal_close();
        pump.tick();
        Ok(DriveControl::Continue)
    });
    assert_eq!(audit.pumps.get(), 1);
    assert_eq!(audit.closes.get(), 1);
    assert_eq!(audit.drops.get(), 0);
}

#[test]
fn embedded_owner_callback_close_arms_before_later_queued_admission() {
    let (audit, handle, owner) = setup();
    let weak = Rc::downgrade(&owner);
    *audit.on_dispatch.borrow_mut() = Some(Rc::new(move || {
        let owner = weak.upgrade().unwrap();
        assert_eq!(progress(&owner), EmbeddedTurn::Reentered);
        owner.signal_close();
    }));
    let first = handle.exchange(frame()).unwrap();
    let second = handle.exchange(frame()).unwrap();
    progress(&owner);
    assert!(first.try_recv().unwrap().is_ok());
    assert_eq!(audit.closes.get(), 1);
    owner.turn(|_| panic!("close from owner callback must retire the driver"));
    assert_eq!(
        second.try_recv().unwrap(),
        Err(HostFailure::UnavailableService)
    );
    assert_eq!(audit.effects.get(), 1);
    assert_eq!(audit.drops.get(), 0);
}

#[test]
fn embedded_driver_error_and_observer_loss_keep_independent_progress() {
    let (audit, handle, owner) = setup();
    drop(handle.exchange(frame()).unwrap());
    drop(handle.subscribe(None).unwrap());
    let result = owner.turn(|pump| {
        pump.tick();
        Err(HostFailure::UnavailableService)
    });
    assert!(matches!(
        result,
        EmbeddedTurn::Retained {
            failure: Some(HostFailure::UnavailableService),
            ..
        }
    ));
    owner.turn(|_| panic!("failed driver must not be called again"));
    assert_eq!(audit.pumps.get(), 2);
    assert_eq!(audit.effects.get(), 1);
    audit.released.set(true);
    assert!(matches!(progress(&owner), EmbeddedTurn::Closed(_)));
}

#[test]
fn embedded_close_error_and_abandonment_never_destroy_retained_owner() {
    let (audit, _handle, owner) = setup();
    audit.close_error.set(true);
    audit.released.set(true);
    owner.signal_close();
    for _ in 0..3 {
        assert!(matches!(
            progress(&owner),
            EmbeddedTurn::Retained {
                failure: Some(HostFailure::CloseUnavailable),
                ..
            }
        ));
    }
    drop(owner);
    assert_eq!(audit.drops.get(), 0);
    assert_eq!(audit.pumps.get(), 3);
}

#[test]
fn embedded_safe_recovery_closes_once_and_preserves_recovery_disposition() {
    let (audit, _handle, owner) = setup();
    audit.released.set(true);
    audit.recovery.set(true);
    owner.signal_close();
    assert!(matches!(
        progress(&owner),
        EmbeddedTurn::Closed(HostExit {
            disposition: CloseDisposition::RecoveryRequired { .. },
            ..
        })
    ));
    assert_eq!(audit.drops.get(), 1);
}

#[test]
fn embedded_driver_panic_permanently_taints_custody_and_prohibits_closed() {
    let (audit, _handle, owner) = setup();
    assert!(matches!(
        owner.turn(|_| panic!("controlled driver panic")),
        EmbeddedTurn::Retained {
            failure: Some(HostFailure::DriverPanicked),
            ..
        }
    ));
    audit.released.set(true);
    for _ in 0..3 {
        assert!(matches!(progress(&owner), EmbeddedTurn::Retained { .. }));
    }
    assert_eq!(audit.pumps.get(), 0);
    drop(owner);
    assert_eq!(audit.drops.get(), 0);
}

#[test]
fn embedded_owner_panic_permanently_stops_owner_calls_without_drop() {
    let (audit, _handle, owner) = setup();
    audit.pump_panic.set(true);
    assert!(matches!(
        progress(&owner),
        EmbeddedTurn::Retained {
            failure: Some(HostFailure::OwnerPanicked),
            ..
        }
    ));
    let calls = audit.threads.borrow().len();
    audit.released.set(true);
    assert!(matches!(progress(&owner), EmbeddedTurn::Retained { .. }));
    assert_eq!(audit.threads.borrow().len(), calls);
    drop(owner);
    assert_eq!(audit.drops.get(), 0);
}

#[test]
fn embedded_destructor_panic_does_not_publish_closed_or_reenter_owner() {
    let (audit, _handle, owner) = setup();
    audit.released.set(true);
    audit.drop_panic.set(true);
    owner.signal_close();
    let weak = Rc::downgrade(&owner);
    *audit.on_drop.borrow_mut() = Some(Rc::new(move || {
        let owner = weak.upgrade().unwrap();
        assert_eq!(progress(&owner), EmbeddedTurn::Reentered);
        owner.signal_close();
    }));
    assert!(matches!(
        progress(&owner),
        EmbeddedTurn::Retained {
            closing: true,
            failure: Some(HostFailure::OwnerPanicked)
        }
    ));
    let calls = audit.threads.borrow().len();
    assert!(matches!(
        owner.turn(|_| panic!("destruction failure cannot restart the driver")),
        EmbeddedTurn::Retained {
            closing: true,
            failure: Some(HostFailure::OwnerPanicked)
        }
    ));
    assert_eq!(audit.threads.borrow().len(), calls);
    assert_eq!(audit.drops.get(), 1);
}

#[test]
fn embedded_external_close_retires_driver_but_fallback_keeps_deferred_work_serviced() {
    let (audit, _handle, owner) = setup();
    owner.signal_close();
    for pumps in 1..=3 {
        assert!(matches!(
            owner.turn(|_| panic!("external close retires driver before its next call")),
            EmbeddedTurn::Retained {
                closing: true,
                failure: None
            }
        ));
        assert_eq!(audit.pumps.get(), pumps);
        assert_eq!(audit.drops.get(), 0);
    }
    audit.released.set(true);
    assert!(matches!(
        owner.turn(|_| panic!("released close does not revive driver")),
        EmbeddedTurn::Closed(_)
    ));
    assert_eq!(audit.drops.get(), 1);
}

#[test]
fn embedded_queued_close_retires_driver_after_the_one_turn_that_observes_it() {
    let (audit, handle, owner) = setup();
    let close = handle.request_close().unwrap();
    let drives = Cell::new(0);
    assert!(matches!(
        owner.turn(|pump| {
            drives.set(drives.get() + 1);
            pump.tick();
            Ok(DriveControl::Continue)
        }),
        EmbeddedTurn::Retained { closing: true, .. }
    ));
    assert!(matches!(
        close.try_recv().unwrap().unwrap(),
        CloseDisposition::Deferred { .. }
    ));
    assert_eq!(drives.get(), 1);
    assert!(matches!(
        owner.turn(|_| panic!("observed inbox close retires later driver")),
        EmbeddedTurn::Retained { closing: true, .. }
    ));
    assert_eq!(audit.pumps.get(), 2);
    audit.released.set(true);
    assert!(matches!(progress(&owner), EmbeddedTurn::Closed(_)));
}

#[test]
fn embedded_abandonment_keeps_owned_state_after_external_observation_is_dropped() {
    let (audit, handle, owner) = setup();
    let weak = Rc::downgrade(&audit);
    owner.signal_close();
    assert!(matches!(
        progress(&owner),
        EmbeddedTurn::Retained { closing: true, .. }
    ));
    drop(handle);
    drop(audit);
    drop(owner);
    // The abandoned wrapper owns its host's strong state; this says nothing
    // about servicing a native loop or successfully completing shutdown.
    let retained = weak.upgrade().expect("owned host state remains retained");
    assert_eq!(retained.drops.get(), 0);
    assert_eq!(Rc::strong_count(&retained), 2);
}

#[test]
fn embedded_every_owner_entry_panic_permanently_refuses_calls_and_destruction() {
    for point in [
        OwnerEntry::Dispatch,
        OwnerEntry::Cursor,
        OwnerEntry::Pump,
        OwnerEntry::Events,
        OwnerEntry::RequestClose,
        OwnerEntry::CloseDisposition,
    ] {
        let (audit, handle, owner) = setup();
        audit.panic_at.set(Some(point));
        let pending = if point == OwnerEntry::Dispatch {
            Some(handle.exchange(frame()).unwrap())
        } else {
            None
        };
        let subscription = if matches!(point, OwnerEntry::Cursor | OwnerEntry::Events) {
            Some(handle.subscribe(None).unwrap())
        } else {
            None
        };
        if matches!(
            point,
            OwnerEntry::RequestClose | OwnerEntry::CloseDisposition
        ) {
            owner.signal_close();
        }
        assert!(matches!(
            progress(&owner),
            EmbeddedTurn::Retained {
                failure: Some(HostFailure::OwnerPanicked),
                ..
            }
        ));
        let calls = audit.threads.borrow().len();
        audit.panic_at.set(None);
        audit.released.set(true);
        for _ in 0..3 {
            assert!(matches!(
                owner.turn(|_| panic!("tainted owner cannot restart driver")),
                EmbeddedTurn::Retained {
                    failure: Some(HostFailure::OwnerPanicked),
                    ..
                }
            ));
        }
        assert_eq!(audit.threads.borrow().len(), calls);
        drop(pending);
        drop(subscription);
        drop(handle);
        drop(owner);
        assert_eq!(audit.drops.get(), 0);
    }
}

#[test]
fn embedded_owner_failure_precedes_a_later_driver_panic() {
    let (audit, _handle, owner) = setup();
    audit.pump_panic.set(true);
    assert!(matches!(
        owner.turn(|pump| {
            pump.tick();
            panic!("driver panics after the caught owner panic");
        }),
        EmbeddedTurn::Retained {
            failure: Some(HostFailure::OwnerPanicked),
            ..
        }
    ));
    let calls = audit.threads.borrow().len();
    assert!(matches!(
        progress(&owner),
        EmbeddedTurn::Retained {
            failure: Some(HostFailure::OwnerPanicked),
            ..
        }
    ));
    assert_eq!(audit.threads.borrow().len(), calls);
    drop(owner);
    assert_eq!(audit.drops.get(), 0);
}

#[test]
fn embedded_deferred_progress_requires_service_outside_the_owner_turn() {
    let (audit, _handle, owner) = setup();
    audit.requires_external_service.set(true);
    owner.signal_close();
    for services in 0..3 {
        assert_eq!(audit.external_services.get(), services);
        assert!(matches!(
            progress(&owner),
            EmbeddedTurn::Retained {
                closing: true,
                failure: None
            }
        ));
        assert_eq!(audit.pumps.get(), services + 1);
        assert_eq!(audit.external_services.get(), services);
        // Explicit simulated OS service occurs after the bounded call returns.
        // It is a portable falsification control, not real GUI responsiveness.
        audit.external_services.set(services + 1);
    }
    audit.released.set(true);
    assert!(matches!(progress(&owner), EmbeddedTurn::Closed(_)));
    assert_eq!(audit.drops.get(), 1);
}
