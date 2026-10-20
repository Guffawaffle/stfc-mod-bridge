mod configuration_support;
use bridge_contracts::v1::*;
use bridge_engine::configuration::*;
use configuration_support::*;

#[test]
fn current_generation_lookup_is_immutable_and_preflight_refusals_keep_local_custody() {
    let ids = std::rc::Rc::new(std::cell::RefCell::new(IdentityControl::default()));
    let entry = ProtectedEntryOutcome::Captured(
        ProtectedValue::new(b"\"local-only-secret\"".to_vec()).unwrap(),
    );
    let (mut workspace, state, codec_calls) = workspace_with_options(
        "",
        vec![],
        Some(entry),
        false,
        false,
        FixtureOptions {
            ids: ids.clone(),
            ..FixtureOptions::default()
        },
    );
    let host = workspace.host_epoch().clone();
    let missing = DraftId::new(id(9000)).unwrap();
    assert_eq!(workspace.current_draft(&host, &missing).unwrap(), None);
    assert_eq!(
        workspace.current_draft(&HostEpoch::new(id(999)).unwrap(), &missing),
        Err(ConfigurationFailure::HostMismatch)
    );
    assert!(ids.borrow().calls.is_empty());
    assert!(codec_calls.borrow().is_empty());
    assert_eq!(
        state.borrow().reads.get() + state.borrow().resolves.get(),
        0
    );
    let document = state.borrow().read.binding.clone();
    let refused_id = std::cell::RefCell::new(None);
    assert_eq!(
        workspace.open_draft_with_preflight(
            &OpenDraftInput {
                document: document.clone()
            },
            |snapshot| {
                *refused_id.borrow_mut() = Some(snapshot.draft.draft_id.clone());
                Err(ConfigurationFailure::Capacity)
            }
        ),
        Err(ConfigurationFailure::Capacity)
    );
    assert_eq!(
        workspace
            .current_draft(&host, &refused_id.into_inner().unwrap())
            .unwrap(),
        None
    );
    let draft = workspace.open_draft(&OpenDraftInput { document }).unwrap();
    let captured = std::cell::RefCell::new(None);
    assert_eq!(
        workspace.request_sensitive_input_with_preflight(
            &RequestSensitiveInputInput {
                draft: draft.draft.clone(),
                field_id: field("sync.token"),
                sensitivity: SensitiveInputKind::Secret,
            },
            |receipt| {
                let SensitiveInputOutcome::CapturedSecret { reference } = &receipt.outcome else {
                    panic!("capture")
                };
                *captured.borrow_mut() = Some(reference.clone());
                Err(ConfigurationFailure::Capacity)
            }
        ),
        Err(ConfigurationFailure::Capacity)
    );
    assert_eq!(
        stage(
            &mut workspace,
            &draft.draft,
            vec![ConfigurationEdit::ReplaceSecret {
                field_id: field("sync.token"),
                reference: captured.into_inner().unwrap(),
            }]
        ),
        Err(ConfigurationFailure::ProtectedRefInvalid),
        "failed capture did not publish a vault entry"
    );
    assert_eq!(
        workspace.discard_draft_with_preflight(
            &DiscardDraftInput {
                draft: draft.draft.clone()
            },
            |_| Err(ConfigurationFailure::Capacity)
        ),
        Err(ConfigurationFailure::Capacity)
    );
    let before = (
        state.borrow().reads.get(),
        state.borrow().resolves.get(),
        ids.borrow().calls.len(),
        codec_calls.borrow().len(),
    );
    assert_eq!(
        workspace
            .current_draft(&host, &draft.draft.draft_id)
            .unwrap(),
        Some(draft)
    );
    assert_eq!(
        before,
        (
            state.borrow().reads.get(),
            state.borrow().resolves.get(),
            ids.borrow().calls.len(),
            codec_calls.borrow().len()
        )
    );
}

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

type AllocationFixture = (
    Workspace,
    std::rc::Rc<std::cell::RefCell<State>>,
    std::rc::Rc<std::cell::RefCell<IdentityControl>>,
);

fn allocation_fixture(
    entry: Option<ProtectedEntryOutcome>,
    captures: Vec<ProtectedEntryOutcome>,
) -> AllocationFixture {
    let ids = std::rc::Rc::new(std::cell::RefCell::new(IdentityControl::default()));
    let (workspace, state, _) = workspace_with_options(
        "",
        vec![],
        entry,
        false,
        false,
        FixtureOptions {
            ids: ids.clone(),
            captures,
            ..FixtureOptions::default()
        },
    );
    (workspace, state, ids)
}

