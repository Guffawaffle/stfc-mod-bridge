use super::{ObservationSource, Views, native::NativeCatalog, unavailable, wire_value};
use bridge_contracts::v1::*;
use bridge_domain::platform::{
    ExactProcess, NativeArchitecture, ObservedFile, PlatformError, PlatformErrorCode,
    ProcessLiveness, ProcessStartStamp,
};
use bridge_profiles::{
    CatalogOperation, CatalogSuccess, ConsumerError, GameSelection, ProfileKind,
};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::{collections::BTreeMap, path::Path};

pub struct ProfilesObservations {
    catalog: Option<NativeCatalog>,
}
impl ProfilesObservations {
    pub fn open(root: &Path) -> Self {
        Self {
            catalog: NativeCatalog::open(root).ok(),
        }
    }
    pub fn available(&self) -> bool {
        self.catalog.is_some()
    }
}
pub fn hash(value: impl AsRef<[u8]>) -> String {
    Sha256::digest(value.as_ref())
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}
fn token(prefix: &str, value: &str) -> String {
    format!("{prefix}-{}", hash(value))
}
fn session_id(value: &str) -> String {
    let mut bytes: [u8; 32] = Sha256::digest(value.as_bytes()).into();
    bytes[6] = (bytes[6] & 0x0f) | 0x80;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    let hex: String = bytes[..16]
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect();
    format!(
        "{}-{}-{}-{}-{}",
        &hex[..8],
        &hex[8..12],
        &hex[12..16],
        &hex[16..20],
        &hex[20..]
    )
}
fn observed(value: Value, evidence: &Evidence) -> Value {
    json!({"status":"observed","value":value,"evidence":evidence})
}
fn unknown(reason: ObservationReason, evidence: &Evidence) -> Value {
    json!({"status":"unknown","reason":reason,"evidence":evidence})
}
fn inventory(items: Vec<Value>, issues: Vec<Value>, evidence: &Evidence) -> Value {
    let revision = token(
        "observation",
        &super::semantic_value(json!([items, issues])).to_string(),
    );
    observed(
        json!({"items":items,"completeness":if issues.is_empty(){"complete"}else{"partial"},"issues":issues,"revision":revision}),
        evidence,
    )
}
fn issue(issues: &mut Vec<Value>, code: ProjectionIssueCode) {
    let value = json!({"code":code});
    if issues.len() < 64 && !issues.contains(&value) {
        issues.push(value);
    }
}
#[cfg(all(windows, target_arch = "x86_64"))]
fn directory(path: &Path) -> Result<ObservedFile, PlatformError> {
    Ok(bridge_platform_windows::capture_directory(path)?
        .observation()
        .clone())
}
#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
fn directory(path: &Path) -> Result<ObservedFile, PlatformError> {
    Ok(bridge_platform_macos::filesystem::capture_directory(path)?
        .observation()
        .clone())
}
#[cfg(not(any(
    all(windows, target_arch = "x86_64"),
    all(target_os = "macos", target_arch = "aarch64")
)))]
fn directory(_: &Path) -> Result<ObservedFile, PlatformError> {
    Err(PlatformError::new(PlatformErrorCode::UnsupportedHost))
}
#[cfg(all(windows, target_arch = "x86_64"))]
fn process(pid: u32) -> Result<ExactProcess, PlatformError> {
    let guard = bridge_platform_windows::capture_process(pid, Some(128 * 1024 * 1024))?;
    if guard.liveness()? != ProcessLiveness::Running {
        return Err(PlatformError::new(PlatformErrorCode::ProcessExited));
    }
    Ok(guard.observation().clone())
}
#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
fn process(pid: u32) -> Result<ExactProcess, PlatformError> {
    let guard = bridge_platform_macos::process::capture_process(pid)?;
    if guard.liveness()? != ProcessLiveness::Running {
        return Err(PlatformError::new(PlatformErrorCode::ProcessExited));
    }
    Ok(guard.observation().clone())
}
#[cfg(not(any(
    all(windows, target_arch = "x86_64"),
    all(target_os = "macos", target_arch = "aarch64")
)))]
fn process(_: u32) -> Result<ExactProcess, PlatformError> {
    Err(PlatformError::new(PlatformErrorCode::UnsupportedHost))
}
fn belongs(
    process: &ExactProcess,
    installation: &ObservedFile,
    directory: &impl Fn(&Path) -> Result<ObservedFile, PlatformError>,
) -> bool {
    #[cfg(windows)]
    let maximum = 1;
    #[cfg(not(windows))]
    let maximum = 8;
    let mut parent = process.executable.physical_path.parent();
    for _ in 0..maximum {
        let Some(path) = parent else {
            return false;
        };
        if directory(path).is_ok_and(|value| value.identity == installation.identity) {
            return true;
        }
        parent = path.parent();
    }
    false
}
fn start_identity(process: &ExactProcess) -> Value {
    match process.start_stamp {
        ProcessStartStamp::Windows { creation_filetime } => {
            json!({"platform":"windows","value":creation_filetime.to_string()})
        }
        ProcessStartStamp::MacOs {
            seconds,
            microseconds,
        } => json!({"platform":"macos","value":format!("{seconds}:{microseconds}")}),
    }
}
fn architecture(value: NativeArchitecture) -> Option<&'static str> {
    match value {
        NativeArchitecture::X86_64 => Some("x86_64"),
        NativeArchitecture::Arm64 => Some("arm64"),
        _ => None,
    }
}

