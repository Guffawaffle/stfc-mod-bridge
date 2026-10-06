mod configuration_support;
use bridge_contracts::v1::*;
use bridge_engine::configuration::*;
use configuration_support::*;

fn admitted(
    w: &mut Workspace,
    prepared: PreparedConfiguration,
) -> (ConfigurationTransaction<Tx>, Lease) {
    let lease = w.acquire_configuration(&prepared).unwrap();
    let recovery = w
        .configuration_recovery_binding(&OperationId::new(id(501)).unwrap(), &prepared, &lease)
        .unwrap();
    (
        w.begin_configuration(&mut Some(prepared), recovery, &lease)
            .unwrap(),
        lease,
    )
}
#[test]
fn missing_meaningful_save_commits_sparse_bytes_without_backup() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    assert_eq!(s.borrow().effects, 0);
    let (mut tx, lease) = admitted(&mut w, p);
    assert_eq!(
        w.advance_configuration(&mut tx, &lease, false).unwrap(),
        None
    );
    let outcome = w
        .advance_configuration(&mut tx, &lease, false)
        .unwrap()
        .unwrap();
    assert!(
        matches!(&outcome,CompletionOutcome::Changed { receipt:Some(receipt),.. } if matches!(receipt.as_ref(),EffectReceipt::ConfigurationWritten { backup:None,.. }))
    );
    assert_eq!(s.borrow().read.bytes, b"setting.boolean = true\n");
    let effects = s.borrow().effects;
    assert_eq!(
        w.advance_configuration(&mut tx, &lease, false).unwrap(),
        Some(outcome)
    );
    assert_eq!(s.borrow().effects, effects);
    w.publish_configuration_completion(&mut tx, &lease, |_| Ok(()))
        .unwrap();
    let clean = w.draft_after_save(&staged.snapshot.draft).unwrap();
    assert_eq!(clean.state, DraftState::Clean);
    assert!(clean.edits.as_slice().is_empty());
    assert_eq!(clean.draft.document, s.borrow().read.binding);
    assert_eq!(
        clean.draft.revision.get(),
        staged.snapshot.draft.revision.get() + 1
    );
    assert_eq!(w.draft_after_save(&staged.snapshot.draft).unwrap(), clean);
}
#[test]
fn no_change_admission_creates_no_file_backup_or_stage() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let p = prepare(&mut w, &d);
    let (mut tx, lease) = admitted(&mut w, p);
    assert!(matches!(
        w.advance_configuration(&mut tx, &lease, false).unwrap(),
        Some(CompletionOutcome::NoChange { .. })
    ));
    assert!(s.borrow().read.bytes.is_empty());
    assert_eq!(s.borrow().effects, 0);
    assert!(s.borrow().backups.is_empty());
}
#[test]
fn appearing_file_and_same_byte_physical_replacement_refuse_before_stage() {
    for text in ["", "setting.boolean = false\n"] {
        let overrides = if text.is_empty() {
            vec![]
        } else {
            vec![override_("setting.boolean", "false")]
        };
        let (mut w, s, _) = workspace(text, overrides, None, false, false);
        let d = open(&mut w, &s);
        let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
        let p = prepare(&mut w, &staged.snapshot);
        s.borrow_mut().read.binding.baseline = DocumentBaseline::Existing {
            file_identity: NativeFileIdentity::new("foreign-same-bytes-file").unwrap(),
            content_digest: hash(text.as_bytes()),
        };
        assert!(matches!(
            w.acquire_configuration(&p),
            Err(ConfigurationFailure::Stale)
        ));
        assert_eq!(s.borrow().effects, 0);
        assert_eq!(w.draft(&staged.snapshot.draft).unwrap(), staged.snapshot);
    }
}
#[test]
fn losing_writer_has_no_native_effects() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    s.borrow_mut().busy = true;
    assert!(matches!(
        w.acquire_configuration(&p),
        Err(ConfigurationFailure::Busy)
    ));
    assert_eq!(s.borrow().effects, 0);
    assert_eq!(s.borrow().drops, 0);
}
#[test]
fn backup_failure_preserves_prior_document_and_draft() {
    let text = "setting.boolean = false\n";
    let (mut w, s, _) = workspace(
        text,
        vec![override_("setting.boolean", "false")],
        None,
        false,
        false,
    );
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    s.borrow_mut().fail_backup = true;
    let (mut tx, lease) = admitted(&mut w, p);
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    assert!(matches!(
        w.advance_configuration(&mut tx, &lease, false),
        Err(ConfigurationFailure::BackupUnavailable)
    ));
    assert_eq!(s.borrow().read.bytes, text.as_bytes());
    assert_eq!(w.draft(&staged.snapshot.draft).unwrap(), staged.snapshot);
}
#[test]
fn existing_write_retains_exact_prior_byte_backup_and_history() {
    let text = "setting.boolean = false\n";
    let (mut w, s, _) = workspace(
        text,
        vec![override_("setting.boolean", "false")],
        None,
        false,
        false,
    );
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    let (mut tx, lease) = admitted(&mut w, p);
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    let outcome = w
        .advance_configuration(&mut tx, &lease, false)
        .unwrap()
        .unwrap();
    assert!(matches!(outcome, CompletionOutcome::Changed { .. }));
    let history = w
        .configuration_history(&ConfigurationHistoryInput {
            document: s.borrow().read.binding.clone(),
        })
        .unwrap();
    assert_eq!(history.items.as_slice().len(), 1);
    assert_eq!(history.items.as_slice()[0].document, d.draft.document);
    assert_eq!(
        history.items.as_slice()[0].retained_digest,
        hash(text.as_bytes())
    );
}
#[test]
fn cancellation_before_first_step_has_no_effect_and_late_cancel_is_committed() {
    for before in [true, false] {
        let (mut w, s, _) = workspace("", vec![], None, false, false);
        let d = open(&mut w, &s);
        let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
        let p = prepare(&mut w, &staged.snapshot);
        let (mut tx, lease) = admitted(&mut w, p);
        if !before {
            w.advance_configuration(&mut tx, &lease, false).unwrap();
        }
        let outcome = w
            .advance_configuration(&mut tx, &lease, true)
            .unwrap()
            .unwrap();
        w.publish_configuration_completion(&mut tx, &lease, |_| Ok(()))
            .unwrap();
        if before {
            assert!(matches!(
                outcome,
                CompletionOutcome::CancelledBeforeCommit { .. }
            ));
            assert_eq!(s.borrow().effects, 0);
        } else {
            assert!(matches!(outcome, CompletionOutcome::Changed { .. }));
        }
        if before {
            assert_eq!(w.draft(&staged.snapshot.draft).unwrap(), staged.snapshot);
        } else {
            assert_eq!(
                w.draft_after_save(&staged.snapshot.draft).unwrap().state,
                DraftState::Clean
            );
        }
    }
}