fn assert_allocation_keeps_persistence(
    state: &std::rc::Rc<std::cell::RefCell<State>>,
    binding: &DocumentBinding,
) {
    let state = state.borrow();
    assert_eq!(&state.read.binding, binding);
    assert!(state.read.bytes.is_empty());
    assert_eq!((state.effects, state.steps, state.drops), (0, 0, 0));
    assert!(state.backups.is_empty());
    assert!(state.backup_bytes.is_empty());
}

fn allocation_request(draft: &DraftRef, kind: SensitiveInputKind) -> RequestSensitiveInputInput {
    RequestSensitiveInputInput {
        draft: draft.clone(),
        field_id: field(match kind {
            SensitiveInputKind::Private => "sync.endpoint",
            SensitiveInputKind::Secret => "sync.token",
        }),
        sensitivity: kind,
    }
}

fn allocation_edit(outcome: SensitiveInputOutcome) -> ConfigurationEdit {
    match outcome {
        SensitiveInputOutcome::CapturedPrivate { reference } => ConfigurationEdit::SetPrivate {
            field_id: reference.field_id.clone(),
            reference,
        },
        SensitiveInputOutcome::CapturedSecret { reference } => ConfigurationEdit::ReplaceSecret {
            field_id: reference.field_id.clone(),
            reference,
        },
        _ => panic!("controlled protected capture"),
    }
}

#[test]
fn draft_identity_allocation_failure_publishes_no_draft_and_does_not_retry() {
    let (mut workspace, state, ids) = allocation_fixture(None, vec![]);
    let binding = state.borrow().read.binding.clone();
    ids.borrow_mut().outcomes.push_back((
        IdentityKind::Draft,
        Err(ConfigurationFailure::NativeUnavailable),
    ));
    assert_eq!(
        workspace.open_draft(&OpenDraftInput {
            document: binding.clone()
        }),
        Err(ConfigurationFailure::NativeUnavailable),
    );
    assert_eq!(ids.borrow().calls, vec![IdentityKind::Draft]);
    let unpublished = DraftRef {
        draft_id: DraftId::new(id(20001)).unwrap(),
        host_epoch: workspace.host_epoch().clone(),
        revision: RevisionCounter::new(0),
        document: binding.clone(),
    };
    assert_eq!(
        workspace.draft(&unpublished),
        Err(ConfigurationFailure::Stale)
    );
    ids.borrow_mut()
        .outcomes
        .push_back((IdentityKind::Draft, Ok(70001)));
    let opened = open(&mut workspace, &state);
    assert_eq!(opened.draft.draft_id, DraftId::new(id(70001)).unwrap());
    assert_eq!(
        ids.borrow().calls,
        vec![IdentityKind::Draft, IdentityKind::Draft]
    );
    assert_allocation_keeps_persistence(&state, &binding);
}

#[test]
fn saved_private_allocation_failure_publishes_no_partial_vault_and_burns_issued_ids() {
    use std::{cell::RefCell, rc::Rc};
    let ids = Rc::new(RefCell::new(IdentityControl::default()));
    ids.borrow_mut().outcomes.extend([
        (IdentityKind::Private, Ok(71001)),
        (
            IdentityKind::Private,
            Err(ConfigurationFailure::NativeUnavailable),
        ),
    ]);
    let sync = Rc::new(RefCell::new(vec![SyncFixture {
        id: "allocation-subject".into(),
        endpoint: "\"saved-endpoint\"".into(),
        proxy: "\"saved-proxy\"".into(),
    }]));
    let (mut workspace, state, _) = workspace_with_options(
        "",
        vec![],
        None,
        false,
        false,
        FixtureOptions {
            ids: ids.clone(),
            sync,
            ..FixtureOptions::default()
        },
    );
    let binding = state.borrow().read.binding.clone();
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
    assert_eq!(
        workspace.read_configuration(&selector),
        Err(ConfigurationFailure::NativeUnavailable)
    );
    assert_eq!(
        ids.borrow().calls,
        vec![IdentityKind::Private, IdentityKind::Private]
    );
    let draft = open(&mut workspace, &state);
    let unpublished = PrivateValueRef {
        value_id: PrivateValueId::new(id(71001)).unwrap(),
        document: binding.clone(),
        field_id: field("sync.endpoint"),
        revision: binding.revision.clone(),
        captured_for: None,
    };
    assert_eq!(
        stage(
            &mut workspace,
            &draft.draft,
            vec![ConfigurationEdit::SetPrivate {
                field_id: field("sync.endpoint"),
                reference: Box::new(unpublished),
            }]
        ),
        Err(ConfigurationFailure::ProtectedRefInvalid),
    );
    assert_eq!(workspace.draft(&draft.draft).unwrap(), draft);
    ids.borrow_mut()
        .outcomes
        .push_back((IdentityKind::Private, Ok(71001)));
    assert_eq!(
        workspace.read_configuration(&selector),
        Err(ConfigurationFailure::Capacity)
    );
    assert_eq!(
        ids.borrow().calls.len(),
        4,
        "collision refuses before a second allocation"
    );
    let projected = workspace.read_configuration(&selector).unwrap();
    let destination = &projected.sync.as_slice()[0];
    assert_ne!(
        destination.endpoint.value_id,
        PrivateValueId::new(id(71001)).unwrap()
    );
    let ProxyChoice::Custom { reference: proxy } = &destination.desired_proxy else {
        panic!("proxy")
    };
    assert_ne!(destination.endpoint.value_id, proxy.value_id);
    assert_eq!(
        ids.borrow().calls.len(),
        6,
        "no failed projection entry was reused"
    );
    assert_allocation_keeps_persistence(&state, &binding);
}