fn missing() -> Views {
    Views {
        profiles: unavailable(ObservationReason::NativeUnavailable),
        installations: unavailable(ObservationReason::NativeUnavailable),
        sessions: unavailable(ObservationReason::NativeUnavailable),
    }
}
impl ObservationSource for ProfilesObservations {
    fn read(&mut self, evidence: &Evidence) -> Views {
        let Some(catalog) = &self.catalog else {
            return missing();
        };
        project(
            |operation| catalog.query(operation),
            directory,
            process,
            evidence,
        )
    }
}
// Keep canonical replies and OS observations separable so read races can be
// exercised without changing the user's catalog or installation directories.
fn project(
    mut query: impl FnMut(CatalogOperation) -> Result<Box<CatalogSuccess>, ConsumerError>,
    directory: impl Fn(&Path) -> Result<ObservedFile, PlatformError>,
    process: impl Fn(u32) -> Result<ExactProcess, PlatformError>,
    evidence: &Evidence,
) -> Views {
    let mut profiles = Vec::new();
    let mut profile_issues = Vec::new();
    match query(CatalogOperation::ResolveDefault) {
        Ok(value) => {
            if let CatalogSuccess::Profile(profile) = *value {
                let profile = profile.profile;
                if profile.kind == ProfileKind::WindowsUser
                    && let Some(owner) = profile.owner_user_id
                {
                    let mut row = json!({"kind":"ordinary","reference":{"kind":"ordinary","catalogId":profile.id.as_str(),
                            "revision":token("revision",profile.revision.as_str()),"ownerScope":token("owner",&owner)}});
                    if !profile.preferred_installation_id.is_empty() {
                        row["preferredInstallation"] = json!(profile.preferred_installation_id);
                    }
                    profiles.push(row);
                }
            }
        }
        Err(_) => issue(&mut profile_issues, ProjectionIssueCode::NativeUnavailable),
    }
    // Preserve the canonical collection even though this milestone cannot
    // launch isolated profiles. Readiness/store state is not inferred from
    // a profile name or the presence of preferences.
    match query(CatalogOperation::List { archived: false }) {
        Ok(value) => {
            if let CatalogSuccess::Profiles(list) = *value {
                if !list.issues.is_empty() || list.profiles.len() > 128 {
                    issue(&mut profile_issues, ProjectionIssueCode::IncompleteCatalog);
                }
                for profile in list.profiles.into_iter().take(128) {
                    if profile.kind != ProfileKind::Isolated {
                        continue;
                    }
                    if profiles.len() >= 128 {
                        issue(&mut profile_issues, ProjectionIssueCode::IncompleteCatalog);
                        break;
                    }
                    let mut row = json!({"kind":"isolated","reference":{"id":profile.id.as_str(),
                        "revision":token("revision",profile.revision.as_str()),"state":"active"},
                        "name":profile.name,"store":unknown(ObservationReason::IncompleteInventory,evidence)});
                    if !profile.preferred_installation_id.is_empty() {
                        row["preferredInstallation"] = json!(profile.preferred_installation_id);
                    }
                    if wire_value::<ProfileProjection>(row.clone()).is_ok() {
                        profiles.push(row);
                    } else {
                        issue(&mut profile_issues, ProjectionIssueCode::InvalidMetadata);
                    }
                }
            }
        }
        Err(_) => issue(&mut profile_issues, ProjectionIssueCode::NativeUnavailable),
    }
    let Ok(value) = query(CatalogOperation::Installations) else {
        return missing();
    };
    let CatalogSuccess::Installations(list) = *value else {
        return missing();
    };
    let mut installation_issues = Vec::new();
    let mut session_issues = Vec::new();
    if !list.issues.is_empty() || list.installations.len() > 128 {
        issue(
            &mut installation_issues,
            ProjectionIssueCode::IncompleteCatalog,
        );
        issue(&mut session_issues, ProjectionIssueCode::IncompleteCatalog);
    }
    let mut installations = Vec::new();
    let mut sessions: BTreeMap<String, Value> = BTreeMap::new();
    let mut process_owners: BTreeMap<String, String> = BTreeMap::new();
    let mut conflicting = Vec::new();
    for installation in list.installations.into_iter().take(128) {
        let Ok(physical) = directory(Path::new(installation.game_directory.as_str())) else {
            issue(
                &mut installation_issues,
                ProjectionIssueCode::NativeUnavailable,
            );
            issue(&mut session_issues, ProjectionIssueCode::NativeUnavailable);
            continue;
        };
        let physical_id = token("installation", &format!("{:?}", physical.identity));
        let binding = json!({"kind":"registered","registrationId":installation.id.as_str(),
                "registrationRevision":token("revision",installation.revision.as_str()),"physicalId":physical_id,
                "nativeTargetRef":token("catalog",installation.id.as_str())});
        let mut client = unknown(ObservationReason::IncompleteInventory, evidence);
        let mut clients = Vec::new();
        match query(CatalogOperation::InstallationStatus(GameSelection {
            installation_id: Some(installation.id.clone()),
            game_directory: None,
        })) {
            Ok(value) => {
                if let CatalogSuccess::Game(game) = *value {
                    let game = game.installation;
                    if !directory(Path::new(game.game_directory.as_str()))
                        .is_ok_and(|observed| observed.identity == physical.identity)
                    {
                        issue(
                            &mut installation_issues,
                            ProjectionIssueCode::ConflictingIdentity,
                        );
                        issue(
                            &mut session_issues,
                            ProjectionIssueCode::ConflictingIdentity,
                        );
                        continue;
                    }
                    if game.running_process_ids.len() > 128 {
                        issue(&mut session_issues, ProjectionIssueCode::IncompleteCatalog);
                    }
                    for pid in game.running_process_ids.into_iter().take(128) {
                        let Ok(process) = process(pid) else {
                            issue(&mut session_issues, ProjectionIssueCode::NativeUnavailable);
                            continue;
                        };
                        if !belongs(&process, &physical, &directory) {
                            issue(
                                &mut session_issues,
                                ProjectionIssueCode::ConflictingIdentity,
                            );
                            continue;
                        }
                        let Some(arch) = architecture(process.architecture) else {
                            issue(
                                &mut session_issues,
                                ProjectionIssueCode::UnsupportedPlatform,
                            );
                            continue;
                        };
                        let identity = format!(
                            "{}:{:?}:{:?}",
                            process.pid, process.start_stamp, process.executable.identity
                        );
                        let id = session_id(&identity);
                        if process_owners
                            .insert(id.clone(), physical_id.clone())
                            .is_some_and(|old| old != physical_id)
                        {
                            conflicting.push(id.clone());
                            issue(
                                &mut session_issues,
                                ProjectionIssueCode::ConflictingIdentity,
                            );
                        }
                        let row = json!({"binding":{"sessionId":id,"revision":token("process",&identity),"process":{
                            "pid":process.pid,"startIdentity":start_identity(&process),"executableIdentity":token("executable",&format!("{:?}",process.executable.identity)),
                            "installationPhysicalId":physical_id,"architecture":arch}},
                            "target":unknown(ObservationReason::UnrecognizedIdentity,evidence),
                            "liveIdentity":observed(json!(true),evidence),"readiness":unknown(ObservationReason::UnrecognizedIdentity,evidence)});
                        sessions.insert(id, row);
                        if let (Some(version), Some(digest)) =
                            (game.installed_version, process.executable.disk_sha256)
                        {
                            let digest: String =
                                digest.iter().map(|byte| format!("{byte:02x}")).collect();
                            let value = json!({"version":version.to_string(),"executableDigest":format!("sha256:{digest}"),"architecture":arch});
                            if !clients.contains(&value) {
                                clients.push(value);
                            }
                        }
                    }
                }
            }
            Err(_) => issue(&mut session_issues, ProjectionIssueCode::NativeUnavailable),
        }
        if clients.len() == 1 {
            client = observed(clients.remove(0), evidence);
        } else if clients.len() > 1 {
            client = unknown(ObservationReason::ConflictingEvidence, evidence);
        }
        installations.push(
            json!({"binding":binding,"name":installation.name,"client":client,
                "update":{"status":"unavailable","reason":"native_unavailable"}}),
        );
    }
    for id in conflicting {
        sessions.remove(&id);
    }
    if sessions.len() > 128 {
        issue(&mut session_issues, ProjectionIssueCode::IncompleteCatalog);
    }
    let sessions = sessions.into_values().take(128).collect();
    let converted = (
        wire_value(inventory(profiles, profile_issues, evidence)),
        wire_value(inventory(installations, installation_issues, evidence)),
        wire_value(inventory(sessions, session_issues, evidence)),
    );
    match converted {
        (Ok(profiles), Ok(installations), Ok(sessions)) => Views {
            profiles,
            installations,
            sessions,
        },
        _ => missing(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn process_generation_is_part_of_stable_session_identity() {
        let first = session_id("22796:start-a:executable-a");
        assert_eq!(first, session_id("22796:start-a:executable-a"));
        assert_ne!(first, session_id("22796:start-b:executable-a"));
        assert_ne!(first, session_id("22796:start-a:executable-b"));
        assert!(SessionId::new(first).is_ok());
    }
    #[test]
    fn status_directory_change_or_loss_cannot_hide_an_installation_in_a_complete_list() {
        use bridge_domain::platform::{FileKind, FileWriteStamp, PhysicalIdentity};
        use std::cell::Cell;
        for mode in ["matching", "changed", "missing"] {
            let captures = Cell::new(0);
            let evidence: Evidence = wire_value(
                json!({"observationId":"10000000-0000-4000-8000-000000000001",
                "observedAt":"2026-10-20T00:00:00Z","source":"native_live"}),
            )
            .unwrap();
            let views = project(
                |operation| {
                    Ok(Box::new(match operation {
                CatalogOperation::Installations => CatalogSuccess::Installations(serde_json::from_value(json!({
                    "installations":[{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","name":"Fixture installation",
                    "gameDirectory":"/fixture/original","physicalIdentity":"catalog-physical","revision":"1","state":"available"}],
                    "issues":[],"revision":"1"})).unwrap()),
                CatalogOperation::InstallationStatus(_) => {
                    // The first physical capture must precede the later status reply.
                    assert_eq!(captures.get(), 1);
                    CatalogSuccess::Game(serde_json::from_value(json!({"installation":{
                        "gameDirectory":"/fixture/status","state":"ready","phase":"idle","requiresRecovery":false,
                        "runningProcessIds":[],"installedVersion":null}})).unwrap())
                },
                _ => return Err(ConsumerError::BindingFailure),
            }))
                },
                |path| {
                    let n = captures.get();
                    captures.set(n + 1);
                    if n == 1 && mode == "missing" {
                        return Err(PlatformError::new(PlatformErrorCode::Missing));
                    }
                    Ok(ObservedFile {
                        physical_path: path.into(),
                        identity: PhysicalIdentity::Windows {
                            volume_serial: 1,
                            file_id: [if n == 1 && mode == "changed" { 2 } else { 1 }; 16],
                        },
                        kind: FileKind::Directory,
                        byte_len: 0,
                        write_stamp: FileWriteStamp::Windows { filetime: 0 },
                        disk_sha256: None,
                    })
                },
                |_| panic!("A fixture with no running PIDs must not observe a process"),
                &evidence,
            );
            let Observation::Observed {
                value: inventory, ..
            } = views.installations
            else {
                panic!("Expected installation projection")
            };
            assert_eq!(captures.get(), 2);
            if mode == "matching" {
                assert_eq!(inventory.completeness, Completeness::Complete);
                assert_eq!(inventory.items.as_slice().len(), 1);
            } else {
                assert_eq!(inventory.completeness, Completeness::Partial);
                assert!(inventory.items.as_slice().is_empty());
                assert!(
                    inventory
                        .issues
                        .as_slice()
                        .iter()
                        .any(|issue| issue.code == ProjectionIssueCode::ConflictingIdentity)
                );
            }
        }
    }
}
