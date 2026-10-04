mod configuration_support;
use bridge_contracts::v1::*;
use bridge_engine::configuration::*;
use configuration_support::*;

#[test]
fn semantic_equality_keeps_source_spelling_comments_and_unknown_keys() {
    let text = "# preserved\r\nsetting.integer = 0x1 # spelling\r\nunknown = \"unchanged\"\r\n";
    let (mut w, s, calls) = workspace(
        text,
        vec![
            override_("setting.integer", "0x1"),
            override_("unknown", "\"unchanged\""),
        ],
        None,
        false,
        false,
    );
    let d = open(&mut w, &s);
    let integer = serde_json::from_str::<SignedInteger>("\"1\"").unwrap();
    let staged = stage(
        &mut w,
        &d.draft,
        vec![ConfigurationEdit::SetPublic {
            field_id: field("setting.integer"),
            value: PublicConfigValue::Integer(integer),
        }],
    )
    .unwrap();
    let candidate = prepare(&mut w, &staged.snapshot);
    assert_eq!(candidate.candidate_bytes().unwrap(), text.as_bytes());
    assert!(!candidate.changed());
    assert!(!calls.borrow().contains(&"set"));
}
#[test]
fn sparse_missing_save_does_not_materialize_unselected_defaults() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(false)]).unwrap();
    let candidate = prepare(&mut w, &staged.snapshot);
    assert_eq!(
        candidate.candidate_bytes().unwrap(),
        b"setting.boolean = false\n"
    );
    assert!(candidate.changed());
    assert_eq!(s.borrow().effects, 0);
}
#[test]
fn missing_remove_override_and_no_change_create_no_empty_document() {
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
    let candidate = prepare(&mut w, &staged.snapshot);
    assert!(!candidate.changed());
    assert!(candidate.candidate_bytes().unwrap().is_empty());
    assert_eq!(s.borrow().effects, 0);
}
#[test]
fn native_codec_semantic_proof_rejects_unowned_mutation() {
    let (mut w, s, _) = workspace("", vec![], None, true, false);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    assert!(matches!(
        w.prepare_save(&SaveConfigurationInput {
            draft: staged.snapshot.draft.clone()
        }),
        Err(ConfigurationFailure::InvalidOwnerResult)
    ));
    assert_eq!(s.borrow().effects, 0);
    assert_eq!(w.draft(&staged.snapshot.draft).unwrap(), staged.snapshot);
}
#[test]
fn complete_candidate_validation_refuses_before_persistence() {
    let (mut w, s, _) = workspace("", vec![], None, false, true);
    let d = open(&mut w, &s);
    let staged = stage(&mut w, &d.draft, vec![boolean(true)]).unwrap();
    assert!(matches!(
        w.prepare_save(&SaveConfigurationInput {
            draft: staged.snapshot.draft.clone()
        }),
        Err(ConfigurationFailure::InvalidDocument)
    ));
    assert_eq!(s.borrow().effects, 0);
}
#[test]
fn complete_edit_set_projects_mixed_apply_timing_and_exact_int64() {
    let (mut w, s, _) = workspace("", vec![], None, false, false);
    let d = open(&mut w, &s);
    let edits = vec![
        boolean(true),
        ConfigurationEdit::SetPublic {
            field_id: field("setting.integer"),
            value: PublicConfigValue::Integer(
                serde_json::from_str("\"-9223372036854775808\"").unwrap(),
            ),
        },
        ConfigurationEdit::SetPublic {
            field_id: field("setting.number"),
            value: PublicConfigValue::Number(DecimalValue::new("5.25").unwrap()),
        },
    ];
    let staged = stage(&mut w, &d.draft, edits).unwrap();
    assert_eq!(
        staged.snapshot.apply.as_slice(),
        &[
            ApplyTiming::Immediate,
            ApplyTiming::NextLaunch,
            ApplyTiming::RestartRequired
        ]
    );
    let candidate = prepare(&mut w, &staged.snapshot);
    let text = std::str::from_utf8(candidate.candidate_bytes().unwrap()).unwrap();
    assert!(text.contains("-9223372036854775808"));
    assert!(text.contains("5.25"));
}