#[test]
fn semantically_empty_save_clears_exact_draft_once_and_keeps_missing_baseline() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(
        &mut w,
        &d.draft,
        vec![ConfigurationEdit::RemoveOverride {
            field_id: field("setting.boolean"),
        }],
    )
    .unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    let (mut tx, lease) = admitted(&mut w, p);
    assert!(matches!(
        w.advance_configuration(&mut tx, &lease, false).unwrap(),
        Some(CompletionOutcome::NoChange { .. })
    ));
    w.publish_configuration_completion(&mut tx, &lease, |_| Ok(()))
        .unwrap();
    let clean = w.draft_after_save(&staged.snapshot.draft).unwrap();
    assert_eq!(clean.draft.document, staged.snapshot.draft.document);
    assert_eq!(
        clean.draft.revision.get(),
        staged.snapshot.draft.revision.get() + 1
    );
    assert_eq!(clean.state, DraftState::Clean);
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    assert_eq!(w.draft_after_save(&staged.snapshot.draft).unwrap(), clean);
    assert_eq!(s.borrow().effects, 0);
}

#[test]
fn older_commit_preserves_newer_local_edits_as_stale() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    let (mut tx, lease) = admitted(&mut w, p);
    let newer = stage(&mut w, &staged.snapshot.draft, vec![boolean(false)]).unwrap();
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    w.publish_configuration_completion(&mut tx, &lease, |_| Ok(()))
        .unwrap();
    let retained = w.draft(&newer.snapshot.draft).unwrap();
    assert_eq!(retained.edits, newer.snapshot.edits);
    assert_eq!(retained.draft, newer.snapshot.draft);
    assert_eq!(retained.state, DraftState::Stale);
    assert!(matches!(
        w.draft_after_save(&staged.snapshot.draft),
        Err(ConfigurationFailure::Stale)
    ));
}

