mod configuration_support;
use bridge_contracts::v1::*;
use bridge_engine::configuration::*;
use configuration_support::*;

#[test]
fn missing_open_stage_discard_do_not_mutate_document() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    assert_eq!(staged.snapshot.state, DraftState::Dirty);
    w.discard_draft(&DiscardDraftInput {
        draft: staged.snapshot.draft.clone(),
    })
    .unwrap();
    assert!(matches!(
        w.draft(&staged.snapshot.draft),
        Err(ConfigurationFailure::Stale)
    ));
    assert_eq!(s.borrow().effects, 0);
    assert!(s.borrow().read.bytes.is_empty());
}
#[test]
fn exact_lost_ack_retry_returns_one_successor_changed_retry_refuses() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let first = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let replay = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    assert_eq!(first, replay);
    assert_eq!(first.snapshot.draft.revision.get(), 1);
    assert!(matches!(
        stage(&mut w, &d.draft, vec![boolean(false)]),
        Err(ConfigurationFailure::Stale)
    ));
    let same = stage(&mut w, &first.snapshot.draft, vec![boolean(true)]).unwrap();
    assert_eq!(same.snapshot.draft.revision.get(), 2);
    assert_eq!(
        stage(&mut w, &first.snapshot.draft, vec![boolean(true)]).unwrap(),
        same
    );
    let next = stage(&mut w, &same.snapshot.draft, vec![boolean(false)]).unwrap();
    assert_eq!(next.snapshot.draft.revision.get(), 3);
    assert!(matches!(
        stage(&mut w, &d.draft, vec![boolean(true)]),
        Err(ConfigurationFailure::Stale)
    ));
}
#[test]
fn protected_capture_stage_transfer_prepare_continuity_and_forgery_refusal() {
    let entry = ProtectedEntryOutcome::Captured(
        ProtectedValue::new(b"\"synthetic-token\"".to_vec()).unwrap(),
    );
    let (mut w, s, _) = workspace("", vec![], Some(entry), false, false);
    let d = open(&mut w, &s);
    let result = w
        .request_sensitive_input(&RequestSensitiveInputInput {
            draft: d.draft.clone(),
            field_id: field("sync.token"),
            sensitivity: SensitiveInputKind::Secret,
        })
        .unwrap();
    let SensitiveInputOutcome::CapturedSecret { reference } = result.outcome else {
        panic!("capture")
    };
    let edits = vec![ConfigurationEdit::ReplaceSecret {
        field_id: field("sync.token"),
        reference: reference.clone(),
    }];
    let first = stage(&mut w, &d.draft, edits.clone()).unwrap();
    assert_eq!(first.protected_transfers.as_slice().len(), 1);
    let ConfigurationEdit::ReplaceSecret {
        reference: successor,
        ..
    } = &first.snapshot.edits.as_slice()[0]
    else {
        panic!("edit")
    };
    assert_eq!(successor.draft, first.snapshot.draft);
    assert_ne!(successor.secret_id, reference.secret_id);
    assert_eq!(stage(&mut w, &d.draft, edits).unwrap(), first);
    let same = stage(
        &mut w,
        &first.snapshot.draft,
        first.snapshot.edits.as_slice().to_vec(),
    )
    .unwrap();
    assert_eq!(
        same.snapshot.draft.revision.get(),
        first.snapshot.draft.revision.get() + 1
    );
    assert_eq!(same.protected_transfers.as_slice().len(), 1);
    assert_eq!(
        stage(
            &mut w,
            &first.snapshot.draft,
            first.snapshot.edits.as_slice().to_vec()
        )
        .unwrap(),
        same
    );
    let candidate = prepare(&mut w, &same.snapshot);
    assert!(
        std::str::from_utf8(candidate.candidate_bytes().unwrap())
            .unwrap()
            .contains("synthetic-token")
    );
    let ConfigurationEdit::ReplaceSecret {
        reference: successor,
        ..
    } = &same.snapshot.edits.as_slice()[0]
    else {
        panic!("secret")
    };
    assert_eq!(successor.draft, same.snapshot.draft);
    let mut forged = (**successor).clone();
    forged.secret_id = SecretRefId::new(id(777)).unwrap();
    assert!(matches!(
        stage(
            &mut w,
            &same.snapshot.draft,
            vec![ConfigurationEdit::ReplaceSecret {
                field_id: field("sync.token"),
                reference: Box::new(forged)
            }]
        ),
        Err(ConfigurationFailure::ProtectedRefInvalid)
    ));
    assert_eq!(s.borrow().effects, 0);
}
#[test]
fn acknowledgment_overflow_or_adapter_failure_preserves_draft_and_capture() {
    let entry =
        ProtectedEntryOutcome::Captured(ProtectedValue::new(b"\"secret\"".to_vec()).unwrap());
    let (mut w, s, _) = workspace("", vec![], Some(entry), false, false);
    let d = open(&mut w, &s);
    let result = w
        .request_sensitive_input(&RequestSensitiveInputInput {
            draft: d.draft.clone(),
            field_id: field("sync.token"),
            sensitivity: SensitiveInputKind::Secret,
        })
        .unwrap();
    let SensitiveInputOutcome::CapturedSecret { reference } = result.outcome else {
        panic!("capture")
    };
    let input = SetDraftChangesInput {
        draft: d.draft.clone(),
        edits: list(vec![ConfigurationEdit::ReplaceSecret {
            field_id: field("sync.token"),
            reference,
        }]),
    };
    assert!(matches!(
        w.set_draft_changes(input.clone(), |_| Err(ConfigurationFailure::Capacity)),
        Err(ConfigurationFailure::Capacity)
    ));
    assert_eq!(w.draft(&d.draft).unwrap(), d);
    assert!(stage(&mut w, &d.draft, input.edits.as_slice().to_vec()).is_ok());
}
#[test]
fn invalid_public_edits_remain_and_sensitive_plaintext_is_hard_refusal() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let invalid = stage(
        &mut w,
        &d.draft,
        vec![ConfigurationEdit::SetPublic {
            field_id: field("setting.integer"),
            value: PublicConfigValue::Boolean(true),
        }],
    )
    .unwrap();
    assert_eq!(invalid.snapshot.state, DraftState::Invalid);
    assert!(matches!(
        w.prepare_save(&SaveConfigurationInput {
            draft: invalid.snapshot.draft.clone()
        }),
        Err(ConfigurationFailure::InvalidDocument)
    ));
    assert!(matches!(
        stage(
            &mut w,
            &invalid.snapshot.draft,
            vec![ConfigurationEdit::SetPublic {
                field_id: field("sync.token"),
                value: PublicConfigValue::String(ConfigString::new("plaintext").unwrap())
            }]
        ),
        Err(ConfigurationFailure::ProtectedRefInvalid)
    ));
    assert_eq!(w.draft(&invalid.snapshot.draft).unwrap(), invalid.snapshot);
}
#[test]
fn cancelled_or_unavailable_entry_creates_no_reference_and_wrong_host_refuses() {
    for entry in [
        ProtectedEntryOutcome::Cancelled,
        ProtectedEntryOutcome::Unavailable(
            SensitiveInputUnavailableReason::ProtectedEntryUnavailable,
        ),
    ] {
        let (mut w, s, _) = workspace("", vec![], Some(entry), false, false);
        let d = open(&mut w, &s);
        let result = w
            .request_sensitive_input(&RequestSensitiveInputInput {
                draft: d.draft.clone(),
                field_id: field("sync.endpoint"),
                sensitivity: SensitiveInputKind::Private,
            })
            .unwrap();
        assert!(matches!(
            result.outcome,
            SensitiveInputOutcome::Cancelled | SensitiveInputOutcome::Unavailable { .. }
        ));
        assert_eq!(w.draft(&d.draft).unwrap(), d);
        let mut old = d.draft;
        old.host_epoch = HostEpoch::new(id(43)).unwrap();
        assert!(matches!(
            w.draft(&old),
            Err(ConfigurationFailure::HostMismatch)
        ));
    }
}
#[test]
fn duplicate_edit_targets_and_foreign_draft_capture_refuse_atomically() {
    let entry =
        ProtectedEntryOutcome::Captured(ProtectedValue::new(b"\"endpoint\"".to_vec()).unwrap());
    let (mut w, s, _) = workspace("", vec![], Some(entry), false, false);
    let d = open(&mut w, &s);
    let other = open(&mut w, &s);
    assert!(matches!(
        stage(&mut w, &d.draft, vec![boolean(true), boolean(false)]),
        Err(ConfigurationFailure::InvalidInput)
    ));
    let result = w
        .request_sensitive_input(&RequestSensitiveInputInput {
            draft: d.draft.clone(),
            field_id: field("sync.endpoint"),
            sensitivity: SensitiveInputKind::Private,
        })
        .unwrap();
    let SensitiveInputOutcome::CapturedPrivate { reference } = result.outcome else {
        panic!("capture")
    };
    assert!(matches!(
        stage(
            &mut w,
            &other.draft,
            vec![ConfigurationEdit::SetPrivate {
                field_id: field("sync.endpoint"),
                reference
            }]
        ),
        Err(ConfigurationFailure::ProtectedRefInvalid | ConfigurationFailure::InvalidInput)
    ));
    assert_eq!(w.draft(&d.draft).unwrap(), d);
    assert_eq!(w.draft(&other.draft).unwrap(), other);
}