fn assert_capture_identity_failure(kind: SensitiveInputKind, failure: ConfigurationFailure) {
    let entry = ProtectedEntryOutcome::Captured(
        ProtectedValue::new(b"\"refused-capture\"".to_vec()).unwrap(),
    );
    let (mut workspace, state, ids) = allocation_fixture(Some(entry), vec![]);
    let binding = state.borrow().read.binding.clone();
    let draft = open(&mut workspace, &state);
    let identity_kind = match kind {
        SensitiveInputKind::Private => IdentityKind::Private,
        SensitiveInputKind::Secret => IdentityKind::Secret,
    };
    ids.borrow_mut()
        .outcomes
        .push_back((identity_kind, Err(failure)));
    let request = allocation_request(&draft.draft, kind);
    assert_eq!(workspace.request_sensitive_input(&request), Err(failure));
    assert_eq!(workspace.draft(&draft.draft).unwrap(), draft);
    assert_eq!(ids.borrow().calls, vec![IdentityKind::Draft, identity_kind]);
    // The captured ProtectedValue was consumed by the refused call. It drops
    // through its wiping owner rather than becoming reusable entry/vault state.
    let next = workspace.request_sensitive_input(&request).unwrap();
    assert!(matches!(
        next.outcome,
        SensitiveInputOutcome::Unavailable { .. }
    ));
    assert_eq!(
        ids.borrow().calls.len(),
        2,
        "unavailable entry has no identity to allocate"
    );
    let phantom = match kind {
        SensitiveInputKind::Private => ConfigurationEdit::SetPrivate {
            field_id: request.field_id.clone(),
            reference: Box::new(PrivateValueRef {
                value_id: PrivateValueId::new(id(20002)).unwrap(),
                document: binding.clone(),
                field_id: request.field_id,
                revision: binding.revision.clone(),
                captured_for: Some(Box::new(draft.draft.clone())),
            }),
        },
        SensitiveInputKind::Secret => ConfigurationEdit::ReplaceSecret {
            field_id: request.field_id.clone(),
            reference: Box::new(SecretRef {
                secret_id: SecretRefId::new(id(20002)).unwrap(),
                draft: draft.draft.clone(),
                field_id: request.field_id,
            }),
        },
    };
    assert_eq!(
        stage(&mut workspace, &draft.draft, vec![phantom]),
        Err(ConfigurationFailure::ProtectedRefInvalid)
    );
    assert_eq!(workspace.draft(&draft.draft).unwrap(), draft);
    assert_allocation_keeps_persistence(&state, &binding);
}

#[test]
fn captured_private_identity_failure_consumes_capture_without_publishing_reference() {
    assert_capture_identity_failure(
        SensitiveInputKind::Private,
        ConfigurationFailure::NativeUnavailable,
    );
}

#[test]
fn captured_secret_identity_failure_preserves_closed_error_and_publishes_no_reference() {
    assert_capture_identity_failure(
        SensitiveInputKind::Secret,
        ConfigurationFailure::InvalidOwnerResult,
    );
}

