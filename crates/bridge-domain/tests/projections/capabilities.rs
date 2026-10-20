use super::*;

#[test]
fn recognized_features_require_manifest_policy_and_preserve_declaration_provenance() {
    let projected = capabilities(&capability_facts(), &[policy()]);
    assert_eq!(projected.completeness, Completeness::Complete);
    assert!(projected.issues.as_slice().is_empty());
    for capability in projected.items.as_slice() {
        assert_eq!(
            capability.status,
            CapabilityStatus::Supported {
                evidence: evidence(EvidenceSource::ArtifactSelfDescription)
            }
        );
    }
}

#[test]
fn manifest_and_policy_feature_intersection_never_grants_an_omitted_feature() {
    let mut allowed = policy();
    allowed.allowed_features = BoundedList::new(vec![feature(ORDINARY_RUNTIME_LAUNCH)]).unwrap();
    let projected = capabilities(&capability_facts(), &[allowed]);
    assert!(matches!(
        projected.items.as_slice()[0].status,
        CapabilityStatus::Supported { .. }
    ));
    assert!(matches!(
        projected.items.as_slice()[1].status,
        CapabilityStatus::Unsupported { .. }
    ));
    let mut facts = capability_facts();
    if let Observation::Observed {
        value: ManifestAssessment::Recognized(manifest),
        ..
    } = &mut facts.manifest
    {
        manifest.features = BoundedList::new(vec![feature(CONFIGURATION_EDIT)]).unwrap();
    }
    let projected = capabilities(&facts, &[policy()]);
    assert!(matches!(
        projected.items.as_slice()[0].status,
        CapabilityStatus::Unsupported { .. }
    ));
    assert!(matches!(
        projected.items.as_slice()[2].status,
        CapabilityStatus::Supported { .. }
    ));
}

#[test]
fn provider_display_names_never_select_or_change_feature_policy() {
    let expected = capabilities(&capability_facts(), &[policy()]);
    for name in [
        "netniv",
        "guffawaffle",
        "Renamed 目录 mod",
        "Another provider",
    ] {
        let mut renamed = policy();
        renamed.display_name = DisplayName::new(name).unwrap();
        assert_eq!(capabilities(&capability_facts(), &[renamed]), expected);
    }
}

#[test]
fn copied_provider_name_cannot_authorize_an_unknown_provider_or_distribution() {
    for provider_changed in [false, true] {
        let mut claimed_policy = policy();
        if provider_changed {
            claimed_policy.provider_id = ProviderId::new("other-provider").unwrap();
        } else {
            claimed_policy.distribution_id = DistributionId::new("other.distribution").unwrap();
        }
        let projected = capabilities(&capability_facts(), &[claimed_policy]);
        assert!(no_support(&projected));
        assert_eq!(projected.completeness, Completeness::Partial);
        assert_eq!(
            projected.issues.as_slice()[0].code,
            ProjectionIssueCode::InvalidMetadata
        );
    }
}

#[test]
fn missing_invalid_and_unrecognized_manifests_do_not_grant_support() {
    for manifest in [
        Observation::Missing {
            evidence: evidence(EvidenceSource::ArtifactSelfDescription),
        },
        described(ManifestAssessment::Invalid),
        described(ManifestAssessment::Unrecognized),
    ] {
        let mut facts = capability_facts();
        facts.manifest = manifest;
        assert!(no_support(&capabilities(&facts, &[policy()])));
    }
}

#[test]
fn every_runtime_binding_identity_part_is_bound_to_the_manifest() {
    let mut mutations: Vec<RuntimeBinding> = vec![];
    let mut changed = runtime();
    changed.provider_id = ProviderId::new("other-provider").unwrap();
    mutations.push(changed);
    let mut changed = runtime();
    changed.distribution_id = DistributionId::new("other.distribution").unwrap();
    mutations.push(changed);
    let mut changed = runtime();
    changed.artifact_digest = digest('d');
    mutations.push(changed);
    let mut changed = runtime();
    changed.manifest = RuntimeManifestObservation::Observed {
        digest: digest('e'),
    };
    mutations.push(changed);
    let mut changed = runtime();
    changed.configuration_schema_digest = digest('f');
    mutations.push(changed);
    let mut changed = runtime();
    changed.client_revision = revision("client-271");
    mutations.push(changed);
    let mut changed = runtime();
    changed.platform = SupportedPlatform::Macos;
    mutations.push(changed);
    let mut changed = runtime();
    changed.architecture = ProcessArchitecture::Arm64;
    mutations.push(changed);
    for binding in mutations {
        let mut facts = capability_facts();
        if let Observation::Observed {
            value: ManifestAssessment::Recognized(manifest),
            ..
        } = &mut facts.manifest
        {
            manifest.runtime = binding;
        }
        assert!(no_support(&capabilities(&facts, &[policy()])));
    }
}

