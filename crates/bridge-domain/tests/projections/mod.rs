mod actions;
mod capabilities;
mod observations;

use super::*;
use bridge_contracts::v1::*;

fn revision(value: &str) -> OpaqueRevision {
    OpaqueRevision::new(value).unwrap()
}

fn digest(value: char) -> Sha256 {
    Sha256::new(format!("sha256:{}", value.to_string().repeat(64))).unwrap()
}

fn evidence(source: EvidenceSource) -> Evidence {
    Evidence {
        observation_id: ObservationId::new("11111111-1111-4111-8111-111111111111").unwrap(),
        observed_at: UtcTimestamp::new("2026-10-03T20:00:00Z").unwrap(),
        source,
    }
}

fn observed<T>(value: T) -> Observation<T> {
    Observation::Observed {
        value,
        evidence: evidence(EvidenceSource::NativeLive),
    }
}

fn described<T>(value: T) -> Observation<T> {
    Observation::Observed {
        value,
        evidence: evidence(EvidenceSource::ArtifactSelfDescription),
    }
}

fn unavailable<T>() -> Observation<T> {
    Observation::Unavailable {
        reason: ObservationReason::NativeUnavailable,
    }
}

fn inventory<T>(items: Vec<T>) -> Inventory<T> {
    Inventory {
        items: BoundedList::new(items).unwrap(),
        completeness: Completeness::Complete,
        issues: BoundedList::new(vec![]).unwrap(),
        revision: revision("inventory-1"),
    }
}

fn feature(value: &str) -> CapabilityId {
    CapabilityId::new(value).unwrap()
}

fn requested_features() -> BoundedList<CapabilityId, 128> {
    BoundedList::new(vec![
        feature(ORDINARY_RUNTIME_LAUNCH),
        feature(ISOLATED_RUNTIME_LAUNCH),
        feature(CONFIGURATION_EDIT),
    ])
    .unwrap()
}

fn runtime() -> RuntimeBinding {
    RuntimeBinding {
        provider_id: ProviderId::new("guffawaffle").unwrap(),
        distribution_id: DistributionId::new("guffawaffle.stfc-community-mod").unwrap(),
        artifact_digest: digest('a'),
        manifest: RuntimeManifestObservation::Observed {
            digest: digest('b'),
        },
        configuration_schema_digest: digest('c'),
        client_revision: revision("client-270"),
        platform: SupportedPlatform::Windows,
        architecture: ProcessArchitecture::X86_64,
    }
}

fn policy() -> ProviderPolicy {
    ProviderPolicy {
        provider_id: runtime().provider_id,
        distribution_id: runtime().distribution_id,
        display_name: DisplayName::new("Community mod").unwrap(),
        manifest_format_version: 1,
        allowed_targets: BoundedList::new(vec![
            RuntimeTarget {
                platform: SupportedPlatform::Windows,
                architecture: ProcessArchitecture::X86_64,
            },
            RuntimeTarget {
                platform: SupportedPlatform::Macos,
                architecture: ProcessArchitecture::Arm64,
            },
        ])
        .unwrap(),
        allowed_features: BoundedList::new(requested_features().as_slice().to_vec()).unwrap(),
    }
}

fn capability_facts() -> RuntimeCapabilityFacts {
    RuntimeCapabilityFacts {
        runtime: observed(runtime()),
        manifest: described(ManifestAssessment::Recognized(Box::new(ProducerManifest {
            format_version: 1,
            runtime: runtime(),
            features: BoundedList::new(requested_features().as_slice().to_vec()).unwrap(),
        }))),
    }
}

fn capabilities(
    facts: &RuntimeCapabilityFacts,
    policies: &[ProviderPolicy],
) -> Inventory<CapabilityProjection> {
    project_capabilities(
        &requested_features(),
        &revision("capabilities-1"),
        facts,
        policies,
    )
    .unwrap()
}

fn target(isolated: bool) -> ResolvedTarget {
    ResolvedTarget {
        installation: InstallationBinding::Registered {
            registration_id: InstallationId::new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").unwrap(),
            registration_revision: revision("installation-1"),
            physical_id: PhysicalInstallationId::new("physical-installation-a").unwrap(),
            native_target_ref: NativeTargetRef::new("native-target-a").unwrap(),
        },
        profile: if isolated {
            ProfileBinding::Isolated {
                id: ProfileId::new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb").unwrap(),
                revision: revision("profile-1"),
            }
        } else {
            ProfileBinding::Ordinary {
                ordinary_id: None,
                owner_scope: OwnerScope::new("native-user-1").unwrap(),
            }
        },
    }
}

