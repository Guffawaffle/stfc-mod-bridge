use super::{
    configuration::*, distribution::*, management::*, outcomes::*, primitives::*, projections::*,
    targets::*,
};
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ActionId {
    LaunchOrdinary,
    LaunchIsolated,
    FocusSession,
    CreateProfile,
    EditOrdinaryProfile,
    EditIsolatedProfile,
    ArchiveProfile,
    RestoreProfile,
    DeleteProfile,
    RegisterInstallation,
    EditInstallation,
    SaveConfiguration,
    RestoreConfiguration,
    RuntimeInstall,
    RuntimeUpdate,
    RuntimeRepair,
    RuntimeAdopt,
    RuntimeRemove,
    RuntimeStopManaging,
    RuntimeSwitchSource,
    GameUpdate,
    RecoverGameUpdate,
    BridgeUpdate,
    RecoverBridgeUpdate,
    ExportDiagnostics,
    SaveApplicationPreferences,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum TrustDomain {
    Session,
    ProfileState,
    Configuration,
    RuntimeDistribution,
    GameClient,
    BridgeApplication,
    ApplicationState,
    Diagnostics,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum StoreMode {
    New,
    Resume,
    Existing,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct OrdinaryLaunchInput {
    pub target: OrdinaryTargetSelector,
    #[serde(default, skip_serializing_if = "UnrecognizedRuntimeChoice::is_reject")]
    pub unrecognized_runtime_choice: UnrecognizedRuntimeChoice,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct IsolatedLaunchInput {
    pub target: IsolatedTargetSelector,
    pub store_mode: StoreMode,
    #[serde(default, skip_serializing_if = "UnrecognizedRuntimeChoice::is_reject")]
    pub unrecognized_runtime_choice: UnrecognizedRuntimeChoice,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FocusSessionInput {
    pub session: SessionBinding,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    content = "input",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum MutationIntent {
    LaunchOrdinary(OrdinaryLaunchInput),
    LaunchIsolated(IsolatedLaunchInput),
    FocusSession(FocusSessionInput),
    CreateProfile(CreateProfileInput),
    EditOrdinaryProfile(EditOrdinaryProfileInput),
    EditIsolatedProfile(EditIsolatedProfileInput),
    ArchiveProfile(ProfileLifecycleInput),
    RestoreProfile(ProfileLifecycleInput),
    DeleteProfile(DeleteProfileInput),
    RegisterInstallation(RegisterInstallationInput),
    EditInstallation(EditInstallationInput),
    SaveConfiguration(SaveConfigurationInput),
    RestoreConfiguration(RestoreConfigurationInput),
    RuntimeInstall(RuntimeDeployInput),
    RuntimeUpdate(RuntimeDeployInput),
    RuntimeRepair(RuntimeDeployInput),
    RuntimeAdopt(RuntimeAdoptInput),
    RuntimeRemove(ManagedRuntimeInput),
    RuntimeStopManaging(ManagedRuntimeInput),
    RuntimeSwitchSource(RuntimeSwitchSourceInput),
    GameUpdate(GameUpdateInput),
    RecoverGameUpdate(RecoverGameUpdateInput),
    BridgeUpdate(BridgeUpdateInput),
    RecoverBridgeUpdate(RecoverBridgeUpdateInput),
    ExportDiagnostics(ExportDiagnosticsInput),
    SaveApplicationPreferences(SaveApplicationPreferencesInput),
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SaveConfigurationInput {
    pub draft: DraftRef,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SaveConfigurationCapture {
    pub draft: DraftSnapshot,
    pub candidate_digest: Sha256,
}
impl SaveConfigurationCapture {
    pub(crate) fn valid(&self) -> bool {
        self.draft.valid() && !matches!(self.draft.state, DraftState::Invalid | DraftState::Stale)
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RestoreConfigurationInput {
    pub document: DocumentBinding,
    pub backup: BackupReceiptRef,
}
impl RestoreConfigurationInput {
    pub(crate) fn valid(&self) -> bool {
        self.document.document_id == self.backup.document.document_id
            && self.document.target == self.backup.document.target
            && self.document.schema.provider_id == self.backup.document.schema.provider_id
            && self.backup.valid()
    }
}
impl MutationIntent {
    pub(crate) fn valid(&self) -> bool {
        match self {
            Self::EditIsolatedProfile(input) => input.valid(),
            Self::ArchiveProfile(input) => input.profile.state == ProfileState::Active,
            Self::RestoreProfile(input) => input.profile.state == ProfileState::Archived,
            Self::EditInstallation(input) => input.valid(),
            Self::RestoreConfiguration(input) => input.valid(),
            Self::RuntimeInstall(input) => {
                input.valid() && matches!(input.expected_ownership, RuntimeOwnership::Absent)
            }
            Self::RuntimeUpdate(input) | Self::RuntimeRepair(input) => {
                input.valid()
                    && matches!(input.expected_ownership, RuntimeOwnership::Managed { .. })
            }
            Self::RuntimeAdopt(input) => input.valid(),
            Self::RuntimeSwitchSource(input) => input.valid(),
            Self::BridgeUpdate(input) => input.selected_release.valid(),
            Self::RecoverBridgeUpdate(input) => input.recovery.valid(),
            Self::ExportDiagnostics(input) => input.valid(),
            _ => true,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RuntimeBinding {
    pub provider_id: ProviderId,
    pub distribution_id: DistributionId,
    pub artifact_digest: Sha256,
    pub manifest: RuntimeManifestObservation,
    pub configuration_schema_digest: Sha256,
    pub client_revision: OpaqueRevision,
    pub platform: SupportedPlatform,
    pub architecture: ProcessArchitecture,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "status", rename_all = "snake_case", deny_unknown_fields)]
pub enum RuntimeManifestObservation {
    Observed { digest: Sha256 },
    Missing,
    Unknown { reason: ObservationReason },
    Unavailable { reason: ObservationReason },
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum UnrecognizedRuntimeChoice {
    #[default]
    Reject,
    AllowOnce,
}
impl UnrecognizedRuntimeChoice {
    pub fn is_reject(&self) -> bool {
        *self == Self::Reject
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum RuntimeExpectation {
    Absent,
    Verified {
        binding: RuntimeBinding,
    },
    Unrecognized {
        artifact_digest: Sha256,
        choice: UnrecognizedRuntimeChoice,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum PreparedCapture {
    LaunchOrdinary {
        target: ResolvedTarget,
        catalog_revision: OpaqueRevision,
        runtime: RuntimeExpectation,
        #[serde(default, skip_serializing_if = "UnrecognizedRuntimeChoice::is_reject")]
        unrecognized_runtime_choice: UnrecognizedRuntimeChoice,
    },
    LaunchIsolated {
        target: ResolvedTarget,
        catalog_revision: OpaqueRevision,
        runtime: RuntimeExpectation,
        store_mode: StoreMode,
        #[serde(default, skip_serializing_if = "UnrecognizedRuntimeChoice::is_reject")]
        unrecognized_runtime_choice: UnrecognizedRuntimeChoice,
    },
    FocusSession {
        session: SessionBinding,
        target: ResolvedTarget,
    },
    CreateProfile {
        input: CreateProfileCapture,
    },
    EditOrdinaryProfile {
        input: EditOrdinaryProfileInput,
    },
    EditIsolatedProfile {
        input: EditIsolatedProfileInput,
    },
    ArchiveProfile {
        input: ProfileLifecycleInput,
    },
    RestoreProfile {
        input: ProfileLifecycleInput,
    },
    DeleteProfile {
        input: DeleteProfileInput,
    },
    RegisterInstallation {
        input: RegisterInstallationCapture,
    },
    EditInstallation {
        input: EditInstallationInput,
    },
    SaveConfiguration {
        input: SaveConfigurationCapture,
    },
    RestoreConfiguration {
        input: RestoreConfigurationInput,
    },
    RuntimeInstall {
        input: RuntimeDeployInput,
        prepared_configuration: PreparedConfigurationEffect,
    },
    RuntimeUpdate {
        input: RuntimeDeployInput,
        prepared_configuration: PreparedConfigurationEffect,
    },
    RuntimeRepair {
        input: RuntimeDeployInput,
        prepared_configuration: PreparedConfigurationEffect,
    },
    RuntimeAdopt {
        input: RuntimeAdoptInput,
    },
    RuntimeRemove {
        input: ManagedRuntimeInput,
    },
    RuntimeStopManaging {
        input: ManagedRuntimeInput,
    },
    RuntimeSwitchSource {
        input: RuntimeSwitchSourceInput,
        prepared_configuration: PreparedConfigurationEffect,
    },
    GameUpdate {
        input: GameUpdateInput,
    },
    RecoverGameUpdate {
        input: RecoverGameUpdateInput,
    },
    BridgeUpdate {
        input: BridgeUpdateInput,
    },
    RecoverBridgeUpdate {
        input: RecoverBridgeUpdateInput,
    },
    ExportDiagnostics {
        input: ExportDiagnosticsInput,
    },
    SaveApplicationPreferences {
        input: SaveApplicationPreferencesInput,
    },
}
impl PreparedCapture {
    pub(crate) fn host_matches(&self, host: &HostEpoch) -> bool {
        match self {
            Self::SaveConfiguration { input } => &input.draft.draft.host_epoch == host,
            Self::RuntimeInstall { input, .. }
            | Self::RuntimeUpdate { input, .. }
            | Self::RuntimeRepair { input, .. } => {
                &input.selected_release.host_epoch == host
                    && configuration_host_matches(&input.configuration, host)
            }
            Self::RuntimeSwitchSource { input, .. } => {
                &input.selected_release.host_epoch == host
                    && configuration_host_matches(&input.configuration, host)
            }
            Self::GameUpdate { input } => &input.checked_update.host_epoch == host,
            Self::BridgeUpdate { input } => &input.selected_release.host_epoch == host,
            Self::ExportDiagnostics { input } => {
                &input.preview.host_epoch == host && &input.destination.host_epoch == host
            }
            _ => true,
        }
    }
    pub const fn trust_domain(&self) -> TrustDomain {
        match self {
            Self::LaunchOrdinary { .. }
            | Self::LaunchIsolated { .. }
            | Self::FocusSession { .. } => TrustDomain::Session,
            Self::CreateProfile { .. }
            | Self::EditOrdinaryProfile { .. }
            | Self::EditIsolatedProfile { .. }
            | Self::ArchiveProfile { .. }
            | Self::RestoreProfile { .. }
            | Self::DeleteProfile { .. }
            | Self::RegisterInstallation { .. }
            | Self::EditInstallation { .. } => TrustDomain::ProfileState,
            Self::SaveConfiguration { .. } | Self::RestoreConfiguration { .. } => {
                TrustDomain::Configuration
            }
            Self::RuntimeInstall { .. }
            | Self::RuntimeUpdate { .. }
            | Self::RuntimeRepair { .. }
            | Self::RuntimeAdopt { .. }
            | Self::RuntimeRemove { .. }
            | Self::RuntimeStopManaging { .. }
            | Self::RuntimeSwitchSource { .. } => TrustDomain::RuntimeDistribution,
            Self::GameUpdate { .. } | Self::RecoverGameUpdate { .. } => TrustDomain::GameClient,
            Self::BridgeUpdate { .. } | Self::RecoverBridgeUpdate { .. } => {
                TrustDomain::BridgeApplication
            }
            Self::ExportDiagnostics { .. } => TrustDomain::Diagnostics,
            Self::SaveApplicationPreferences { .. } => TrustDomain::ApplicationState,
        }
    }
    pub const fn action(&self) -> ActionId {
        match self {
            Self::LaunchOrdinary { .. } => ActionId::LaunchOrdinary,
            Self::LaunchIsolated { .. } => ActionId::LaunchIsolated,
            Self::FocusSession { .. } => ActionId::FocusSession,
            Self::CreateProfile { .. } => ActionId::CreateProfile,
            Self::EditOrdinaryProfile { .. } => ActionId::EditOrdinaryProfile,
            Self::EditIsolatedProfile { .. } => ActionId::EditIsolatedProfile,
            Self::ArchiveProfile { .. } => ActionId::ArchiveProfile,
            Self::RestoreProfile { .. } => ActionId::RestoreProfile,
            Self::DeleteProfile { .. } => ActionId::DeleteProfile,
            Self::RegisterInstallation { .. } => ActionId::RegisterInstallation,
            Self::EditInstallation { .. } => ActionId::EditInstallation,
            Self::SaveConfiguration { .. } => ActionId::SaveConfiguration,
            Self::RestoreConfiguration { .. } => ActionId::RestoreConfiguration,
            Self::RuntimeInstall { .. } => ActionId::RuntimeInstall,
            Self::RuntimeUpdate { .. } => ActionId::RuntimeUpdate,
            Self::RuntimeRepair { .. } => ActionId::RuntimeRepair,
            Self::RuntimeAdopt { .. } => ActionId::RuntimeAdopt,
            Self::RuntimeRemove { .. } => ActionId::RuntimeRemove,
            Self::RuntimeStopManaging { .. } => ActionId::RuntimeStopManaging,
            Self::RuntimeSwitchSource { .. } => ActionId::RuntimeSwitchSource,
            Self::GameUpdate { .. } => ActionId::GameUpdate,
            Self::RecoverGameUpdate { .. } => ActionId::RecoverGameUpdate,
            Self::BridgeUpdate { .. } => ActionId::BridgeUpdate,
            Self::RecoverBridgeUpdate { .. } => ActionId::RecoverBridgeUpdate,
            Self::ExportDiagnostics { .. } => ActionId::ExportDiagnostics,
            Self::SaveApplicationPreferences { .. } => ActionId::SaveApplicationPreferences,
        }
    }
    pub fn target(&self) -> Option<&ResolvedTarget> {
        match self {
            Self::LaunchOrdinary { target, .. }
            | Self::LaunchIsolated { target, .. }
            | Self::FocusSession { target, .. } => Some(target),
            Self::SaveConfiguration { input } => Some(&input.draft.draft.document.target),
            Self::RestoreConfiguration { input } => Some(&input.document.target),
            Self::RuntimeInstall { input, .. }
            | Self::RuntimeUpdate { input, .. }
            | Self::RuntimeRepair { input, .. } => Some(&input.target),
            Self::RuntimeAdopt { input } => Some(&input.target),
            Self::RuntimeRemove { input } | Self::RuntimeStopManaging { input } => {
                Some(&input.reference.target)
            }
            Self::RuntimeSwitchSource { input, .. } => Some(&input.current.target),
            Self::ExportDiagnostics { input } => Some(&input.preview.target),
            _ => None,
        }
    }
    pub(crate) fn valid(&self) -> bool {
        match self {
            Self::LaunchOrdinary {
                target,
                runtime,
                unrecognized_runtime_choice,
                ..
            } => {
                matches!(target.profile, ProfileBinding::Ordinary { .. })
                    && runtime_choice_matches(runtime, unrecognized_runtime_choice)
            }
            Self::LaunchIsolated {
                target,
                runtime,
                unrecognized_runtime_choice,
                ..
            } => {
                matches!(target.profile, ProfileBinding::Isolated { .. })
                    && runtime_choice_matches(runtime, unrecognized_runtime_choice)
            }
            Self::FocusSession { session, target } => {
                &session.process.installation_physical_id == target.installation.physical_id()
            }
            Self::CreateProfile { input } => input.valid(),
            Self::EditOrdinaryProfile { .. } => true,
            Self::EditIsolatedProfile { input } => input.valid(),
            Self::ArchiveProfile { input } => input.profile.state == ProfileState::Active,
            Self::RestoreProfile { input } => input.profile.state == ProfileState::Archived,
            Self::DeleteProfile { .. } | Self::RegisterInstallation { .. } => true,
            Self::EditInstallation { input } => input.valid(),
            Self::SaveConfiguration { input } => input.valid(),
            Self::RestoreConfiguration { input } => input.valid(),
            Self::RuntimeInstall {
                input,
                prepared_configuration,
            } => {
                input.valid()
                    && matches!(input.expected_ownership, RuntimeOwnership::Absent)
                    && prepared_configuration.valid_for(
                        &input.configuration,
                        &input.selected_release.configuration_schema,
                    )
            }
            Self::RuntimeUpdate {
                input,
                prepared_configuration,
            }
            | Self::RuntimeRepair {
                input,
                prepared_configuration,
            } => {
                input.valid()
                    && matches!(input.expected_ownership, RuntimeOwnership::Managed { .. })
                    && prepared_configuration.valid_for(
                        &input.configuration,
                        &input.selected_release.configuration_schema,
                    )
            }
            Self::RuntimeAdopt { input } => input.valid(),
            Self::RuntimeRemove { .. } | Self::RuntimeStopManaging { .. } => true,
            Self::RuntimeSwitchSource {
                input,
                prepared_configuration,
            } => {
                input.valid()
                    && prepared_configuration.valid_for(
                        &input.configuration,
                        &input.selected_release.configuration_schema,
                    )
            }
            Self::GameUpdate { .. } | Self::RecoverGameUpdate { .. } => true,
            Self::BridgeUpdate { input } => input.selected_release.valid(),
            Self::RecoverBridgeUpdate { input } => input.recovery.valid(),
            Self::ExportDiagnostics { input } => input.valid(),
            Self::SaveApplicationPreferences { .. } => true,
        }
    }
}
fn configuration_host_matches(participant: &ConfigurationParticipant, host: &HostEpoch) -> bool {
    match participant {
        ConfigurationParticipant::SaveReviewedDraft { draft } => &draft.host_epoch == host,
        _ => true,
    }
}
fn runtime_choice_matches(
    runtime: &RuntimeExpectation,
    choice: &UnrecognizedRuntimeChoice,
) -> bool {
    match runtime {
        RuntimeExpectation::Unrecognized {
            choice: captured, ..
        } => captured == choice,
        _ => true,
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
pub enum HashProfile {
    #[serde(rename = "bridge-plan-semantic-json-v1")]
    BridgePlanSemanticJsonV1,
}
#[derive(
    Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize, JsonSchema,
)]
#[serde(rename_all = "snake_case")]
pub enum ProposedEffect {
    LaunchSession,
    CreateIsolatedStore,
    FocusSession,
    PublishProfile,
    EditProfileMetadata,
    ArchiveProfile,
    RestoreProfile,
    DeleteOwnedProfile,
    RegisterInstallation,
    EditInstallationMetadata,
    WriteConfiguration,
    ReplaceRuntime,
    AdoptRuntime,
    RemoveManagedRuntime,
    ReleaseRuntimeManagement,
    UpdateGame,
    RecoverGame,
    ReplaceBridge,
    RecoverBridge,
    ExportDiagnostics,
    SaveApplicationPreferences,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PlanSemantics {
    pub hash_profile: HashProfile,
    pub action: ActionId,
    pub capture: PreparedCapture,
    pub trust_domain: TrustDomain,
    pub effects: BoundedList<ProposedEffect, 16>,
}
impl PlanSemantics {
    pub(crate) fn valid(&self) -> bool {
        if self.action != self.capture.action()
            || self.trust_domain != self.capture.trust_domain()
            || !self.capture.valid()
        {
            return false;
        }
        let mut effects = self.effects.as_slice().to_vec();
        effects.sort();
        effects.dedup();
        if effects.len() != self.effects.as_slice().len() {
            return false;
        }
        let mut expected = match &self.capture {
            PreparedCapture::LaunchOrdinary { .. } => vec![ProposedEffect::LaunchSession],
            PreparedCapture::LaunchIsolated {
                store_mode: StoreMode::New,
                ..
            } => vec![
                ProposedEffect::LaunchSession,
                ProposedEffect::CreateIsolatedStore,
            ],
            PreparedCapture::LaunchIsolated { .. } => vec![ProposedEffect::LaunchSession],
            PreparedCapture::FocusSession { .. } => vec![ProposedEffect::FocusSession],
            PreparedCapture::CreateProfile { .. } => vec![ProposedEffect::PublishProfile],
            PreparedCapture::EditOrdinaryProfile { .. }
            | PreparedCapture::EditIsolatedProfile { .. } => {
                vec![ProposedEffect::EditProfileMetadata]
            }
            PreparedCapture::ArchiveProfile { .. } => vec![ProposedEffect::ArchiveProfile],
            PreparedCapture::RestoreProfile { .. } => vec![ProposedEffect::RestoreProfile],
            PreparedCapture::DeleteProfile { .. } => vec![ProposedEffect::DeleteOwnedProfile],
            PreparedCapture::RegisterInstallation { .. } => {
                vec![ProposedEffect::RegisterInstallation]
            }
            PreparedCapture::EditInstallation { .. } => {
                vec![ProposedEffect::EditInstallationMetadata]
            }
            PreparedCapture::SaveConfiguration { .. }
            | PreparedCapture::RestoreConfiguration { .. } => {
                vec![ProposedEffect::WriteConfiguration]
            }
            PreparedCapture::RuntimeInstall {
                prepared_configuration,
                ..
            }
            | PreparedCapture::RuntimeUpdate {
                prepared_configuration,
                ..
            }
            | PreparedCapture::RuntimeRepair {
                prepared_configuration,
                ..
            }
            | PreparedCapture::RuntimeSwitchSource {
                prepared_configuration,
                ..
            } => {
                if prepared_configuration.writes() {
                    vec![
                        ProposedEffect::ReplaceRuntime,
                        ProposedEffect::WriteConfiguration,
                    ]
                } else {
                    vec![ProposedEffect::ReplaceRuntime]
                }
            }
            PreparedCapture::RuntimeAdopt { .. } => vec![ProposedEffect::AdoptRuntime],
            PreparedCapture::RuntimeRemove { .. } => vec![ProposedEffect::RemoveManagedRuntime],
            PreparedCapture::RuntimeStopManaging { .. } => {
                vec![ProposedEffect::ReleaseRuntimeManagement]
            }
            PreparedCapture::GameUpdate { .. } => vec![ProposedEffect::UpdateGame],
            PreparedCapture::RecoverGameUpdate { .. } => vec![ProposedEffect::RecoverGame],
            PreparedCapture::BridgeUpdate { .. } => vec![ProposedEffect::ReplaceBridge],
            PreparedCapture::RecoverBridgeUpdate { .. } => vec![ProposedEffect::RecoverBridge],
            PreparedCapture::ExportDiagnostics { .. } => vec![ProposedEffect::ExportDiagnostics],
            PreparedCapture::SaveApplicationPreferences { .. } => {
                vec![ProposedEffect::SaveApplicationPreferences]
            }
        };
        expected.sort();
        effects == expected
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PlanRef {
    pub plan_id: PlanId,
    pub host_epoch: HostEpoch,
    pub review_digest: Sha256,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PreparedPlan {
    pub plan_ref: PlanRef,
    pub semantics: PlanSemantics,
    pub expires_at: UtcTimestamp,
    pub grants_lock: FalseFlag,
    pub grants_permission: FalseFlag,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ErrorCode {
    InvalidRequest,
    UnsupportedProtocol,
    UnknownCommand,
    InvalidTarget,
    ConflictingTarget,
    TargetMissing,
    TargetUnknown,
    StaleRevision,
    ProfileArchived,
    WrongOwner,
    UnsupportedCapability,
    OperationBusy,
    PlanExpired,
    PlanHostMismatch,
    IdempotencyConflict,
    NativeUnavailable,
    RecoveryRequired,
    ExportPreviewChanged,
    InternalFailure,
    InvalidConfiguration,
    UnsupportedSchema,
    UnsupportedPreservationSyntax,
    BackupUnavailable,
    PersistenceFailed,
    ArtifactUnrecognized,
    VerificationFailed,
    ReleaseWithdrawn,
    PairingMismatch,
    UnsupportedPlatform,
    ResnapshotRequired,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum RetryDisposition {
    Never,
    AfterResnapshot,
    AfterUserChoice,
    AfterRecovery,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum FieldPath {
    Envelope,
    ProtocolVersion,
    RequestId,
    Body,
    Target,
    Session,
    Plan,
    Operation,
    Cursor,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ViolationCode {
    InvalidFraming,
    InvalidShape,
    InvalidValue,
    ConflictingBinding,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FieldViolation {
    pub field: FieldPath,
    pub code: ViolationCode,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct BridgeError {
    pub code: ErrorCode,
    pub retry_disposition: RetryDisposition,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub supported_versions: Option<[ProtocolVersion; 1]>,
    pub violations: BoundedList<FieldViolation, 8>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub expected_revision: Option<ScopedRevisionRef>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub observed_revision: Option<ScopedRevisionRef>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub recovery: Option<RecoveryRef>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum RevisionScope {
    Profile {
        id: ProfileId,
    },
    Installation {
        physical_id: PhysicalInstallationId,
    },
    Document {
        id: DocumentId,
        target: ResolvedTarget,
        schema_digest: Sha256,
    },
    Operation {
        id: OperationId,
    },
    Application {
        id: ApplicationIdentity,
    },
    Catalog {
        owner: OwnerScope,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ScopedRevisionRef {
    pub scope: RevisionScope,
    pub revision: OpaqueRevision,
}
impl BridgeError {
    pub(crate) fn valid(&self) -> bool {
        (self.code == ErrorCode::UnsupportedProtocol) == self.supported_versions.is_some()
            && (self.code == ErrorCode::RecoveryRequired) == self.recovery.is_some()
            && (self.code != ErrorCode::RecoveryRequired
                || self.retry_disposition == RetryDisposition::AfterRecovery)
            && self.recovery.as_ref().is_none_or(RecoveryRef::valid)
            && match (&self.expected_revision, &self.observed_revision) {
                (Some(expected), Some(observed)) => expected.scope == observed.scope,
                (None, None) => true,
                _ => false,
            }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "unit",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum Measurement {
    Unknown,
    Bytes {
        completed: ProgressCount,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        total: Option<ProgressCount>,
    },
    Files {
        completed: ProgressCount,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        total: Option<ProgressCount>,
    },
}
impl Measurement {
    fn valid(&self) -> bool {
        match self {
            Self::Unknown => true,
            Self::Bytes { completed, total } | Self::Files { completed, total } => {
                total.is_none_or(|t| *completed <= t)
            }
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Progress {
    pub phase: PhaseId,
    pub measurement: Measurement,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum CompletionReason {
    Applied,
    AlreadySatisfied,
    CancellationAccepted,
    RollbackCompleted,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum CompletionOutcome {
    Changed {
        reason: CompletionReason,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        receipt: Option<Box<EffectReceipt>>,
    },
    NoChange {
        reason: CompletionReason,
    },
    CancelledBeforeCommit {
        reason: CompletionReason,
    },
    RolledBack {
        reason: CompletionReason,
    },
    Failed {
        error: Box<BridgeError>,
    },
}
impl CompletionOutcome {
    fn valid(&self) -> bool {
        match self {
            Self::Changed { reason, .. } => *reason == CompletionReason::Applied,
            Self::NoChange { reason } => *reason == CompletionReason::AlreadySatisfied,
            Self::CancelledBeforeCommit { reason } => {
                *reason == CompletionReason::CancellationAccepted
            }
            Self::RolledBack { reason } => *reason == CompletionReason::RollbackCompleted,
            Self::Failed { error } => error.valid() && error.code != ErrorCode::RecoveryRequired,
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
pub enum RecoveryTarget {
    Launch {
        target: ResolvedTarget,
    },
    Session {
        session: SessionBinding,
        target: ResolvedTarget,
    },
    Profile {
        profile: IsolatedProfileRef,
    },
    OrdinaryProfile {
        profile: OrdinaryProfileRef,
    },
    ProfileCreation {
        owner: OwnerScope,
        native_preparation_ref: NativePreparationRef,
    },
    Installation {
        installation: InstallationBinding,
    },
    InstallationRegistration {
        physical_id: PhysicalInstallationId,
        native_target_ref: NativeTargetRef,
    },
    Configuration {
        document: DocumentBinding,
    },
    Runtime {
        target: ResolvedTarget,
    },
    Game {
        recovery: NativeGameRecoveryRef,
    },
    Bridge {
        recovery: BridgeRecoveryRef,
    },
    Diagnostics {
        preview: PreviewRef,
        destination: ExportDestinationRef,
    },
    ApplicationPreferences {
        revision: OpaqueRevision,
    },
}
impl RecoveryTarget {
    fn valid(&self) -> bool {
        match self {
            Self::Bridge { recovery } => recovery.valid(),
            _ => true,
        }
    }

    fn matches(&self, capture: &PreparedCapture) -> bool {
        match (self, capture) {
            (
                Self::Launch { target },
                PreparedCapture::LaunchOrdinary {
                    target: captured, ..
                }
                | PreparedCapture::LaunchIsolated {
                    target: captured, ..
                },
            ) => target == captured,
            (
                Self::Session { session, target },
                PreparedCapture::FocusSession {
                    session: captured_session,
                    target: captured_target,
                },
            ) => session == captured_session && target == captured_target,
            (Self::Profile { profile }, PreparedCapture::EditIsolatedProfile { input }) => {
                profile == &input.profile
            }
            (
                Self::Profile { profile },
                PreparedCapture::ArchiveProfile { input }
                | PreparedCapture::RestoreProfile { input },
            ) => profile == &input.profile,
            (Self::Profile { profile }, PreparedCapture::DeleteProfile { input }) => {
                profile == &input.profile
            }
            (Self::OrdinaryProfile { profile }, PreparedCapture::EditOrdinaryProfile { input }) => {
                profile == &input.profile
            }
            (
                Self::ProfileCreation {
                    owner,
                    native_preparation_ref,
                },
                PreparedCapture::CreateProfile { input },
            ) => {
                owner == &input.destination_owner
                    && native_preparation_ref == &input.native_preparation_ref
            }
            (Self::Installation { installation }, PreparedCapture::EditInstallation { input }) => {
                installation == &input.installation
            }
            (
                Self::InstallationRegistration {
                    physical_id,
                    native_target_ref,
                },
                PreparedCapture::RegisterInstallation { input },
            ) => physical_id == &input.physical_id && native_target_ref == &input.native_target_ref,
            (Self::Configuration { document }, PreparedCapture::SaveConfiguration { input }) => {
                document == &input.draft.draft.document
            }
            (Self::Configuration { document }, PreparedCapture::RestoreConfiguration { input }) => {
                document == &input.document
            }
            (
                Self::Runtime { target },
                PreparedCapture::RuntimeInstall { input, .. }
                | PreparedCapture::RuntimeUpdate { input, .. }
                | PreparedCapture::RuntimeRepair { input, .. },
            ) => target == &input.target,
            (Self::Runtime { target }, PreparedCapture::RuntimeAdopt { input }) => {
                target == &input.target
            }
            (
                Self::Runtime { target },
                PreparedCapture::RuntimeRemove { input }
                | PreparedCapture::RuntimeStopManaging { input },
            ) => target == &input.reference.target,
            (Self::Runtime { target }, PreparedCapture::RuntimeSwitchSource { input, .. }) => {
                target == &input.current.target
            }
            (Self::Game { recovery }, PreparedCapture::GameUpdate { input }) => {
                recovery.installation == input.checked_update.installation
                    && input.checked_update.route == GameUpdateRoute::CanonicalNativeDirect
                    && recovery.expected_client == input.checked_update.current_client
            }
            (Self::Game { recovery }, PreparedCapture::RecoverGameUpdate { input }) => {
                recovery == &input.recovery
            }
            (Self::Bridge { recovery }, PreparedCapture::BridgeUpdate { input }) => {
                recovery.application == input.selected_release.current
                    && (recovery.expected_application == input.selected_release.current
                        || recovery.expected_application == input.selected_release.offered)
            }
            (Self::Bridge { recovery }, PreparedCapture::RecoverBridgeUpdate { input }) => {
                recovery == &input.recovery
            }
            (
                Self::Diagnostics {
                    preview,
                    destination,
                },
                PreparedCapture::ExportDiagnostics { input },
            ) => preview == &input.preview && destination == &input.destination,
            (
                Self::ApplicationPreferences { revision },
                PreparedCapture::SaveApplicationPreferences { input },
            ) => revision == &input.expected_revision,
            _ => false,
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RecoveryRef {
    pub operation_id: OperationId,
    pub transaction: NativeTransactionRef,
    pub target: RecoveryTarget,
}
impl RecoveryRef {
    fn valid(&self) -> bool {
        self.target.valid()
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum RecoveryReason {
    InterruptedTransaction,
    RollbackIncomplete,
    NativeCustodyUnresolved,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "status",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum OperationState {
    Admitted,
    Running {
        progress: Progress,
    },
    CancellationRequested {
        progress: Progress,
    },
    Completed {
        outcome: CompletionOutcome,
    },
    RecoveryRequired {
        recovery: Box<RecoveryRef>,
        reason: RecoveryReason,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct OperationSnapshot {
    pub operation_id: OperationId,
    pub operation_revision: RevisionCounter,
    pub semantics: PlanSemantics,
    pub state: OperationState,
}
impl OperationSnapshot {
    pub(crate) fn valid(&self) -> bool {
        self.semantics.valid()
            && match &self.state {
                OperationState::Admitted => true,
                OperationState::Running { progress }
                | OperationState::CancellationRequested { progress } => {
                    progress.measurement.valid()
                }
                OperationState::Completed { outcome } => {
                    outcome.valid()
                        && match outcome {
                            CompletionOutcome::Changed {
                                receipt: Some(receipt),
                                ..
                            } => receipt.matches(&self.semantics.capture),
                            CompletionOutcome::Changed { receipt: None, .. } => matches!(
                                self.semantics.capture,
                                PreparedCapture::LaunchOrdinary { .. }
                                    | PreparedCapture::LaunchIsolated { .. }
                                    | PreparedCapture::FocusSession { .. }
                            ),
                            _ => true,
                        }
                }
                OperationState::RecoveryRequired { recovery, .. } => {
                    recovery.valid()
                        && recovery.operation_id == self.operation_id
                        && recovery.target.matches(&self.semantics.capture)
                }
            }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum CancelDisposition {
    Requested { operation: OperationSnapshot },
    CancelledBeforeCommit { operation: OperationSnapshot },
    TooLate { operation: OperationSnapshot },
    AlreadyTerminal { operation: OperationSnapshot },
    RecoveryRequired { operation: OperationSnapshot },
}
impl CancelDisposition {
    pub(crate) fn valid(&self) -> bool {
        let operation = match self {
            Self::Requested { operation }
            | Self::CancelledBeforeCommit { operation }
            | Self::TooLate { operation }
            | Self::AlreadyTerminal { operation }
            | Self::RecoveryRequired { operation } => operation,
        };
        operation.valid()
            && match self {
                Self::Requested { .. } => matches!(
                    operation.state,
                    OperationState::CancellationRequested { .. }
                ),
                Self::CancelledBeforeCommit { .. } => matches!(
                    operation.state,
                    OperationState::Completed {
                        outcome: CompletionOutcome::CancelledBeforeCommit { .. }
                    }
                ),
                Self::TooLate { .. } => matches!(
                    operation.state,
                    OperationState::Running { .. }
                        | OperationState::Completed {
                            outcome: CompletionOutcome::Changed { .. }
                                | CompletionOutcome::NoChange { .. }
                        }
                ),
                Self::AlreadyTerminal { .. } => {
                    matches!(operation.state, OperationState::Completed { .. })
                }
                Self::RecoveryRequired { .. } => {
                    matches!(operation.state, OperationState::RecoveryRequired { .. })
                }
            }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Cursor {
    pub host_epoch: HostEpoch,
    pub stream_id: StreamId,
    pub sequence: Sequence,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum CloseObligation {
    Operation {
        operation_id: OperationId,
        operation_revision: RevisionCounter,
    },
    SessionCustody {
        session: SessionBinding,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum CloseDisposition {
    Ready,
    Deferred {
        // One pending operation and one retained session per 64-operation snapshot.
        obligations: BoundedList<CloseObligation, 128>,
    },
    RecoveryRequired {
        recoveries: BoundedList<RecoveryRef, 64>,
    },
}
impl CloseDisposition {
    pub(crate) fn valid(&self) -> bool {
        match self {
            Self::Ready => true,
            Self::Deferred { obligations } => !obligations.as_slice().is_empty(),
            Self::RecoveryRequired { recoveries } => {
                !recoveries.as_slice().is_empty()
                    && recoveries.as_slice().iter().all(RecoveryRef::valid)
            }
        }
    }
}
