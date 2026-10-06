use super::{
    ISOLATED_RUNTIME_LAUNCH, ORDINARY_RUNTIME_LAUNCH, ProjectionError, observation_reason, reason,
    unique,
};
use bridge_contracts::v1::{
    ActionAvailability, ActionId, ActionProjection, ActionScope, AvailabilityReasonCode,
    BoundedList, CapabilityProjection, CapabilityStatus, Completeness, EvidenceSource, FalseFlag,
    GetActionsInput, Inventory, IsolatedStoreState, Observation, OpaqueRevision,
    OrdinaryProfileRef, ProfileBinding, ProfileProjection, ProfileState, ResolvedTarget,
    RuntimeBinding, RuntimeOwnership, SessionBinding, SessionProjection, UnrecognizedRuntimeChoice,
};

/// These facts belong to one exact observed scope. A saved next-launch
/// preference is intentionally not an input to an existing scope or session.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ActionFacts {
    pub revision: OpaqueRevision,
    pub scope: Observation<ActionScope>,
    pub profile: Observation<ProfileProjection>,
    pub runtime: Observation<RuntimeOwnership>,
    pub capability_runtime: Observation<RuntimeBinding>,
    pub capabilities: Inventory<CapabilityProjection>,
    pub sessions: Observation<Inventory<SessionProjection>>,
    pub busy: Observation<bool>,
    pub recovery_required: Observation<bool>,
    /// A per-attempt choice, never a persisted provider/profile preference.
    pub ordinary_runtime_choice: UnrecognizedRuntimeChoice,
}

/// An implemented native route is a separate observation from manifest support.
/// It still grants no lock, permission, runtime or release qualification.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct NativeRouteObservation {
    pub action: ActionId,
    pub scope: ActionScope,
    pub support: Observation<bool>,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct LaunchPolicy {
    pub allow_unmodded_ordinary: bool,
    pub allow_unrecognized_ordinary: bool,
}

pub fn project_actions(
    request: &GetActionsInput,
    facts: &ActionFacts,
    policy: &LaunchPolicy,
    native_routes: &[NativeRouteObservation],
) -> Result<BoundedList<ActionProjection, 64>, ProjectionError> {
    if !unique(request.actions.as_slice(), |action| *action) {
        return Err(ProjectionError::DuplicateAction);
    }
    BoundedList::new(
        request
            .actions
            .as_slice()
            .iter()
            .map(|action| ActionProjection {
                action: *action,
                availability: action_availability(
                    *action,
                    &request.scope,
                    facts,
                    policy,
                    native_routes,
                ),
            })
            .collect(),
    )
    .map_err(|_| ProjectionError::InvalidInventory)
}

fn action_availability(
    action: ActionId,
    scope: &ActionScope,
    facts: &ActionFacts,
    policy: &LaunchPolicy,
    native_routes: &[NativeRouteObservation],
) -> ActionAvailability {
    if !matches!(
        action,
        ActionId::LaunchOrdinary | ActionId::LaunchIsolated | ActionId::FocusSession
    ) {
        // Future operation families need their own complete semantic rules.
        return unavailable(AvailabilityReasonCode::UnavailableNativeRoute);
    }
    let check = || -> Result<(), ActionAvailability> {
        if !matches!(&facts.scope, Observation::Observed { evidence, .. } if evidence.source == EvidenceSource::NativeLive)
        {
            return match &facts.scope {
                Observation::Observed { .. } => {
                    Err(unknown(AvailabilityReasonCode::UnknownIdentity))
                }
                _ => observe(&facts.scope, AvailabilityReasonCode::MissingTarget).map(|_| ()),
            };
        }
        let observed_scope = observe(&facts.scope, AvailabilityReasonCode::MissingTarget)?;
        if observed_scope != scope {
            return Err(blocked(AvailabilityReasonCode::StaleRevision));
        }
        let mut routes = native_routes
            .iter()
            .filter(|route| route.action == action && &route.scope == scope);
        let route = routes
            .next()
            .ok_or_else(|| unavailable(AvailabilityReasonCode::UnavailableNativeRoute))?;
        if routes.next().is_some() {
            return Err(unknown(AvailabilityReasonCode::UnknownIdentity));
        }
        if !live_flag(
            &route.support,
            AvailabilityReasonCode::UnavailableNativeRoute,
        )? {
            return Err(unavailable(AvailabilityReasonCode::UnavailableNativeRoute));
        }
        match (action, scope) {
            (ActionId::FocusSession, ActionScope::Session { session }) => focus(session, facts),
            (
                ActionId::LaunchOrdinary | ActionId::LaunchIsolated,
                ActionScope::Target { target },
            ) => launch(action, target, facts, policy),
            _ => Err(blocked(AvailabilityReasonCode::MissingTarget)),
        }
    };
    match check() {
        Ok(()) => ActionAvailability::Available {
            revision: facts.revision.clone(),
            grants_lock: FalseFlag,
            grants_permission: FalseFlag,
        },
        Err(availability) => availability,
    }
}

