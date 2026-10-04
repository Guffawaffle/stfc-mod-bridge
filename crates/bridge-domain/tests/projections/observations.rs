use super::*;

#[test]
fn current_session_projection_preserves_exact_capture_and_evidence_without_preference_input() {
    let source = observed(inventory(vec![session(true, 1), session(false, 2)]));
    assert_eq!(project_sessions(&source).unwrap(), source);
}

#[test]
fn conflicting_physical_target_becomes_unknown_without_relabeling_the_session() {
    let mut captured = session(true, 1);
    let before = captured.binding.clone();
    if let Observation::Observed { value, .. } = &mut captured.target
        && let InstallationBinding::Registered { physical_id, .. } = &mut value.installation
    {
        *physical_id = PhysicalInstallationId::new("other-physical-installation").unwrap();
    }
    let output = project_sessions(&observed(inventory(vec![captured]))).unwrap();
    let Observation::Observed { value, .. } = output else {
        panic!("known partial inventory")
    };
    assert_eq!(value.completeness, Completeness::Partial);
    assert_eq!(value.items.as_slice()[0].binding, before);
    assert_eq!(
        value.items.as_slice()[0].target,
        Observation::Unknown {
            reason: ObservationReason::ConflictingEvidence,
            evidence: evidence(EvidenceSource::NativeLive)
        }
    );
    assert!(matches!(
        value.items.as_slice()[0].readiness,
        Observation::Unknown {
            reason: ObservationReason::ConflictingEvidence,
            ..
        }
    ));
    assert_eq!(
        value.issues.as_slice()[0].resource,
        Some(ResourceRef::Session {
            id: before.session_id
        })
    );
}

#[test]
fn isolation_readiness_cannot_change_an_ordinary_captured_profile() {
    let mut captured = session(false, 1);
    let target_before = captured.target.clone();
    captured.readiness = observed(SessionReadiness::IsolatedReady);
    let Observation::Observed { value, .. } =
        project_sessions(&observed(inventory(vec![captured]))).unwrap()
    else {
        panic!("known partial inventory")
    };
    assert_eq!(value.items.as_slice()[0].target, target_before);
    assert!(matches!(
        value.items.as_slice()[0].readiness,
        Observation::Unknown {
            reason: ObservationReason::ConflictingEvidence,
            ..
        }
    ));
}

#[test]
fn unknown_session_facts_and_unavailable_inventory_are_never_successful_empty_defaults() {
    let mut captured = session(true, 1);
    captured.target = Observation::Unknown {
        reason: ObservationReason::UnrecognizedIdentity,
        evidence: evidence(EvidenceSource::SessionReceipt),
    };
    captured.live_identity = Observation::Unknown {
        reason: ObservationReason::AccessDenied,
        evidence: evidence(EvidenceSource::NativeLive),
    };
    captured.readiness = unavailable();
    let source = observed(inventory(vec![captured]));
    assert_eq!(project_sessions(&source).unwrap(), source);
    let source: Observation<Inventory<SessionProjection>> = unavailable();
    assert_eq!(project_sessions(&source).unwrap(), source);
}

#[test]
fn historical_live_or_exited_receipt_remains_unknown_to_current_process_projection() {
    for was_live in [false, true] {
        let mut captured = session(true, 1);
        captured.live_identity = Observation::Observed {
            value: was_live,
            evidence: evidence(EvidenceSource::SessionReceipt),
        };
        let Observation::Observed { value, .. } =
            project_sessions(&observed(inventory(vec![captured]))).unwrap()
        else {
            panic!("known partial inventory")
        };
        assert_eq!(
            value.items.as_slice()[0].live_identity,
            Observation::Unknown {
                reason: ObservationReason::ConflictingEvidence,
                evidence: evidence(EvidenceSource::SessionReceipt)
            }
        );
        assert_eq!(value.completeness, Completeness::Partial);
    }
}

#[test]
fn duplicate_sessions_and_partial_inventory_without_reason_are_refused() {
    let captured = session(true, 1);
    assert_eq!(
        project_sessions(&observed(inventory(vec![captured.clone(), captured]))),
        Err(ProjectionError::InvalidInventory)
    );
    let mut partial: Inventory<SessionProjection> = inventory(vec![]);
    partial.completeness = Completeness::Partial;
    assert_eq!(
        project_sessions(&observed(partial)),
        Err(ProjectionError::InvalidInventory)
    );
}

#[test]
fn redacted_diagnostics_preserve_conflicting_provenance_without_claiming_requested_identity() {
    let requested = target(false);
    let runtime = observed(managed(&target(true)));
    let mut selected = session(true, 1).binding;
    selected.process.installation_physical_id =
        PhysicalInstallationId::new("other-physical-installation").unwrap();
    let session = Observation::Observed {
        value: selected,
        evidence: evidence(EvidenceSource::SessionReceipt),
    };
    let content = project_diagnostic_content(
        &requested,
        &runtime,
        &session,
        &unavailable(),
        &capabilities(&capability_facts(), &[policy()]),
    )
    .unwrap();
    assert_eq!(content.target, requested);
    assert_eq!(content.disclosure, DiagnosticDisclosure::Redacted);
    assert_eq!(content.paths, None);
    assert!(
        matches!(&content.facts.as_slice()[1], DiagnosticFact::Runtime { value: Observation::Unknown { reason: ObservationReason::ConflictingEvidence, evidence: retained } } if retained == &evidence(EvidenceSource::NativeLive))
    );
    assert!(
        matches!(&content.facts.as_slice()[2], DiagnosticFact::Session { value: Observation::Unknown { reason: ObservationReason::ConflictingEvidence, evidence: retained } } if retained == &evidence(EvidenceSource::SessionReceipt))
    );
    assert!(matches!(
        &content.facts.as_slice()[3],
        DiagnosticFact::Game {
            value: Observation::Unavailable {
                reason: ObservationReason::NativeUnavailable
            }
        }
    ));
}

#[test]
fn diagnostic_fact_bound_refuses_overflow_instead_of_dropping_inconvenient_facts() {
    let capabilities = inventory(
        (0..61)
            .map(|index| CapabilityProjection {
                id: feature(&format!("feature.{index}")),
                status: CapabilityStatus::Unknown {
                    reason: AvailabilityReason {
                        code: AvailabilityReasonCode::UnknownIdentity,
                        resource: None,
                        remediation: None,
                    },
                },
            })
            .collect(),
    );
    assert_eq!(
        project_diagnostic_content(
            &target(false),
            &unavailable(),
            &unavailable(),
            &unavailable(),
            &capabilities
        ),
        Err(ProjectionError::TooManyFacts)
    );
}