#[test]
fn stages_between_prepare_and_admission_invalidate_exact_old_plan_without_effect() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    let newer = stage(&mut w, &staged.snapshot.draft, vec![boolean(false)]).unwrap();
    assert!(matches!(
        w.acquire_configuration(&p),
        Err(ConfigurationFailure::Stale)
    ));
    assert_eq!(s.borrow().effects, 0);
    assert_eq!(s.borrow().drops, 0);
    assert_eq!(w.draft(&newer.snapshot.draft).unwrap(), newer.snapshot);
}

#[test]
fn stage_after_acquisition_before_durable_begin_still_refuses_old_plan() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    let lease = w.acquire_configuration(&p).unwrap();
    let recovery = w
        .configuration_recovery_binding(&OperationId::new(id(501)).unwrap(), &p, &lease)
        .unwrap();
    let newer = stage(&mut w, &staged.snapshot.draft, vec![boolean(false)]).unwrap();
    let identity = candidate_identity(&p);
    let mut slot = Some(p);
    assert!(matches!(
        w.begin_configuration(&mut slot, recovery, &lease),
        Err(ConfigurationFailure::Stale)
    ));
    assert_eq!(candidate_identity(slot.as_ref().unwrap()), identity);
    assert_eq!(s.borrow().begins, 0);
    assert_eq!(s.borrow().acquisitions, 1);
    assert!(s.borrow().live_leases.contains(&lease.id));
    assert_eq!(s.borrow().effects, 0);
    assert_eq!(w.draft(&newer.snapshot.draft).unwrap(), newer.snapshot);
}

#[test]
fn explicit_restore_validates_backup_and_backs_up_current_document() {
    let text = "setting.boolean = true\n";
    let (mut w, s, _) = workspace(
        text,
        vec![override_("setting.boolean", "true")],
        None,
        false,
        false,
    );
    let backup = retain_backup(&s, "setting.boolean = false\n");
    let original = s.borrow().read.binding.clone();
    let p = w
        .prepare_restore(&RestoreConfigurationInput {
            document: original.clone(),
            backup,
        })
        .unwrap();
    let (mut tx, lease) = admitted(&mut w, p);
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    let outcome = w
        .advance_configuration(&mut tx, &lease, false)
        .unwrap()
        .unwrap();
    assert!(matches!(outcome, CompletionOutcome::Changed { .. }));
    assert_eq!(s.borrow().read.bytes, b"setting.boolean = false\n");
    assert_eq!(s.borrow().backups.last().unwrap().document, original);
}

#[test]
fn backup_tamper_between_restore_prepare_and_admission_refuses_before_stage() {
    let text = "setting.boolean = true\n";
    let (mut w, s, _) = workspace(
        text,
        vec![override_("setting.boolean", "true")],
        None,
        false,
        false,
    );
    let backup = retain_backup(&s, "setting.boolean = false\n");
    let p = w
        .prepare_restore(&RestoreConfigurationInput {
            document: s.borrow().read.binding.clone(),
            backup: backup.clone(),
        })
        .unwrap();
    s.borrow_mut()
        .backup_bytes
        .insert(backup.backup_id, b"tampered".to_vec());
    assert!(matches!(
        w.acquire_configuration(&p),
        Err(ConfigurationFailure::BackupUnavailable)
    ));
    assert_eq!(s.borrow().read.bytes, text.as_bytes());
    assert_eq!(s.borrow().effects, 0);
}