fn launch(
    action: ActionId,
    target: &ResolvedTarget,
    facts: &ActionFacts,
    policy: &LaunchPolicy,
) -> Result<(), ActionAvailability> {
    if matches!(target.profile, ProfileBinding::Ordinary { .. })
        != (action == ActionId::LaunchOrdinary)
    {
        return Err(blocked(AvailabilityReasonCode::WrongProfileKind));
    }
    profile_matches(target, facts)?;
    if live_flag(&facts.busy, AvailabilityReasonCode::UnknownIdentity)? {
        return Err(blocked(AvailabilityReasonCode::Busy));
    }
    if live_flag(
        &facts.recovery_required,
        AvailabilityReasonCode::UnknownIdentity,
    )? {
        return Err(blocked(AvailabilityReasonCode::InterruptedTransaction));
    }
    no_live_session(target, facts)?;
    if matches!(&facts.runtime, Observation::Observed { evidence, .. } if evidence.source != EvidenceSource::NativeLive)
    {
        return Err(unknown(AvailabilityReasonCode::UnknownIdentity));
    }
    let runtime = observe(&facts.runtime, AvailabilityReasonCode::UnrecognizedRuntime)?;
    match runtime {
        RuntimeOwnership::Absent if action == ActionId::LaunchOrdinary => {
            if policy.allow_unmodded_ordinary {
                Ok(())
            } else {
                Err(unavailable(AvailabilityReasonCode::UnrecognizedRuntime))
            }
        }
        RuntimeOwnership::Absent => Err(unavailable(AvailabilityReasonCode::UnqualifiedIsolation)),
        RuntimeOwnership::Unmanaged { .. }
            if action == ActionId::LaunchOrdinary
                && policy.allow_unrecognized_ordinary
                && facts.ordinary_runtime_choice == UnrecognizedRuntimeChoice::AllowOnce =>
        {
            Ok(())
        }
        RuntimeOwnership::Unmanaged { .. } => {
            Err(blocked(AvailabilityReasonCode::UnrecognizedRuntime))
        }
        RuntimeOwnership::Managed { reference } => {
            if &reference.target != target {
                return Err(blocked(AvailabilityReasonCode::WrongOwner));
            }
            let capability_runtime = observe(
                &facts.capability_runtime,
                AvailabilityReasonCode::UnrecognizedRuntime,
            )?;
            if capability_runtime != &reference.binding {
                return Err(blocked(AvailabilityReasonCode::StaleRevision));
            }
            feature_available(
                if action == ActionId::LaunchOrdinary {
                    ORDINARY_RUNTIME_LAUNCH
                } else {
                    ISOLATED_RUNTIME_LAUNCH
                },
                &facts.capabilities,
            )
        }
    }
}

fn profile_matches(target: &ResolvedTarget, facts: &ActionFacts) -> Result<(), ActionAvailability> {
    let profile = observe(&facts.profile, AvailabilityReasonCode::MissingTarget)?;
    match (&target.profile, profile) {
        (
            ProfileBinding::Ordinary {
                ordinary_id,
                owner_scope,
            },
            ProfileProjection::Ordinary {
                reference:
                    OrdinaryProfileRef::Ordinary {
                        catalog_id,
                        owner_scope: observed_owner,
                        ..
                    },
                ..
            },
        ) if owner_scope == observed_owner
            && ordinary_id.as_ref().is_none_or(|id| id == catalog_id) =>
        {
            Ok(())
        }
        (
            ProfileBinding::Isolated { id, revision },
            ProfileProjection::Isolated {
                reference, store, ..
            },
        ) => {
            if id != &reference.id {
                return Err(blocked(AvailabilityReasonCode::WrongOwner));
            }
            if revision != &reference.revision {
                return Err(blocked(AvailabilityReasonCode::StaleRevision));
            }
            if reference.state == ProfileState::Archived {
                return Err(blocked(AvailabilityReasonCode::ArchivedProfile));
            }
            // Store readiness can change without changing the catalog revision.
            // A captured/historical value cannot establish current launch safety.
            if matches!(store, Observation::Observed { evidence, .. } if evidence.source != EvidenceSource::NativeLive)
            {
                return Err(unknown(AvailabilityReasonCode::UnknownIdentity));
            }
            match observe(store, AvailabilityReasonCode::UnknownIdentity)? {
                IsolatedStoreState::New | IsolatedStoreState::Established => Ok(()),
                IsolatedStoreState::Interrupted => {
                    Err(blocked(AvailabilityReasonCode::InterruptedTransaction))
                }
                IsolatedStoreState::MissingEstablished => {
                    Err(blocked(AvailabilityReasonCode::UnknownIdentity))
                }
            }
        }
        _ => Err(blocked(AvailabilityReasonCode::WrongOwner)),
    }
}

