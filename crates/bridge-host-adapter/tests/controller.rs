//! Candidate-controlled portable transport integration only. No native caller,
//! publication, executor, timer, journal, store or application qualification.
mod controller_support;
use bridge_contracts::v1::*;
use bridge_engine::host::*;
use bridge_host_adapter::{controller::*, registration::*};
use controller_support::*;
use std::{
    cell::{Cell, RefCell},
    panic::{AssertUnwindSafe, catch_unwind},
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};

#[test]
fn exchange_admission_bounds_precede_enqueue() {
    let f = setup();
    assert_fault(
        f.controller
            .submit_exchange(vec![b' '; MAX_FRAME_BYTES + 1])
            .err()
            .unwrap(),
        FaultCode::DeliveryFailed,
        Delivery::NotSent,
    );
    assert_eq!(f.audit.dispatches.get(), 0);
    assert_eq!(f.controller.snapshot().unwrap().original_request_bytes, 0);
    let mut tickets = Vec::new();
    for _ in 0..MAX_EXCHANGES {
        tickets.push(f.controller.submit_exchange(padded_request()).unwrap());
        progress(&f.owner);
        assert_eq!(f.controller.service_exchanges().unwrap(), 1);
    }
    let snapshot = f.controller.snapshot().unwrap();
    assert_eq!(
        (snapshot.settled, snapshot.original_request_bytes),
        (32, MAX_REQUEST_BYTES)
    );
    assert_fault(
        f.controller.submit_exchange(request()).err().unwrap(),
        FaultCode::DeliveryFailed,
        Delivery::NotSent,
    );
    assert_eq!(f.audit.dispatches.get(), 32);
    // Settled reply custody remains reserved through build and commit. Both
    // callbacks can reenter controller observations without a held mutex.
    let built = response(&mut tickets[0])
        .build(|bytes| {
            assert!(!bytes.is_empty());
            assert_eq!(f.controller.snapshot().unwrap().building, 1);
            assert!(f.controller.submit_exchange(request()).is_err());
            Ok(bytes.to_vec())
        })
        .unwrap();
    assert_eq!(
        f.controller.snapshot().unwrap().original_request_bytes,
        MAX_REQUEST_BYTES
    );
    built
        .commit(|bytes| {
            assert!(!bytes.is_empty());
            assert!(f.controller.submit_exchange(request()).is_err());
            Ok(())
        })
        .unwrap();
    assert_eq!(
        f.controller.snapshot().unwrap().original_request_bytes,
        MAX_REQUEST_BYTES - MAX_FRAME_BYTES
    );
    let extra = f.controller.submit_exchange(request()).unwrap();
    drop(extra);
    progress(&f.owner);
    f.controller.service_exchanges().unwrap();
    drop(tickets);
    assert_eq!(f.controller.snapshot().unwrap().original_request_bytes, 0);
    finish(&f.owner, &f.audit);
}

#[test]
fn exchange_submission_failure_is_not_sent() {
    let f = setup();
    let mut direct = Vec::new();
    for _ in 0..MAX_QUEUED_REQUESTS {
        direct.push(
            f.handle
                .exchange(OwnedFrame::new(request()).unwrap())
                .unwrap(),
        );
    }
    assert_fault(
        f.controller.submit_exchange(request()).err().unwrap(),
        FaultCode::DeliveryFailed,
        Delivery::NotSent,
    );
    assert_eq!(f.controller.snapshot().unwrap().original_request_bytes, 0);
    for _ in 0..MAX_QUEUED_REQUESTS {
        progress(&f.owner);
    }
    for _ in MAX_QUEUED_REQUESTS..MAX_PENDING_REPLIES {
        direct.push(
            f.handle
                .exchange(OwnedFrame::new(request()).unwrap())
                .unwrap(),
        );
        progress(&f.owner);
    }
    assert_fault(
        f.controller.submit_exchange(request()).err().unwrap(),
        FaultCode::DeliveryFailed,
        Delivery::NotSent,
    );
    assert_eq!(f.controller.snapshot().unwrap().pending, 0);
    assert_eq!(f.audit.dispatches.get(), MAX_PENDING_REPLIES);
    drop(direct);
    finish(&f.owner, &f.audit);
    let (handle, inbox) = owner_channel();
    drop(inbox);
    let disconnected = TransportController::new(
        handle,
        TrustedEpoch::from_root(EPOCH).unwrap(),
        Arc::new(Clock(Mutex::new(Instant::now()))),
    );
    assert_fault(
        disconnected.submit_exchange(request()).err().unwrap(),
        FaultCode::Disconnected,
        Delivery::NotSent,
    );
    assert_eq!(disconnected.snapshot().unwrap().original_request_bytes, 0);
}

