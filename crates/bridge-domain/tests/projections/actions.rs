use super::*;

#[test]
fn declared_runtime_support_cannot_supply_a_missing_or_self_described_native_route() {
    let facts = action_facts(false);
    let scope = ActionScope::Target {
        target: target(false),
    };
    assert!(
        matches!(action(ActionId::LaunchOrdinary, scope.clone(), &facts, LaunchPolicy::default(), vec![]), ActionAvailability::Unavailable { reason } if reason.code == AvailabilityReasonCode::UnavailableNativeRoute)
    );
    let mut claimed = route(ActionId::LaunchOrdinary, &scope);
    claimed.support = described(true);
    assert!(matches!(
        action(
            ActionId::LaunchOrdinary,
            scope,
            &facts,
            LaunchPolicy::default(),
            vec![claimed]
        ),
        ActionAvailability::Unknown { .. }
    ));
}

#[test]
fn known_absent_runtime_can_allow_unmodded_ordinary_without_granting_features() {
    let mut facts = action_facts(false);
    facts.runtime = observed(RuntimeOwnership::Absent);
    facts.capability_runtime = unavailable();
    facts.capabilities = inventory(vec![]);
    let scope = ActionScope::Target {
        target: target(false),
    };
    let availability = action(
        ActionId::LaunchOrdinary,
        scope.clone(),
        &facts,
        LaunchPolicy {
            allow_unmodded_ordinary: true,
            ..LaunchPolicy::default()
        },
        vec![route(ActionId::LaunchOrdinary, &scope)],
    );
    assert_eq!(
        availability,
        ActionAvailability::Available {
            revision: revision("actions-1"),
            grants_lock: FalseFlag,
            grants_permission: FalseFlag
        }
    );
    assert!(facts.capabilities.items.as_slice().is_empty());
    assert!(!matches!(
        default_launch(ActionId::LaunchOrdinary, &facts),
        ActionAvailability::Available { .. }
    ));
}

#[test]
fn unknown_runtime_is_not_absence_even_with_unrecognized_per_attempt_consent() {
    let mut facts = action_facts(false);
    facts.runtime = Observation::Unknown {
        reason: ObservationReason::AccessDenied,
        evidence: evidence(EvidenceSource::NativeLive),
    };
    facts.ordinary_runtime_choice = UnrecognizedRuntimeChoice::AllowOnce;
    let scope = ActionScope::Target {
        target: target(false),
    };
    assert!(matches!(
        action(
            ActionId::LaunchOrdinary,
            scope.clone(),
            &facts,
            LaunchPolicy {
                allow_unmodded_ordinary: true,
                allow_unrecognized_ordinary: true
            },
            vec![route(ActionId::LaunchOrdinary, &scope)]
        ),
        ActionAvailability::Unknown { .. }
    ));
}

#[test]
fn unmanaged_ordinary_launch_requires_both_policy_and_exact_per_attempt_choice() {
    let mut facts = action_facts(false);
    facts.runtime = observed(RuntimeOwnership::Unmanaged {
        artifact_digest: digest('d'),
    });
    let scope = ActionScope::Target {
        target: target(false),
    };
    for allow in [false, true] {
        for choice in [
            UnrecognizedRuntimeChoice::Reject,
            UnrecognizedRuntimeChoice::AllowOnce,
        ] {
            facts.ordinary_runtime_choice = choice;
            let availability = action(
                ActionId::LaunchOrdinary,
                scope.clone(),
                &facts,
                LaunchPolicy {
                    allow_unrecognized_ordinary: allow,
                    ..LaunchPolicy::default()
                },
                vec![route(ActionId::LaunchOrdinary, &scope)],
            );
            assert_eq!(
                matches!(availability, ActionAvailability::Available { .. }),
                allow && choice == UnrecognizedRuntimeChoice::AllowOnce
            );
        }
    }
}

