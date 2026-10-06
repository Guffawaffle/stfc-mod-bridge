//! Pure, advisory projections over immutable observations.
//!
//! A supported manifest feature is not a native route, an admission, a lock or
//! permission. Callers retain the exact observed scope and revalidate it during
//! preparation/commit. This module has no filesystem, process or store ports.

mod actions;
mod capabilities;
mod observations;

pub use actions::{ActionFacts, LaunchPolicy, NativeRouteObservation, project_actions};
pub use capabilities::{
    CONFIGURATION_EDIT, ISOLATED_RUNTIME_LAUNCH, ManifestAssessment, ORDINARY_RUNTIME_LAUNCH,
    ProducerManifest, ProviderPolicy, RuntimeCapabilityFacts, RuntimeTarget, project_capabilities,
};
pub use observations::{project_diagnostic_content, project_sessions};

use bridge_contracts::v1::{AvailabilityReason, AvailabilityReasonCode, ObservationReason};
use std::fmt;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ProjectionError {
    DuplicateCapability,
    DuplicateAction,
    InvalidInventory,
    TooManyIssues,
    TooManyFacts,
}

impl fmt::Display for ProjectionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{self:?}")
    }
}

impl std::error::Error for ProjectionError {}

pub(crate) fn reason(code: AvailabilityReasonCode) -> AvailabilityReason {
    AvailabilityReason {
        code,
        resource: None,
        remediation: None,
    }
}

pub(crate) fn observation_reason(reason: ObservationReason) -> AvailabilityReasonCode {
    match reason {
        ObservationReason::UnsupportedPlatform => AvailabilityReasonCode::UnsupportedPlatform,
        ObservationReason::NativeUnavailable => AvailabilityReasonCode::UnavailableNativeRoute,
        ObservationReason::AccessDenied
        | ObservationReason::IncompleteInventory
        | ObservationReason::UnrecognizedIdentity
        | ObservationReason::ConflictingEvidence => AvailabilityReasonCode::UnknownIdentity,
    }
}

pub(crate) fn unique<T, K: PartialEq>(items: &[T], key: impl Fn(&T) -> K) -> bool {
    items
        .iter()
        .enumerate()
        .all(|(index, item)| !items[..index].iter().any(|other| key(item) == key(other)))
}

#[cfg(test)]
#[path = "../../tests/projections/mod.rs"]
mod tests;
