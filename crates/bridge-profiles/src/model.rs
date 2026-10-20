use crate::{API_VERSION, ConsumerError, MAX_PATH_BYTES, MAX_REQUEST_BYTES};
use serde::{Deserialize, Deserializer, Serialize, Serializer, de::Error};

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(transparent)]
pub struct NativeId(String);
impl NativeId {
    pub fn new(value: impl Into<String>) -> Result<Self, ConsumerError> {
        let value = value.into();
        if value.len() != 32
            || !value
                .bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
        {
            return Err(ConsumerError::InvalidInput);
        }
        Ok(Self(value))
    }
    pub fn as_str(&self) -> &str {
        &self.0
    }
}
impl<'de> Deserialize<'de> for NativeId {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        Self::new(String::deserialize(d)?).map_err(|_| D::Error::custom("invalid native identity"))
    }
}

/// Bounded UTF-8 path assertion. The native owner resolves physical identity.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(transparent)]
pub struct NativePath(String);
impl NativePath {
    pub fn new(value: impl Into<String>) -> Result<Self, ConsumerError> {
        let value = value.into();
        let bytes = value.as_bytes();
        let absolute = value.starts_with('/')
            || value.starts_with("\\\\")
            || (bytes.len() >= 3
                && bytes[0].is_ascii_alphabetic()
                && bytes[1] == b':'
                && matches!(bytes[2], b'/' | b'\\'));
        if !absolute || value.len() > MAX_PATH_BYTES || value.contains('\0') {
            return Err(ConsumerError::InvalidInput);
        }
        Ok(Self(value))
    }
    pub fn as_str(&self) -> &str {
        &self.0
    }
}
impl<'de> Deserialize<'de> for NativePath {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        Self::new(String::deserialize(d)?).map_err(|_| D::Error::custom("invalid native path"))
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(transparent)]
pub struct NativeRevision(String);
impl NativeRevision {
    pub fn new(value: impl Into<String>) -> Result<Self, ConsumerError> {
        let value = value.into();
        if value.is_empty() || value.len() > 1024 || value.contains('\0') {
            return Err(ConsumerError::InvalidInput);
        }
        Ok(Self(value))
    }
    pub fn as_str(&self) -> &str {
        &self.0
    }
}
impl<'de> Deserialize<'de> for NativeRevision {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        Self::new(String::deserialize(d)?).map_err(|_| D::Error::custom("invalid native revision"))
    }
}

/// An omitted preference is unchanged; an explicit empty value clears it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum InstallationPreference {
    Clear,
    Registered(NativeId),
}
impl Serialize for InstallationPreference {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(match self {
            Self::Clear => "",
            Self::Registered(id) => id.as_str(),
        })
    }
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchSelection {
    pub id: NativeId,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub installation_id: Option<NativeId>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub game_directory: Option<NativePath>,
}
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NewProfile {
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub preferred_installation_id: Option<NativeId>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub game_directory: Option<NativePath>,
}
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProfileEdit {
    pub id: NativeId,
    pub expected_revision: NativeRevision,
    pub archived: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub preferred_installation_id: Option<InstallationPreference>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub game_directory: Option<NativePath>,
}
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportSelection {
    pub source_user_sid: String,
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub preferred_installation_id: Option<NativeId>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub game_directory: Option<NativePath>,
}
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportCommit {
    #[serde(flatten)]
    pub selection: ImportSelection,
    pub expected_destination_sid: String,
    pub expected_installation_revision: String,
    pub allow_elevation: bool,
}
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GameSelection {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub installation_id: Option<NativeId>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub game_directory: Option<NativePath>,
}

/// Exact producer operations; this enum does not add lifecycle policy or grants.
#[derive(Clone, Debug, Serialize)]
#[serde(
    tag = "operation",
    rename_all = "kebab-case",
    rename_all_fields = "camelCase"
)]
pub enum CatalogOperation {
    CatalogLocation,
    List {
        archived: bool,
    },
    Paths {
        id: NativeId,
        archived: bool,
    },
    Sessions,
    Installations,
    RegisterInstallation {
        name: String,
        game_directory: NativePath,
    },
    InstallationPaths {
        installation_id: NativeId,
    },
    EnsureDefault,
    ResolveDefault,
    Create(NewProfile),
    Edit(ProfileEdit),
    Rename(ProfileEdit),
    Archive {
        id: NativeId,
        expected_revision: NativeRevision,
    },
    Restore {
        id: NativeId,
        expected_revision: NativeRevision,
    },
    Delete {
        id: NativeId,
        expected_revision: NativeRevision,
        archived: bool,
        permanent: bool,
    },
    Launch(LaunchSelection),
    LaunchOrdinary(LaunchSelection),
    ImportSources {
        allow_elevation: bool,
        #[serde(skip_serializing_if = "Option::is_none")]
        expected_destination_sid: Option<String>,
    },
    PrepareUserImport(ImportSelection),
    ImportUser(ImportCommit),
    InstallationStatus(GameSelection),
    CheckGameUpdate(GameSelection),
    UpdateGame {
        #[serde(flatten)]
        selection: GameSelection,
        expected_version: u64,
    },
    RecoverGameUpdate(GameSelection),
}

#[derive(Clone, Debug)]
pub struct CatalogRequest {
    pub root: Option<NativePath>,
    pub operation: CatalogOperation,
}
impl CatalogRequest {
    pub fn encoded(&self) -> Result<Vec<u8>, ConsumerError> {
        if matches!(self.operation, CatalogOperation::CatalogLocation) && self.root.is_some() {
            return Err(ConsumerError::InvalidInput);
        }
        let mut body =
            serde_json::to_value(&self.operation).map_err(|_| ConsumerError::InvalidInput)?;
        let object = body.as_object_mut().ok_or(ConsumerError::InvalidInput)?;
        object.insert("apiVersion".into(), API_VERSION.into());
        if let Some(root) = &self.root {
            object.insert("root".into(), root.as_str().into());
        }
        // Native strings cannot contain embedded NUL, including a JSON escape.
        fn check(value: &serde_json::Value) -> bool {
            match value {
                serde_json::Value::String(value) => !value.contains('\0'),
                serde_json::Value::Array(values) => values.iter().all(check),
                serde_json::Value::Object(values) => values.values().all(check),
                _ => true,
            }
        }
        if !check(&body) {
            return Err(ConsumerError::InvalidInput);
        }
        if let CatalogOperation::InstallationStatus(s)
        | CatalogOperation::CheckGameUpdate(s)
        | CatalogOperation::RecoverGameUpdate(s)
        | CatalogOperation::UpdateGame { selection: s, .. } = &self.operation
            && s.installation_id.is_none()
            && s.game_directory.is_none()
        {
            return Err(ConsumerError::InvalidInput);
        }
        let bytes = serde_json::to_vec(&body).map_err(|_| ConsumerError::InvalidInput)?;
        if bytes.len() > MAX_REQUEST_BYTES {
            return Err(ConsumerError::RequestTooLarge);
        }
        Ok(bytes)
    }
}