#[test]
fn exchange_pending_failure_is_uncertain_and_never_retried() {
    let f = setup();
    f.audit.fail_dispatch.set(true);
    let mut ticket = f.controller.submit_exchange(request()).unwrap();
    assert!(
        ticket.take_response().unwrap().is_none(),
        "pending is not a not-sent timeout"
    );
    assert_eq!(f.controller.service_exchanges().unwrap(), 0);
    assert_eq!(f.audit.dispatches.get(), 0);
    progress(&f.owner);
    assert_eq!(f.controller.service_exchanges().unwrap(), 1);
    assert_fault(
        ticket.take_response().err().unwrap(),
        FaultCode::DeliveryFailed,
        Delivery::MayHaveReachedBackend,
    );
    assert_eq!(f.controller.service_exchanges().unwrap(), 0);
    assert_eq!(f.audit.dispatches.get(), 1);
    finish(&f.owner, &f.audit);
    let (handle, inbox) = owner_channel();
    let controller = TransportController::new(
        handle,
        TrustedEpoch::from_root(EPOCH).unwrap(),
        Arc::new(Clock(Mutex::new(Instant::now()))),
    );
    let mut ticket = controller.submit_exchange(request()).unwrap();
    drop(inbox); // Actual admitted reply receiver disconnect, no invented retry.
    assert_eq!(controller.service_exchanges().unwrap(), 1);
    assert_fault(
        ticket.take_response().err().unwrap(),
        FaultCode::Disconnected,
        Delivery::MayHaveReachedBackend,
    );
    assert_eq!(controller.snapshot().unwrap().original_request_bytes, 0);
}

#[test]
fn abandoned_exchange_ticket_keeps_job_quota_until_settlement() {
    let f = setup();
    let ticket = f.controller.submit_exchange(padded_request()).unwrap();
    drop(ticket);
    assert_eq!(f.controller.snapshot().unwrap().pending, 1);
    assert_eq!(
        f.controller.snapshot().unwrap().original_request_bytes,
        MAX_FRAME_BYTES
    );
    assert_eq!(f.controller.service_exchanges().unwrap(), 0);
    assert_eq!(f.audit.closes.get(), 0);
    progress(&f.owner);
    assert_eq!(f.controller.service_exchanges().unwrap(), 1);
    assert_eq!(f.audit.dispatches.get(), 1);
    assert_eq!(f.audit.closes.get(), 0);
    assert_eq!(f.controller.snapshot().unwrap().original_request_bytes, 0);
    // A settled constructed response is explicitly abandoned by token drop;
    // errors/unwind in either callback release only that already settled job.
    for failure in 0..5 {
        let mut ticket = f.controller.submit_exchange(request()).unwrap();
        progress(&f.owner);
        f.controller.service_exchanges().unwrap();
        let work = response(&mut ticket);
        match failure {
            0 => {
                assert!(work.build::<()>(|_| Err(ResponseFailure)).is_err());
            }
            1 => {
                assert!(
                    catch_unwind(AssertUnwindSafe(
                        || work.build::<()>(|_| panic!("controlled builder failure"))
                    ))
                    .is_err()
                );
            }
            2 => {
                drop(work.build(|bytes| Ok(bytes.to_vec())).unwrap());
            }
            3 => {
                assert!(
                    work.build(|bytes| Ok(bytes.to_vec()))
                        .unwrap()
                        .commit::<()>(|_| Err(ResponseFailure))
                        .is_err()
                );
            }
            _ => {
                assert!(
                    catch_unwind(AssertUnwindSafe(|| work
                        .build(|bytes| Ok(bytes.to_vec()))
                        .unwrap()
                        .commit::<()>(|_| panic!("controlled commit failure"))))
                    .is_err()
                );
            }
        }
        assert_eq!(f.controller.snapshot().unwrap().original_request_bytes, 0);
    }
    assert_eq!(f.audit.dispatches.get(), 6);
    finish(&f.owner, &f.audit);
}

