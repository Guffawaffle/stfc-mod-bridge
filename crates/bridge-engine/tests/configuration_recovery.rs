mod configuration_support;
use bridge_contracts::v1::*;
use bridge_engine::configuration::*;
use configuration_support::*;

#[test]
fn ambiguous_replacement_stays_recovery_required_with_exact_custody() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    let lease = w.acquire_configuration(&p).unwrap();
    let recovery = w
        .configuration_recovery_binding(&OperationId::new(id(501)).unwrap(), &p, &lease)
        .unwrap();
    s.borrow_mut().ambiguous = true;
    let mut tx = w
        .begin_configuration(&mut Some(p), recovery, &lease)
        .unwrap();
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    assert!(matches!(
        w.advance_configuration(&mut tx, &lease, false),
        Err(ConfigurationFailure::RecoveryRequired)
    ));
    assert_eq!(s.borrow().drops, 0);
    assert_eq!(w.draft(&staged.snapshot.draft).unwrap(), staged.snapshot);
}
#[test]
fn restart_recovery_uses_captured_digest_without_protected_payload_recreation() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    let capture = PreparedCapture::SaveConfiguration {
        input: p.save_capture().unwrap(),
    };
    let scope = RecoveryConfiguration::from_capture(&capture).unwrap();
    let lease = w.acquire_configuration(&p).unwrap();
    let recovery = w
        .configuration_recovery_binding(&OperationId::new(id(501)).unwrap(), &p, &lease)
        .unwrap();
    let mut tx = w
        .begin_configuration(&mut Some(p), recovery.clone(), &lease)
        .unwrap();
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    // Dropping candidate/draft host custody does not recreate secrets. Recovery
    // consumes durable identity and exact owner observations, with no rewrite.
    drop(tx);
    let effects = s.borrow().effects;
    let recovery_lease = w.acquire_configuration_recovery(&scope).unwrap();
    assert!(matches!(
        w.recover_configuration(&scope, &recovery, &recovery_lease)
            .unwrap(),
        Some(CompletionOutcome::Changed { .. })
    ));
    assert_eq!(s.borrow().effects, effects);
}
#[test]
fn foreign_destination_or_recovery_target_is_not_overwritten() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    let scope = RecoveryConfiguration::from_capture(&PreparedCapture::SaveConfiguration {
        input: p.save_capture().unwrap(),
    })
    .unwrap();
    let lease = w.acquire_configuration(&p).unwrap();
    let mut recovery = w
        .configuration_recovery_binding(&OperationId::new(id(501)).unwrap(), &p, &lease)
        .unwrap();
    s.borrow_mut().read.binding.baseline = DocumentBaseline::Existing {
        file_identity: NativeFileIdentity::new("foreign").unwrap(),
        content_digest: hash(b"foreign"),
    };
    s.borrow_mut().read.bytes = b"foreign".to_vec();
    assert!(matches!(
        w.recover_configuration(&scope, &recovery, &lease),
        Err(ConfigurationFailure::RecoveryRequired)
    ));
    recovery.target = RecoveryTarget::Configuration {
        document: {
            let mut d = scope.baseline.clone();
            d.document_id = DocumentId::new(id(999)).unwrap();
            d
        },
    };
    assert!(matches!(
        w.recover_configuration(&scope, &recovery, &lease),
        Err(ConfigurationFailure::InvalidOwnerResult)
    ));
    assert_eq!(s.borrow().effects, 0);
    assert_eq!(s.borrow().read.bytes, b"foreign");
}
#[test]
fn unstarted_recovery_proves_no_effect_and_invalidates_previous_host_drafts() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let p = prepare(&mut w, &d);
    let scope = RecoveryConfiguration::from_capture(&PreparedCapture::SaveConfiguration {
        input: p.save_capture().unwrap(),
    })
    .unwrap();
    let lease = w.acquire_configuration(&p).unwrap();
    let recovery = w
        .configuration_recovery_binding(&OperationId::new(id(501)).unwrap(), &p, &lease)
        .unwrap();
    assert!(matches!(
        w.recover_configuration(&scope, &recovery, &lease).unwrap(),
        Some(CompletionOutcome::CancelledBeforeCommit { .. })
    ));
    let (new_host, _, _) = workspace("", vec![], None, false, false);
    assert!(matches!(
        new_host.draft(&d.draft),
        Err(ConfigurationFailure::Stale)
    ));
    assert_eq!(s.borrow().effects, 0);
}