fn candidate_identity(p: &PreparedConfiguration) -> (usize, usize, Sha256) {
    let bytes = p.candidate_bytes().unwrap();
    (
        bytes.as_ptr() as usize,
        bytes.len(),
        p.candidate_digest().clone(),
    )
}

#[test]
fn repeated_held_lease_revalidation_never_reacquires_and_begin_moves_once() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    let lease = w.acquire_configuration(&p).unwrap();
    let recovery = w
        .configuration_recovery_binding(&OperationId::new(id(501)).unwrap(), &p, &lease)
        .unwrap();
    s.borrow_mut().lease_observations.clear();
    s.borrow().schema_lease_observations.borrow_mut().clear();
    w.revalidate_configuration(&p, &lease).unwrap();
    w.revalidate_configuration(&p, &lease).unwrap();
    let mut slot = Some(p);
    let mut tx = w
        .begin_configuration(&mut slot, recovery.clone(), &lease)
        .unwrap();
    assert!(slot.is_none());
    assert_eq!(s.borrow().acquisitions, 1);
    assert_eq!(s.borrow().begins, 1);
    assert_eq!(
        s.borrow().lease_observations,
        vec![
            ("revalidate", lease.id),
            ("revalidate", lease.id),
            ("revalidate", lease.id),
            ("begin", lease.id)
        ]
    );
    assert_eq!(
        *s.borrow().schema_lease_observations.borrow(),
        vec![vec![lease.id]; 3]
    );
    let observations = s.borrow().lease_observations.clone();
    let schema_observations = s.borrow().schema_lease_observations.borrow().clone();
    assert!(matches!(
        w.begin_configuration(&mut slot, recovery, &lease),
        Err(ConfigurationFailure::InvalidInput)
    ));
    assert_eq!(s.borrow().lease_observations, observations);
    assert_eq!(
        *s.borrow().schema_lease_observations.borrow(),
        schema_observations
    );
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    assert!(matches!(
        w.advance_configuration(&mut tx, &lease, false).unwrap(),
        Some(CompletionOutcome::Changed { .. })
    ));
    assert_eq!(s.borrow().begins, 1);
    assert_eq!(s.borrow().drops, 0);
    assert!(
        s.borrow()
            .lease_observations
            .iter()
            .all(|(_, id)| *id == lease.id)
    );
    drop(tx);
    drop(lease);
    assert!(s.borrow().live_leases.is_empty());
    assert_eq!(s.borrow().drops, 1);
}

#[test]
fn held_lease_physical_and_schema_refusals_retain_candidate_before_begin() {
    for physical in [true, false] {
        let unavailable = std::rc::Rc::new(std::cell::Cell::new(false));
        let (mut w, s, _) = workspace_with_options(
            "",
            vec![],
            None,
            false,
            false,
            FixtureOptions {
                schema_unavailable: unavailable.clone(),
                ..FixtureOptions::default()
            },
        );
        let d = open(&mut w, &s);
        let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
        let p = prepare(&mut w, &staged.snapshot);
        let identity = candidate_identity(&p);
        let lease = w.acquire_configuration(&p).unwrap();
        let recovery = w
            .configuration_recovery_binding(&OperationId::new(id(501)).unwrap(), &p, &lease)
            .unwrap();
        if physical {
            s.borrow_mut().read.binding.baseline = DocumentBaseline::Existing {
                file_identity: NativeFileIdentity::new("foreign-appearing-file").unwrap(),
                content_digest: hash(b""),
            };
        } else {
            unavailable.set(true);
        }
        let expected = if physical {
            ConfigurationFailure::Stale
        } else {
            ConfigurationFailure::UnsupportedSchema
        };
        assert_eq!(w.revalidate_configuration(&p, &lease), Err(expected));
        let mut slot = Some(p);
        assert!(
            matches!(w.begin_configuration(&mut slot, recovery, &lease),Err(failure) if failure == expected)
        );
        assert_eq!(candidate_identity(slot.as_ref().unwrap()), identity);
        assert_eq!(s.borrow().begins, 0);
        assert_eq!(s.borrow().effects, 0);
        assert_eq!(s.borrow().acquisitions, 1);
        assert_eq!(s.borrow().drops, 0);
        assert!(
            s.borrow()
                .lease_observations
                .iter()
                .all(|(_, id)| *id == lease.id)
        );
        assert_eq!(w.draft(&staged.snapshot.draft).unwrap(), staged.snapshot);
    }
}

