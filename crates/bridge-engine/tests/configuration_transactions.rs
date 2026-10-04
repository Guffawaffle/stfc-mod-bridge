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
        w.begin_configuration(prepared, recovery, &lease).unwrap(),
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
    assert!(matches!(
        w.begin_configuration(p, recovery, &lease),
        Err(ConfigurationFailure::Stale)
    ));
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