#[test]
fn managed_features_for_another_runtime_or_target_cannot_enable_launch() {
    let mut facts = action_facts(false);
    let mut other_binding = runtime();
    other_binding.artifact_digest = digest('d');
    facts.capability_runtime = observed(other_binding);
    assert!(blocked_with(
        &default_launch(ActionId::LaunchOrdinary, &facts),
        AvailabilityReasonCode::StaleRevision
    ));
    let mut facts = action_facts(false);
    if let Observation::Observed {
        value: RuntimeOwnership::Managed { reference },
        ..
    } = &mut facts.runtime
    {
        reference.target = target(true);
    }
    assert!(blocked_with(
        &default_launch(ActionId::LaunchOrdinary, &facts),
        AvailabilityReasonCode::WrongOwner
    ));
}

#[test]
fn a_changed_selection_does_not_retarget_observed_scope_or_route_support() {
    let facts = action_facts(false);
    let mut changed_target = target(false);
    if let InstallationBinding::Registered {
        physical_id,
        native_target_ref,
        ..
    } = &mut changed_target.installation
    {
        *physical_id = PhysicalInstallationId::new("physical-installation-b").unwrap();
        *native_target_ref = NativeTargetRef::new("native-target-b").unwrap();
    }
    let scope = ActionScope::Target {
        target: changed_target,
    };
    assert!(blocked_with(
        &action(
            ActionId::LaunchOrdinary,
            scope.clone(),
            &facts,
            LaunchPolicy::default(),
            vec![route(ActionId::LaunchOrdinary, &scope)]
        ),
        AvailabilityReasonCode::StaleRevision
    ));
}

#[test]
fn renamed_profile_and_next_launch_preference_do_not_rewrite_current_target() {
    let mut facts = action_facts(true);
    let original_scope = facts.scope.clone();
    let original_runtime = facts.runtime.clone();
    let expected = default_launch(ActionId::LaunchIsolated, &facts);
    if let Observation::Observed {
        value:
            ProfileProjection::Isolated {
                name,
                preferred_installation,
                ..
            },
        ..
    } = &mut facts.profile
    {
        *name = DisplayName::new("Different account-shaped label 目录").unwrap();
        *preferred_installation =
            Some(InstallationId::new("dddddddddddddddddddddddddddddddd").unwrap());
    }
    assert_eq!(default_launch(ActionId::LaunchIsolated, &facts), expected);
    assert_eq!(facts.scope, original_scope);
    assert_eq!(facts.runtime, original_runtime);
}

#[test]
fn incomplete_unknown_or_historical_session_inventory_cannot_mean_stopped() {
    let mut variants = vec![];
    variants.push(Observation::Unknown {
        reason: ObservationReason::AccessDenied,
        evidence: evidence(EvidenceSource::NativeLive),
    });
    let mut partial = inventory(vec![]);
    partial.completeness = Completeness::Partial;
    partial.issues = BoundedList::new(vec![ProjectionIssue {
        code: ProjectionIssueCode::IncompleteCatalog,
        resource: None,
    }])
    .unwrap();
    variants.push(observed(partial));
    variants.push(Observation::Observed {
        value: inventory(vec![]),
        evidence: evidence(EvidenceSource::SessionReceipt),
    });
    for sessions in variants {
        let mut facts = action_facts(false);
        facts.sessions = sessions;
        assert!(matches!(
            default_launch(ActionId::LaunchOrdinary, &facts),
            ActionAvailability::Unknown { .. }
        ));
    }
}

#[test]
fn active_session_blocks_its_physical_installation_even_when_profile_preferences_change() {
    let mut facts = action_facts(false);
    let active = session(true, 1);
    facts.sessions = observed(inventory(vec![active]));
    if let Observation::Observed {
        value:
            ProfileProjection::Ordinary {
                preferred_installation,
                ..
            },
        ..
    } = &mut facts.profile
    {
        *preferred_installation =
            Some(InstallationId::new("dddddddddddddddddddddddddddddddd").unwrap());
    }
    assert!(blocked_with(
        &default_launch(ActionId::LaunchOrdinary, &facts),
        AvailabilityReasonCode::ActiveSession
    ));
}