fn no_live_session(target: &ResolvedTarget, facts: &ActionFacts) -> Result<(), ActionAvailability> {
    let sessions = live_inventory(&facts.sessions)?;
    for session in sessions.items.as_slice().iter().filter(|session| {
        &session.binding.process.installation_physical_id == target.installation.physical_id()
    }) {
        if live_flag(
            &session.live_identity,
            AvailabilityReasonCode::UnknownIdentity,
        )? {
            return Err(blocked(AvailabilityReasonCode::ActiveSession));
        }
    }
    Ok(())
}

fn focus(binding: &SessionBinding, facts: &ActionFacts) -> Result<(), ActionAvailability> {
    let sessions = live_inventory(&facts.sessions)?;
    let session = sessions
        .items
        .as_slice()
        .iter()
        .find(|session| session.binding.session_id == binding.session_id)
        .ok_or_else(|| blocked(AvailabilityReasonCode::MissingTarget))?;
    if &session.binding != binding {
        return Err(blocked(AvailabilityReasonCode::StaleRevision));
    }
    if !super::observations::session_coherent(session) {
        return Err(unknown(AvailabilityReasonCode::UnknownIdentity));
    }
    if !live_flag(
        &session.live_identity,
        AvailabilityReasonCode::UnknownIdentity,
    )? {
        return Err(blocked(AvailabilityReasonCode::MissingTarget));
    }
    Ok(())
}

fn live_inventory(
    observation: &Observation<Inventory<SessionProjection>>,
) -> Result<&Inventory<SessionProjection>, ActionAvailability> {
    let Observation::Observed { value, evidence } = observation else {
        return observe(observation, AvailabilityReasonCode::UnknownIdentity);
    };
    if evidence.source != EvidenceSource::NativeLive
        || value.completeness != Completeness::Complete
        || !value.issues.as_slice().is_empty()
        || !unique(value.items.as_slice(), |session| {
            session.binding.session_id.clone()
        })
        || value
            .items
            .as_slice()
            .iter()
            .any(|session| !super::observations::session_coherent(session))
    {
        return Err(unknown(AvailabilityReasonCode::UnknownIdentity));
    }
    Ok(value)
}

fn feature_available(
    id: &str,
    capabilities: &Inventory<CapabilityProjection>,
) -> Result<(), ActionAvailability> {
    if capabilities.completeness != Completeness::Complete
        || !capabilities.issues.as_slice().is_empty()
        || !unique(capabilities.items.as_slice(), |capability| {
            capability.id.clone()
        })
    {
        return Err(unknown(AvailabilityReasonCode::UnsupportedSchema));
    }
    let capability = capabilities
        .items
        .as_slice()
        .iter()
        .find(|capability| capability.id.as_str() == id)
        .ok_or_else(|| unavailable(AvailabilityReasonCode::UnsupportedSchema))?;
    match &capability.status {
        CapabilityStatus::Supported { evidence }
            if matches!(
                evidence.source,
                EvidenceSource::ArtifactSelfDescription | EvidenceSource::VerifiedRelease
            ) =>
        {
            Ok(())
        }
        CapabilityStatus::Supported { .. } => Err(unknown(AvailabilityReasonCode::UnknownIdentity)),
        CapabilityStatus::Unsupported { reason } => Err(ActionAvailability::Unavailable {
            reason: reason.clone(),
        }),
        CapabilityStatus::Unknown { reason } => Err(ActionAvailability::Unknown {
            reason: reason.clone(),
        }),
    }
}

fn live_flag(
    observation: &Observation<bool>,
    missing: AvailabilityReasonCode,
) -> Result<bool, ActionAvailability> {
    match observation {
        Observation::Observed { value, evidence }
            if evidence.source == EvidenceSource::NativeLive =>
        {
            Ok(*value)
        }
        Observation::Observed { .. } => Err(unknown(AvailabilityReasonCode::UnknownIdentity)),
        _ => observe(observation, missing).copied(),
    }
}

fn observe<T>(
    observation: &Observation<T>,
    missing: AvailabilityReasonCode,
) -> Result<&T, ActionAvailability> {
    match observation {
        Observation::Observed { value, .. } => Ok(value),
        Observation::Missing { .. } => Err(blocked(missing)),
        Observation::Unknown { reason, .. } => Err(unknown(observation_reason(*reason))),
        Observation::Unavailable { reason } => Err(unavailable(observation_reason(*reason))),
    }
}

fn blocked(code: AvailabilityReasonCode) -> ActionAvailability {
    ActionAvailability::Blocked {
        reasons: BoundedList::new(vec![reason(code)]).expect("one reason fits the wire bound"),
    }
}

fn unavailable(code: AvailabilityReasonCode) -> ActionAvailability {
    ActionAvailability::Unavailable {
        reason: reason(code),
    }
}

fn unknown(code: AvailabilityReasonCode) -> ActionAvailability {
    ActionAvailability::Unknown {
        reason: reason(code),
    }
}
