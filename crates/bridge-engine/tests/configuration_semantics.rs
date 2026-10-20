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

fn structure_edit() -> ConfigurationEdit {
    ConfigurationEdit::RemoveSyncDestination {
        destination_id: DestinationId::new("synthetic-destination").unwrap(),
    }
}

#[test]
fn unknown_empty_descendant_refuses_owned_parent_removal_and_rename() {
    for rename in [false, true] {
        let mutation = if rename {
            SemanticMutation::RenameTable {
                path: path("sync"),
                destination: path("moved"),
            }
        } else {
            SemanticMutation::RemoveTable { path: path("sync") }
        };
        let options = FixtureOptions {
            tables: vec![table("sync.unknown")],
            mutations: vec![mutation],
            additional_owned: vec![path("sync"), path("moved"), path("moved.endpoint")],
            ..FixtureOptions::default()
        };
        let (mut w, state, calls) = workspace_with_options(
            "[sync]\nendpoint = \"endpoint\"\n[sync.unknown]\n",
            vec![override_("sync.endpoint", "\"endpoint\"")],
            None,
            false,
            false,
            options,
        );
        let draft = open(&mut w, &state);
        let staged = stage(&mut w, &draft.draft, vec![structure_edit()]).unwrap();
        assert!(matches!(
            w.prepare_save(&SaveConfigurationInput {
                draft: staged.snapshot.draft.clone()
            }),
            Err(ConfigurationFailure::UnsupportedSchema)
        ));
        assert_eq!(w.draft(&staged.snapshot.draft).unwrap(), staged.snapshot);
        assert_eq!(state.borrow().effects, 0);
        assert!(
            !calls.borrow().contains(&"remove_table") && !calls.borrow().contains(&"rename_table")
        );
    }
}

#[test]
fn fully_owned_table_moves_preserve_unrelated_empty_tables_and_require_new_parents() {
    use bridge_toml::TomlSnapshot;
    for rename in [false, true] {
        for omit_unrelated in [false, true] {
            let mutation = if rename {
                SemanticMutation::RenameTable {
                    path: path("sync"),
                    destination: path("moved.destination"),
                }
            } else {
                SemanticMutation::RemoveTable { path: path("sync") }
            };
            let candidate = if rename {
                "[moved.destination]\nendpoint = \"endpoint\"\n[moved.destination.owned]\n"
            } else {
                ""
            };
            let candidate = format!(
                "{candidate}{}",
                if omit_unrelated { "" } else { "[unrelated]\n" }
            );
            let mut tables = if rename {
                vec![
                    table("moved"),
                    table("moved.destination"),
                    table("moved.destination.owned"),
                ]
            } else {
                vec![]
            };
            if !omit_unrelated {
                tables.push(table("unrelated"));
            }
            let options = FixtureOptions {
                tables: vec![table("sync.owned"), table("unrelated")],
                mutations: vec![mutation],
                additional_owned: vec![
                    path("sync"),
                    path("sync.owned"),
                    path("moved.destination"),
                    path("moved.destination.owned"),
                    path("moved.destination.endpoint"),
                ],
                candidate: Some((
                    candidate.clone(),
                    TomlSnapshot {
                        overrides: if rename {
                            vec![override_("moved.destination.endpoint", "\"endpoint\"")]
                        } else {
                            vec![]
                        },
                        tables,
                    },
                )),
                ..FixtureOptions::default()
            };
            let (mut w, state, _) = workspace_with_options(
                "[sync]\nendpoint = \"endpoint\"\n[sync.owned]\n[unrelated]\n",
                vec![override_("sync.endpoint", "\"endpoint\"")],
                None,
                false,
                false,
                options,
            );
            let draft = open(&mut w, &state);
            let staged = stage(&mut w, &draft.draft, vec![structure_edit()]).unwrap();
            let result = w.prepare_save(&SaveConfigurationInput {
                draft: staged.snapshot.draft,
            });
            if omit_unrelated {
                assert!(matches!(
                    result,
                    Err(ConfigurationFailure::InvalidOwnerResult)
                ));
            } else {
                assert_eq!(
                    result.unwrap().candidate_bytes().unwrap(),
                    candidate.as_bytes()
                );
            }
            assert_eq!(state.borrow().effects, 0);
        }
    }
}

