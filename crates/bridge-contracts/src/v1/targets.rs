use super::primitives::*;
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "platform",
    content = "value",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum NativeAbsolutePath {
    Windows(WindowsAbsolutePath),
    Macos(MacAbsolutePath),
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum InstallationSelector {
    Registered {
        id: InstallationId,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        directory_assertion: Option<NativeAbsolutePath>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        revision_assertion: Option<OpaqueRevision>,
    },
    Directory {
        directory: NativeAbsolutePath,
    },
}
/// A saved native catalog preference names a registration. Path overrides use
/// `InstallationSelector` separately and never create a registration implicitly.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum RegisteredInstallationSelector {
    Registered {
        id: InstallationId,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        directory_assertion: Option<NativeAbsolutePath>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        revision_assertion: Option<OpaqueRevision>,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum RegisteredInstallationBinding {
    Registered {
        registration_id: InstallationId,
        registration_revision: OpaqueRevision,
        physical_id: PhysicalInstallationId,
        native_target_ref: NativeTargetRef,
    },
}
impl RegisteredInstallationBinding {
    pub fn registration_id(&self) -> &InstallationId {
        let Self::Registered {
            registration_id, ..
        } = self;
        registration_id
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum ProfileSelector {
    Ordinary {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        catalog_id_assertion: Option<ProfileId>,
    },
    Isolated {
        id: ProfileId,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        revision_assertion: Option<OpaqueRevision>,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum OrdinaryProfileSelector {
    Ordinary {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        catalog_id_assertion: Option<ProfileId>,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum IsolatedProfileSelector {
    Isolated {
        id: ProfileId,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        revision_assertion: Option<OpaqueRevision>,
    },
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct TargetSelector {
    pub installation: InstallationSelector,
    pub profile: ProfileSelector,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct OrdinaryTargetSelector {
    pub installation: InstallationSelector,
    pub profile: OrdinaryProfileSelector,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct IsolatedTargetSelector {
    pub installation: InstallationSelector,
    pub profile: IsolatedProfileSelector,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum InstallationBinding {
    Registered {
        registration_id: InstallationId,
        registration_revision: OpaqueRevision,
        physical_id: PhysicalInstallationId,
        native_target_ref: NativeTargetRef,
    },
    Directory {
        physical_id: PhysicalInstallationId,
        native_target_ref: NativeTargetRef,
    },
}
impl InstallationBinding {
    pub fn physical_id(&self) -> &PhysicalInstallationId {
        match self {
            Self::Registered { physical_id, .. } | Self::Directory { physical_id, .. } => {
                physical_id
            }
        }
    }

    /// A historical native journal may retain an older catalog metadata revision.
    /// That revision does not change registration, physical or native-custody
    /// identity. Directory bindings never imply an equivalent registration.
    pub(crate) fn same_identity(&self, other: &Self) -> bool {
        match (self, other) {
            (
                Self::Registered {
                    registration_id: left_registration,
                    physical_id: left_physical,
                    native_target_ref: left_native,
                    ..
                },
                Self::Registered {
                    registration_id: right_registration,
                    physical_id: right_physical,
                    native_target_ref: right_native,
                    ..
                },
            ) => {
                left_registration == right_registration
                    && left_physical == right_physical
                    && left_native == right_native
            }
            (
                Self::Directory {
                    physical_id: left_physical,
                    native_target_ref: left_native,
                },
                Self::Directory {
                    physical_id: right_physical,
                    native_target_ref: right_native,
                },
            ) => left_physical == right_physical && left_native == right_native,
            _ => false,
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum ProfileBinding {
    Ordinary {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        ordinary_id: Option<ProfileId>,
        owner_scope: OwnerScope,
    },
    Isolated {
        id: ProfileId,
        revision: OpaqueRevision,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ResolvedTarget {
    pub installation: InstallationBinding,
    pub profile: ProfileBinding,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "platform",
    content = "value",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum ProcessStartIdentity {
    Windows(ProcessGeneration),
    Macos(ProcessGeneration),
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ProcessArchitecture {
    X86_64,
    Arm64,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProcessIdentity {
    pub pid: Pid,
    pub start_identity: ProcessStartIdentity,
    pub executable_identity: ExecutableIdentity,
    pub installation_physical_id: PhysicalInstallationId,
    pub architecture: ProcessArchitecture,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionBinding {
    pub session_id: SessionId,
    pub revision: OpaqueRevision,
    pub process: ProcessIdentity,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum EvidenceSource {
    NativeLive,
    CatalogMetadata,
    SessionReceipt,
    DiskFileHash,
    ArtifactSelfDescription,
    VerifiedRelease,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Evidence {
    pub observation_id: ObservationId,
    pub observed_at: UtcTimestamp,
    pub source: EvidenceSource,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ObservationReason {
    AccessDenied,
    IncompleteInventory,
    NativeUnavailable,
    UnrecognizedIdentity,
    ConflictingEvidence,
    UnsupportedPlatform,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "status", rename_all = "snake_case", deny_unknown_fields)]
pub enum Observation<T> {
    Observed {
        value: T,
        evidence: Evidence,
    },
    Missing {
        evidence: Evidence,
    },
    Unknown {
        reason: ObservationReason,
        evidence: Evidence,
    },
    Unavailable {
        reason: ObservationReason,
    },
}