fn assert_transfer_identity_failure(kind: SensitiveInputKind) {
    let entry = ProtectedEntryOutcome::Captured(
        ProtectedValue::new(b"\"retained-capture\"".to_vec()).unwrap(),
    );
    let (mut workspace, state, ids) = allocation_fixture(Some(entry), vec![]);
    let binding = state.borrow().read.binding.clone();
    let clean = open(&mut workspace, &state);
    let previous_ack = stage(&mut workspace, &clean.draft, vec![boolean(true)]).unwrap();
    let before = previous_ack.snapshot.clone();
    let captured = workspace
        .request_sensitive_input(&allocation_request(&before.draft, kind))
        .unwrap();
    let edit = allocation_edit(captured.outcome);
    let identity_kind = match kind {
        SensitiveInputKind::Private => IdentityKind::Private,
        SensitiveInputKind::Secret => IdentityKind::Secret,
    };
    ids.borrow_mut()
        .outcomes
        .push_back((identity_kind, Err(ConfigurationFailure::NativeUnavailable)));
    let input = SetDraftChangesInput {
        draft: before.draft.clone(),
        edits: list(vec![edit]),
    };
    let preflight_calls = std::cell::Cell::new(0);
    assert_eq!(
        workspace.set_draft_changes(input.clone(), |_| {
            preflight_calls.set(preflight_calls.get() + 1);
            Ok(())
        }),
        Err(ConfigurationFailure::NativeUnavailable),
    );
    assert_eq!(preflight_calls.get(), 0);
    assert_eq!(workspace.draft(&before.draft).unwrap(), before);
    let calls_after_failure = ids.borrow().calls.clone();
    assert_eq!(
        stage(&mut workspace, &clean.draft, vec![boolean(true)]).unwrap(),
        previous_ack
    );
    assert_eq!(
        ids.borrow().calls,
        calls_after_failure,
        "prior acknowledgment remains replayable"
    );
    let accepted = stage(
        &mut workspace,
        &before.draft,
        input.edits.as_slice().to_vec(),
    )
    .unwrap();
    assert_eq!(
        accepted.snapshot.draft.revision.get(),
        before.draft.revision.get() + 1
    );
    assert_eq!(accepted.protected_transfers.as_slice().len(), 1);
    let allocated_calls = ids.borrow().calls.len();
    assert_eq!(
        stage(
            &mut workspace,
            &before.draft,
            input.edits.as_slice().to_vec()
        )
        .unwrap(),
        accepted
    );
    assert_eq!(
        ids.borrow().calls.len(),
        allocated_calls,
        "lost acknowledgment replay allocates nothing"
    );
    let candidate = prepare(&mut workspace, &accepted.snapshot);
    assert!(
        std::str::from_utf8(candidate.candidate_bytes().unwrap())
            .unwrap()
            .contains("retained-capture")
    );
    assert_allocation_keeps_persistence(&state, &binding);
}

#[test]
fn private_transfer_identity_failure_preserves_capture_draft_and_previous_acknowledgment() {
    assert_transfer_identity_failure(SensitiveInputKind::Private);
}

#[test]
fn secret_transfer_identity_failure_preserves_capture_draft_and_previous_acknowledgment() {
    assert_transfer_identity_failure(SensitiveInputKind::Secret);
}