fn profile(isolated: bool) -> ProfileProjection {
    let preference = Some(InstallationId::new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").unwrap());
    if isolated {
        ProfileProjection::Isolated {
            reference: IsolatedProfileRef {
                id: ProfileId::new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb").unwrap(),
                revision: revision("profile-1"),
                state: ProfileState::Active,
            },
            name: DisplayName::new("Commander label is not identity").unwrap(),
            preferred_installation: preference,
            store: observed(IsolatedStoreState::Established),
        }
    } else {
        ProfileProjection::Ordinary {
            reference: OrdinaryProfileRef::Ordinary {
                catalog_id: ProfileId::new("cccccccccccccccccccccccccccccccc").unwrap(),
                revision: revision("ordinary-1"),
                owner_scope: OwnerScope::new("native-user-1").unwrap(),
            },
            preferred_installation: preference,
        }
    }
}

fn managed(target: &ResolvedTarget) -> RuntimeOwnership {
    RuntimeOwnership::Managed {
        reference: Box::new(ManagedRuntimeRef {
            receipt_id: RuntimeReceiptId::new("managed-runtime-1").unwrap(),
            revision: revision("managed-runtime-1"),
            target: target.clone(),
            binding: runtime(),
        }),
    }
}

fn action_facts(isolated: bool) -> ActionFacts {
    ActionFacts {
        revision: revision("actions-1"),
        scope: observed(ActionScope::Target {
            target: target(isolated),
        }),
        profile: observed(profile(isolated)),
        runtime: observed(managed(&target(isolated))),
        capability_runtime: observed(runtime()),
        capabilities: capabilities(&capability_facts(), &[policy()]),
        sessions: observed(inventory(vec![])),
        busy: observed(false),
        recovery_required: observed(false),
        ordinary_runtime_choice: UnrecognizedRuntimeChoice::Reject,
    }
}

fn route(action: ActionId, scope: &ActionScope) -> NativeRouteObservation {
    NativeRouteObservation {
        action,
        scope: scope.clone(),
        support: observed(true),
    }
}

fn action(
    action: ActionId,
    scope: ActionScope,
    facts: &ActionFacts,
    policy: LaunchPolicy,
    routes: Vec<NativeRouteObservation>,
) -> ActionAvailability {
    project_actions(
        &GetActionsInput {
            scope,
            actions: BoundedList::new(vec![action]).unwrap(),
        },
        facts,
        &policy,
        &routes,
    )
    .unwrap()
    .as_slice()[0]
        .availability
        .clone()
}

fn default_launch(action_id: ActionId, facts: &ActionFacts) -> ActionAvailability {
    let Observation::Observed { value: scope, .. } = &facts.scope else {
        panic!("test needs explicit scope")
    };
    action(
        action_id,
        scope.clone(),
        facts,
        LaunchPolicy::default(),
        vec![route(action_id, scope)],
    )
}

fn session(isolated: bool, sequence: u32) -> SessionProjection {
    SessionProjection {
        binding: SessionBinding {
            session_id: SessionId::new(format!("{sequence:08x}-1111-4111-8111-111111111111"))
                .unwrap(),
            revision: revision("session-1"),
            process: ProcessIdentity {
                pid: Pid::new(4200 + sequence).unwrap(),
                start_identity: ProcessStartIdentity::Windows(
                    ProcessGeneration::new("generation-1").unwrap(),
                ),
                executable_identity: ExecutableIdentity::new("game-executable-1").unwrap(),
                installation_physical_id: target(isolated).installation.physical_id().clone(),
                architecture: ProcessArchitecture::X86_64,
            },
        },
        target: observed(target(isolated)),
        live_identity: observed(true),
        readiness: observed(if isolated {
            SessionReadiness::IsolatedReady
        } else {
            SessionReadiness::OrdinarySpawned
        }),
    }
}

fn blocked_with(availability: &ActionAvailability, code: AvailabilityReasonCode) -> bool {
    matches!(availability, ActionAvailability::Blocked { reasons } if reasons.as_slice().iter().any(|reason| reason.code == code))
}

fn no_support(projections: &Inventory<CapabilityProjection>) -> bool {
    projections
        .items
        .as_slice()
        .iter()
        .all(|projection| !matches!(projection.status, CapabilityStatus::Supported { .. }))
}