#[test]
fn held_restore_backup_refusal_preserves_exact_candidate_and_owner_lease() {
    let text = "setting.boolean = true\n";
    let (mut w, s, _) = workspace(
        text,
        vec![override_("setting.boolean", "true")],
        None,
        false,
        false,
    );
    let backup = retain_backup(&s, "setting.boolean = false\n");
    let p = w
        .prepare_restore(&RestoreConfigurationInput {
            document: s.borrow().read.binding.clone(),
            backup: backup.clone(),
        })
        .unwrap();
    let identity = candidate_identity(&p);
    let lease = w.acquire_configuration(&p).unwrap();
    let recovery = w
        .configuration_recovery_binding(&OperationId::new(id(501)).unwrap(), &p, &lease)
        .unwrap();
    s.borrow_mut()
        .backup_bytes
        .insert(backup.backup_id, b"tampered".to_vec());
    assert_eq!(
        w.revalidate_configuration(&p, &lease),
        Err(ConfigurationFailure::BackupUnavailable)
    );
    let mut slot = Some(p);
    assert!(matches!(
        w.begin_configuration(&mut slot, recovery, &lease),
        Err(ConfigurationFailure::BackupUnavailable)
    ));
    assert_eq!(candidate_identity(slot.as_ref().unwrap()), identity);
    assert_eq!(s.borrow().begins, 0);
    assert_eq!(s.borrow().effects, 0);
    assert_eq!(s.borrow().acquisitions, 1);
    assert_eq!(s.borrow().drops, 0);
    assert!(
        s.borrow()
            .lease_observations
            .iter()
            .any(|(call, id)| *call == "backup" && *id == lease.id)
    );
    assert_eq!(s.borrow().read.bytes, text.as_bytes());
}

#[test]
fn foreign_recovery_binding_refuses_without_consuming_candidate_or_beginning() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    let identity = candidate_identity(&p);
    let lease = w.acquire_configuration(&p).unwrap();
    let mut recovery = w
        .configuration_recovery_binding(&OperationId::new(id(501)).unwrap(), &p, &lease)
        .unwrap();
    let mut foreign = p.baseline().clone();
    foreign.document_id = DocumentId::new(id(999)).unwrap();
    recovery.target = RecoveryTarget::Configuration { document: foreign };
    let mut slot = Some(p);
    assert!(matches!(
        w.begin_configuration(&mut slot, recovery, &lease),
        Err(ConfigurationFailure::InvalidOwnerResult)
    ));
    assert_eq!(candidate_identity(slot.as_ref().unwrap()), identity);
    assert_eq!(s.borrow().begins, 0);
    assert_eq!(s.borrow().effects, 0);
    assert_eq!(s.borrow().acquisitions, 1);
    assert_eq!(s.borrow().drops, 0);
}