#[test]
fn a_recognized_parser_result_with_missing_runtime_manifest_is_not_support() {
    let mut facts = capability_facts();
    let mut binding = runtime();
    binding.manifest = RuntimeManifestObservation::Missing;
    facts.runtime = observed(binding.clone());
    if let Observation::Observed {
        value: ManifestAssessment::Recognized(manifest),
        ..
    } = &mut facts.manifest
    {
        manifest.runtime = binding;
    }
    assert!(no_support(&capabilities(&facts, &[policy()])));
}

#[test]
fn conflicting_policy_entries_and_duplicate_manifest_features_fail_closed() {
    assert!(no_support(&capabilities(
        &capability_facts(),
        &[policy(), policy()]
    )));
    let mut facts = capability_facts();
    if let Observation::Observed {
        value: ManifestAssessment::Recognized(manifest),
        ..
    } = &mut facts.manifest
    {
        manifest.features = BoundedList::new(vec![
            feature(ORDINARY_RUNTIME_LAUNCH),
            feature(ORDINARY_RUNTIME_LAUNCH),
        ])
        .unwrap();
    }
    assert!(no_support(&capabilities(&facts, &[policy()])));
}

#[test]
fn unsupported_manifest_version_and_malformed_policy_cannot_expand_support() {
    let mut changed = policy();
    changed.manifest_format_version = 2;
    assert!(no_support(&capabilities(&capability_facts(), &[changed])));
    let mut changed = policy();
    changed.manifest_format_version = 0;
    assert!(no_support(&capabilities(&capability_facts(), &[changed])));
    let mut changed = policy();
    changed.allowed_features = BoundedList::new(vec![
        feature(CONFIGURATION_EDIT),
        feature(CONFIGURATION_EDIT),
    ])
    .unwrap();
    assert!(no_support(&capabilities(&capability_facts(), &[changed])));
    let mut changed = policy();
    changed.allowed_targets =
        BoundedList::new(vec![changed.allowed_targets.as_slice()[0]; 2]).unwrap();
    assert!(no_support(&capabilities(&capability_facts(), &[changed])));
}

#[test]
fn file_hash_and_historical_receipt_do_not_prove_parsed_manifest_features() {
    for source in [
        EvidenceSource::DiskFileHash,
        EvidenceSource::SessionReceipt,
        EvidenceSource::CatalogMetadata,
        EvidenceSource::NativeLive,
    ] {
        let mut facts = capability_facts();
        if let Observation::Observed { evidence, .. } = &mut facts.manifest {
            evidence.source = source;
        }
        let projected = capabilities(&facts, &[policy()]);
        assert!(
            projected
                .items
                .as_slice()
                .iter()
                .all(|capability| matches!(capability.status, CapabilityStatus::Unknown { .. }))
        );
        assert_eq!(
            projected.issues.as_slice()[0].code,
            ProjectionIssueCode::StaleReceipt
        );
    }
}

#[test]
fn unknown_runtime_and_manifest_observations_remain_unknown() {
    for runtime_unknown in [false, true] {
        let mut facts = capability_facts();
        if runtime_unknown {
            facts.runtime = Observation::Unknown {
                reason: ObservationReason::ConflictingEvidence,
                evidence: evidence(EvidenceSource::DiskFileHash),
            };
        } else {
            facts.manifest = Observation::Unknown {
                reason: ObservationReason::AccessDenied,
                evidence: evidence(EvidenceSource::ArtifactSelfDescription),
            };
        }
        assert!(
            capabilities(&facts, &[policy()])
                .items
                .as_slice()
                .iter()
                .all(|capability| matches!(capability.status, CapabilityStatus::Unknown { .. }))
        );
    }
}

#[test]
fn declared_apple_silicon_support_is_distinct_from_excluded_intel_target() {
    for architecture in [ProcessArchitecture::Arm64, ProcessArchitecture::X86_64] {
        let mut binding = runtime();
        binding.platform = SupportedPlatform::Macos;
        binding.architecture = architecture;
        let mut facts = capability_facts();
        facts.runtime = observed(binding.clone());
        if let Observation::Observed {
            value: ManifestAssessment::Recognized(manifest),
            ..
        } = &mut facts.manifest
        {
            manifest.runtime = binding;
        }
        let projected = capabilities(&facts, &[policy()]);
        if architecture == ProcessArchitecture::Arm64 {
            assert!(!no_support(&projected));
        } else {
            assert!(projected.items.as_slice().iter().all(|capability| matches!(&capability.status, CapabilityStatus::Unsupported { reason } if reason.code == AvailabilityReasonCode::UnsupportedPlatform)));
        }
    }
}

#[test]
fn duplicate_requested_capability_ids_cannot_emit_an_invalid_inventory() {
    let requested = BoundedList::new(vec![feature(ORDINARY_RUNTIME_LAUNCH); 2]).unwrap();
    assert_eq!(
        project_capabilities(
            &requested,
            &revision("caps-1"),
            &capability_facts(),
            &[policy()]
        ),
        Err(ProjectionError::DuplicateCapability)
    );
}