#[test]
fn scalar_set_proof_requires_table_parents_and_preserves_unowned_empty_tables() {
    use bridge_toml::TomlSnapshot;
    for omit_parent in [false, true] {
        let mut tables = vec![table("unknown"), table("unknown.empty")];
        if !omit_parent {
            tables.push(table("setting"));
        }
        let options = FixtureOptions {
            tables: vec![table("unknown"), table("unknown.empty")],
            candidate: Some((
                "setting.boolean = true\n[unknown.empty]\n".into(),
                TomlSnapshot {
                    overrides: vec![override_("setting.boolean", "true")],
                    tables,
                },
            )),
            ..FixtureOptions::default()
        };
        let (mut w, state, _) =
            workspace_with_options("[unknown.empty]\n", vec![], None, false, false, options);
        let draft = open(&mut w, &state);
        let staged = stage(&mut w, &draft.draft, vec![boolean(true)]).unwrap();
        let result = w.prepare_save(&SaveConfigurationInput {
            draft: staged.snapshot.draft,
        });
        if omit_parent {
            assert!(matches!(
                result,
                Err(ConfigurationFailure::InvalidOwnerResult)
            ));
        } else {
            assert!(result.is_ok());
        }
        assert_eq!(state.borrow().effects, 0);
    }
}

#[test]
fn scalar_remove_prunes_only_explicitly_owned_newly_empty_ancestors() {
    use bridge_toml::TomlSnapshot;
    for own_parent in [false, true] {
        for omit_unrelated in [false, true] {
            let options = FixtureOptions {
                tables: vec![table("unrelated")],
                additional_owned: if own_parent {
                    vec![path("setting"), path("unrelated")]
                } else {
                    vec![]
                },
                candidate: Some((
                    if omit_unrelated { "" } else { "[unrelated]\n" }.into(),
                    TomlSnapshot {
                        overrides: vec![],
                        tables: if omit_unrelated {
                            vec![]
                        } else {
                            vec![table("unrelated")]
                        },
                    },
                )),
                ..FixtureOptions::default()
            };
            let (mut w, state, _) = workspace_with_options(
                "setting.boolean = true\n[unrelated]\n",
                vec![override_("setting.boolean", "true")],
                None,
                false,
                false,
                options,
            );
            let draft = open(&mut w, &state);
            let staged = stage(
                &mut w,
                &draft.draft,
                vec![ConfigurationEdit::RemoveOverride {
                    field_id: field("setting.boolean"),
                }],
            )
            .unwrap();
            let result = w.prepare_save(&SaveConfigurationInput {
                draft: staged.snapshot.draft,
            });
            if own_parent && !omit_unrelated {
                assert!(result.is_ok());
            } else {
                assert!(matches!(
                    result,
                    Err(ConfigurationFailure::InvalidOwnerResult)
                ));
            }
            assert_eq!(state.borrow().effects, 0);
        }
    }
}

#[test]
fn table_rename_refuses_unowned_relocated_values_and_existing_destinations() {
    for collision in [false, true] {
        let options = FixtureOptions {
            tables: if collision {
                vec![table("moved")]
            } else {
                vec![]
            },
            mutations: vec![SemanticMutation::RenameTable {
                path: path("sync"),
                destination: path("moved"),
            }],
            additional_owned: if collision {
                vec![path("sync"), path("moved"), path("moved.endpoint")]
            } else {
                vec![path("sync"), path("moved")]
            },
            ..FixtureOptions::default()
        };
        let (mut w, state, _) = workspace_with_options(
            "[sync]\nendpoint = \"endpoint\"\n[moved]\n",
            vec![override_("sync.endpoint", "\"endpoint\"")],
            None,
            false,
            false,
            options,
        );
        let draft = open(&mut w, &state);
        let staged = stage(&mut w, &draft.draft, vec![structure_edit()]).unwrap();
        let result = w.prepare_save(&SaveConfigurationInput {
            draft: staged.snapshot.draft,
        });
        if collision {
            assert!(matches!(result, Err(ConfigurationFailure::InvalidInput)));
        } else {
            assert!(matches!(
                result,
                Err(ConfigurationFailure::UnsupportedSchema)
            ));
        }
        assert_eq!(state.borrow().effects, 0);
    }
}
