use super::{primitives::*, targets::*};
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ProfileState {
    Active,
    Archived,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct IsolatedProfileRef {
    pub id: ProfileId,
    pub revision: OpaqueRevision,
    pub state: ProfileState,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum OrdinaryProfileRef {
    Ordinary {
        catalog_id: ProfileId,
        revision: OpaqueRevision,
        owner_scope: OwnerScope,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ImportSourceRef {
    pub source_id: ImportSourceId,
    pub source_revision: OpaqueRevision,
    pub destination_owner: OwnerScope,
    pub requires_native_approval: bool,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum NativeApprovalChoice {
    Decline,
    RequestNativeApproval,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum ProfileSetup {
    New,
    WindowsUserImport {
        source: ImportSourceRef,
        approval: NativeApprovalChoice,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CreateProfileInput {
    pub name: DisplayName,
    pub setup: ProfileSetup,
    pub preferred_installation: RegisteredInstallationSelector,
    pub expected_catalog_revision: OpaqueRevision,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CreateProfileCapture {
    pub name: DisplayName,
    pub setup: ProfileSetup,
    pub preferred_installation: RegisteredInstallationBinding,
    pub catalog_revision: OpaqueRevision,
    pub native_preparation_ref: NativePreparationRef,
    pub destination_owner: OwnerScope,
}
impl CreateProfileCapture {
    pub(crate) fn valid(&self) -> bool {
        match &self.setup {
            ProfileSetup::WindowsUserImport { source, approval } => {
                source.destination_owner == self.destination_owner
                    && (!source.requires_native_approval
                        || *approval == NativeApprovalChoice::RequestNativeApproval)
            }
            _ => true,
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum PreferredInstallationEdit {
    Keep {
        expected: SavedInstallationPreference,
    },
    Clear,
    Set {
        installation: RegisteredInstallationBinding,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum SavedInstallationPreference {
    None,
    Registered { id: InstallationId },
}
impl SavedInstallationPreference {
    pub(crate) fn matches(&self, observed: Option<&InstallationId>) -> bool {
        match (self, observed) {
            (Self::None, None) => true,
            (Self::Registered { id }, Some(observed)) => id == observed,
            _ => false,
        }
    }
}
impl PreferredInstallationEdit {
    pub(crate) fn matches(&self, observed: Option<&InstallationId>) -> bool {
        match self {
            Self::Keep { expected } => expected.matches(observed),
            Self::Clear => observed.is_none(),
            Self::Set { installation } => observed == Some(installation.registration_id()),
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct EditOrdinaryProfileInput {
    pub profile: OrdinaryProfileRef,
    pub preferred_installation: PreferredInstallationEdit,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct EditIsolatedProfileInput {
    pub profile: IsolatedProfileRef,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<DisplayName>,
    pub preferred_installation: PreferredInstallationEdit,
}
impl EditIsolatedProfileInput {
    pub(crate) fn valid(&self) -> bool {
        self.name.is_some()
            || !matches!(
                self.preferred_installation,
                PreferredInstallationEdit::Keep { .. }
            )
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProfileLifecycleInput {
    pub profile: IsolatedProfileRef,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
pub enum DeletionConfirmation {
    #[serde(rename = "delete_entire_owned_profile")]
    DeleteEntireOwnedProfile,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DeleteProfileInput {
    pub profile: IsolatedProfileRef,
    pub confirmation: DeletionConfirmation,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RegisterInstallationInput {
    pub directory: NativeAbsolutePath,
    pub name: DisplayName,
    pub expected_catalog_revision: OpaqueRevision,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RegisterInstallationCapture {
    pub name: DisplayName,
    pub physical_id: PhysicalInstallationId,
    pub native_target_ref: NativeTargetRef,
    pub catalog_revision: OpaqueRevision,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct EditInstallationInput {
    pub installation: InstallationBinding,
    pub name: DisplayName,
}
impl EditInstallationInput {
    pub(crate) fn valid(&self) -> bool {
        matches!(self.installation, InstallationBinding::Registered { .. })
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ThemePreference {
    System,
    Light,
    Dark,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum MotionPreference {
    System,
    Reduced,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProviderPreference {
    pub provider_id: ProviderId,
    pub channel_id: ChannelId,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SavedTargetPreference {
    pub installation_id: InstallationId,
    pub profile: ProfileSelector,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ApplicationPreferences {
    pub theme: ThemePreference,
    pub motion: MotionPreference,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_target: Option<SavedTargetPreference>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub provider: Option<ProviderPreference>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ApplicationPreferencesSnapshot {
    pub revision: OpaqueRevision,
    pub values: ApplicationPreferences,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SaveApplicationPreferencesInput {
    pub expected_revision: OpaqueRevision,
    pub values: ApplicationPreferences,
}
