use super::{ProjectionError, observation_reason, reason, unique};
use bridge_contracts::v1::{
    AvailabilityReasonCode, BoundedList, CapabilityId, CapabilityProjection, CapabilityStatus,
    Completeness, DisplayName, DistributionId, Evidence, EvidenceSource, Inventory, Observation,
    OpaqueRevision, ProcessArchitecture, ProjectionIssue, ProjectionIssueCode, ProviderId,
    RuntimeBinding, RuntimeManifestObservation, SupportedPlatform,
};

pub const ORDINARY_RUNTIME_LAUNCH: &str = "runtime.ordinary_launch";
pub const ISOLATED_RUNTIME_LAUNCH: &str = "runtime.isolated_launch";
pub const CONFIGURATION_EDIT: &str = "configuration.edit";

/// Producer declarations, after the owning adapter has parsed the manifest.
/// This internal input is deliberately not a second transport or a verifier.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ProducerManifest {
    pub format_version: u32,
    pub runtime: RuntimeBinding,
    pub features: BoundedList<CapabilityId, 32>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ManifestAssessment {
    Recognized(Box<ProducerManifest>),
    Invalid,
    Unrecognized,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct RuntimeTarget {
    pub platform: SupportedPlatform,
    pub architecture: ProcessArchitecture,
}

/// Policy is selected by immutable IDs. Display names are presentation only.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ProviderPolicy {
    pub provider_id: ProviderId,
    pub distribution_id: DistributionId,
    pub display_name: DisplayName,
    pub manifest_format_version: u32,
    pub allowed_targets: BoundedList<RuntimeTarget, 4>,
    pub allowed_features: BoundedList<CapabilityId, 32>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RuntimeCapabilityFacts {
    pub runtime: Observation<RuntimeBinding>,
    pub manifest: Observation<ManifestAssessment>,
}

enum Assessment<'a> {
    Supported {
        manifest: &'a ProducerManifest,
        policy: &'a ProviderPolicy,
        evidence: &'a Evidence,
    },
    Unsupported(AvailabilityReasonCode),
    Unknown(AvailabilityReasonCode),
}

/// Reports declared support separately from operational availability.
/// Missing, invalid or unrecognized manifests cannot produce Supported.
pub fn project_capabilities(
    requested: &BoundedList<CapabilityId, 128>,
    revision: &OpaqueRevision,
    facts: &RuntimeCapabilityFacts,
    policies: &[ProviderPolicy],
) -> Result<Inventory<CapabilityProjection>, ProjectionError> {
    if !unique(requested.as_slice(), Clone::clone) {
        return Err(ProjectionError::DuplicateCapability);
    }
    let (assessment, issue) = assess(facts, policies);
    let items = requested
        .as_slice()
        .iter()
        .map(|id| CapabilityProjection {
            id: id.clone(),
            status: match &assessment {
                Assessment::Supported {
                    manifest,
                    policy,
                    evidence,
                } if manifest.features.as_slice().contains(id)
                    && policy.allowed_features.as_slice().contains(id) =>
                {
                    CapabilityStatus::Supported {
                        evidence: (*evidence).clone(),
                    }
                }
                Assessment::Supported { .. } => CapabilityStatus::Unsupported {
                    reason: reason(AvailabilityReasonCode::UnsupportedSchema),
                },
                Assessment::Unsupported(code) => CapabilityStatus::Unsupported {
                    reason: reason(*code),
                },
                Assessment::Unknown(code) => CapabilityStatus::Unknown {
                    reason: reason(*code),
                },
            },
        })
        .collect();
    Ok(Inventory {
        items: BoundedList::new(items).map_err(|_| ProjectionError::InvalidInventory)?,
        completeness: if issue.is_some() {
            Completeness::Partial
        } else {
            Completeness::Complete
        },
        issues: BoundedList::new(issue.into_iter().collect())
            .map_err(|_| ProjectionError::TooManyIssues)?,
        revision: revision.clone(),
    })
}

fn assess<'a>(
    facts: &'a RuntimeCapabilityFacts,
    policies: &'a [ProviderPolicy],
) -> (Assessment<'a>, Option<ProjectionIssue>) {
    let runtime = match &facts.runtime {
        Observation::Observed { value, .. } => value,
        Observation::Missing { .. } => {
            return (
                Assessment::Unsupported(AvailabilityReasonCode::UnrecognizedRuntime),
                None,
            );
        }
        Observation::Unknown { reason, .. } => {
            return (Assessment::Unknown(observation_reason(*reason)), None);
        }
        Observation::Unavailable { reason } => {
            return (Assessment::Unsupported(observation_reason(*reason)), None);
        }
    };
    let invalid = |code| {
        (
            Assessment::Unsupported(code),
            Some(ProjectionIssue {
                code: ProjectionIssueCode::InvalidMetadata,
                resource: None,
            }),
        )
    };
    let (manifest, evidence) = match &facts.manifest {
        Observation::Observed {
            value: ManifestAssessment::Recognized(manifest),
            evidence,
        } => (manifest.as_ref(), evidence),
        Observation::Observed {
            value: ManifestAssessment::Invalid,
            ..
        } => return invalid(AvailabilityReasonCode::UnrecognizedRuntime),
        Observation::Observed {
            value: ManifestAssessment::Unrecognized,
            ..
        } => return invalid(AvailabilityReasonCode::UnsupportedSchema),
        Observation::Missing { .. } => {
            return (
                Assessment::Unsupported(AvailabilityReasonCode::UnrecognizedRuntime),
                None,
            );
        }
        Observation::Unknown { reason, .. } => {
            return (Assessment::Unknown(observation_reason(*reason)), None);
        }
        Observation::Unavailable { reason } => {
            return (Assessment::Unsupported(observation_reason(*reason)), None);
        }
    };
    if !matches!(
        evidence.source,
        EvidenceSource::ArtifactSelfDescription | EvidenceSource::VerifiedRelease
    ) {
        // Hashing a path or reading a session receipt does not parse a manifest.
        return (
            Assessment::Unknown(AvailabilityReasonCode::UnknownIdentity),
            Some(ProjectionIssue {
                code: ProjectionIssueCode::StaleReceipt,
                resource: None,
            }),
        );
    }
    if runtime != &manifest.runtime
        || !matches!(
            runtime.manifest,
            RuntimeManifestObservation::Observed { .. }
        )
        || !unique(manifest.features.as_slice(), Clone::clone)
    {
        return invalid(AvailabilityReasonCode::UnrecognizedRuntime);
    }
    let mut matches = policies.iter().filter(|policy| {
        policy.provider_id == runtime.provider_id
            && policy.distribution_id == runtime.distribution_id
    });
    let Some(policy) = matches.next() else {
        return invalid(AvailabilityReasonCode::UnrecognizedRuntime);
    };
    if matches.next().is_some()
        || policy.manifest_format_version == 0
        || manifest.format_version != policy.manifest_format_version
        || !unique(policy.allowed_features.as_slice(), Clone::clone)
        || !unique(policy.allowed_targets.as_slice(), |target| *target)
    {
        return invalid(AvailabilityReasonCode::UnsupportedSchema);
    }
    if !policy.allowed_targets.as_slice().contains(&RuntimeTarget {
        platform: runtime.platform,
        architecture: runtime.architecture,
    }) {
        return (
            Assessment::Unsupported(AvailabilityReasonCode::UnsupportedPlatform),
            None,
        );
    }
    (
        Assessment::Supported {
            manifest,
            policy,
            evidence,
        },
        None,
    )
}
