//! Typed projections of producer schemas and Bridge-owned draft custody.
//! These DTOs do not replace the shared TOML engine or expose its source bytes.
use super::{primitives::*, targets::*, wire::SetDraftChangesInput};
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SchemaBinding {
    pub provider_id: ProviderId,
    pub schema_id: SchemaId,
    pub schema_version: SchemaVersion,
    pub digest: Sha256,
    pub runtime_artifact_digest: Sha256,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum DocumentBaseline {
    Missing,
    Existing {
        file_identity: NativeFileIdentity,
        content_digest: Sha256,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DocumentBinding {
    pub document_id: DocumentId,
    pub target: ResolvedTarget,
    pub revision: OpaqueRevision,
    pub baseline: DocumentBaseline,
    pub schema: SchemaBinding,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DraftRef {
    pub draft_id: DraftId,
    pub host_epoch: HostEpoch,
    pub revision: RevisionCounter,
    pub document: DocumentBinding,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SecretRef {
    pub secret_id: SecretRefId,
    pub draft: DraftRef,
    pub field_id: FieldId,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PrivateValueRef {
    pub value_id: PrivateValueId,
    pub document: DocumentBinding,
    pub field_id: FieldId,
    pub revision: OpaqueRevision,
    /// Present only for Bridge-protected entry custody. Saved document values
    /// remain bound to the document revision and do not claim a native handle.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub captured_for: Option<Box<DraftRef>>,
}
impl PrivateValueRef {
    pub(crate) fn valid_for(&self, document: &DocumentBinding) -> bool {
        &self.document == document
            && self
                .captured_for
                .as_deref()
                .is_none_or(|draft| &draft.document == document)
    }
    pub(crate) fn valid_for_draft(&self, draft: &DraftRef) -> bool {
        self.valid_for(&draft.document)
            && self
                .captured_for
                .as_deref()
                .is_none_or(|captured| captured == draft)
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum SensitiveInputKind {
    Private,
    Secret,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RequestSensitiveInputInput {
    pub draft: DraftRef,
    pub field_id: FieldId,
    pub sensitivity: SensitiveInputKind,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum SensitiveInputUnavailableReason {
    NativeUnavailable,
    UnsupportedPlatform,
    AccessDenied,
    ProtectedEntryUnavailable,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "status", rename_all = "snake_case", deny_unknown_fields)]
pub enum SensitiveInputOutcome {
    CapturedPrivate {
        reference: Box<PrivateValueRef>,
    },
    CapturedSecret {
        reference: Box<SecretRef>,
    },
    Cancelled,
    Unavailable {
        reason: SensitiveInputUnavailableReason,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SensitiveInputResult {
    pub binding: RequestSensitiveInputInput,
    pub outcome: SensitiveInputOutcome,
}
impl SensitiveInputResult {
    pub(crate) fn valid(&self) -> bool {
        match &self.outcome {
            SensitiveInputOutcome::CapturedPrivate { reference } => {
                self.binding.sensitivity == SensitiveInputKind::Private
                    && reference.valid_for(&self.binding.draft.document)
                    && reference.field_id == self.binding.field_id
                    && reference.captured_for.as_deref() == Some(&self.binding.draft)
            }
            SensitiveInputOutcome::CapturedSecret { reference } => {
                self.binding.sensitivity == SensitiveInputKind::Secret
                    && reference.draft == self.binding.draft
                    && reference.field_id == self.binding.field_id
            }
            SensitiveInputOutcome::Cancelled | SensitiveInputOutcome::Unavailable { .. } => true,
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum Modifier {
    Control,
    Alt,
    Shift,
    Meta,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct KeyChord {
    pub key: KeyToken,
    pub modifiers: BoundedList<Modifier, 4>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum NotificationPolicy {
    Disabled,
    SystemOnly,
    Channels {
        system: bool,
        audio: bool,
        sound: EnumValue,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    content = "value",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum PublicConfigValue {
    Boolean(bool),
    Integer(SignedInteger),
    Number(DecimalValue),
    String(ConfigString),
    Enum(EnumValue),
    Keybinding(BoundedList<KeyChord, 8>),
    NotificationPolicy(NotificationPolicy),
}
impl PublicConfigValue {
    pub(crate) fn valid(&self) -> bool {
        match self {
            Self::Keybinding(chords) => {
                super::unique(chords.as_slice())
                    && chords
                        .as_slice()
                        .iter()
                        .all(|c| super::unique(c.modifiers.as_slice()))
            }
            _ => true,
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum Sensitivity {
    Public,
    Private,
    Secret,
}
#[derive(
    Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize, JsonSchema,
)]
#[serde(rename_all = "snake_case")]
pub enum ApplyTiming {
    Immediate,
    NextLaunch,
    RestartRequired,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum SupportedPlatform {
    Windows,
    Macos,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum FieldType {
    Boolean,
    Integer {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        minimum: Option<SignedInteger>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        maximum: Option<SignedInteger>,
    },
    Number {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        minimum: Option<DecimalValue>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        maximum: Option<DecimalValue>,
    },
    String {
        maximum_length: ProgressCount,
    },
    Enum {
        values: BoundedList<EnumValue, 128>,
    },
    Keybinding {
        multiple: bool,
        keys: BoundedList<KeyToken, 128>,
    },
    NotificationPolicy {
        sounds: BoundedList<EnumValue, 128>,
    },
}
impl FieldType {
    fn valid(&self) -> bool {
        match self {
            Self::Integer { minimum, maximum } => {
                minimum.zip(*maximum).is_none_or(|(min, max)| min <= max)
            }
            Self::Number { minimum, maximum } => match (minimum, maximum) {
                (Some(min), Some(max)) => min.compare(max) != std::cmp::Ordering::Greater,
                _ => true,
            },
            Self::String { maximum_length } => maximum_length.get() <= 4096,
            Self::Enum { values } | Self::NotificationPolicy { sounds: values } => {
                !values.as_slice().is_empty() && super::unique(values.as_slice())
            }
            Self::Keybinding { keys, .. } => {
                !keys.as_slice().is_empty() && super::unique(keys.as_slice())
            }
            Self::Boolean => true,
        }
    }
    pub(crate) fn accepts(&self, value: &PublicConfigValue) -> bool {
        value.valid()
            && match (self, value) {
                (Self::Boolean, PublicConfigValue::Boolean(_)) => true,
                (Self::Integer { minimum, maximum }, PublicConfigValue::Integer(v)) => {
                    minimum.is_none_or(|m| *v >= m) && maximum.is_none_or(|m| *v <= m)
                }
                (Self::Number { minimum, maximum }, PublicConfigValue::Number(v)) => {
                    minimum
                        .as_ref()
                        .is_none_or(|m| v.compare(m) != std::cmp::Ordering::Less)
                        && maximum
                            .as_ref()
                            .is_none_or(|m| v.compare(m) != std::cmp::Ordering::Greater)
                }
                (Self::String { maximum_length }, PublicConfigValue::String(v)) => {
                    v.as_str().chars().count() as u64 <= maximum_length.get()
                }
                (Self::Enum { values }, PublicConfigValue::Enum(v)) => {
                    values.as_slice().contains(v)
                }
                (Self::Keybinding { multiple, keys }, PublicConfigValue::Keybinding(chords)) => {
                    (*multiple || chords.as_slice().len() <= 1)
                        && chords
                            .as_slice()
                            .iter()
                            .all(|c| keys.as_slice().contains(&c.key))
                }
                (
                    Self::NotificationPolicy { sounds },
                    PublicConfigValue::NotificationPolicy(policy),
                ) => match policy {
                    NotificationPolicy::Channels { sound, .. } => sounds.as_slice().contains(sound),
                    _ => true,
                },
                _ => false,
            }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FieldDefinition {
    pub field_id: FieldId,
    pub path: BoundedList<TomlPathSegment, 16>,
    pub value_type: FieldType,
    pub sensitivity: Sensitivity,
    pub category: CategoryId,
    pub search_terms: BoundedList<SchemaText, 32>,
    pub apply: ApplyTiming,
    pub platforms: BoundedList<SupportedPlatform, 2>,
    pub aliases: BoundedList<BoundedList<TomlPathSegment, 16>, 16>,
    pub deprecated: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub default_value: Option<PublicConfigValue>,
}
impl FieldDefinition {
    fn valid(&self) -> bool {
        !self.path.as_slice().is_empty()
            && self.value_type.valid()
            && !self.platforms.as_slice().is_empty()
            && super::unique(self.platforms.as_slice())
            && self
                .aliases
                .as_slice()
                .iter()
                .all(|a| !a.as_slice().is_empty())
            && super::unique(self.aliases.as_slice())
            && match &self.default_value {
                Some(v) => self.sensitivity == Sensitivity::Public && self.value_type.accepts(v),
                None => true,
            }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ConfigurationSchema {
    pub binding: SchemaBinding,
    pub fields: BoundedList<FieldDefinition, 512>,
    pub sync: BoundedList<SyncTypeDefinition, 16>,
}
impl ConfigurationSchema {
    pub(crate) fn valid(&self) -> bool {
        // A decoded TOML key must have one schema owner, including legacy
        // aliases. Literal segments stay distinct from dotted path segments.
        let paths: Vec<_> = self
            .fields
            .as_slice()
            .iter()
            .flat_map(|field| std::iter::once(&field.path).chain(field.aliases.as_slice().iter()))
            .collect();
        self.fields.as_slice().iter().all(FieldDefinition::valid)
            && super::unique_by(self.fields.as_slice(), |f| f.field_id.clone())
            && super::unique(&paths)
            && self.sync.as_slice().iter().all(SyncTypeDefinition::valid)
            && self.sync.as_slice().iter().all(|definition| {
                definition.fields.as_slice().iter().all(|id| {
                    self.fields
                        .as_slice()
                        .iter()
                        .any(|field| &field.field_id == id)
                }) && self.fields.as_slice().iter().any(|field| {
                    field.field_id == definition.endpoint_field_id
                        && field.sensitivity == Sensitivity::Private
                }) && self.fields.as_slice().iter().any(|field| {
                    field.field_id == definition.secret_field_id
                        && field.sensitivity == Sensitivity::Secret
                }) && definition.proxy_field_id.as_ref().is_none_or(|id| {
                    self.fields.as_slice().iter().any(|field| {
                        &field.field_id == id && field.sensitivity == Sensitivity::Private
                    })
                })
            })
            && super::unique_by(self.sync.as_slice(), |s| s.mode)
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum ProjectedValue {
    Public { value: PublicConfigValue },
    Private { reference: Box<PrivateValueRef> },
    Secret { configured: bool },
    Absent,
    Unknown { reason: ObservationReason },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FieldState {
    pub field_id: FieldId,
    pub overridden: bool,
    pub value: ProjectedValue,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum PreservationState {
    Supported,
    UnsupportedTargetSyntax,
    InvalidDocument,
    NativeUnavailable,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DocumentSnapshot {
    pub binding: DocumentBinding,
    pub schema: ConfigurationSchema,
    pub fields: BoundedList<FieldState, 512>,
    pub preservation: PreservationState,
    pub sync: BoundedList<SyncDestination, 128>,
}
impl DocumentSnapshot {
    pub(crate) fn valid(&self) -> bool {
        if self.binding.schema != self.schema.binding
            || !self.schema.valid()
            || self.fields.as_slice().len() != self.schema.fields.as_slice().len()
            || !super::unique_by(self.fields.as_slice(), |f| f.field_id.clone())
            || (matches!(self.binding.baseline, DocumentBaseline::Missing)
                && (self.fields.as_slice().iter().any(|f| f.overridden)
                    || !self.sync.as_slice().is_empty()))
        {
            return false;
        }
        self.fields.as_slice().iter().all(|state| {
            self.schema
                .fields
                .as_slice()
                .iter()
                .find(|f| f.field_id == state.field_id)
                .is_some_and(|definition| match &state.value {
                    ProjectedValue::Absent => !state.overridden,
                    ProjectedValue::Public { value } => {
                        definition.sensitivity == Sensitivity::Public
                            && definition.value_type.accepts(value)
                    }
                    ProjectedValue::Private { reference } => {
                        definition.sensitivity == Sensitivity::Private
                            && reference.valid_for(&self.binding)
                            && reference.field_id == state.field_id
                    }
                    ProjectedValue::Secret { .. } => definition.sensitivity == Sensitivity::Secret,
                    ProjectedValue::Unknown { .. } => true,
                })
        }) && self.sync.as_slice().iter().all(|s| {
            s.valid(&self.binding)
                && self
                    .schema
                    .sync
                    .as_slice()
                    .iter()
                    .find(|definition| definition.mode == s.mode)
                    .is_some_and(|definition| {
                        definition.exposure == s.exposure
                            && s.feeds
                                .as_slice()
                                .iter()
                                .all(|f| definition.feeds.as_slice().contains(&f.feed_id))
                    })
        }) && super::unique_by(self.sync.as_slice(), |s| s.id.clone())
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "kind",
    rename_all = "snake_case",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum ConfigurationEdit {
    SetPublic {
        field_id: FieldId,
        value: PublicConfigValue,
    },
    SetPrivate {
        field_id: FieldId,
        reference: Box<PrivateValueRef>,
    },
    ReplaceSecret {
        field_id: FieldId,
        reference: Box<SecretRef>,
    },
    ClearSecret {
        field_id: FieldId,
    },
    RemoveOverride {
        field_id: FieldId,
    },
    AddSyncDestination {
        destination: Box<NewSyncDestination>,
    },
    RemoveSyncDestination {
        destination_id: DestinationId,
    },
    SetSyncFeed {
        destination_id: DestinationId,
        feed_id: FeedId,
        value: InheritedBoolean,
    },
    SetSyncProxy {
        destination_id: DestinationId,
        value: ProxyChoice,
    },
}
impl ConfigurationEdit {
    pub(crate) fn valid(&self, draft: &DraftRef) -> bool {
        match self {
            Self::SetPublic { value, .. } => value.valid(),
            Self::SetPrivate {
                field_id,
                reference,
            } => reference.valid_for_draft(draft) && reference.field_id == *field_id,
            Self::ReplaceSecret {
                field_id,
                reference,
            } => reference.draft == *draft && reference.field_id == *field_id,
            Self::AddSyncDestination { destination } => {
                destination.valid(&draft.document)
                    && destination.secret.draft == *draft
                    && destination.endpoint.valid_for_draft(draft)
                    && destination.proxy.valid_for_draft(draft)
            }
            Self::SetSyncProxy { value, .. } => value.valid_for_draft(draft),
            _ => true,
        }
    }
    pub(crate) fn key(&self) -> String {
        match self {
            Self::SetPublic { field_id, .. }
            | Self::SetPrivate { field_id, .. }
            | Self::ReplaceSecret { field_id, .. }
            | Self::ClearSecret { field_id }
            | Self::RemoveOverride { field_id } => format!("field:{}", field_id.as_str()),
            Self::AddSyncDestination { destination } => {
                format!("destination:{}", destination.id.as_str())
            }
            Self::RemoveSyncDestination { destination_id } => {
                format!("destination:{}", destination_id.as_str())
            }
            Self::SetSyncFeed {
                destination_id,
                feed_id,
                ..
            } => format!("feed:{}:{}", destination_id.as_str(), feed_id.as_str()),
            Self::SetSyncProxy { destination_id, .. } => {
                format!("proxy:{}", destination_id.as_str())
            }
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DraftSnapshot {
    pub draft: DraftRef,
    pub schema: ConfigurationSchema,
    pub edits: BoundedList<ConfigurationEdit, 256>,
    pub apply: BoundedList<ApplyTiming, 3>,
    pub state: DraftState,
    pub validation: BoundedList<DraftViolation, 64>,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum DraftViolationCode {
    UnknownField,
    InvalidType,
    ConstraintViolation,
    PrivateValueRequired,
    SecretReferenceRequired,
    UnsupportedSyncField,
    StaleSchema,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DraftViolation {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub field_id: Option<FieldId>,
    pub code: DraftViolationCode,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum DraftState {
    Clean,
    Dirty,
    Invalid,
    Stale,
}
impl DraftSnapshot {
    pub(crate) fn valid(&self) -> bool {
        if self.schema.binding != self.draft.document.schema
            || !self.schema.valid()
            || !self.edits.as_slice().iter().all(|e| e.valid(&self.draft))
            || !super::unique_by(self.edits.as_slice(), |e| e.key())
            || !super::unique(self.apply.as_slice())
            || (self.state == DraftState::Clean && !self.edits.as_slice().is_empty())
            || (self.state == DraftState::Invalid) != !self.validation.as_slice().is_empty()
        {
            return false;
        }
        let mut apply = Vec::new();
        let mut edits_valid = true;
        let mut sensitivity_valid = true;
        for edit in self.edits.as_slice() {
            let field_id = match edit {
                ConfigurationEdit::SetPublic { field_id, .. }
                | ConfigurationEdit::SetPrivate { field_id, .. }
                | ConfigurationEdit::ReplaceSecret { field_id, .. }
                | ConfigurationEdit::ClearSecret { field_id }
                | ConfigurationEdit::RemoveOverride { field_id } => Some(field_id),
                _ => None,
            };
            if let Some(id) = field_id {
                if let Some(field) = self
                    .schema
                    .fields
                    .as_slice()
                    .iter()
                    .find(|f| &f.field_id == id)
                {
                    apply.push(field.apply);
                    sensitivity_valid &= match edit {
                        ConfigurationEdit::SetPublic { .. } => {
                            field.sensitivity == Sensitivity::Public
                        }
                        ConfigurationEdit::SetPrivate { .. } => {
                            field.sensitivity == Sensitivity::Private
                        }
                        ConfigurationEdit::ReplaceSecret { .. }
                        | ConfigurationEdit::ClearSecret { .. } => {
                            field.sensitivity == Sensitivity::Secret
                        }
                        _ => true,
                    };
                    edits_valid &= match edit {
                        ConfigurationEdit::SetPublic { value, .. } => {
                            field.sensitivity == Sensitivity::Public
                                && field.value_type.accepts(value)
                        }
                        ConfigurationEdit::SetPrivate { .. } => {
                            field.sensitivity == Sensitivity::Private
                        }
                        ConfigurationEdit::ReplaceSecret { .. }
                        | ConfigurationEdit::ClearSecret { .. } => {
                            field.sensitivity == Sensitivity::Secret
                        }
                        _ => true,
                    };
                } else {
                    edits_valid = false;
                    sensitivity_valid &= matches!(edit, ConfigurationEdit::RemoveOverride { .. });
                }
            } else {
                apply.push(ApplyTiming::NextLaunch);
                let has_sensitivity = |id: &FieldId, sensitivity: Sensitivity| {
                    self.schema
                        .fields
                        .as_slice()
                        .iter()
                        .any(|field| &field.field_id == id && field.sensitivity == sensitivity)
                };
                match edit {
                    ConfigurationEdit::AddSyncDestination { destination } => {
                        sensitivity_valid &=
                            has_sensitivity(&destination.endpoint.field_id, Sensitivity::Private)
                                && has_sensitivity(
                                    &destination.secret.field_id,
                                    Sensitivity::Secret,
                                );
                        if let ProxyChoice::Custom { reference } = &destination.proxy {
                            sensitivity_valid &=
                                has_sensitivity(&reference.field_id, Sensitivity::Private);
                        }
                        edits_valid &= self
                            .schema
                            .sync
                            .as_slice()
                            .iter()
                            .find(|definition| definition.mode == destination.mode)
                            .is_some_and(|definition| {
                                definition.exposure == SyncExposure::Creatable
                                    && definition.endpoint_field_id == destination.endpoint.field_id
                                    && definition.secret_field_id == destination.secret.field_id
                                    && match &destination.proxy {
                                        ProxyChoice::Global => definition.inherits_global_proxy,
                                        ProxyChoice::None => true,
                                        ProxyChoice::Custom { reference } => {
                                            definition.proxy_field_id.as_ref()
                                                == Some(&reference.field_id)
                                        }
                                    }
                                    && destination.feeds.as_slice().iter().all(|feed| {
                                        definition.feeds.as_slice().contains(&feed.feed_id)
                                    })
                            });
                    }
                    ConfigurationEdit::SetSyncProxy {
                        value: ProxyChoice::Custom { reference },
                        ..
                    } => {
                        sensitivity_valid &=
                            has_sensitivity(&reference.field_id, Sensitivity::Private)
                    }
                    ConfigurationEdit::SetSyncFeed { feed_id, .. } => {
                        edits_valid &= self
                            .schema
                            .sync
                            .as_slice()
                            .iter()
                            .any(|definition| definition.feeds.as_slice().contains(feed_id))
                    }
                    _ => {}
                }
            }
        }
        apply.sort();
        apply.dedup();
        let mut observed_apply = self.apply.as_slice().to_vec();
        observed_apply.sort();
        sensitivity_valid
            && apply == observed_apply
            && (edits_valid || self.state == DraftState::Invalid)
    }
}
/// A closed substitution of backend-owned protected entry references. The
/// protocol proves binding continuity, never the native payload or custody.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum ProtectedReferenceTransfer {
    Private {
        from: Box<PrivateValueRef>,
        to: Box<PrivateValueRef>,
    },
    Secret {
        from: Box<SecretRef>,
        to: Box<SecretRef>,
    },
}
impl ProtectedReferenceTransfer {
    fn source_key(&self) -> (&'static str, &str) {
        match self {
            Self::Private { from, .. } => ("private", from.value_id.as_str()),
            Self::Secret { from, .. } => ("secret", from.secret_id.as_str()),
        }
    }
    fn destination_key(&self) -> (&'static str, &str) {
        match self {
            Self::Private { to, .. } => ("private", to.value_id.as_str()),
            Self::Secret { to, .. } => ("secret", to.secret_id.as_str()),
        }
    }
    fn valid_for(&self, previous: &DraftRef, successor: &DraftRef) -> bool {
        match self {
            Self::Private { from, to } => {
                from.valid_for_draft(previous)
                    && to.valid_for_draft(successor)
                    && from.captured_for.as_deref() == Some(previous)
                    && to.captured_for.as_deref() == Some(successor)
                    && from.document == to.document
                    && from.field_id == to.field_id
                    && from.revision == to.revision
                    && from.value_id != to.value_id
            }
            Self::Secret { from, to } => {
                from.draft == *previous
                    && to.draft == *successor
                    && from.field_id == to.field_id
                    && from.secret_id != to.secret_id
            }
        }
    }
}

/// Exact old input and its single successor. Only the backend may mint the
/// transfers after proving native custody. An exact retry returns this retained
/// acknowledgment; it must not perform another transfer or revision increment.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SetDraftChangesResult {
    pub accepted: SetDraftChangesInput,
    pub snapshot: DraftSnapshot,
    pub protected_transfers: BoundedList<ProtectedReferenceTransfer, 256>,
}
impl SetDraftChangesResult {
    pub(crate) fn valid(&self) -> bool {
        let previous = &self.accepted.draft;
        let successor = &self.snapshot.draft;
        let sources: Vec<_> = self
            .protected_transfers
            .as_slice()
            .iter()
            .map(ProtectedReferenceTransfer::source_key)
            .collect();
        let destinations: Vec<_> = self
            .protected_transfers
            .as_slice()
            .iter()
            .map(ProtectedReferenceTransfer::destination_key)
            .collect();
        if !self.accepted.valid()
            || !self.snapshot.valid()
            || previous.draft_id != successor.draft_id
            || previous.host_epoch != successor.host_epoch
            || previous.document != successor.document
            || previous.revision.get().checked_add(1) != Some(successor.revision.get())
            || !self
                .protected_transfers
                .as_slice()
                .iter()
                .all(|transfer| transfer.valid_for(previous, successor))
            || !super::unique(&sources)
            || !super::unique(&destinations)
        {
            return false;
        }
        // Derive the sole permitted edit result. Values, keys, order and all
        // non-reference intent remain exact; every supplied transfer is used.
        let mut edits = self.accepted.edits.as_slice().to_vec();
        let mut used = vec![false; self.protected_transfers.as_slice().len()];
        for edit in &mut edits {
            let valid = match edit {
                ConfigurationEdit::SetPrivate { reference, .. } => {
                    self.transfer_private(reference, &mut used)
                }
                ConfigurationEdit::ReplaceSecret { reference, .. } => {
                    self.transfer_secret(reference, &mut used)
                }
                ConfigurationEdit::AddSyncDestination { destination } => {
                    self.transfer_private(&mut destination.endpoint, &mut used)
                        && self.transfer_secret(&mut destination.secret, &mut used)
                        && self.transfer_proxy(&mut destination.proxy, &mut used)
                }
                ConfigurationEdit::SetSyncProxy { value, .. } => {
                    self.transfer_proxy(value, &mut used)
                }
                _ => true,
            };
            if !valid {
                return false;
            }
        }
        used.into_iter().all(|value| value) && edits.as_slice() == self.snapshot.edits.as_slice()
    }

    fn transfer_private(&self, reference: &mut PrivateValueRef, used: &mut [bool]) -> bool {
        // A new handle must not alias any previously accepted protected/saved
        // handle. Saved document references are preserved and never transferred.
        if self
            .protected_transfers
            .as_slice()
            .iter()
            .any(|transfer| transfer.destination_key() == ("private", reference.value_id.as_str()))
        {
            return false;
        }
        if reference.captured_for.is_none() {
            return true;
        }
        for (index, transfer) in self.protected_transfers.as_slice().iter().enumerate() {
            if let ProtectedReferenceTransfer::Private { from, to } = transfer
                && from.as_ref() == &*reference
            {
                *reference = to.as_ref().clone();
                used[index] = true;
                return true;
            }
        }
        false
    }

    fn transfer_secret(&self, reference: &mut SecretRef, used: &mut [bool]) -> bool {
        if self
            .protected_transfers
            .as_slice()
            .iter()
            .any(|transfer| transfer.destination_key() == ("secret", reference.secret_id.as_str()))
        {
            return false;
        }
        for (index, transfer) in self.protected_transfers.as_slice().iter().enumerate() {
            if let ProtectedReferenceTransfer::Secret { from, to } = transfer
                && from.as_ref() == &*reference
            {
                *reference = to.as_ref().clone();
                used[index] = true;
                return true;
            }
        }
        false
    }

    fn transfer_proxy(&self, proxy: &mut ProxyChoice, used: &mut [bool]) -> bool {
        match proxy {
            ProxyChoice::Custom { reference } => self.transfer_private(reference, used),
            ProxyChoice::Global | ProxyChoice::None => true,
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct BackupReceiptRef {
    pub backup_id: BackupId,
    pub document: DocumentBinding,
    pub retained_digest: Sha256,
    pub native_backup_ref: NativeTargetRef,
    pub created_at: UtcTimestamp,
}
impl BackupReceiptRef {
    pub(crate) fn valid(&self) -> bool {
        matches!(&self.document.baseline, DocumentBaseline::Existing {content_digest, ..} if content_digest==&self.retained_digest)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum SyncMode {
    Legacy,
    Sidecar,
    Majel,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum SyncExposure {
    Creatable,
    ExistingConfigurationOnly,
    Hidden,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SyncTypeDefinition {
    pub mode: SyncMode,
    pub exposure: SyncExposure,
    pub feeds: BoundedList<FeedId, 128>,
    pub fields: BoundedList<FieldId, 128>,
    pub endpoint_field_id: FieldId,
    pub secret_field_id: FieldId,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub proxy_field_id: Option<FieldId>,
    pub inherits_global_proxy: bool,
}
impl SyncTypeDefinition {
    fn valid(&self) -> bool {
        super::unique(self.feeds.as_slice())
            && super::unique(self.fields.as_slice())
            && self.endpoint_field_id != self.secret_field_id
            && self.fields.as_slice().contains(&self.endpoint_field_id)
            && self.fields.as_slice().contains(&self.secret_field_id)
            && self.proxy_field_id.as_ref().is_none_or(|id| {
                id != &self.endpoint_field_id
                    && id != &self.secret_field_id
                    && self.fields.as_slice().contains(id)
            })
            && match self.mode {
                SyncMode::Legacy => true,
                SyncMode::Sidecar => {
                    self.exposure == SyncExposure::ExistingConfigurationOnly
                        && !self.inherits_global_proxy
                }
                SyncMode::Majel => self.exposure == SyncExposure::Hidden,
            }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum InheritedBoolean {
    Inherit,
    On,
    Off,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum ProxyChoice {
    Global,
    None,
    Custom { reference: Box<PrivateValueRef> },
}
impl ProxyChoice {
    fn valid(&self, document: &DocumentBinding) -> bool {
        match self {
            Self::Custom { reference } => reference.valid_for(document),
            _ => true,
        }
    }
    fn valid_for_draft(&self, draft: &DraftRef) -> bool {
        match self {
            Self::Custom { reference } => reference.valid_for_draft(draft),
            _ => true,
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SyncFeed {
    pub feed_id: FeedId,
    pub desired: InheritedBoolean,
    pub resolved: Observation<bool>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SyncFeedOverride {
    pub feed_id: FeedId,
    pub desired: InheritedBoolean,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SyncDestination {
    pub id: DestinationId,
    pub mode: SyncMode,
    pub exposure: SyncExposure,
    pub endpoint: PrivateValueRef,
    pub secret_configured: bool,
    pub desired_proxy: ProxyChoice,
    pub resolved_proxy: Observation<PrivateValueRef>,
    pub feeds: BoundedList<SyncFeed, 128>,
}
impl SyncDestination {
    fn valid(&self, document: &DocumentBinding) -> bool {
        self.endpoint.valid_for(document)
            && self.desired_proxy.valid(document)
            && match &self.resolved_proxy {
                Observation::Observed { value, .. } => value.valid_for(document),
                _ => true,
            }
            && super::unique_by(self.feeds.as_slice(), |f| f.feed_id.clone())
            && match self.mode {
                SyncMode::Legacy => true,
                SyncMode::Sidecar => {
                    self.id.as_str() == "local-sidecar"
                        && self.exposure == SyncExposure::ExistingConfigurationOnly
                        && !matches!(self.desired_proxy, ProxyChoice::Global)
                }
                SyncMode::Majel => self.exposure == SyncExposure::Hidden,
            }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NewSyncDestination {
    pub id: DestinationId,
    pub mode: SyncMode,
    pub endpoint: PrivateValueRef,
    pub secret: SecretRef,
    pub proxy: ProxyChoice,
    pub feeds: BoundedList<SyncFeedOverride, 128>,
}
impl NewSyncDestination {
    fn valid(&self, document: &DocumentBinding) -> bool {
        self.id.as_str() != "local-sidecar"
            && self
                .id
                .as_str()
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b"_-".contains(&b))
            && self.endpoint.valid_for(document)
            && self.secret.draft.document == *document
            && self.proxy.valid(document)
            && super::unique_by(self.feeds.as_slice(), |f| f.feed_id.clone())
    }
}