#[test]
fn repeated_protected_use_gets_one_closed_transfer_for_all_occurrences() {
    let entry =
        ProtectedEntryOutcome::Captured(ProtectedValue::new(b"\"proxy-value\"".to_vec()).unwrap());
    let (mut w, s, _) = workspace("", vec![], Some(entry), false, false);
    let d = open(&mut w, &s);
    let captured = w
        .request_sensitive_input(&RequestSensitiveInputInput {
            draft: d.draft.clone(),
            field_id: field("sync.endpoint"),
            sensitivity: SensitiveInputKind::Private,
        })
        .unwrap();
    let SensitiveInputOutcome::CapturedPrivate { reference } = captured.outcome else {
        panic!("capture")
    };
    let first = stage(
        &mut w,
        &d.draft,
        vec![
            ConfigurationEdit::SetPrivate {
                field_id: field("sync.endpoint"),
                reference: reference.clone(),
            },
            ConfigurationEdit::SetSyncProxy {
                destination_id: DestinationId::new("synthetic-existing").unwrap(),
                value: ProxyChoice::Custom { reference },
            },
        ],
    )
    .unwrap();
    assert_eq!(first.protected_transfers.as_slice().len(), 1);
    let ConfigurationEdit::SetPrivate { reference: a, .. } = &first.snapshot.edits.as_slice()[0]
    else {
        panic!("private")
    };
    let ConfigurationEdit::SetSyncProxy {
        value: ProxyChoice::Custom { reference: b },
        ..
    } = &first.snapshot.edits.as_slice()[1]
    else {
        panic!("proxy")
    };
    assert_eq!(a, b);
    assert_eq!(a.captured_for.as_deref(), Some(&first.snapshot.draft));
    let same = stage(
        &mut w,
        &first.snapshot.draft,
        first.snapshot.edits.as_slice().to_vec(),
    )
    .unwrap();
    assert_eq!(
        same.snapshot.draft.revision.get(),
        first.snapshot.draft.revision.get() + 1
    );
    assert_eq!(same.protected_transfers.as_slice().len(), 1);
    let ConfigurationEdit::SetPrivate {
        reference: next_a, ..
    } = &same.snapshot.edits.as_slice()[0]
    else {
        panic!("private")
    };
    let ConfigurationEdit::SetSyncProxy {
        value: ProxyChoice::Custom { reference: next_b },
        ..
    } = &same.snapshot.edits.as_slice()[1]
    else {
        panic!("proxy")
    };
    assert_eq!(next_a, next_b);
    assert_ne!(next_a.value_id, a.value_id);
    assert_eq!(next_a.captured_for.as_deref(), Some(&same.snapshot.draft));
    assert_eq!(
        stage(
            &mut w,
            &first.snapshot.draft,
            first.snapshot.edits.as_slice().to_vec()
        )
        .unwrap(),
        same
    );
}

