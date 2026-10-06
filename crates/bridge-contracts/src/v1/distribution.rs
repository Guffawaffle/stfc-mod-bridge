//! Three separate release/check references. Metadata selection is not admission
//! or signature/native qualification; each owning engine policy revalidates it.
use super::{configuration::*, primitives::*, targets::*, workflow::RuntimeBinding};
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ArtifactRole {
    RuntimeModule,
    RuntimeLoader,
    RuntimeManifest,
    ConfigurationSchema,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RuntimeArtifactSubject {
    pub role: ArtifactRole,
    pub platform: SupportedPlatform,
    pub architecture: ProcessArchitecture,
    pub digest: Sha256,
    pub size: ProgressCount,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RuntimeReleaseSelectionRef {
    pub selection_id: RuntimeReleaseId,
    pub host_epoch: HostEpoch,
    pub revision: OpaqueRevision,
    pub target: ResolvedTarget,
    pub provider_id: ProviderId,
    pub distribution_id: DistributionId,
    pub channel_id: ChannelId,
    pub release_version: ReleaseVersion,
    pub client_revision: OpaqueRevision,
    pub artifacts: BoundedList<RuntimeArtifactSubject, 16>,
    pub configuration_schema: SchemaBinding,
    pub authority: ArtifactAuthorityRef,
}
impl RuntimeReleaseSelectionRef {
    pub(crate) fn valid(&self) -> bool {
        self.configuration_schema.provider_id == self.provider_id
            && !self.artifacts.as_slice().is_empty()
            && super::unique_by(self.artifacts.as_slice(), |a| {
                (a.role, a.platform, a.architecture)
            })
            && self.artifacts.as_slice().iter().any(|a| {
                a.role == ArtifactRole::RuntimeModule
                    && a.digest == self.configuration_schema.runtime_artifact_digest
            })
            && self.artifacts.as_slice().iter().all(|a| a.size.get() > 0)
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ManagedRuntimeRef {
    pub receipt_id: RuntimeReceiptId,
    pub revision: OpaqueRevision,
    pub target: ResolvedTarget,
    pub binding: RuntimeBinding,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum RuntimeOwnership {
    Absent,
    Managed { reference: Box<ManagedRuntimeRef> },
    Unmanaged { artifact_digest: Sha256 },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum ConfigurationParticipant {
    Unchanged {
        document: DocumentBinding,
    },
    SaveReviewedDraft {
        draft: DraftRef,
    },
    CompatibleMigration {
        document: DocumentBinding,
        destination: SchemaBinding,
    },
}
impl ConfigurationParticipant {
    pub(crate) fn valid_for(&self, target: &ResolvedTarget, schema: &SchemaBinding) -> bool {
        match self {
            Self::Unchanged { document } => {
                document.target == *target && same_schema_contract(&document.schema, schema)
            }
            Self::SaveReviewedDraft { draft } => {
                draft.document.target == *target
                    && same_schema_contract(&draft.document.schema, schema)
            }
            Self::CompatibleMigration {
                document,
                destination,
            } => document.target == *target && destination == schema,
        }
    }
    pub(crate) fn document(&self) -> &DocumentBinding {
        match self {
            Self::Unchanged { document } | Self::CompatibleMigration { document, .. } => document,
            Self::SaveReviewedDraft { draft } => &draft.document,
        }
    }
}
fn same_schema_contract(left: &SchemaBinding, right: &SchemaBinding) -> bool {
    left.provider_id == right.provider_id
        && left.schema_id == right.schema_id
        && left.schema_version == right.schema_version
        && left.digest == right.digest
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum PreparedConfigurationEffect {
    Unchanged {
        document: DocumentBinding,
    },
    Write {
        baseline: DocumentBinding,
        destination_schema: SchemaBinding,
        candidate_digest: Sha256,
    },
}
impl PreparedConfigurationEffect {
    pub(crate) fn valid_for(
        &self,
        participant: &ConfigurationParticipant,
        selected: &SchemaBinding,
    ) -> bool {
        match (self, participant) {
            (
                Self::Unchanged { document },
                ConfigurationParticipant::Unchanged { document: expected },
            ) => document == expected && same_schema_contract(&document.schema, selected),
            (
                Self::Write {
                    baseline,
                    destination_schema,
                    ..
                },
                ConfigurationParticipant::SaveReviewedDraft { draft },
            ) => baseline == &draft.document && destination_schema == selected,
            (
                Self::Write {
                    baseline,
                    destination_schema,
                    ..
                },
                ConfigurationParticipant::CompatibleMigration {
                    document,
                    destination,
                },
            ) => {
                baseline == document
                    && destination_schema == destination
                    && destination_schema == selected
            }
            _ => false,
        }
    }
    pub(crate) fn writes(&self) -> bool {
        matches!(self, Self::Write { .. })
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RuntimeDeployInput {
    pub target: ResolvedTarget,
    pub selected_release: RuntimeReleaseSelectionRef,
    pub expected_ownership: RuntimeOwnership,
    pub configuration: ConfigurationParticipant,
}
impl RuntimeDeployInput {
    pub(crate) fn valid(&self) -> bool {
        self.selected_release.valid()
            && self.target == self.selected_release.target
            && self
                .configuration
                .valid_for(&self.target, &self.selected_release.configuration_schema)
            && match &self.expected_ownership {
                RuntimeOwnership::Managed { reference } => {
                    reference.target == self.target
                        && reference.binding.provider_id == self.selected_release.provider_id
                        && reference.binding.distribution_id
                            == self.selected_release.distribution_id
                        && reference.binding.provider_id
                            == self.configuration.document().schema.provider_id
                        && reference.binding.configuration_schema_digest
                            == self.configuration.document().schema.digest
                }
                _ => true,
            }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
pub enum RuntimeAdoptionConfirmation {
    #[serde(rename = "adopt_exact_recognized_artifact")]
    AdoptExactRecognizedArtifact,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RuntimeAdoptInput {
    pub target: ResolvedTarget,
    pub observed_artifact_digest: Sha256,
    pub recognized_binding: RuntimeBinding,
    pub recognition_authority: ArtifactAuthorityRef,
    pub confirmation: RuntimeAdoptionConfirmation,
}
impl RuntimeAdoptInput {
    pub(crate) fn valid(&self) -> bool {
        self.observed_artifact_digest == self.recognized_binding.artifact_digest
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ManagedRuntimeInput {
    pub reference: ManagedRuntimeRef,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
pub enum SourceSwitchConfirmation {
    #[serde(rename = "switch_runtime_and_configuration_source")]
    SwitchRuntimeAndConfigurationSource,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RuntimeSwitchSourceInput {
    pub current: ManagedRuntimeRef,
    pub selected_release: RuntimeReleaseSelectionRef,
    pub configuration: ConfigurationParticipant,
    pub confirmation: SourceSwitchConfirmation,
}
impl RuntimeSwitchSourceInput {
    pub(crate) fn valid(&self) -> bool {
        self.selected_release.valid()
            && self.current.target == self.selected_release.target
            && self.current.binding.provider_id != self.selected_release.provider_id
            && self.configuration.valid_for(
                &self.current.target,
                &self.selected_release.configuration_schema,
            )
            && self.configuration.document().schema.provider_id == self.current.binding.provider_id
            && self.configuration.document().schema.digest
                == self.current.binding.configuration_schema_digest
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct GameClientBinding {
    pub version: ReleaseVersion,
    pub executable_digest: Sha256,
    pub architecture: ProcessArchitecture,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum GameUpdateRoute {
    CanonicalNativeDirect,
    CanonicalManagedHandoff,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CheckedGameUpdateRef {
    pub check_id: GameUpdateId,
    pub host_epoch: HostEpoch,
    pub revision: OpaqueRevision,
    pub installation: InstallationBinding,
    pub current_client: GameClientBinding,
    pub offered_client: GameClientBinding,
    pub official_manifest_digest: Sha256,
    pub route: GameUpdateRoute,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct GameUpdateInput {
    pub checked_update: CheckedGameUpdateRef,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NativeGameRecoveryRef {
    pub installation: InstallationBinding,
    pub native_transaction: NativeTransactionRef,
    pub revision: OpaqueRevision,
    pub expected_client: GameClientBinding,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RecoverGameUpdateInput {
    pub recovery: NativeGameRecoveryRef,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum BridgePayloadRole {
    Application,
    ProfilesNative,
    TomlNative,
    UpdateHelper,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct BridgePayloadSubject {
    pub role: BridgePayloadRole,
    pub digest: Sha256,
    pub size: ProgressCount,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct BridgeApplicationBinding {
    pub application_id: ApplicationIdentity,
    pub installation_ref: NativeTargetRef,
    pub revision: OpaqueRevision,
    pub channel_id: ChannelId,
    pub platform: SupportedPlatform,
    pub architecture: ProcessArchitecture,
    pub package_digest: Sha256,
    pub pairing_digest: Sha256,
    pub payloads: BoundedList<BridgePayloadSubject, 8>,
}
impl BridgeApplicationBinding {
    pub(crate) fn valid(&self) -> bool {
        !self.payloads.as_slice().is_empty()
            && super::unique_by(self.payloads.as_slice(), |p| p.role)
            && self
                .payloads
                .as_slice()
                .iter()
                .any(|p| p.role == BridgePayloadRole::Application)
            && self.payloads.as_slice().iter().all(|p| p.size.get() > 0)
            && matches!(
                (self.platform, self.architecture),
                (SupportedPlatform::Windows, ProcessArchitecture::X86_64)
                    | (SupportedPlatform::Macos, ProcessArchitecture::Arm64)
            )
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct BridgeReleaseSelectionRef {
    pub selection_id: BridgeReleaseId,
    pub host_epoch: HostEpoch,
    pub current: BridgeApplicationBinding,
    pub offered: BridgeApplicationBinding,
    pub release_version: ReleaseVersion,
    pub authority: BridgeAuthorityRef,
    pub revision: OpaqueRevision,
}
impl BridgeReleaseSelectionRef {
    pub(crate) fn valid(&self) -> bool {
        self.current.valid()
            && self.offered.valid()
            && self.current.application_id == self.offered.application_id
            && self.current.installation_ref == self.offered.installation_ref
            && self.current.channel_id == self.offered.channel_id
            && self.current.platform == self.offered.platform
            && self.current.architecture == self.offered.architecture
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct BridgeUpdateInput {
    pub selected_release: BridgeReleaseSelectionRef,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct BridgeRecoveryRef {
    pub application: BridgeApplicationBinding,
    pub expected_application: BridgeApplicationBinding,
    pub bridge_journal_ref: NativeTransactionRef,
    pub revision: OpaqueRevision,
}
impl BridgeRecoveryRef {
    pub(crate) fn valid(&self) -> bool {
        self.application.valid()
            && self.expected_application.valid()
            && self.application.application_id == self.expected_application.application_id
            && self.application.installation_ref == self.expected_application.installation_ref
            && self.application.channel_id == self.expected_application.channel_id
            && self.application.platform == self.expected_application.platform
            && self.application.architecture == self.expected_application.architecture
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RecoverBridgeUpdateInput {
    pub recovery: BridgeRecoveryRef,
}