#[test]
fn reply_bytes_are_exact_and_correlation_is_not_rewritten() {
    let f = setup();
    for bytes in [request(), b"{malformed-wire-frame".to_vec()] {
        let original = decode_request(&bytes)
            .ok()
            .map(|request| request.as_inner().request_id.clone());
        let mut ticket = f.controller.submit_exchange(bytes).unwrap();
        progress(&f.owner);
        f.controller.service_exchanges().unwrap();
        let raw = response(&mut ticket)
            .build(|bytes| Ok(bytes.to_vec()))
            .unwrap()
            .commit(Ok)
            .unwrap();
        let decoded = decode_reply(&raw).unwrap().into_inner();
        assert_eq!(decoded.request_id.as_ref(), original.as_ref());
        assert_eq!(raw, serde_json::to_vec(&decoded).unwrap());
        if original.is_some() {
            let mut expected = decode_reply(fixture("reply")).unwrap().into_inner();
            expected.request_id = decoded.request_id.clone();
            assert_eq!(raw, serde_json::to_vec(&expected).unwrap());
        } else {
            assert!(matches!(decoded.body, ReplyBody::Rejected { .. }));
        }
    }
    assert_eq!(
        f.audit.dispatches.get(),
        1,
        "wire rejection remains engine behavior"
    );
    finish(&f.owner, &f.audit);
}

#[test]
fn readiness_work_keeps_deadline_and_waits_for_engine_ack() {
    let f = setup();
    let work = prepare(&f.controller, 1);
    let deadline = work.deadline();
    let close = waiting(f.controller.unsubscribe(&key(1)).unwrap());
    assert!(close.poll_closed().is_none());
    assert_eq!(f.controller.observation_snapshot().unwrap().closing, 1);
    drop(work); // Actual scheduling token disposal, not caller-task registration.
    assert!(close.poll_closed().is_some());
    drop(close);
    let delayed = prepare(&f.controller, 2);
    assert_eq!(delayed.deadline(), deadline);
    f.clock.advance(PREPARING_TIMEOUT);
    assert!(
        delayed.attach().is_err(),
        "task start cannot reset the admitted deadline"
    );
    assert_eq!(f.audit.dispatches.get(), 0);
    let mut wait = prepare(&f.controller, 3).attach().unwrap();
    assert!(matches!(wait.step().unwrap(), ReadinessStep::Pending));
    assert_eq!(f.controller.observation_snapshot().unwrap().preparing, 1);
    progress(&f.owner);
    let ReadinessStep::Ready(candidate) = wait.step().unwrap() else {
        panic!("actual engine ACK")
    };
    candidate.recheck().unwrap();
    assert_eq!(f.controller.observation_snapshot().unwrap().ready, 1);
    let ack: serde_json::Value = serde_json::to_value(candidate.ack()).unwrap();
    assert_eq!(ack["registrationKey"], key(3));
    assert_eq!(
        f.audit.dispatches.get(),
        0,
        "readiness is not an invented Snapshot exchange"
    );
    finish(&f.owner, &f.audit);
}

#[test]
fn readiness_candidate_recheck_refuses_retired_key() {
    for retirement in 0..3 {
        let f = setup();
        let candidate = ready(&f, 1);
        candidate.recheck().unwrap();
        match retirement {
            0 => {
                f.clock.advance(IDLE_TIMEOUT);
                assert_eq!(f.controller.expire().unwrap(), 1);
            }
            1 => {
                assert!(matches!(
                    f.controller.unsubscribe(&key(1)).unwrap(),
                    CloseStart::Closed(_)
                ));
            }
            _ => {
                f.controller.revoke_document().unwrap();
            }
        }
        assert_eq!(
            candidate.recheck().err().unwrap().delivery(),
            Delivery::MayHaveReachedBackend
        );
        assert!(f.controller.begin_poll(&key(1)).is_err());
        finish(&f.owner, &f.audit);
    }
}

