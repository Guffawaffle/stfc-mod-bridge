use super::{
    configuration::*, distribution::*, management::*, primitives::*, projections::*, targets::*,
    workflow::*,
};
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum EffectReceipt {
    SessionSpawned {
        session: Box<SessionProjection>,
    },
    SessionFocused {
        session: SessionBinding,
    },
    ProfilePublished {
        profile: ProfileProjection,
    },
    ProfileEdited {
        profile: ProfileProjection,
    },
    ProfileArchived {
        profile: IsolatedProfileRef,
    },
    ProfileRestored {
        profile: IsolatedProfileRef,
    },
    ProfileDeleted {
        profile_id: ProfileId,
    },
    InstallationRegistered {
        installation: InstallationBinding,
        name: DisplayName,
    },
    InstallationEdited {
        installation: InstallationBinding,
        name: DisplayName,
    },
    ConfigurationWritten {
        document: DocumentBinding,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        backup: Option<Box<BackupReceiptRef>>,
    },
    RuntimeManaged {
        reference: ManagedRuntimeRef,
        configuration: Box<ConfigurationParticipantOutcome>,
    },
    RuntimeAdopted {
        reference: ManagedRuntimeRef,
    },
    RuntimeRemoved {
        target: ResolvedTarget,
    },
    RuntimeUnmanaged {
        target: ResolvedTarget,
    },
    GameUpdated {
        installation: InstallationBinding,
        client: GameClientBinding,
    },
    BridgeUpdated {
        application: BridgeApplicationBinding,
    },
    DiagnosticsExported {
        preview_digest: Sha256,
        destination: ExportDestinationRef,
    },
    ApplicationPreferencesSaved {
        snapshot: ApplicationPreferencesSnapshot,
    },
}
impl EffectReceipt {
    pub(crate) fn matches(&self, capture: &PreparedCapture) -> bool {
        match (self, capture) {
            (
                Self::SessionSpawned { session },
                PreparedCapture::LaunchOrdinary { target, .. }
                | PreparedCapture::LaunchIsolated { target, .. },
            ) => {
                session.valid()
                    && match &session.target {
                        Observation::Observed { value, .. } => value == target,
                        _ => false,
                    }
            }
            (
                Self::SessionFocused { session },
                PreparedCapture::FocusSession {
                    session: expected, ..
                },
            ) => session == expected,
            (
                Self::ProfilePublished {
                    profile:
                        ProfileProjection::Isolated {
                            reference,
                            name,
                            preferred_installation,
                            ..
                        },
                },
                PreparedCapture::CreateProfile { input },
            ) => {
                reference.state == ProfileState::Active
                    && name == &input.name
                    && preferred_installation.as_ref()
                        == Some(input.preferred_installation.registration_id())
            }
            (
                Self::ProfileEdited {
                    profile:
                        ProfileProjection::Ordinary {
                            reference,
                            preferred_installation,
                        },
                },
                PreparedCapture::EditOrdinaryProfile { input },
            ) => match (reference, &input.profile) {
                (
                    OrdinaryProfileRef::Ordinary {
                        catalog_id,
                        owner_scope,
                        ..
                    },
                    OrdinaryProfileRef::Ordinary {
                        catalog_id: expected_id,
                        owner_scope: expected_owner,
                        ..
                    },
                ) => {
                    catalog_id == expected_id
                        && owner_scope == expected_owner
                        && input
                            .preferred_installation
                            .matches(preferred_installation.as_ref())
                }
            },
            (
                Self::ProfileEdited {
                    profile:
                        ProfileProjection::Isolated {
                            reference,
                            name,
                            preferred_installation,
                            ..
                        },
                },
                PreparedCapture::EditIsolatedProfile { input },
            ) => {
                reference.id == input.profile.id
                    && reference.state == input.profile.state
                    && input.name.as_ref().is_none_or(|expected| name == expected)
                    && input
                        .preferred_installation
                        .matches(preferred_installation.as_ref())
            }
            (Self::ProfileArchived { profile }, PreparedCapture::ArchiveProfile { input }) => {
                profile.id == input.profile.id && profile.state == ProfileState::Archived
            }
            (Self::ProfileRestored { profile }, PreparedCapture::RestoreProfile { input }) => {
                profile.id == input.profile.id && profile.state == ProfileState::Active
            }
            (Self::ProfileDeleted { profile_id }, PreparedCapture::DeleteProfile { input }) => {
                profile_id == &input.profile.id
            }
            (
                Self::InstallationRegistered { installation, name },
                PreparedCapture::RegisterInstallation { input },
            ) => {
                matches!(installation, InstallationBinding::Registered { native_target_ref,.. } if native_target_ref==&input.native_target_ref)
                    && installation.physical_id() == &input.physical_id
                    && name == &input.name
            }
            (
                Self::InstallationEdited { installation, name },
                PreparedCapture::EditInstallation { input },
            ) => match (installation, &input.installation) {
                (
                    InstallationBinding::Registered {
                        registration_id,
                        physical_id,
                        native_target_ref,
                        ..
                    },
                    InstallationBinding::Registered {
                        registration_id: expected_id,
                        physical_id: expected_physical,
                        native_target_ref: expected_target,
                        ..
                    },
                ) => {
                    registration_id == expected_id
                        && physical_id == expected_physical
                        && native_target_ref == expected_target
                        && name == &input.name
                }
                _ => false,
            },
            (
                Self::ConfigurationWritten { document, backup },
                PreparedCapture::SaveConfiguration { input },
            ) => configuration_receipt_matches(
                document,
                backup,
                &input.draft.draft.document,
                &input.draft.draft.document.schema,
                &input.candidate_digest,
            ),
            (
                Self::ConfigurationWritten { document, backup },
                PreparedCapture::RestoreConfiguration { input },
            ) => configuration_receipt_matches(
                document,
                backup,
                &input.document,
                &input.document.schema,
                &input.backup.retained_digest,
            ),
            (
                Self::RuntimeManaged {
                    reference,
                    configuration,
                },
                PreparedCapture::RuntimeInstall {
                    input,
                    prepared_configuration,
                }
                | PreparedCapture::RuntimeUpdate {
                    input,
                    prepared_configuration,
                }
                | PreparedCapture::RuntimeRepair {
                    input,
                    prepared_configuration,
                },
            ) => {
                runtime_receipt_matches(reference, &input.selected_release)
                    && configuration.matches(prepared_configuration)
            }
            (
                Self::RuntimeManaged {
                    reference,
                    configuration,
                },
                PreparedCapture::RuntimeSwitchSource {
                    input,
                    prepared_configuration,
                },
            ) => {
                runtime_receipt_matches(reference, &input.selected_release)
                    && configuration.matches(prepared_configuration)
            }
            (Self::RuntimeAdopted { reference }, PreparedCapture::RuntimeAdopt { input }) => {
                reference.target == input.target && reference.binding == input.recognized_binding
            }
            (Self::RuntimeRemoved { target }, PreparedCapture::RuntimeRemove { input })
            | (Self::RuntimeUnmanaged { target }, PreparedCapture::RuntimeStopManaging { input }) => {
                target == &input.reference.target
            }
            (
                Self::GameUpdated {
                    installation,
                    client,
                },
                PreparedCapture::GameUpdate { input },
            ) => {
                installation == &input.checked_update.installation
                    && client == &input.checked_update.offered_client
            }
            (
                Self::GameUpdated {
                    installation,
                    client,
                },
                PreparedCapture::RecoverGameUpdate { input },
            ) => {
                installation == &input.recovery.installation
                    && client == &input.recovery.expected_client
            }
            (Self::BridgeUpdated { application }, PreparedCapture::BridgeUpdate { input }) => {
                application.valid() && application == &input.selected_release.offered
            }
            (
                Self::BridgeUpdated { application },
                PreparedCapture::RecoverBridgeUpdate { input },
            ) => input.recovery.valid() && application == &input.recovery.expected_application,
            (
                Self::DiagnosticsExported {
                    preview_digest,
                    destination,
                },
                PreparedCapture::ExportDiagnostics { input },
            ) => preview_digest == &input.preview.digest && destination == &input.destination,
            (
                Self::ApplicationPreferencesSaved { snapshot },
                PreparedCapture::SaveApplicationPreferences { input },
            ) => snapshot.values == input.values && snapshot.revision != input.expected_revision,
            _ => false,
        }
    }
}
fn configuration_receipt_matches(
    document: &DocumentBinding,
    backup: &Option<Box<BackupReceiptRef>>,
    expected: &DocumentBinding,
    destination_schema: &SchemaBinding,
    candidate_digest: &Sha256,
) -> bool {
    document.document_id == expected.document_id
        && document.target == expected.target
        && &document.schema == destination_schema
        && matches!(&document.baseline,DocumentBaseline::Existing{content_digest,..} if content_digest==candidate_digest)
        && matches!(document.baseline, DocumentBaseline::Existing { .. })
        && match (&expected.baseline, backup) {
            (DocumentBaseline::Missing, None) => true,
            (DocumentBaseline::Existing { content_digest, .. }, Some(backup)) => {
                backup.document == *expected && &backup.retained_digest == content_digest
            }
            _ => false,
        }
}
fn runtime_receipt_matches(
    reference: &ManagedRuntimeRef,
    release: &RuntimeReleaseSelectionRef,
) -> bool {
    reference.target == release.target
        && reference.binding.provider_id == release.provider_id
        && reference.binding.distribution_id == release.distribution_id
        && reference.binding.artifact_digest == release.configuration_schema.runtime_artifact_digest
        && reference.binding.configuration_schema_digest == release.configuration_schema.digest
        && reference.binding.client_revision == release.client_revision
        && release.artifacts.as_slice().iter().any(|a| {
            a.role == ArtifactRole::RuntimeModule
                && a.digest == reference.binding.artifact_digest
                && a.platform == reference.binding.platform
                && a.architecture == reference.binding.architecture
        })
        && match release.artifacts.as_slice().iter().find(|a| {
            a.role == ArtifactRole::RuntimeManifest
                && a.platform == reference.binding.platform
                && a.architecture == reference.binding.architecture
        }) {
            Some(subject) => {
                matches!(&reference.binding.manifest,RuntimeManifestObservation::Observed{digest} if digest==&subject.digest)
            }
            None => !matches!(
                reference.binding.manifest,
                RuntimeManifestObservation::Observed { .. }
            ),
        }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum ConfigurationParticipantOutcome {
    Unchanged {
        document: DocumentBinding,
    },
    Written {
        document: DocumentBinding,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        backup: Option<Box<BackupReceiptRef>>,
    },
}
impl ConfigurationParticipantOutcome {
    fn matches(&self, prepared: &PreparedConfigurationEffect) -> bool {
        match (self, prepared) {
            (
                Self::Unchanged { document },
                PreparedConfigurationEffect::Unchanged { document: expected },
            ) => document == expected,
            (
                Self::Written { document, backup },
                PreparedConfigurationEffect::Write {
                    baseline,
                    destination_schema,
                    candidate_digest,
                },
            ) => configuration_receipt_matches(
                document,
                backup,
                baseline,
                destination_schema,
                candidate_digest,
            ),
            _ => false,
        }
    }
}