#[test]
fn live_observed_exit_and_unrelated_installation_are_distinct_from_unknown_identity() {
    let mut facts = action_facts(false);
    let mut exited = session(false, 1);
    exited.live_identity = observed(false);
    let mut unrelated = session(false, 2);
    unrelated.binding.process.installation_physical_id =
        PhysicalInstallationId::new("other-physical-installation").unwrap();
    unrelated.target = Observation::Unknown {
        reason: ObservationReason::UnrecognizedIdentity,
        evidence: evidence(EvidenceSource::NativeLive),
    };
    facts.sessions = observed(inventory(vec![exited.clone(), unrelated]));
    assert!(matches!(
        default_launch(ActionId::LaunchOrdinary, &facts),
        ActionAvailability::Available { .. }
    ));
    exited.live_identity = Observation::Unknown {
        reason: ObservationReason::AccessDenied,
        evidence: evidence(EvidenceSource::NativeLive),
    };
    facts.sessions = observed(inventory(vec![exited]));
    assert!(matches!(
        default_launch(ActionId::LaunchOrdinary, &facts),
        ActionAvailability::Unknown { .. }
    ));
}

#[test]
fn conflicting_session_installation_identity_cannot_be_filtered_into_launch_availability() {
    let mut facts = action_facts(false);
    assert!(matches!(
        default_launch(ActionId::LaunchOrdinary, &facts),
        ActionAvailability::Available { .. }
    ));
    let captured_scope = facts.scope.clone();
    let mut conflicting = session(false, 1);
    // Its target still identifies the requested installation. Trusting only the
    // contradictory process binding would filter this live session out.
    conflicting.binding.process.installation_physical_id =
        PhysicalInstallationId::new("other-physical-installation").unwrap();
    facts.sessions = observed(inventory(vec![conflicting]));
    assert!(matches!(
        default_launch(ActionId::LaunchOrdinary, &facts),
        ActionAvailability::Unknown { .. }
    ));
    facts.sessions = project_sessions(&facts.sessions).unwrap();
    assert!(matches!(
        default_launch(ActionId::LaunchOrdinary, &facts),
        ActionAvailability::Unknown { .. }
    ));
    assert_eq!(facts.scope, captured_scope);
}

#[test]
fn conflicting_session_readiness_cannot_mean_an_unrelated_installation_is_safe() {
    for is_live in [false, true] {
        let mut unrelated = session(false, 1);
        unrelated.binding.process.installation_physical_id =
            PhysicalInstallationId::new("other-physical-installation").unwrap();
        if let Observation::Observed { value: target, .. } = &mut unrelated.target
            && let InstallationBinding::Registered { physical_id, .. } = &mut target.installation
        {
            *physical_id = PhysicalInstallationId::new("other-physical-installation").unwrap();
        }
        unrelated.live_identity = observed(is_live);
        unrelated.readiness = observed(SessionReadiness::IsolatedReady);
        let mut facts = action_facts(false);
        facts.sessions = observed(inventory(vec![unrelated.clone()]));
        assert!(matches!(
            default_launch(ActionId::LaunchOrdinary, &facts),
            ActionAvailability::Unknown { .. }
        ));
        // Live/exited rows that coherently describe another physical installation
        // still allow the requested launch; contradictions alone cause Unknown.
        unrelated.readiness = observed(SessionReadiness::OrdinarySpawned);
        facts.sessions = observed(inventory(vec![unrelated]));
        assert!(matches!(
            default_launch(ActionId::LaunchOrdinary, &facts),
            ActionAvailability::Available { .. }
        ));
    }
}

