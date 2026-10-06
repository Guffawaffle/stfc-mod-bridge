use crate::{
    API_VERSION, CatalogOperation, CatalogRequest, ConsumerError, NativeId, NativePath,
    NativeRevision,
};
use serde::{
    Deserialize, Deserializer,
    de::{Error, MapAccess, SeqAccess, Visitor},
};
use serde_json::{Map, Value};

/// Producer diagnostics are validated as strings and discarded immediately.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SuppressedDiagnostic;
impl<'de> Deserialize<'de> for SuppressedDiagnostic {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        let _suppressed = String::deserialize(d)?;
        Ok(Self)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeErrorCode {
    Busy,
    StaleRevision,
    InvalidId,
    InvalidPath,
    UnsafePath,
    InvalidRoot,
    RootUnavailable,
    RootRedirected,
    InvalidRequest,
    InvalidInstallation,
    LockFailed,
    HashUnavailable,
    HashFailed,
    ProfileMissing,
    ArchivedProfileMissing,
    ProfileKind,
    ProfileRunning,
    ProcessCheckFailed,
    ProcessCheckIncomplete,
    InstallationChanged,
    InstallationRequired,
    InstallationUnknown,
    PlatformUnavailable,
    UnsupportedPlatform,
    LaunchUnqualified,
    RecoveryRequired,
    ReadinessTimeout,
    IsolationFailed,
    GameExited,
    DestinationChanged,
    ElevationRequired,
    OperationFailed,
    Unknown,
}
impl<'de> Deserialize<'de> for NativeErrorCode {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        let code = String::deserialize(d)?;
        Ok(match code.as_str() {
            "busy" => Self::Busy,
            "stale_revision" => Self::StaleRevision,
            "invalid_id" => Self::InvalidId,
            "invalid_path" => Self::InvalidPath,
            "unsafe_path" => Self::UnsafePath,
            "invalid_root" => Self::InvalidRoot,
            "root_unavailable" => Self::RootUnavailable,
            "root_redirected" => Self::RootRedirected,
            "invalid_request" => Self::InvalidRequest,
            "invalid_installation" => Self::InvalidInstallation,
            "lock_failed" => Self::LockFailed,
            "hash_unavailable" => Self::HashUnavailable,
            "hash_failed" => Self::HashFailed,
            "profile_missing" => Self::ProfileMissing,
            "archived_profile_missing" => Self::ArchivedProfileMissing,
            "profile_kind" => Self::ProfileKind,
            "profile_running" => Self::ProfileRunning,
            "process_check_failed" => Self::ProcessCheckFailed,
            "process_check_incomplete" => Self::ProcessCheckIncomplete,
            "installation_changed" => Self::InstallationChanged,
            "installation_required" => Self::InstallationRequired,
            "installation_unknown" => Self::InstallationUnknown,
            "platform_unavailable" => Self::PlatformUnavailable,
            "unsupported_platform" => Self::UnsupportedPlatform,
            "launch_unqualified" => Self::LaunchUnqualified,
            "recovery_required" => Self::RecoveryRequired,
            "readiness_timeout" => Self::ReadinessTimeout,
            "isolation_failed" => Self::IsolationFailed,
            "game_exited" => Self::GameExited,
            "destination_changed" => Self::DestinationChanged,
            "elevation_required" => Self::ElevationRequired,
            "operation_failed" => Self::OperationFailed,
            _ => Self::Unknown,
        })
    }
}

