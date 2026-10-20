use super::*;
use bridge_contracts::v1::*;
use bridge_toml::{TomlPath, TomlSnapshot};
use std::collections::BTreeSet;

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum AliasPolicy {
    /// Multiple simultaneous overrides are ambiguous, so preparation refuses.
    RejectMultiple,
    /// Producer explicitly supplies precedence, highest priority first.
    Precedence(Vec<TomlPath>),
}
#[derive(Clone, Debug)]
pub struct FieldPolicy {
    pub field_id: FieldId,
    pub canonical: TomlPath,
    pub aliases: Vec<TomlPath>,
    pub precedence: AliasPolicy,
}
/// Adoption must verify producer/runtime/source pairing before returning this
/// policy. A UI schema projection alone cannot invent provider encodings.
#[derive(Clone, Debug)]
pub struct AdoptedSchema {
    pub schema: ConfigurationSchema,
    pub fields: Vec<FieldPolicy>,
    pub platform: SupportedPlatform,
}
pub struct SyncProjection {
    pub id: DestinationId,
    pub mode: SyncMode,
    pub exposure: SyncExposure,
    pub endpoint_field: FieldId,
    pub endpoint: ProtectedValue,
    pub secret_configured: bool,
    pub proxy: SyncProjectedProxy,
    pub feeds: BoundedList<SyncFeed, 128>,
}
pub enum SyncProjectedProxy {
    Global,
    None,
    Custom {
        field: FieldId,
        value: ProtectedValue,
    },
}

/// Producer-specific encodings and complete migration semantics stay with the
/// adopted source. This port is not a second handwritten runtime catalog.
pub trait SchemaSource {
    fn resolve(&mut self, binding: &SchemaBinding) -> ConfigurationResult<AdoptedSchema>;
    fn encode_public(
        &mut self,
        schema: &AdoptedSchema,
        field: &FieldDefinition,
        value: &PublicConfigValue,
    ) -> ConfigurationResult<String>;
    fn decode_public(
        &mut self,
        schema: &AdoptedSchema,
        field: &FieldDefinition,
        normalized: &str,
    ) -> ConfigurationResult<PublicConfigValue>;
    fn project_sync(
        &mut self,
        schema: &AdoptedSchema,
        snapshot: &TomlSnapshot,
    ) -> ConfigurationResult<Vec<SyncProjection>>;
    /// Return only mutations of producer-owned Sync paths, with an exact
    /// complete semantic proof. The engine independently checks the proof.
    fn compile_sync(
        &mut self,
        schema: &AdoptedSchema,
        baseline: &TomlSnapshot,
        edits: &[ResolvedSyncEdit],
    ) -> ConfigurationResult<Vec<SemanticMutation>>;
    fn migrate(
        &mut self,
        source: &AdoptedSchema,
        destination: &AdoptedSchema,
        baseline: &TomlSnapshot,
    ) -> ConfigurationResult<Vec<SemanticMutation>>;
    /// Exact allowed source keys/tables for Sync/migration mutations. Unknown
    /// document keys may never be claimed merely because a table was selected.
    fn owned_paths(&mut self, schema: &AdoptedSchema) -> ConfigurationResult<Vec<TomlPath>>;
    /// Validate every known field and Sync destination in the final document,
    /// including provider-only constraints absent from the public projection.
    fn validate_candidate(
        &mut self,
        schema: &AdoptedSchema,
        candidate: &TomlSnapshot,
    ) -> ConfigurationResult<()>;
}
pub enum ResolvedSyncEdit {
    Add {
        destination: Box<NewSyncDestination>,
        endpoint: String,
        secret: String,
        proxy: Option<String>,
    },
    Remove(DestinationId),
    Feed {
        destination: DestinationId,
        feed: FeedId,
        value: InheritedBoolean,
    },
    Proxy {
        destination: DestinationId,
        choice: ProxyChoice,
        value: Option<String>,
    },
}
pub enum SemanticMutation {
    Set {
        path: TomlPath,
        value: String,
    },
    Remove {
        path: TomlPath,
    },
    RemoveTable {
        path: TomlPath,
    },
    RenameTable {
        path: TomlPath,
        destination: TomlPath,
    },
}
impl SemanticMutation {
    pub(crate) fn paths(&self) -> Vec<&TomlPath> {
        match self {
            Self::Set { path, .. } | Self::Remove { path } | Self::RemoveTable { path } => {
                vec![path]
            }
            Self::RenameTable { path, destination } => vec![path, destination],
        }
    }
}
impl AdoptedSchema {
    pub(crate) fn validate(&self, expected: &SchemaBinding) -> ConfigurationResult<()> {
        if &self.schema.binding != expected
            || self.fields.len() != self.schema.fields.as_slice().len()
        {
            return Err(ConfigurationFailure::UnsupportedSchema);
        }
        let mut ids = BTreeSet::new();
        let mut paths = BTreeSet::new();
        for field in self.schema.fields.as_slice() {
            if !ids.insert(field.field_id.clone()) || field.path.as_slice().is_empty() {
                return Err(ConfigurationFailure::UnsupportedSchema);
            }
            let policy = self.field(&field.field_id)?;
            let segments = |path: &BoundedList<TomlPathSegment, 16>| {
                path.as_slice()
                    .iter()
                    .map(|p| p.as_str().to_owned())
                    .collect::<Vec<_>>()
            };
            if policy.canonical.segments() != segments(&field.path)
                || policy.aliases.len() != field.aliases.as_slice().len()
                || !policy
                    .aliases
                    .iter()
                    .zip(field.aliases.as_slice())
                    .all(|(a, b)| a.segments() == segments(b))
            {
                return Err(ConfigurationFailure::UnsupportedSchema);
            }
            for path in std::iter::once(&policy.canonical).chain(&policy.aliases) {
                if !paths.insert(path.segments().to_vec()) {
                    return Err(ConfigurationFailure::UnsupportedSchema);
                }
            }
            if let AliasPolicy::Precedence(order) = &policy.precedence {
                let required = std::iter::once(&policy.canonical)
                    .chain(&policy.aliases)
                    .map(|p| p.segments().to_vec())
                    .collect::<BTreeSet<_>>();
                if order.len() != required.len()
                    || order
                        .iter()
                        .map(|p| p.segments().to_vec())
                        .collect::<BTreeSet<_>>()
                        != required
                {
                    return Err(ConfigurationFailure::UnsupportedSchema);
                }
            }
        }
        Ok(())
    }
    pub(crate) fn field(&self, id: &FieldId) -> ConfigurationResult<&FieldPolicy> {
        self.fields
            .iter()
            .find(|p| &p.field_id == id)
            .ok_or(ConfigurationFailure::UnsupportedSchema)
    }
    pub(crate) fn definition(&self, id: &FieldId) -> Option<&FieldDefinition> {
        self.schema
            .fields
            .as_slice()
            .iter()
            .find(|p| &p.field_id == id)
    }
}