#[test]
fn partial_transfer_allocation_failure_keeps_payloads_and_burns_unpublished_successor_id() {
    let private = ProtectedEntryOutcome::Captured(
        ProtectedValue::new(b"\"retained-endpoint\"".to_vec()).unwrap(),
    );
    let secret = ProtectedEntryOutcome::Captured(
        ProtectedValue::new(b"\"retained-secret\"".to_vec()).unwrap(),
    );
    let (mut workspace, state, ids) = allocation_fixture(Some(private), vec![secret]);
    let binding = state.borrow().read.binding.clone();
    let draft = open(&mut workspace, &state);
    let private = allocation_edit(
        workspace
            .request_sensitive_input(&allocation_request(
                &draft.draft,
                SensitiveInputKind::Private,
            ))
            .unwrap()
            .outcome,
    );
    let secret = allocation_edit(
        workspace
            .request_sensitive_input(&allocation_request(
                &draft.draft,
                SensitiveInputKind::Secret,
            ))
            .unwrap()
            .outcome,
    );
    let input = SetDraftChangesInput {
        draft: draft.draft.clone(),
        edits: list(vec![private.clone(), secret]),
    };
    ids.borrow_mut().outcomes.extend([
        (IdentityKind::Private, Ok(72001)),
        (
            IdentityKind::Secret,
            Err(ConfigurationFailure::NativeUnavailable),
        ),
    ]);
    let preflight_calls = std::cell::Cell::new(0);
    assert_eq!(
        workspace.set_draft_changes(input.clone(), |_| {
            preflight_calls.set(preflight_calls.get() + 1);
            Ok(())
        }),
        Err(ConfigurationFailure::NativeUnavailable)
    );
    assert_eq!(preflight_calls.get(), 0);
    assert_eq!(workspace.draft(&draft.draft).unwrap(), draft);
    let ConfigurationEdit::SetPrivate { mut reference, .. } = private else {
        panic!("private")
    };
    reference.value_id = PrivateValueId::new(id(72001)).unwrap();
    assert_eq!(
        stage(
            &mut workspace,
            &draft.draft,
            vec![ConfigurationEdit::SetPrivate {
                field_id: field("sync.endpoint"),
                reference
            }]
        ),
        Err(ConfigurationFailure::ProtectedRefInvalid)
    );
    ids.borrow_mut()
        .outcomes
        .push_back((IdentityKind::Private, Ok(72001)));
    let calls_before_collision = ids.borrow().calls.len();
    assert_eq!(
        stage(
            &mut workspace,
            &draft.draft,
            input.edits.as_slice().to_vec()
        ),
        Err(ConfigurationFailure::Capacity)
    );
    assert_eq!(ids.borrow().calls.len(), calls_before_collision + 1);
    assert_eq!(workspace.draft(&draft.draft).unwrap(), draft);
    ids.borrow_mut().outcomes.extend([
        (IdentityKind::Private, Ok(72002)),
        (IdentityKind::Secret, Ok(72003)),
    ]);
    let accepted = stage(
        &mut workspace,
        &draft.draft,
        input.edits.as_slice().to_vec(),
    )
    .unwrap();
    assert_eq!(accepted.protected_transfers.as_slice().len(), 2);
    assert_eq!(accepted.snapshot.draft.revision.get(), 1);
    let calls_after_acceptance = ids.borrow().calls.len();
    assert_eq!(
        stage(
            &mut workspace,
            &draft.draft,
            input.edits.as_slice().to_vec()
        )
        .unwrap(),
        accepted
    );
    assert_eq!(ids.borrow().calls.len(), calls_after_acceptance);
    let candidate = prepare(&mut workspace, &accepted.snapshot);
    let text = std::str::from_utf8(candidate.candidate_bytes().unwrap()).unwrap();
    assert!(text.contains("retained-endpoint"));
    assert!(text.contains("retained-secret"));
    assert_allocation_keeps_persistence(&state, &binding);
}

#[test]
fn discarded_draft_identity_remains_burned_across_draft_and_protected_id_kinds() {
    let entry = ProtectedEntryOutcome::Captured(
        ProtectedValue::new(b"\"collision-capture\"".to_vec()).unwrap(),
    );
    let (mut workspace, state, ids) = allocation_fixture(Some(entry), vec![]);
    let binding = state.borrow().read.binding.clone();
    ids.borrow_mut()
        .outcomes
        .push_back((IdentityKind::Draft, Ok(73001)));
    let first = open(&mut workspace, &state);
    workspace
        .discard_draft(&DiscardDraftInput {
            draft: first.draft.clone(),
        })
        .unwrap();
    ids.borrow_mut()
        .outcomes
        .push_back((IdentityKind::Draft, Ok(73001)));
    assert_eq!(
        workspace.open_draft(&OpenDraftInput {
            document: binding.clone()
        }),
        Err(ConfigurationFailure::Capacity)
    );
    assert_eq!(
        workspace.draft(&first.draft),
        Err(ConfigurationFailure::Stale)
    );
    ids.borrow_mut()
        .outcomes
        .push_back((IdentityKind::Draft, Ok(73002)));
    let current = open(&mut workspace, &state);
    ids.borrow_mut()
        .outcomes
        .push_back((IdentityKind::Private, Ok(73001)));
    assert_eq!(
        workspace.request_sensitive_input(&allocation_request(
            &current.draft,
            SensitiveInputKind::Private
        )),
        Err(ConfigurationFailure::Capacity)
    );
    assert_eq!(workspace.draft(&current.draft).unwrap(), current);
    assert_eq!(
        ids.borrow().calls,
        vec![
            IdentityKind::Draft,
            IdentityKind::Draft,
            IdentityKind::Draft,
            IdentityKind::Private
        ]
    );
    assert_allocation_keeps_persistence(&state, &binding);
}