#[test]
fn busy_recovery_and_historical_false_flags_never_become_ready() {
    let mut facts = action_facts(false);
    facts.busy = observed(true);
    assert!(blocked_with(
        &default_launch(ActionId::LaunchOrdinary, &facts),
        AvailabilityReasonCode::Busy
    ));
    facts.busy = observed(false);
    facts.recovery_required = observed(true);
    assert!(blocked_with(
        &default_launch(ActionId::LaunchOrdinary, &facts),
        AvailabilityReasonCode::InterruptedTransaction
    ));
    facts.recovery_required = Observation::Observed {
        value: false,
        evidence: evidence(EvidenceSource::CatalogMetadata),
    };
    assert!(matches!(
        default_launch(ActionId::LaunchOrdinary, &facts),
        ActionAvailability::Unknown { .. }
    ));
}

#[test]
fn isolated_launch_requires_active_matching_profile_and_known_established_store_state() {
    let mut facts = action_facts(true);
    assert!(matches!(
        default_launch(ActionId::LaunchIsolated, &facts),
        ActionAvailability::Available { .. }
    ));
    if let Observation::Observed {
        value: ProfileProjection::Isolated { reference, .. },
        ..
    } = &mut facts.profile
    {
        reference.state = ProfileState::Archived;
    }
    assert!(blocked_with(
        &default_launch(ActionId::LaunchIsolated, &facts),
        AvailabilityReasonCode::ArchivedProfile
    ));
    for store in [
        observed(IsolatedStoreState::MissingEstablished),
        observed(IsolatedStoreState::Interrupted),
        Observation::Unknown {
            reason: ObservationReason::AccessDenied,
            evidence: evidence(EvidenceSource::NativeLive),
        },
    ] {
        let mut facts = action_facts(true);
        if let Observation::Observed {
            value: ProfileProjection::Isolated { store: state, .. },
            ..
        } = &mut facts.profile
        {
            *state = store;
        }
        assert!(!matches!(
            default_launch(ActionId::LaunchIsolated, &facts),
            ActionAvailability::Available { .. }
        ));
    }
}

#[test]
fn historical_new_isolated_store_state_cannot_enable_current_launch() {
    assert_store_readiness_requires_live_observation(IsolatedStoreState::New);
}

#[test]
fn historical_established_isolated_store_state_cannot_enable_current_launch() {
    assert_store_readiness_requires_live_observation(IsolatedStoreState::Established);
}

fn assert_store_readiness_requires_live_observation(state: IsolatedStoreState) {
    for source in [
        EvidenceSource::SessionReceipt,
        EvidenceSource::CatalogMetadata,
        EvidenceSource::DiskFileHash,
        EvidenceSource::ArtifactSelfDescription,
        EvidenceSource::VerifiedRelease,
    ] {
        let mut facts = action_facts(true);
        let captured_scope = facts.scope.clone();
        let captured_revision = facts.revision.clone();
        let Observation::Observed {
            value: ProfileProjection::Isolated { store, .. },
            ..
        } = &mut facts.profile
        else {
            panic!("test requires the exact active isolated profile")
        };
        *store = Observation::Observed {
            value: state,
            evidence: evidence(source),
        };
        assert!(
            matches!(default_launch(ActionId::LaunchIsolated, &facts), ActionAvailability::Unknown { reason } if reason.code == AvailabilityReasonCode::UnknownIdentity)
        );
        assert_eq!(facts.scope, captured_scope);
        assert_eq!(facts.revision, captured_revision);
        let Observation::Observed {
            value: ProfileProjection::Isolated { store, .. },
            ..
        } = &mut facts.profile
        else {
            unreachable!()
        };
        *store = observed(state);
        assert!(matches!(
            default_launch(ActionId::LaunchIsolated, &facts),
            ActionAvailability::Available { .. }
        ));
    }
}