#[derive(Clone, Debug, PartialEq)]
pub enum CatalogReply {
    Success(Box<CatalogSuccess>),
    Refused(OperationFailure),
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct OperationFailure {
    pub code: NativeErrorCode,
    pub process_id: Option<u32>,
}

#[derive(Clone, Debug, PartialEq)]
pub enum CatalogSuccess {
    CatalogLocation(CatalogLocation),
    Profiles(ProfileList),
    Profile(ProfileReply),
    Deleted(DeletedProfile),
    Installations(InstallationList),
    Installation(InstallationReply),
    Sessions(SessionList),
    ImportSources(ImportSources),
    ImportPlan(ImportPlanReply),
    IsolatedLaunch(IsolatedLaunch),
    OrdinaryLaunch(OrdinaryLaunch),
    Game(GameReply),
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CatalogLocation {
    pub catalog_root: NativePath,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ProfileKind {
    Isolated,
    WindowsUser,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ProfileState {
    Active,
    Archived,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum PreferenceScope {
    Profile,
    WindowsUser,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ConfigurationScope {
    Profile,
    Installation,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum RegistrationState {
    Available,
    Unknown,
}

#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Profile {
    pub id: NativeId,
    pub name: String,
    pub game_directory: String,
    pub directory: NativePath,
    pub revision: NativeRevision,
    pub state: ProfileState,
    pub kind: ProfileKind,
    pub built_in: bool,
    pub preference_scope: PreferenceScope,
    pub configuration_scope: ConfigurationScope,
    pub preferred_installation_id: String,
    pub installation_state: Option<RegistrationState>,
    pub owner_user_id: Option<String>,
    pub config_path: Option<NativePath>,
    pub log_path: Option<NativePath>,
    pub preferences_initialized: Option<bool>,
}
impl Profile {
    fn valid(&self) -> bool {
        if self.name.is_empty()
            || self.name.contains('\0')
            || (!self.game_directory.is_empty()
                && NativePath::new(self.game_directory.clone()).is_err())
            || (!self.preferred_installation_id.is_empty()
                && NativeId::new(self.preferred_installation_id.clone()).is_err())
        {
            return false;
        }
        match self.kind {
            ProfileKind::WindowsUser => {
                self.name == "Default"
                    && self.state == ProfileState::Active
                    && self.built_in
                    && self.preference_scope == PreferenceScope::WindowsUser
                    && self.configuration_scope == ConfigurationScope::Installation
                    && self
                        .owner_user_id
                        .as_ref()
                        .is_some_and(|id| !id.is_empty() && !id.contains('\0'))
                    && self.config_path.is_none()
                    && self.log_path.is_none()
                    && self.preferences_initialized.is_none()
            }
            ProfileKind::Isolated => {
                !self.built_in
                    && self.preference_scope == PreferenceScope::Profile
                    && self.configuration_scope == ConfigurationScope::Profile
                    && self.owner_user_id.is_none()
                    && self.config_path.is_some()
                    && self.log_path.is_some()
                    && self.preferences_initialized.is_some()
            }
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CatalogIssue {
    pub id: String,
    pub path: Option<String>,
    pub code: Option<NativeErrorCode>,
    pub message: SuppressedDiagnostic,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProfileList {
    pub revision: NativeRevision,
    pub profiles: Vec<Profile>,
    pub issues: Vec<CatalogIssue>,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProfileReply {
    pub profile: Profile,
    pub revision: Option<NativeRevision>,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DeletedProfile {
    pub id: NativeId,
    pub deleted: bool,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Installation {
    pub id: NativeId,
    pub name: String,
    pub game_directory: NativePath,
    pub physical_identity: String,
    pub revision: NativeRevision,
    pub state: RegistrationState,
    pub message: Option<SuppressedDiagnostic>,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InstallationList {
    pub installations: Vec<Installation>,
    pub issues: Vec<CatalogIssue>,
    pub revision: NativeRevision,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InstallationReply {
    pub installation: Installation,
    pub created: Option<bool>,
}

#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProcessSnapshot {
    pub process_id: u32,
    pub started: String,
    pub executable: NativePath,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum SessionPhase {
    Pending,
    Initializing,
    Ready,
    Failed,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum SessionReadiness {
    Initializing,
    Ready,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NativeSession {
    pub api_version: u32,
    pub id: NativeId,
    pub process_id: u32,
    pub started: String,
    pub executable: NativePath,
    pub phase: SessionPhase,
    pub readiness: SessionReadiness,
    pub reason: Option<SuppressedDiagnostic>,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionList {
    pub sessions: Vec<NativeSession>,
    pub issues: Vec<CatalogIssue>,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct IsolatedLaunch {
    pub profile: Profile,
    pub process_id: u32,
    pub readiness: SessionReadiness,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum OrdinaryReadiness {
    Ordinary,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct OrdinaryLaunch {
    pub profile: Profile,
    pub process_id: u32,
    pub started: String,
    pub executable: NativePath,
    pub readiness: OrdinaryReadiness,
    pub session: ProcessSnapshot,
}

#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ImportUser {
    pub sid: String,
    pub name: String,
    pub current_user: bool,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DestinationUser {
    pub sid: String,
    pub name: String,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ImportSources {
    pub users: Vec<ImportUser>,
    pub destination_user: DestinationUser,
    pub requires_elevation: bool,
    pub unavailable_users: u64,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ImportPlan {
    pub source_user_sid: String,
    pub source_user_name: String,
    pub destination_user_sid: String,
    pub destination_user_name: String,
    pub name: String,
    pub game_directory: String,
    pub preferred_installation_id: String,
    pub installation_revision: String,
    pub requires_elevation: bool,
    pub reason: SuppressedDiagnostic,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ImportPlanReply {
    pub import_plan: ImportPlan,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum GameState {
    Ready,
    Running,
    Updating,
    RecoveryRequired,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum GamePhase {
    Idle,
    Initializing,
    Invalid,
    Downloading,
    Extracting,
    Staged,
    Committing,
    RollingBack,
    Committed,
    Recovered,
}
#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct GameInstallation {
    pub game_directory: NativePath,
    pub state: GameState,
    pub phase: GamePhase,
    pub requires_recovery: bool,
    pub running_process_ids: Vec<u32>,
    #[serde(deserialize_with = "required_nullable_version")]
    pub installed_version: Option<u64>,
    pub transaction_id: Option<String>,
    pub available_version: Option<u64>,
    pub update_available: Option<bool>,
    pub downloaded_bytes: Option<u64>,
    pub download_bytes: Option<u64>,
    pub extracted_bytes: Option<u64>,
    pub extracted_total_bytes: Option<u64>,
    pub completed_files: Option<u64>,
    pub total_files: Option<u64>,
    pub progress_percent: Option<f64>,
    pub message: Option<SuppressedDiagnostic>,
}
fn required_nullable_version<'de, D: Deserializer<'de>>(d: D) -> Result<Option<u64>, D::Error> {
    Option::<u64>::deserialize(d)
}
#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct GameReply {
    pub installation: GameInstallation,
    pub updated: Option<bool>,
    pub recovered: Option<bool>,
    pub history_retained: Option<bool>,
}

struct UniqueJson(Value);
impl<'de> Deserialize<'de> for UniqueJson {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        struct JsonVisitor;
        impl<'de> Visitor<'de> for JsonVisitor {
            type Value = UniqueJson;
            fn expecting(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.write_str("unique-key JSON")
            }
            fn visit_bool<E: Error>(self, value: bool) -> Result<Self::Value, E> {
                Ok(UniqueJson(value.into()))
            }
            fn visit_i64<E: Error>(self, value: i64) -> Result<Self::Value, E> {
                Ok(UniqueJson(value.into()))
            }
            fn visit_u64<E: Error>(self, value: u64) -> Result<Self::Value, E> {
                Ok(UniqueJson(value.into()))
            }
            fn visit_f64<E: Error>(self, value: f64) -> Result<Self::Value, E> {
                serde_json::Number::from_f64(value)
                    .map(|v| UniqueJson(Value::Number(v)))
                    .ok_or_else(|| E::custom("nonfinite JSON"))
            }
            fn visit_str<E: Error>(self, value: &str) -> Result<Self::Value, E> {
                Ok(UniqueJson(value.into()))
            }
            fn visit_string<E: Error>(self, value: String) -> Result<Self::Value, E> {
                Ok(UniqueJson(value.into()))
            }
            fn visit_none<E: Error>(self) -> Result<Self::Value, E> {
                Ok(UniqueJson(Value::Null))
            }
            fn visit_unit<E: Error>(self) -> Result<Self::Value, E> {
                Ok(UniqueJson(Value::Null))
            }
            fn visit_seq<A: SeqAccess<'de>>(self, mut seq: A) -> Result<Self::Value, A::Error> {
                let mut values = Vec::new();
                while let Some(v) = seq.next_element::<UniqueJson>()? {
                    values.push(v.0);
                }
                Ok(UniqueJson(Value::Array(values)))
            }
            fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<Self::Value, A::Error> {
                let mut values = Map::new();
                while let Some(key) = map.next_key::<String>()? {
                    if values.contains_key(&key) {
                        return Err(A::Error::custom("duplicate JSON member"));
                    }
                    values.insert(key, map.next_value::<UniqueJson>()?.0);
                }
                Ok(UniqueJson(Value::Object(values)))
            }
        }
        d.deserialize_any(JsonVisitor)
    }
}

fn payload<T: serde::de::DeserializeOwned>(value: Value) -> Result<T, ConsumerError> {
    serde_json::from_value(value).map_err(|_| ConsumerError::InvalidResponse)
}
pub(crate) fn decode(
    request: &CatalogRequest,
    bytes: &[u8],
) -> Result<CatalogReply, ConsumerError> {
    let UniqueJson(value) =
        serde_json::from_slice(bytes).map_err(|_| ConsumerError::InvalidJson)?;
    let Value::Object(mut body) = value else {
        return Err(ConsumerError::InvalidResponse);
    };
    if body.remove("apiVersion") != Some(API_VERSION.into()) {
        return Err(ConsumerError::UnsupportedApiVersion);
    }
    let ok = body.remove("ok").ok_or(ConsumerError::InvalidResponse)?;
    if ok == false {
        #[derive(Deserialize)]
        #[serde(deny_unknown_fields)]
        struct ErrorBody {
            code: NativeErrorCode,
            message: SuppressedDiagnostic,
        }
        let error: ErrorBody =
            payload(body.remove("error").ok_or(ConsumerError::InvalidResponse)?)?;
        let _diagnostic = error.message;
        let process_id: Option<u32> = body.remove("processId").map(payload).transpose()?;
        if !body.is_empty()
            || process_id == Some(0)
            || (process_id.is_some() && !matches!(request.operation, CatalogOperation::Launch(_)))
        {
            return Err(ConsumerError::InvalidResponse);
        }
        return Ok(CatalogReply::Refused(OperationFailure {
            code: error.code,
            process_id,
        }));
    }
    if ok != true || body.contains_key("error") {
        return Err(ConsumerError::InvalidResponse);
    }
    let value = Value::Object(body);
    let output = match &request.operation {
        CatalogOperation::CatalogLocation => CatalogSuccess::CatalogLocation(payload(value)?),
        CatalogOperation::List { archived } => {
            let p: ProfileList = payload(value)?;
            let expected = if *archived {
                ProfileState::Archived
            } else {
                ProfileState::Active
            };
            if !p
                .profiles
                .iter()
                .all(|profile| profile.valid() && profile.state == expected)
            {
                return Err(ConsumerError::InvalidResponse);
            }
            CatalogSuccess::Profiles(p)
        }
        CatalogOperation::Paths { .. }
        | CatalogOperation::EnsureDefault
        | CatalogOperation::ResolveDefault
        | CatalogOperation::Create(_)
        | CatalogOperation::Edit(_)
        | CatalogOperation::Rename(_)
        | CatalogOperation::Archive { .. }
        | CatalogOperation::Restore { .. }
        | CatalogOperation::ImportUser(_) => {
            let p: ProfileReply = payload(value)?;
            if !p.profile.valid()
                || (!matches!(request.operation, CatalogOperation::Paths { .. })
                    && p.revision.is_none())
            {
                return Err(ConsumerError::InvalidResponse);
            }
            let selected_id = match &request.operation {
                CatalogOperation::Paths { id, .. }
                | CatalogOperation::Archive { id, .. }
                | CatalogOperation::Restore { id, .. } => Some(id),
                CatalogOperation::Edit(input) | CatalogOperation::Rename(input) => Some(&input.id),
                _ => None,
            };
            if selected_id.is_some_and(|id| id != &p.profile.id)
                || (matches!(
                    request.operation,
                    CatalogOperation::EnsureDefault | CatalogOperation::ResolveDefault
                ) && p.profile.kind != ProfileKind::WindowsUser)
                || (matches!(
                    request.operation,
                    CatalogOperation::Create(_) | CatalogOperation::ImportUser(_)
                ) && p.profile.kind != ProfileKind::Isolated)
            {
                return Err(ConsumerError::InvalidResponse);
            }
            let archived = match &request.operation {
                CatalogOperation::Paths { archived, .. } => *archived,
                CatalogOperation::Edit(input) | CatalogOperation::Rename(input) => input.archived,
                CatalogOperation::Archive { .. } => true,
                _ => false,
            };
            if p.profile.state
                != if archived {
                    ProfileState::Archived
                } else {
                    ProfileState::Active
                }
                || (matches!(
                    request.operation,
                    CatalogOperation::Archive { .. } | CatalogOperation::Restore { .. }
                ) && p.profile.kind != ProfileKind::Isolated)
            {
                return Err(ConsumerError::InvalidResponse);
            }
            CatalogSuccess::Profile(p)
        }
        CatalogOperation::Delete { id, .. } => {
            let p: DeletedProfile = payload(value)?;
            if !p.deleted || &p.id != id {
                return Err(ConsumerError::InvalidResponse);
            }
            CatalogSuccess::Deleted(p)
        }
        CatalogOperation::Installations => CatalogSuccess::Installations(payload(value)?),
        CatalogOperation::RegisterInstallation { .. }
        | CatalogOperation::InstallationPaths { .. } => {
            let p: InstallationReply = payload(value)?;
            if matches!(
                request.operation,
                CatalogOperation::RegisterInstallation { .. }
            ) && p.created.is_none()
            {
                return Err(ConsumerError::InvalidResponse);
            }
            if let CatalogOperation::InstallationPaths { installation_id } = &request.operation
                && installation_id != &p.installation.id
            {
                return Err(ConsumerError::InvalidResponse);
            }
            CatalogSuccess::Installation(p)
        }
        CatalogOperation::Sessions => {
            let p: SessionList = payload(value)?;
            if p.sessions.iter().any(|s| {
                s.api_version != 1
                    || s.process_id == 0
                    || s.started.is_empty()
                    || (s.phase == SessionPhase::Ready) != (s.readiness == SessionReadiness::Ready)
            }) {
                return Err(ConsumerError::InvalidResponse);
            }
            CatalogSuccess::Sessions(p)
        }
        CatalogOperation::ImportSources { .. } => CatalogSuccess::ImportSources(payload(value)?),
        CatalogOperation::PrepareUserImport(_) => CatalogSuccess::ImportPlan(payload(value)?),
        CatalogOperation::Launch(input) => {
            let p: IsolatedLaunch = payload(value)?;
            if p.process_id == 0
                || !p.profile.valid()
                || p.profile.id != input.id
                || p.profile.kind != ProfileKind::Isolated
                || p.profile.state != ProfileState::Active
                || p.readiness != SessionReadiness::Ready
            {
                return Err(ConsumerError::InvalidResponse);
            }
            CatalogSuccess::IsolatedLaunch(p)
        }
        CatalogOperation::LaunchOrdinary(input) => {
            let p: OrdinaryLaunch = payload(value)?;
            if p.process_id == 0
                || p.started.is_empty()
                || !p.profile.valid()
                || p.profile.id != input.id
                || p.profile.kind != ProfileKind::WindowsUser
                || p.process_id != p.session.process_id
                || p.started != p.session.started
                || p.executable != p.session.executable
            {
                return Err(ConsumerError::InvalidResponse);
            }
            CatalogSuccess::OrdinaryLaunch(p)
        }
        CatalogOperation::InstallationStatus(_)
        | CatalogOperation::CheckGameUpdate(_)
        | CatalogOperation::UpdateGame { .. }
        | CatalogOperation::RecoverGameUpdate(_) => {
            let p: GameReply = payload(value)?;
            if p.installation.running_process_ids.contains(&0)
                || p.installation
                    .progress_percent
                    .is_some_and(|n| !n.is_finite() || !(0.0..=100.0).contains(&n))
                || (matches!(request.operation, CatalogOperation::UpdateGame { .. })
                    && p.updated.is_none())
                || (matches!(request.operation, CatalogOperation::RecoverGameUpdate(_))
                    && p.recovered.is_none())
                || (matches!(request.operation, CatalogOperation::CheckGameUpdate(_))
                    && (p.installation.available_version.is_none()
                        || p.installation.update_available.is_none()
                        || p.installation.download_bytes.is_none()
                        || p.installation.extracted_total_bytes.is_none()))
            {
                return Err(ConsumerError::InvalidResponse);
            }
            CatalogSuccess::Game(p)
        }
    };
    Ok(CatalogReply::Success(Box::new(output)))
}