#[test]
fn native_begin_errors_keep_exact_candidate_for_recovery_without_retry() {
    for failure in [
        ConfigurationFailure::InvalidOwnerResult,
        ConfigurationFailure::RecoveryRequired,
    ] {
        let (mut w, s, _) = workspace("", vec![], None, false, false);
        let d = open(&mut w, &s);
        let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
        let p = prepare(&mut w, &staged.snapshot);
        let scope = RecoveryConfiguration::from_capture(&PreparedCapture::SaveConfiguration {
            input: p.save_capture().unwrap(),
        })
        .unwrap();
        let identity = candidate_identity(&p);
        let lease = w.acquire_configuration(&p).unwrap();
        let recovery = w
            .configuration_recovery_binding(&OperationId::new(id(501)).unwrap(), &p, &lease)
            .unwrap();
        s.borrow_mut().begin_failure = Some(failure);
        let mut slot = Some(p);
        assert!(
            matches!(w.begin_configuration(&mut slot,recovery.clone(),&lease),Err(observed) if observed==failure)
        );
        assert_eq!(candidate_identity(slot.as_ref().unwrap()), identity);
        assert_eq!(s.borrow().begins, 1);
        assert_eq!(s.borrow().acquisitions, 1);
        assert_eq!(s.borrow().drops, 0);
        assert_eq!(w.draft(&staged.snapshot.draft).unwrap(), staged.snapshot);
        let effects = s.borrow().effects;
        let recovered = w.recover_configuration(&scope, &recovery, &lease);
        if failure == ConfigurationFailure::RecoveryRequired {
            assert_eq!(effects, 1);
            assert_eq!(recovered, Err(ConfigurationFailure::RecoveryRequired));
        } else {
            assert_eq!(effects, 0);
            assert!(matches!(
                recovered,
                Ok(Some(CompletionOutcome::CancelledBeforeCommit { .. }))
            ));
        }
        assert_eq!(s.borrow().effects, effects);
        assert_eq!(s.borrow().begins, 1);
        assert_eq!(candidate_identity(slot.as_ref().unwrap()), identity);
        assert!(
            s.borrow()
                .lease_observations
                .iter()
                .all(|(_, id)| *id == lease.id)
        );
        drop(slot);
        assert_eq!(s.borrow().drops, 0);
        drop(lease);
        assert_eq!(s.borrow().drops, 1);
    }
}

#[test]
fn completed_write_keeps_dirty_draft_until_durable_callback_and_publishes_once() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    let (mut tx, lease) = admitted(&mut w, p);
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    let outcome = w.advance_configuration(&mut tx, &lease, false).unwrap();
    assert!(tx.completion_pending());
    assert_eq!(w.draft(&staged.snapshot.draft).unwrap(), staged.snapshot);
    let steps = s.borrow().steps;
    let effects = s.borrow().effects;
    let mut projected = None;
    assert_eq!(
        w.publish_configuration_completion(&mut tx, &lease, |changed| {
            assert_eq!(s.borrow().steps, steps);
            assert_eq!(s.borrow().drops, 0);
            assert!(s.borrow().live_leases.contains(&lease.id));
            assert_eq!(changed.len(), 1);
            assert_eq!(changed[0].state, DraftState::Clean);
            projected = Some(changed[0].clone());
            Err(ConfigurationFailure::PersistenceFailed)
        }),
        Err(ConfigurationFailure::PersistenceFailed)
    );
    assert_eq!(w.draft(&staged.snapshot.draft).unwrap(), staged.snapshot);
    let reads = s.borrow().reads.get();
    assert_eq!(
        w.advance_configuration(&mut tx, &lease, true).unwrap(),
        outcome
    );
    assert_eq!(s.borrow().steps, steps);
    w.publish_configuration_completion(&mut tx, &lease, |changed| {
        assert_eq!(changed, &[projected.clone().unwrap()]);
        assert_eq!(s.borrow().effects, effects);
        assert_eq!(s.borrow().reads.get(), reads);
        Ok(())
    })
    .unwrap();
    assert_eq!(
        w.draft_after_save(&staged.snapshot.draft).unwrap(),
        projected.unwrap()
    );
    assert!(!tx.completion_pending());
    w.publish_configuration_completion(&mut tx, &lease, |_| {
        panic!("publication replay must not invoke commit")
    })
    .unwrap();
    assert_eq!(s.borrow().effects, effects);
    assert_eq!(s.borrow().drops, 0);
}