#[test]
fn wrong_profile_kind_and_absent_runtime_do_not_grant_isolation() {
    assert!(blocked_with(
        &default_launch(ActionId::LaunchIsolated, &action_facts(false)),
        AvailabilityReasonCode::WrongProfileKind
    ));
    let mut facts = action_facts(true);
    facts.runtime = observed(RuntimeOwnership::Absent);
    assert!(
        matches!(default_launch(ActionId::LaunchIsolated, &facts), ActionAvailability::Unavailable { reason } if reason.code == AvailabilityReasonCode::UnqualifiedIsolation)
    );
}

#[test]
fn focus_selects_exact_session_instead_of_first_process_or_profile_preference() {
    let first = session(false, 1);
    let captured = session(true, 2);
    let scope = ActionScope::Session {
        session: captured.binding.clone(),
    };
    let mut facts = action_facts(true);
    facts.scope = observed(scope.clone());
    facts.sessions = observed(inventory(vec![first, captured.clone()]));
    let before = facts.clone();
    assert!(matches!(
        action(
            ActionId::FocusSession,
            scope.clone(),
            &facts,
            LaunchPolicy::default(),
            vec![route(ActionId::FocusSession, &scope)]
        ),
        ActionAvailability::Available { .. }
    ));
    assert_eq!(facts, before);
    for altered_part in 0..4 {
        let mut requested = captured.binding.clone();
        match altered_part {
            0 => requested.process.pid = Pid::new(9000).unwrap(),
            1 => {
                requested.process.start_identity = ProcessStartIdentity::Windows(
                    ProcessGeneration::new("different-generation").unwrap(),
                )
            }
            2 => {
                requested.process.executable_identity =
                    ExecutableIdentity::new("different-executable").unwrap()
            }
            _ => requested.revision = revision("session-2"),
        }
        let changed = ActionScope::Session { session: requested };
        facts.scope = observed(changed.clone());
        assert!(blocked_with(
            &action(
                ActionId::FocusSession,
                changed.clone(),
                &facts,
                LaunchPolicy::default(),
                vec![route(ActionId::FocusSession, &changed)]
            ),
            AvailabilityReasonCode::StaleRevision
        ));
    }
}

#[test]
fn focus_requires_live_identity_and_cannot_use_a_historical_ready_receipt() {
    for live in [
        Observation::Unknown {
            reason: ObservationReason::AccessDenied,
            evidence: evidence(EvidenceSource::NativeLive),
        },
        Observation::Observed {
            value: true,
            evidence: evidence(EvidenceSource::SessionReceipt),
        },
        observed(false),
    ] {
        let mut selected = session(true, 1);
        selected.live_identity = live;
        let scope = ActionScope::Session {
            session: selected.binding.clone(),
        };
        let mut facts = action_facts(true);
        facts.scope = observed(scope.clone());
        facts.sessions = observed(inventory(vec![selected]));
        assert!(!matches!(
            action(
                ActionId::FocusSession,
                scope.clone(),
                &facts,
                LaunchPolicy::default(),
                vec![route(ActionId::FocusSession, &scope)]
            ),
            ActionAvailability::Available { .. }
        ));
    }
}

#[test]
fn conflicting_native_route_observations_are_not_resolved_by_enumeration_order() {
    let facts = action_facts(false);
    let scope = ActionScope::Target {
        target: target(false),
    };
    let accepted = route(ActionId::LaunchOrdinary, &scope);
    let mut denied = accepted.clone();
    denied.support = observed(false);
    for routes in [
        vec![accepted.clone(), denied.clone()],
        vec![denied, accepted],
    ] {
        assert!(matches!(
            action(
                ActionId::LaunchOrdinary,
                scope.clone(),
                &facts,
                LaunchPolicy::default(),
                routes
            ),
            ActionAvailability::Unknown { .. }
        ));
    }
}