#[test]
fn poll_response_builder_keeps_single_lease_until_return() {
    for retire_during_build in [false, true] {
        let f = setup();
        ready(&f, 1);
        append(&f.audit, 1);
        progress(&f.owner);
        let cleanup = RefCell::new(None);
        let built = f
            .controller
            .begin_poll(&key(1))
            .unwrap()
            .build(|encoded| {
                assert!(f.controller.observation_snapshot().unwrap().poll_reserved);
                assert!(f.controller.begin_poll(&key(1)).is_err());
                let dto: serde_json::Value = serde_json::from_str(encoded).unwrap();
                assert_eq!(dto["frames"].as_array().unwrap().len(), 1);
                assert!(decode_event(dto["frames"][0].as_str().unwrap().as_bytes()).is_ok());
                if retire_during_build {
                    let wait = waiting(f.controller.unsubscribe(&key(1)).unwrap());
                    assert!(wait.poll_closed().is_none());
                    cleanup.replace(Some(wait));
                }
                Ok(encoded.as_bytes().to_vec()) // Actual constructed raw test response.
            })
            .unwrap();
        assert!(f.controller.observation_snapshot().unwrap().poll_reserved);
        let committed = Cell::new(false);
        let result = built.commit(|raw| {
            committed.set(true);
            assert!(!raw.is_empty());
            assert!(f.controller.observation_snapshot().unwrap().poll_reserved);
            assert!(f.controller.begin_poll(&key(1)).is_err());
            Ok(())
        });
        if retire_during_build {
            assert!(result.is_err());
            assert!(!committed.get());
            assert!(cleanup.borrow().as_ref().unwrap().poll_closed().is_some());
        } else {
            result.unwrap();
            assert!(committed.get());
            assert!(!f.controller.observation_snapshot().unwrap().poll_reserved);
            f.controller
                .begin_poll(&key(1))
                .unwrap()
                .build(|encoded| Ok(encoded.to_owned()))
                .unwrap()
                .commit(Ok)
                .unwrap();
        }
        finish(&f.owner, &f.audit);
    }
}

#[test]
fn poll_builder_error_or_unwind_retires_stream() {
    for failure in 0..6 {
        let f = setup();
        ready(&f, 1);
        append(&f.audit, 2);
        progress(&f.owner);
        let poll = f.controller.begin_poll(&key(1)).unwrap();
        match failure {
            0 => {
                assert!(poll.build::<()>(|_| Err(ResponseFailure)).is_err());
            }
            1 => {
                assert!(
                    catch_unwind(AssertUnwindSafe(
                        || poll.build::<()>(|_| panic!("controlled raw builder unwind"))
                    ))
                    .is_err()
                );
            }
            2 => {
                drop(poll.build(|encoded| Ok(encoded.to_owned())).unwrap());
            }
            3 => {
                assert!(
                    poll.build(|encoded| Ok(encoded.to_owned()))
                        .unwrap()
                        .commit::<()>(|_| Err(ResponseFailure))
                        .is_err()
                );
            }
            4 => {
                assert!(
                    catch_unwind(AssertUnwindSafe(|| poll
                        .build(|encoded| Ok(encoded.to_owned()))
                        .unwrap()
                        .commit::<()>(|_| panic!("controlled raw commit unwind"))))
                    .is_err()
                );
            }
            _ => {
                let built = poll.build(|encoded| Ok(encoded.to_owned())).unwrap();
                f.clock.advance(IDLE_TIMEOUT);
                assert_eq!(f.controller.expire().unwrap(), 1);
                let invoked = Cell::new(false);
                assert!(
                    built
                        .commit(|_| {
                            invoked.set(true);
                            Ok(())
                        })
                        .is_err()
                );
                assert!(
                    !invoked.get(),
                    "fresh final eligibility must refuse before callback"
                );
            }
        }
        assert!(
            f.controller.begin_poll(&key(1)).is_err(),
            "a popped event loss cannot hide the stream gap"
        );
        let snapshot = f.controller.observation_snapshot().unwrap();
        assert!(!snapshot.poll_reserved);
        assert_eq!((snapshot.ready, snapshot.closing), (0, 0));
        assert!(matches!(
            f.controller.unsubscribe(&key(1)).unwrap(),
            CloseStart::Closed(_)
        ));
        finish(&f.owner, &f.audit);
    }
}

