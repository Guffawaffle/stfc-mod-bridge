use super::{ProjectionError, unique};
use bridge_contracts::v1::{
    BoundedList, CapabilityProjection, Completeness, DiagnosticContent, DiagnosticDisclosure,
    DiagnosticFact, EvidenceSource, GameClientBinding, Inventory, Observation, ObservationReason,
    ProfileBinding, ProjectionIssue, ProjectionIssueCode, ResolvedTarget, ResourceRef,
    RuntimeOwnership, SessionBinding, SessionProjection, SessionReadiness,
};

/// Keep a session's captured target. Contradictions become unknown rather than
/// being repaired from the current UI selection or a next-launch preference.
pub fn project_sessions(
    source: &Observation<Inventory<SessionProjection>>,
) -> Result<Observation<Inventory<SessionProjection>>, ProjectionError> {
    let Observation::Observed { value, evidence } = source else {
        return Ok(source.clone());
    };
    if (value.completeness == Completeness::Partial && value.issues.as_slice().is_empty())
        || !unique(value.items.as_slice(), |session| {
            session.binding.session_id.clone()
        })
    {
        return Err(ProjectionError::InvalidInventory);
    }
    let mut projected = value.clone();
    let mut issues = value.issues.as_slice().to_vec();
    let mut sessions = Vec::with_capacity(value.items.as_slice().len());
    for source_session in value.items.as_slice() {
        let mut session = source_session.clone();
        let mut conflicting = false;
        if !target_coherent(&session) {
            session.target = conflicting_observation(&session.target);
            session.readiness = conflicting_observation(&session.readiness);
            conflicting = true;
        } else if !readiness_coherent(&session) {
            session.readiness = conflicting_observation(&session.readiness);
            conflicting = true;
        }
        if let Observation::Observed { evidence, .. } = &session.live_identity
            && evidence.source != EvidenceSource::NativeLive
        {
            session.live_identity = conflicting_observation(&session.live_identity);
            conflicting = true;
        }
        if conflicting {
            let issue = ProjectionIssue {
                code: ProjectionIssueCode::ConflictingIdentity,
                resource: Some(ResourceRef::Session {
                    id: session.binding.session_id.clone(),
                }),
            };
            if !issues.contains(&issue) {
                issues.push(issue);
            }
            projected.completeness = Completeness::Partial;
        }
        sessions.push(session);
    }
    projected.items = BoundedList::new(sessions).map_err(|_| ProjectionError::InvalidInventory)?;
    projected.issues = BoundedList::new(issues).map_err(|_| ProjectionError::TooManyIssues)?;
    Ok(Observation::Observed {
        value: projected,
        evidence: evidence.clone(),
    })
}

pub(crate) fn session_coherent(session: &SessionProjection) -> bool {
    target_coherent(session) && readiness_coherent(session)
}

fn target_coherent(session: &SessionProjection) -> bool {
    match &session.target {
        Observation::Observed { value, .. } => {
            value.installation.physical_id() == &session.binding.process.installation_physical_id
        }
        _ => true,
    }
}

fn readiness_coherent(session: &SessionProjection) -> bool {
    match (&session.target, &session.readiness) {
        (
            Observation::Observed { value: target, .. },
            Observation::Observed {
                value: readiness, ..
            },
        ) => {
            matches!(target.profile, ProfileBinding::Ordinary { .. })
                == (*readiness == SessionReadiness::OrdinarySpawned)
        }
        _ => true,
    }
}

fn conflicting_observation<T: Clone>(observation: &Observation<T>) -> Observation<T> {
    match observation {
        Observation::Observed { evidence, .. }
        | Observation::Missing { evidence }
        | Observation::Unknown { evidence, .. } => Observation::Unknown {
            reason: ObservationReason::ConflictingEvidence,
            evidence: evidence.clone(),
        },
        Observation::Unavailable { reason } => Observation::Unavailable { reason: *reason },
    }
}

/// Redacted facts retain each observation's provenance. An incompatible
/// observation cannot be relabeled with the requested target. No path disclosure
/// or preview digest is inferred here; those require their separate explicit flow.
pub fn project_diagnostic_content(
    target: &ResolvedTarget,
    runtime: &Observation<RuntimeOwnership>,
    session: &Observation<SessionBinding>,
    game: &Observation<GameClientBinding>,
    capabilities: &Inventory<CapabilityProjection>,
) -> Result<DiagnosticContent, ProjectionError> {
    if (capabilities.completeness == Completeness::Partial
        && capabilities.issues.as_slice().is_empty())
        || !unique(capabilities.items.as_slice(), |capability| {
            capability.id.clone()
        })
    {
        return Err(ProjectionError::InvalidInventory);
    }
    let runtime = match runtime {
        Observation::Observed {
            value: RuntimeOwnership::Managed { reference },
            ..
        } if &reference.target != target => conflicting_observation(runtime),
        _ => runtime.clone(),
    };
    let session = match session {
        Observation::Observed { value, .. }
            if &value.process.installation_physical_id != target.installation.physical_id() =>
        {
            conflicting_observation(session)
        }
        _ => session.clone(),
    };
    let mut facts = vec![
        DiagnosticFact::Target {
            value: target.clone(),
        },
        DiagnosticFact::Runtime { value: runtime },
        DiagnosticFact::Session { value: session },
        DiagnosticFact::Game {
            value: game.clone(),
        },
    ];
    facts.extend(
        capabilities
            .items
            .as_slice()
            .iter()
            .cloned()
            .map(|value| DiagnosticFact::Capability { value }),
    );
    facts.extend(
        capabilities
            .issues
            .as_slice()
            .iter()
            .cloned()
            .map(|value| DiagnosticFact::Issue { value }),
    );
    Ok(DiagnosticContent {
        target: target.clone(),
        disclosure: DiagnosticDisclosure::Redacted,
        facts: BoundedList::new(facts).map_err(|_| ProjectionError::TooManyFacts)?,
        paths: None,
    })
}