pub(crate) fn accepts(kind: &FieldType, value: &PublicConfigValue) -> bool {
    match (kind, value) {
        (FieldType::Boolean, PublicConfigValue::Boolean(_)) => true,
        (FieldType::Integer { minimum, maximum }, PublicConfigValue::Integer(v)) => {
            minimum.is_none_or(|m| *v >= m) && maximum.is_none_or(|m| *v <= m)
        }
        (FieldType::Number { minimum, maximum }, PublicConfigValue::Number(v)) => {
            minimum
                .as_ref()
                .is_none_or(|m| v.compare(m) != std::cmp::Ordering::Less)
                && maximum
                    .as_ref()
                    .is_none_or(|m| v.compare(m) != std::cmp::Ordering::Greater)
        }
        (FieldType::String { maximum_length }, PublicConfigValue::String(v)) => {
            v.as_str().chars().count() as u64 <= maximum_length.get()
        }
        (FieldType::Enum { values }, PublicConfigValue::Enum(v)) => values.as_slice().contains(v),
        (FieldType::Keybinding { multiple, keys }, PublicConfigValue::Keybinding(chords)) => {
            (*multiple || chords.as_slice().len() <= 1)
                && chords
                    .as_slice()
                    .iter()
                    .all(|c| keys.as_slice().contains(&c.key))
        }
        (
            FieldType::NotificationPolicy { sounds },
            PublicConfigValue::NotificationPolicy(NotificationPolicy::Channels { sound, .. }),
        ) => sounds.as_slice().contains(sound),
        (FieldType::NotificationPolicy { .. }, PublicConfigValue::NotificationPolicy(_)) => true,
        _ => false,
    }
}