#[test]
fn document_epoch_is_immutable_and_shutdown_preserves_pending_work() {
    let f = setup();
    let candidate = ready(&f, 1);
    let work = prepare(&f.controller, 2);
    let mut ticket = f.controller.submit_exchange(request()).unwrap();
    f.controller.stop_submission().unwrap();
    assert_fault(
        f.controller.submit_exchange(request()).err().unwrap(),
        FaultCode::UnavailableBinding,
        Delivery::NotSent,
    );
    assert!(f.controller.begin_subscribe(&key(3)).is_err());
    assert!(ticket.take_response().unwrap().is_none());
    let mut wait = work.attach().unwrap(); // Admitted readiness custody survives stop.
    progress(&f.owner);
    f.controller.service_exchanges().unwrap();
    f.owner.signal_close();
    assert!(matches!(
        progress(&f.owner),
        EmbeddedTurn::Retained { closing: true, .. }
    ));
    candidate.recheck().unwrap();
    let ReadinessStep::Ready(second) = wait.step().unwrap() else {
        panic!("queued real readiness ACK")
    };
    second.recheck().unwrap();
    response(&mut ticket)
        .build(|bytes| Ok(bytes.to_vec()))
        .unwrap()
        .commit(Ok)
        .unwrap();
    f.controller
        .begin_poll(&key(1))
        .unwrap()
        .build(|encoded| Ok(encoded.to_owned()))
        .unwrap()
        .commit(Ok)
        .unwrap();
    f.controller.revoke_document().unwrap();
    assert!(candidate.recheck().is_err());
    assert!(f.controller.begin_subscribe(&key(4)).is_err());
    let other = TransportController::new(
        f.handle.clone(),
        TrustedEpoch::from_root("22222222-2222-4222-8222-222222222222").unwrap(),
        f.clock.clone(),
    );
    assert_fault(
        other.begin_subscribe(&key(1)).err().unwrap(),
        FaultCode::UnavailableBinding,
        Delivery::NotSent,
    );
    finish(&f.owner, &f.audit);
    // Losing controller observation is not owner closure. Root's independent
    // lifecycle handle and original-thread shell still retain admitted work.
    let lost = setup();
    let ticket = lost.controller.submit_exchange(request()).unwrap();
    drop(ticket);
    drop(lost.controller);
    assert_eq!(lost.audit.drops.get(), 0);
    assert_eq!(lost.audit.closes.get(), 0);
    progress(&lost.owner);
    assert_eq!(lost.audit.dispatches.get(), 1);
    assert_eq!(lost.audit.closes.get(), 0);
    finish(&lost.owner, &lost.audit);
}

#[test]
fn controlled_publication_boundary_serializes_retirement_and_publish() {
    let f = setup();
    let candidate = ready(&f, 1);
    let boundary = Arc::new(Mutex::new(()));
    let published = Cell::new(false);
    let guard = boundary.lock().unwrap();
    std::thread::scope(|scope| {
        let controller = f.controller.clone();
        let retiring_boundary = boundary.clone();
        let (entered, observed) = std::sync::mpsc::sync_channel(1);
        let worker = scope.spawn(move || {
            entered.send(()).unwrap();
            let _admission = retiring_boundary.lock().unwrap();
            controller.revoke_document().unwrap();
        });
        observed.recv_timeout(Duration::from_secs(2)).unwrap();
        // This explicit test gate represents fake caller/document authority.
        // The point-in-time recheck alone provides no native publication claim.
        candidate.recheck().unwrap();
        published.set(true);
        drop(guard);
        worker.join().unwrap();
    });
    assert!(published.get());
    assert!(candidate.recheck().is_err());
    finish(&f.owner, &f.audit);
}