#[test]
fn newer_edit_after_completion_refusal_is_preserved_as_stale_without_writer_replay() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    let (mut tx, lease) = admitted(&mut w, p);
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    assert_eq!(
        w.publish_configuration_completion(&mut tx, &lease, |_| Err(
            ConfigurationFailure::Capacity
        )),
        Err(ConfigurationFailure::Capacity)
    );
    let newer = stage(&mut w, &staged.snapshot.draft, vec![boolean(false)]).unwrap();
    let steps = s.borrow().steps;
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    w.publish_configuration_completion(&mut tx, &lease, |changed| {
        assert_eq!(changed.len(), 1);
        assert_eq!(changed[0].state, DraftState::Stale);
        assert_eq!(changed[0].edits, newer.snapshot.edits);
        assert_eq!(changed[0].draft, newer.snapshot.draft);
        assert_eq!(s.borrow().steps, steps);
        Ok(())
    })
    .unwrap();
    let retained = w.draft(&newer.snapshot.draft).unwrap();
    assert_eq!(retained.state, DraftState::Stale);
    assert_eq!(retained.edits, newer.snapshot.edits);
    assert_eq!(s.borrow().begins, 1);
}

#[test]
fn owner_read_refusal_after_native_completion_keeps_intent_and_cached_native_outcome() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    let (mut tx, lease) = admitted(&mut w, p);
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    let outcome = w.advance_configuration(&mut tx, &lease, false).unwrap();
    let original = s.borrow().read.binding.clone();
    s.borrow_mut().read.binding.revision = OpaqueRevision::new("foreign-after-write").unwrap();
    assert_eq!(
        w.publish_configuration_completion(&mut tx, &lease, |_| panic!(
            "foreign read must refuse before commit"
        )),
        Err(ConfigurationFailure::RecoveryRequired)
    );
    assert_eq!(w.draft(&staged.snapshot.draft).unwrap(), staged.snapshot);
    assert!(tx.completion_pending());
    let steps = s.borrow().steps;
    assert_eq!(
        w.advance_configuration(&mut tx, &lease, false).unwrap(),
        outcome
    );
    assert_eq!(s.borrow().steps, steps);
    s.borrow_mut().read.binding = original;
    w.publish_configuration_completion(&mut tx, &lease, |_| Ok(()))
        .unwrap();
    assert_eq!(
        w.draft_after_save(&staged.snapshot.draft).unwrap().state,
        DraftState::Clean
    );
    assert_eq!(s.borrow().begins, 1);
}

#[test]
fn no_change_cleanup_waits_for_commit_and_does_not_create_file_backup_or_stage() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(
        &mut w,
        &d.draft,
        vec![ConfigurationEdit::RemoveOverride {
            field_id: field("setting.boolean"),
        }],
    )
    .unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    let (mut tx, lease) = admitted(&mut w, p);
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    assert_eq!(
        w.publish_configuration_completion(&mut tx, &lease, |changed| {
            assert_eq!(changed.len(), 1);
            assert_eq!(changed[0].draft.document, staged.snapshot.draft.document);
            Err(ConfigurationFailure::Capacity)
        }),
        Err(ConfigurationFailure::Capacity)
    );
    assert_eq!(w.draft(&staged.snapshot.draft).unwrap(), staged.snapshot);
    assert_eq!(s.borrow().effects, 0);
    assert_eq!(s.borrow().begins, 0);
    assert!(s.borrow().backups.is_empty());
    assert!(s.borrow().read.bytes.is_empty());
    w.publish_configuration_completion(&mut tx, &lease, |_| Ok(()))
        .unwrap();
    let clean = w.draft_after_save(&staged.snapshot.draft).unwrap();
    assert_eq!(clean.state, DraftState::Clean);
    assert_eq!(
        clean.draft.revision.get(),
        staged.snapshot.draft.revision.get() + 1
    );
    w.publish_configuration_completion(&mut tx, &lease, |_| panic!("already published"))
        .unwrap();
}