#[test]
fn unimplemented_action_families_stay_unavailable_despite_claimed_native_routes() {
    let actions = vec![
        ActionId::CreateProfile,
        ActionId::EditOrdinaryProfile,
        ActionId::EditIsolatedProfile,
        ActionId::ArchiveProfile,
        ActionId::RestoreProfile,
        ActionId::DeleteProfile,
        ActionId::RegisterInstallation,
        ActionId::EditInstallation,
        ActionId::SaveConfiguration,
        ActionId::RestoreConfiguration,
        ActionId::RuntimeInstall,
        ActionId::RuntimeUpdate,
        ActionId::RuntimeRepair,
        ActionId::RuntimeAdopt,
        ActionId::RuntimeRemove,
        ActionId::RuntimeStopManaging,
        ActionId::RuntimeSwitchSource,
        ActionId::GameUpdate,
        ActionId::RecoverGameUpdate,
        ActionId::BridgeUpdate,
        ActionId::RecoverBridgeUpdate,
        ActionId::ExportDiagnostics,
        ActionId::SaveApplicationPreferences,
    ];
    let scope = ActionScope::Target {
        target: target(false),
    };
    let facts = action_facts(false);
    let routes = actions
        .iter()
        .map(|action| route(*action, &scope))
        .collect::<Vec<_>>();
    let output = project_actions(
        &GetActionsInput {
            scope,
            actions: BoundedList::new(actions).unwrap(),
        },
        &facts,
        &LaunchPolicy::default(),
        &routes,
    )
    .unwrap();
    assert_eq!(output.as_slice().len(), 23);
    assert!(output.as_slice().iter().all(|action| matches!(&action.availability, ActionAvailability::Unavailable { reason } if reason.code == AvailabilityReasonCode::UnavailableNativeRoute)));
}

#[test]
fn partial_or_foreign_capability_evidence_never_enables_managed_runtime_launch() {
    let mut facts = action_facts(false);
    facts.capabilities.completeness = Completeness::Partial;
    facts.capabilities.issues = BoundedList::new(vec![ProjectionIssue {
        code: ProjectionIssueCode::InvalidMetadata,
        resource: None,
    }])
    .unwrap();
    assert!(matches!(
        default_launch(ActionId::LaunchOrdinary, &facts),
        ActionAvailability::Unknown { .. }
    ));
    let mut facts = action_facts(false);
    let mut projected = facts.capabilities.items.as_slice().to_vec();
    projected[0].status = CapabilityStatus::Supported {
        evidence: evidence(EvidenceSource::SessionReceipt),
    };
    facts.capabilities.items = BoundedList::new(projected).unwrap();
    assert!(matches!(
        default_launch(ActionId::LaunchOrdinary, &facts),
        ActionAvailability::Unknown { .. }
    ));
}

#[test]
fn duplicate_requested_actions_do_not_emit_ambiguous_action_inventory() {
    let scope = ActionScope::Target {
        target: target(false),
    };
    let request = GetActionsInput {
        scope,
        actions: BoundedList::new(vec![ActionId::LaunchOrdinary; 2]).unwrap(),
    };
    assert_eq!(
        project_actions(
            &request,
            &action_facts(false),
            &LaunchPolicy::default(),
            &[]
        ),
        Err(ProjectionError::DuplicateAction)
    );
}

#[test]
fn historical_scope_and_runtime_receipts_cannot_enable_current_launch() {
    for scope_is_historical in [false, true] {
        let mut facts = action_facts(false);
        if scope_is_historical {
            if let Observation::Observed { evidence, .. } = &mut facts.scope {
                evidence.source = EvidenceSource::SessionReceipt;
            }
        } else if let Observation::Observed { evidence, .. } = &mut facts.runtime {
            evidence.source = EvidenceSource::SessionReceipt;
        }
        assert!(matches!(
            default_launch(ActionId::LaunchOrdinary, &facts),
            ActionAvailability::Unknown { .. }
        ));
    }
}