#[test]
fn repeated_sync_observation_preserves_distinct_saved_subjects_and_payloads() {
    use std::{cell::RefCell, rc::Rc};
    let sync = Rc::new(RefCell::new(vec![
        SyncFixture {
            id: "one".into(),
            endpoint: "\"endpoint-one\"".into(),
            proxy: "\"proxy-one\"".into(),
        },
        SyncFixture {
            id: "two".into(),
            endpoint: "\"endpoint-two\"".into(),
            proxy: "\"proxy-two\"".into(),
        },
    ]));
    let (mut w, s, _) = workspace_with_sync("", vec![], None, false, false, sync.clone());
    let selector = TargetSelector {
        installation: InstallationSelector::Registered {
            id: InstallationId::new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").unwrap(),
            directory_assertion: None,
            revision_assertion: None,
        },
        profile: ProfileSelector::Ordinary {
            catalog_id_assertion: None,
        },
    };
    let first = w.read_configuration(&selector).unwrap();
    let second = w.read_configuration(&selector).unwrap();
    assert_eq!(first, second);
    let mut references = vec![];
    for destination in first.sync.as_slice() {
        references.push((
            destination.endpoint.clone(),
            format!("endpoint-{}", destination.id.as_str()),
        ));
        let ProxyChoice::Custom { reference } = &destination.desired_proxy else {
            panic!("proxy")
        };
        references.push((
            (**reference).clone(),
            format!("proxy-{}", destination.id.as_str()),
        ));
    }
    assert_eq!(
        references
            .iter()
            .map(|(reference, _)| &reference.value_id)
            .collect::<std::collections::BTreeSet<_>>()
            .len(),
        4
    );
    // Even an inconsistent repeated producer binding must not retarget an old
    // handle to new bytes. The current projection gets a distinct handle.
    sync.borrow_mut()[0].endpoint = "\"replacement-one\"".into();
    let replacement = w.read_configuration(&selector).unwrap();
    assert_ne!(
        replacement.sync.as_slice()[0].endpoint.value_id,
        first.sync.as_slice()[0].endpoint.value_id
    );
    references.push((
        replacement.sync.as_slice()[0].endpoint.clone(),
        "replacement-one".into(),
    ));
    for (reference, expected) in references {
        let draft = open(&mut w, &s);
        let staged = stage(
            &mut w,
            &draft.draft,
            vec![ConfigurationEdit::SetPrivate {
                field_id: reference.field_id.clone(),
                reference: Box::new(reference),
            }],
        )
        .unwrap();
        let candidate = prepare(&mut w, &staged.snapshot);
        assert!(
            std::str::from_utf8(candidate.candidate_bytes().unwrap())
                .unwrap()
                .contains(&format!("\"{expected}\""))
        );
        w.discard_draft(&DiscardDraftInput {
            draft: staged.snapshot.draft,
        })
        .unwrap();
    }
    assert_eq!(s.borrow().effects, 0);
}

#[test]
fn read_cannot_substitute_ordinary_target_for_requested_isolated_profile() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let selector = TargetSelector {
        installation: InstallationSelector::Registered {
            id: InstallationId::new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").unwrap(),
            directory_assertion: None,
            revision_assertion: None,
        },
        profile: ProfileSelector::Isolated {
            id: ProfileId::new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb").unwrap(),
            revision_assertion: None,
        },
    };
    assert!(matches!(
        w.read_configuration(&selector),
        Err(ConfigurationFailure::InvalidOwnerResult)
    ));
    assert_eq!(s.borrow().effects, 0);
}