#[test]
fn restore_completion_preserves_matching_local_edits_until_stale_publication() {
    let text = "setting.boolean = true\n";
    let (mut w, s, _) = workspace(
        text,
        vec![override_("setting.boolean", "true")],
        None,
        false,
        false,
    );
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(false)]).unwrap();
    let backup = retain_backup(&s, "setting.boolean = false\n");
    let p = w
        .prepare_restore(&RestoreConfigurationInput {
            document: d.draft.document.clone(),
            backup,
        })
        .unwrap();
    let (mut tx, lease) = admitted(&mut w, p);
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    assert_eq!(w.draft(&staged.snapshot.draft).unwrap(), staged.snapshot);
    w.publish_configuration_completion(&mut tx, &lease, |changed| {
        assert_eq!(changed.len(), 1);
        assert_eq!(changed[0].state, DraftState::Stale);
        assert_eq!(changed[0].edits, staged.snapshot.edits);
        assert!(s.borrow().live_leases.contains(&lease.id));
        Ok(())
    })
    .unwrap();
    assert_eq!(
        w.draft(&staged.snapshot.draft).unwrap().state,
        DraftState::Stale
    );
    assert_eq!(s.borrow().drops, 0);
}

#[test]
fn completion_refusal_preserves_protected_payload_until_exact_clean_publication() {
    let marker = "synthetic-completion-private-marker";
    let entry = ProtectedEntryOutcome::Captured(
        ProtectedValue::new(format!("\"{marker}\"").into_bytes()).unwrap(),
    );
    let (mut w, s, _) = workspace("", vec![], Some(entry), false, false);
    let d = open(&mut w, &s);
    let captured = w
        .request_sensitive_input(&RequestSensitiveInputInput {
            draft: d.draft.clone(),
            field_id: field("sync.token"),
            sensitivity: SensitiveInputKind::Secret,
        })
        .unwrap();
    let SensitiveInputOutcome::CapturedSecret { reference } = captured.outcome else {
        panic!("secret capture");
    };
    let staged = stage(
        &mut w,
        &d.draft,
        vec![ConfigurationEdit::ReplaceSecret {
            field_id: field("sync.token"),
            reference,
        }],
    )
    .unwrap();
    let p = prepare(&mut w, &staged.snapshot);
    let (mut tx, lease) = admitted(&mut w, p);
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    w.advance_configuration(&mut tx, &lease, false).unwrap();
    assert_eq!(
        w.publish_configuration_completion(&mut tx, &lease, |changed| {
            assert!(!serde_json::to_string(changed).unwrap().contains(marker));
            Err(ConfigurationFailure::PersistenceFailed)
        }),
        Err(ConfigurationFailure::PersistenceFailed)
    );
    assert_eq!(w.draft(&staged.snapshot.draft).unwrap(), staged.snapshot);
    let retained = prepare(&mut w, &staged.snapshot);
    assert!(
        std::str::from_utf8(retained.candidate_bytes().unwrap())
            .unwrap()
            .contains(marker)
    );
    drop(retained);
    w.publish_configuration_completion(&mut tx, &lease, |changed| {
        assert!(!serde_json::to_string(changed).unwrap().contains(marker));
        assert_eq!(s.borrow().drops, 0);
        Ok(())
    })
    .unwrap();
    let clean = w.draft_after_save(&staged.snapshot.draft).unwrap();
    assert!(clean.edits.as_slice().is_empty());
    assert!(!serde_json::to_string(&clean).unwrap().contains(marker));
    assert!(matches!(
        w.prepare_save(&SaveConfigurationInput {
            draft: staged.snapshot.draft
        }),
        Err(ConfigurationFailure::Stale)
    ));
}
