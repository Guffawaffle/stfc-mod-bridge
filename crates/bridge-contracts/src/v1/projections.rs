use super::{
    configuration::*, distribution::*, management::*, primitives::*, targets::*, workflow::*,
};
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ProjectionIssueCode {
    AccessDenied,
    MissingCatalog,
    IncompleteCatalog,
    InvalidMetadata,
    ConflictingIdentity,
    StaleReceipt,
    NativeUnavailable,
    UnsupportedPlatform,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum ResourceRef {
    Profile { id: ProfileId },
    Installation { id: InstallationId },
    Session { id: SessionId },
    Document { id: DocumentId },
    Operation { id: OperationId },
    Application { identity: ApplicationIdentity },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProjectionIssue {
    pub code: ProjectionIssueCode,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub resource: Option<ResourceRef>,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum Completeness {
    Complete,
    Partial,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Inventory<T> {
    pub items: BoundedList<T, 128>,
    pub completeness: Completeness,
    pub issues: BoundedList<ProjectionIssue, 64>,
    pub revision: OpaqueRevision,
}
impl<T> Inventory<T> {
    pub(crate) fn valid(&self) -> bool {
        self.completeness != Completeness::Partial || !self.issues.as_slice().is_empty()
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum ProfileProjection {
    Ordinary {
        reference: OrdinaryProfileRef,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        preferred_installation: Option<InstallationId>,
    },
    Isolated {
        reference: IsolatedProfileRef,
        name: DisplayName,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        preferred_installation: Option<InstallationId>,
        store: Observation<IsolatedStoreState>,
    },
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum IsolatedStoreState {
    New,
    Established,
    Interrupted,
    MissingEstablished,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InstallationProjection {
    pub binding: InstallationBinding,
    pub name: DisplayName,
    pub client: Observation<GameClientBinding>,
    pub update: Observation<NativeGameRecoveryRef>,
}
impl InstallationProjection {
    pub(crate) fn valid(&self) -> bool {
        observation_valid(&self.update, |recovery| {
            self.binding.same_identity(&recovery.installation)
        })
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum SessionReadiness {
    OrdinarySpawned,
    IsolatedInitializing,
    IsolatedReady,
    IsolationFailed,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionProjection {
    pub binding: SessionBinding,
    pub target: Observation<ResolvedTarget>,
    pub live_identity: Observation<bool>,
    pub readiness: Observation<SessionReadiness>,
}
impl SessionProjection {
    pub(crate) fn valid(&self) -> bool {
        match &self.target {
            Observation::Observed { value, .. } => {
                value.installation.physical_id() == &self.binding.process.installation_physical_id
                    && match &self.readiness {
                        Observation::Observed {
                            value: readiness, ..
                        } => match readiness {
                            SessionReadiness::OrdinarySpawned => {
                                matches!(value.profile, ProfileBinding::Ordinary { .. })
                            }
                            _ => matches!(value.profile, ProfileBinding::Isolated { .. }),
                        },
                        _ => true,
                    }
            }
            _ => true,
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ImportSourceProjection {
    pub reference: ImportSourceRef,
    pub name: DisplayName,
    pub accessibility: Observation<bool>,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum AvailabilityReasonCode {
    MissingTarget,
    UnknownIdentity,
    WrongProfileKind,
    ArchivedProfile,
    WrongOwner,
    ActiveSession,
    Busy,
    StaleRevision,
    UnrecognizedRuntime,
    UnsupportedSchema,
    UnavailableNativeRoute,
    UnqualifiedIsolation,
    Offline,
    InterruptedTransaction,
    DirtyDraft,
    UnsupportedPlatform,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct AvailabilityReason {
    pub code: AvailabilityReasonCode,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub resource: Option<ResourceRef>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub remediation: Option<ActionId>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "status",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum ActionAvailability {
    Available {
        revision: OpaqueRevision,
        grants_lock: FalseFlag,
        grants_permission: FalseFlag,
    },
    Blocked {
        reasons: BoundedList<AvailabilityReason, 16>,
    },
    Unavailable {
        reason: AvailabilityReason,
    },
    Unknown {
        reason: AvailabilityReason,
    },
}
impl ActionAvailability {
    pub(crate) fn valid(&self) -> bool {
        match self {
            Self::Blocked { reasons } => !reasons.as_slice().is_empty(),
            _ => true,
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ActionProjection {
    pub action: ActionId,
    pub availability: ActionAvailability,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum ActionScope {
    Target {
        target: ResolvedTarget,
    },
    Session {
        session: SessionBinding,
    },
    Profile {
        profile: IsolatedProfileRef,
    },
    Document {
        document: DocumentBinding,
    },
    Application {
        application: BridgeApplicationBinding,
    },
    Catalog {
        revision: OpaqueRevision,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "status", rename_all = "snake_case", deny_unknown_fields)]
pub enum CapabilityStatus {
    Supported { evidence: Evidence },
    Unsupported { reason: AvailabilityReason },
    Unknown { reason: AvailabilityReason },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CapabilityProjection {
    pub id: CapabilityId,
    pub status: CapabilityStatus,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Snapshot {
    pub cursor: Cursor,
    pub preferences: Observation<ApplicationPreferencesSnapshot>,
    pub profiles: Observation<Inventory<ProfileProjection>>,
    pub installations: Observation<Inventory<InstallationProjection>>,
    pub sessions: Observation<Inventory<SessionProjection>>,
    pub operations: Inventory<OperationSnapshot>,
    pub capabilities: Inventory<CapabilityProjection>,
}
impl Snapshot {
    pub(crate) fn valid(&self) -> bool {
        observation_valid(&self.profiles, Inventory::valid)
            && observation_valid(&self.installations, |inventory| {
                inventory.valid()
                    && inventory
                        .items
                        .as_slice()
                        .iter()
                        .all(InstallationProjection::valid)
            })
            && observation_valid(&self.sessions, |inventory| {
                inventory.valid()
                    && inventory
                        .items
                        .as_slice()
                        .iter()
                        .all(SessionProjection::valid)
            })
            && self.operations.valid()
            && self
                .operations
                .items
                .as_slice()
                .iter()
                .all(OperationSnapshot::valid)
            && super::unique_by(self.operations.items.as_slice(), |o| o.operation_id.clone())
            && self.capabilities.valid()
            && super::unique_by(self.capabilities.items.as_slice(), |c| c.id.clone())
    }
}
pub(crate) fn observation_valid<T>(
    observation: &Observation<T>,
    valid: impl Fn(&T) -> bool,
) -> bool {
    match observation {
        Observation::Observed { value, .. } => valid(value),
        _ => true,
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum DiagnosticDisclosure {
    Redacted,
    IncludePaths,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DisclosedPaths {
    pub installation: NativeAbsolutePath,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub profile: Option<NativeAbsolutePath>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum DiagnosticFact {
    Target {
        value: ResolvedTarget,
    },
    Session {
        value: Observation<SessionBinding>,
    },
    Runtime {
        value: Observation<RuntimeOwnership>,
    },
    Game {
        value: Observation<GameClientBinding>,
    },
    Bridge {
        value: Observation<BridgeApplicationBinding>,
    },
    Capability {
        value: CapabilityProjection,
    },
    Issue {
        value: ProjectionIssue,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DiagnosticContent {
    pub target: ResolvedTarget,
    pub disclosure: DiagnosticDisclosure,
    pub facts: BoundedList<DiagnosticFact, 64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub paths: Option<DisclosedPaths>,
}
impl DiagnosticContent {
    pub(crate) fn valid(&self) -> bool {
        if self.disclosure == DiagnosticDisclosure::Redacted && self.paths.is_some() {
            return false;
        }
        self.facts.as_slice().iter().all(|fact| match fact {
            DiagnosticFact::Target { value } => value == &self.target,
            DiagnosticFact::Session { value } => observation_valid(value, |s| {
                &s.process.installation_physical_id == self.target.installation.physical_id()
            }),
            DiagnosticFact::Runtime { value } => observation_valid(value, |r| match r {
                RuntimeOwnership::Managed { reference } => reference.target == self.target,
                _ => true,
            }),
            DiagnosticFact::Bridge { value } => {
                observation_valid(value, BridgeApplicationBinding::valid)
            }
            _ => true,
        })
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PreviewRef {
    pub preview_id: PreviewId,
    pub host_epoch: HostEpoch,
    pub revision: OpaqueRevision,
    pub target: ResolvedTarget,
    pub disclosure: DiagnosticDisclosure,
    pub digest: Sha256,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DiagnosticPreview {
    pub reference: PreviewRef,
    pub content: DiagnosticContent,
}
impl DiagnosticPreview {
    pub(crate) fn valid(&self) -> bool {
        self.content.valid()
            && self.reference.target == self.content.target
            && self.reference.disclosure == self.content.disclosure
            && super::diagnostic_preview_digest(&self.content)
                .is_ok_and(|d| d == self.reference.digest)
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ExportDestinationRef {
    pub destination_id: ExportDestinationId,
    pub host_epoch: HostEpoch,
    pub native_target_ref: NativeTargetRef,
    pub revision: OpaqueRevision,
}
/// A transport-neutral request to the backend's native/headless save-selection
/// port. The opaque reference remains backend custody, never a client path.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RequestExportDestinationInput {
    pub preview: PreviewRef,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ExportDestinationUnavailableReason {
    NativeUnavailable,
    UnsupportedPlatform,
    AccessDenied,
    SelectionUnavailable,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "status", rename_all = "snake_case", deny_unknown_fields)]
pub enum ExportDestinationOutcome {
    Captured {
        destination: ExportDestinationRef,
    },
    Cancelled,
    Unavailable {
        reason: ExportDestinationUnavailableReason,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ExportDestinationResult {
    pub binding: RequestExportDestinationInput,
    pub outcome: ExportDestinationOutcome,
}
impl ExportDestinationResult {
    pub(crate) fn valid(&self) -> bool {
        match &self.outcome {
            ExportDestinationOutcome::Captured { destination } => {
                destination.host_epoch == self.binding.preview.host_epoch
            }
            ExportDestinationOutcome::Cancelled | ExportDestinationOutcome::Unavailable { .. } => {
                true
            }
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ExportDiagnosticsInput {
    pub preview: PreviewRef,
    pub destination: ExportDestinationRef,
}
impl ExportDiagnosticsInput {
    pub(crate) fn valid(&self) -> bool {
        self.preview.host_epoch == self.destination.host_epoch
    }
}
