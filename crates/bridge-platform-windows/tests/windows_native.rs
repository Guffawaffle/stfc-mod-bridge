#![cfg(all(windows, target_arch = "x86_64"))]

use bridge_domain::platform::*;
use bridge_platform_windows::*;
use std::ffi::{OsStr, OsString};
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::os::windows::fs::{MetadataExt, OpenOptionsExt};
use std::os::windows::io::AsRawHandle;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use windows::Win32::Foundation::{GENERIC_WRITE, HANDLE};
use windows::Win32::Storage::FileSystem::{
    FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT,
};
use windows::Win32::System::Com::{
    CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx,
    CoUninitialize, IPersistFile, STGM_READ,
};
use windows::Win32::System::IO::DeviceIoControl;
use windows::Win32::System::Ioctl::FSCTL_SET_REPARSE_POINT;
use windows::Win32::UI::Shell::{IShellLinkW, ShellLink};
use windows::Win32::UI::WindowsAndMessaging::*;
use windows::core::{Interface, PCWSTR, w};

// These are real native probes, not skipped successes. Root's qualification
// controller supplies an explicit new ignored fixture root and invokes each
// test by exact name. No test opens an account/catalog/configuration store.
struct Fixture {
    path: PathBuf,
}
impl Fixture {
    fn new(label: &str) -> Self {
        let root = fixture_root();
        let timestamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let path = root.join(format!("{label}-{}-{timestamp:x}", std::process::id()));
        fs::create_dir(&path).unwrap();
        Self { path }
    }
    fn write(&self, name: &str, bytes: &[u8]) -> PathBuf {
        let path = self.path.join(name);
        let mut file = OpenOptions::new()
            .create_new(true)
            .write(true)
            .open(&path)
            .unwrap();
        file.write_all(bytes).unwrap();
        file.sync_all().unwrap();
        path
    }
    fn parent(&self) -> AdmittedDirectoryGuard {
        let capture = capture_directory(&self.path).unwrap();
        admit_directory(capture.observation()).unwrap()
    }
}

fn fixture_root() -> PathBuf {
    let input = PathBuf::from(
        std::env::var_os("BRIDGE_TEST_WINDOWS_FIXTURE_ROOT")
            .expect("explicit BRIDGE_TEST_WINDOWS_FIXTURE_ROOT required"),
    );
    let owner_input = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let owner = owner_input.canonicalize().unwrap();
    let allowed = owner.join("artifacts/next/windows-platform");
    let allowed_input = owner_input.join("artifacts/next/windows-platform");
    assert!(
        input.is_absolute()
            && ((input.starts_with(&allowed) && input != allowed)
                || (input.starts_with(&allowed_input) && input != allowed_input)),
        "fixture root must be a fresh owner-scoped directory"
    );
    assert!(
        !input
            .components()
            .any(|c| matches!(c, std::path::Component::ParentDir)),
        "no parent routes"
    );
    let lexical_owner = if input.starts_with(&owner) {
        owner.as_path()
    } else {
        owner_input
    };
    let mut cursor = lexical_owner.to_path_buf();
    for component in input.strip_prefix(lexical_owner).unwrap().components() {
        cursor.push(component);
        let metadata = fs::symlink_metadata(&cursor).unwrap();
        assert!(
            metadata.is_dir() && metadata.file_attributes() & 0x400 == 0,
            "fixture ancestry cannot be a reparse point"
        );
    }
    let physical = input.canonicalize().unwrap();
    assert!(
        physical.starts_with(&allowed) && physical != allowed,
        "fixture root must be physically owned"
    );
    // Rust's canonical DOS path uses the verbatim prefix; an ordinary root
    // selector from Node must identify the same physical directory, not fail
    // solely because its representation lacks that prefix.
    physical
}

#[test]
#[ignore = "root-controlled Windows native fixture gate"]
fn physical_identity_retains_hardlink_alias_and_replacement() {
    let fixture = Fixture::new("physical");
    let path = fixture.write("original.bin", b"same bytes");
    let mut old = capture_file(&path, Some(100)).unwrap();
    let original = old.observation().clone();
    let alias = fixture.path.join("alias.bin");
    fs::hard_link(&path, &alias).unwrap();
    let alias = capture_file(&alias, Some(100)).unwrap();
    assert_eq!(original.identity, alias.observation().identity);
    assert_ne!(original.physical_path, alias.observation().physical_path);
    fs::rename(&path, fixture.path.join("retained-original.bin")).unwrap();
    fixture.write("original.bin", b"same bytes");
    let replacement = capture_file(&path, Some(100)).unwrap();
    assert_ne!(original.identity, replacement.observation().identity);
    assert_eq!(original.disk_sha256, replacement.observation().disk_sha256);
    assert_eq!(old.refresh(Some(100)).unwrap().identity, original.identity);
    assert_eq!(
        admit_file(&original).err().unwrap().code,
        PlatformErrorCode::IdentityChanged
    );
}

#[test]
#[ignore = "root-controlled Windows native fixture gate"]
fn capture_does_not_exclude_and_admission_blocks_write_delete() {
    let fixture = Fixture::new("admission");
    let path = fixture.write("file.bin", b"data");
    let capture = capture_file(&path, None).unwrap();
    let writer = OpenOptions::new().write(true).open(&path).unwrap();
    drop(writer);
    let guard = admit_file(capture.observation()).unwrap();
    assert!(OpenOptions::new().write(true).open(&path).is_err());
    assert!(fs::remove_file(&path).is_err());
    drop(guard);
    assert!(OpenOptions::new().write(true).open(&path).is_ok());
}

#[test]
#[ignore = "root-controlled Windows native fixture gate"]
fn bounded_hash_and_device_stream_inputs_are_refused() {
    let fixture = Fixture::new("bounds");
    let path = fixture.write("file.bin", b"123456");
    assert_eq!(
        capture_file(&path, Some(5)).err().unwrap().code,
        PlatformErrorCode::TooLarge
    );
    assert_eq!(
        capture_file(&fixture.path, None).err().unwrap().code,
        PlatformErrorCode::WrongKind
    );
    for path in [
        PathBuf::from("\\\\.\\pipe\\unrelated"),
        PathBuf::from(format!("{}:stream", path.display())),
    ] {
        assert_eq!(
            capture_file(&path, None).err().unwrap().code,
            PlatformErrorCode::InvalidInput
        );
    }
}

/// Establish only one fresh owned directory junction to another directory in
/// the same private fixture. This packs the SDK mount-point reparse buffer;
/// there is no external helper, privilege change, fallback or cleanup route.
fn create_private_junction(fixture: &Fixture, destination: &Path, target: &Path) {
    assert_eq!(destination.parent(), Some(fixture.path.as_path()));
    assert_eq!(target.parent(), Some(fixture.path.as_path()));
    let target_metadata = fs::symlink_metadata(target).unwrap();
    assert!(target_metadata.is_dir() && target_metadata.file_attributes() & 0x400 == 0);
    assert_eq!(fixture.path.canonicalize().unwrap(), fixture.path);
    let target = target.canonicalize().unwrap();
    let native: Vec<u16> = target.as_os_str().encode_wide().collect();
    assert!(native.starts_with(&[b'\\' as u16, b'\\' as u16, b'?' as u16, b'\\' as u16]));
    let print = &native[4..];
    let mut substitute: Vec<u16> = OsStr::new("\\??\\").encode_wide().collect();
    substitute.extend_from_slice(print);
    let substitute_bytes = u16::try_from(substitute.len() * 2).unwrap();
    let print_bytes = u16::try_from(print.len() * 2).unwrap();
    let print_offset = substitute_bytes.checked_add(2).unwrap();
    let data_length = 8usize + (substitute.len() + print.len() + 2) * 2;
    assert!(data_length + 8 <= 16 * 1024, "bounded SDK reparse data");
    let mut buffer = Vec::with_capacity(data_length + 8);
    buffer.extend_from_slice(&0xA0000003u32.to_le_bytes()); // IO_REPARSE_TAG_MOUNT_POINT.
    buffer.extend_from_slice(&u16::try_from(data_length).unwrap().to_le_bytes());
    buffer.extend_from_slice(&0u16.to_le_bytes()); // Reserved.
    buffer.extend_from_slice(&0u16.to_le_bytes()); // SubstituteNameOffset.
    buffer.extend_from_slice(&substitute_bytes.to_le_bytes());
    buffer.extend_from_slice(&print_offset.to_le_bytes());
    buffer.extend_from_slice(&print_bytes.to_le_bytes());
    for unit in substitute
        .iter()
        .copied()
        .chain([0])
        .chain(print.iter().copied())
        .chain([0])
    {
        buffer.extend_from_slice(&unit.to_le_bytes());
    }
    assert_eq!(buffer.len(), data_length + 8);
    fs::create_dir(destination).unwrap();
    let handle = OpenOptions::new()
        .write(true)
        .access_mode(GENERIC_WRITE.0)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS.0 | FILE_FLAG_OPEN_REPARSE_POINT.0)
        .open(destination)
        .unwrap();
    let mut returned = 0;
    // SAFETY: the live owned directory handle receives a valid bounded encoded
    // mount-point input buffer. Synchronous METHOD_BUFFERED call retains no
    // pointer; there is no output buffer or overlapped state.
    unsafe {
        DeviceIoControl(
            HANDLE(handle.as_raw_handle()),
            FSCTL_SET_REPARSE_POINT,
            Some(buffer.as_ptr().cast()),
            u32::try_from(buffer.len()).unwrap(),
            None,
            0,
            Some(&mut returned),
            None,
        )
    }
    .unwrap(); // Unsupported/denied fixture creation fails; it is never skipped.
    assert_ne!(
        fs::symlink_metadata(destination).unwrap().file_attributes() & 0x400,
        0
    );
    assert_eq!(destination.canonicalize().unwrap(), target);
}

#[test]
#[ignore = "root-controlled private junction fixtures; no cleanup or external targets"]
fn final_and_ancestor_junctions_are_refused_without_following_targets() {
    let fixture = Fixture::new("reparse");
    let target = fixture.path.join("target");
    fs::create_dir(&target).unwrap();
    let nested = target.join("nested");
    fs::create_dir(&nested).unwrap();
    let original = b"private reparse target remains unchanged";
    let mut file = OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(target.join("file.bin"))
        .unwrap();
    file.write_all(original).unwrap();
    file.sync_all().unwrap();
    drop(file);
    let junction = fixture.path.join("junction");
    create_private_junction(&fixture, &junction, &target);
    for path in [&junction, &junction.join("nested")] {
        assert_eq!(
            capture_directory(path).err().unwrap().code,
            PlatformErrorCode::LinkOrReparsePoint
        );
    }
    assert_eq!(
        capture_file(&junction, None).err().unwrap().code,
        PlatformErrorCode::LinkOrReparsePoint
    );
    assert_eq!(
        capture_file(&junction.join("file.bin"), Some(100))
            .err()
            .unwrap()
            .code,
        PlatformErrorCode::LinkOrReparsePoint
    );
    assert_eq!(fs::read(target.join("file.bin")).unwrap(), original);
    assert!(capture_directory(&nested).is_ok());
    assert!(capture_file(&target.join("file.bin"), Some(100)).is_ok());
    // Every fixture and junction is retained. No recursive delete touches a
    // reparse route, even on a test failure.
    assert_ne!(
        fs::symlink_metadata(junction).unwrap().file_attributes() & 0x400,
        0
    );
}

#[test]
#[ignore = "root-controlled Windows native fixture gate"]
fn directory_guard_retains_identity_and_blocks_rename() {
    let fixture = Fixture::new("directory");
    let captured = capture_directory(&fixture.path).unwrap();
    let renamed = fixture.path.with_extension("renamed");
    // A metadata-only capture observes without reserving rename exclusion.
    fs::rename(&fixture.path, &renamed).unwrap();
    fs::rename(&renamed, &fixture.path).unwrap();
    let guard = admit_directory(captured.observation()).unwrap();
    assert!(fs::rename(&fixture.path, &renamed).is_err());
    assert_eq!(
        guard.observation().identity,
        captured.observation().identity
    );
    drop(guard);
    drop(captured);
    fs::rename(&fixture.path, &renamed).unwrap();
}

#[test]
#[ignore = "root-controlled Windows native fixture gate"]
fn replacement_flushes_retains_backup_and_refuses_existing_backup() {
    let fixture = Fixture::new("replace");
    let path = fixture.write("destination.bin", b"old");
    let old = capture_file(&path, Some(100))
        .unwrap()
        .observation()
        .clone();
    let parent = fixture.parent();
    let staged =
        StagedReplacement::create(&parent, &old, "stage.tmp", "backup.bin", b"new").unwrap();
    let staged_identity = staged.staged_observation().identity;
    match staged.replace_under_owner_exclusion(&parent) {
        ReplaceOutcome::Replaced {
            destination,
            backup,
            staged_file_flushed,
        } => {
            assert!(staged_file_flushed);
            assert_eq!(destination.identity, staged_identity);
            assert_eq!(backup.identity, old.identity);
            assert_eq!(fs::read(&destination.physical_path).unwrap(), b"new");
            assert_eq!(fs::read(&backup.physical_path).unwrap(), b"old");
            assert_eq!(
                StagedReplacement::create(
                    &parent,
                    &destination,
                    "stage2.tmp",
                    "backup.bin",
                    b"other"
                )
                .err()
                .unwrap()
                .code,
                PlatformErrorCode::Busy
            );
        }
        other => panic!("expected observed replacement, got {other:?}"),
    }
}

#[test]
#[ignore = "root-controlled Windows native fixture gate"]
fn replacement_changed_destination_refuses_before_call() {
    let fixture = Fixture::new("stale");
    let path = fixture.write("destination.bin", b"old");
    let old = capture_file(&path, Some(100))
        .unwrap()
        .observation()
        .clone();
    let parent = fixture.parent();
    let staged =
        StagedReplacement::create(&parent, &old, "stage.tmp", "backup.bin", b"new").unwrap();
    fs::rename(&path, fixture.path.join("old-retained.bin")).unwrap();
    fixture.write("destination.bin", b"old");
    assert!(matches!(
        staged.replace_under_owner_exclusion(&parent),
        ReplaceOutcome::RefusedBeforeCall {
            error: PlatformError {
                code: PlatformErrorCode::IdentityChanged,
                ..
            }
        }
    ));
    assert!(!fixture.path.join("backup.bin").exists());
    assert_eq!(fs::read(path).unwrap(), b"old");
}

#[test]
#[ignore = "root-controlled Windows native fixture gate"]
fn replacement_native_failure_remains_ambiguous() {
    let fixture = Fixture::new("ambiguous");
    let path = fixture.write("destination.bin", b"old");
    let old = capture_file(&path, Some(100))
        .unwrap()
        .observation()
        .clone();
    let parent = fixture.parent();
    let staged =
        StagedReplacement::create(&parent, &old, "stage.tmp", "backup.bin", b"new").unwrap();
    let exclusion = admit_file(&old).unwrap();
    assert!(matches!(
        staged.replace_under_owner_exclusion(&parent),
        ReplaceOutcome::AmbiguousAfterCall {
            native_code: Some(_)
        }
    ));
    assert_eq!(fs::read(path).unwrap(), b"old");
    drop(exclusion);
}

#[test]
#[ignore = "root-controlled Windows native fixture gate"]
fn dpapi_current_user_roundtrip_rejects_purpose_version_and_tamper() {
    let _fixture = Fixture::new("dpapi");
    let purpose = SecretPurpose {
        version: 1,
        domain: SecretDomain::ApplicationPreferences,
        context: "synthetic/native-fixture".into(),
    };
    let sealed = protect_secret(&purpose, b"synthetic secret").unwrap();
    assert!(!sealed.windows(16).any(|part| part == b"synthetic secret"));
    assert_eq!(
        unprotect_secret(&purpose, &sealed).unwrap().as_bytes(),
        b"synthetic secret"
    );
    for changed in [
        SecretPurpose {
            domain: SecretDomain::UpdateState,
            ..purpose.clone()
        },
        SecretPurpose {
            context: "other".into(),
            ..purpose.clone()
        },
        SecretPurpose {
            version: 2,
            ..purpose.clone()
        },
    ] {
        assert!(unprotect_secret(&changed, &sealed).is_err());
    }
    let mut tampered = sealed.clone();
    *tampered.last_mut().unwrap() ^= 1;
    assert!(unprotect_secret(&purpose, &tampered).is_err());
    assert!(protect_secret(&purpose, &vec![0; 1024 * 1024 + 1]).is_err());
}

#[test]
#[ignore = "root-controlled Windows native fixture gate"]
fn signature_domains_remain_separate_and_unsigned_is_refused() {
    let fixture = Fixture::new("signature");
    let path = fixture.write("unsigned.bin", b"private unsigned fixture");
    let observed = capture_file(&path, Some(100)).unwrap();
    let guard = admit_file(observed.observation()).unwrap();
    for domain in [
        TrustDomain::Mod,
        TrustDomain::OfficialGame,
        TrustDomain::Bridge,
    ] {
        let result = observe_signature(&guard, domain, RevocationPolicy::CacheOnly, 0).unwrap();
        assert_eq!(result.domain, domain);
        assert_eq!(result.signature_index, 0);
        assert_eq!(result.subject.identity, guard.observation().identity);
        assert!(matches!(result.trust, SignatureTrust::OsRefused { .. }));
        assert!(result.signer_certificate_sha256.is_none());
    }
    assert!(
        observe_signature(&guard, TrustDomain::Bridge, RevocationPolicy::CacheOnly, 16).is_err()
    );
}

fn fixture_digest(value: &str) -> [u8; 32] {
    assert!(
        value.len() == 64
            && value
                .bytes()
                .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
    );
    std::array::from_fn(|index| u8::from_str_radix(&value[index * 2..index * 2 + 2], 16).unwrap())
}

fn observation_hex(value: &[u8]) -> String {
    value.iter().map(|byte| format!("{byte:02x}")).collect()
}

fn file_observation(value: &ObservedFile) -> serde_json::Value {
    let PhysicalIdentity::Windows {
        volume_serial,
        file_id,
    } = value.identity
    else {
        panic!("actual Windows file identity required");
    };
    let FileWriteStamp::Windows { filetime } = value.write_stamp else {
        panic!("actual Windows file timestamp required");
    };
    serde_json::json!({
        "physicalPath": value.physical_path.to_str().expect("synthetic fixture UTF-16 path"),
        "identity": { "kind": "windows", "volumeSerial": volume_serial.to_string(), "fileId": observation_hex(&file_id) },
        "kind": match value.kind { FileKind::File => "file", FileKind::Directory => "directory" },
        "byteLength": value.byte_len.to_string(),
        "writeStamp": { "kind": "windows", "filetime": filetime.to_string() },
        "diskSha256": value.disk_sha256.map(|digest| observation_hex(&digest)),
    })
}

fn process_observation(value: &ExactProcess) -> serde_json::Value {
    let ProcessStartStamp::Windows { creation_filetime } = value.start_stamp else {
        panic!("actual Windows process generation required");
    };
    serde_json::json!({
        "pid": value.pid,
        "startStamp": { "kind": "windows", "creationFiletime": creation_filetime.to_string() },
        "architecture": match value.architecture { NativeArchitecture::X86 => "x86", NativeArchitecture::X86_64 => "x86_64", NativeArchitecture::Arm64 => "arm64" },
        "executable": file_observation(&value.executable),
    })
}

fn emit_observation(test: &str, value: serde_json::Value) {
    let line = serde_json::to_string(&serde_json::json!({
        "schemaVersion": "bridge-windows-native-observation/v1",
        "testName": test,
        "observation": value,
        "grantsAuthority": false,
        "mappedImageAttested": false,
    }))
    .unwrap();
    assert!(
        line.len() + "BRIDGE_WINDOWS_OBSERVATION ".len() < 16 * 1024,
        "bounded synthetic native observation"
    );
    println!("BRIDGE_WINDOWS_OBSERVATION {line}");
}

fn focus_label(value: FocusOutcome) -> &'static str {
    match value {
        FocusOutcome::ForegroundObserved => "foreground_observed",
        FocusOutcome::Denied => "denied",
        FocusOutcome::NoWindow => "no_window",
        FocusOutcome::ProcessExited => "process_exited",
    }
}

fn signature_observation(value: &SignatureObservation) -> serde_json::Value {
    serde_json::json!({
        "domain": match value.domain { TrustDomain::Mod => "mod", TrustDomain::OfficialGame => "official_game", TrustDomain::Bridge => "bridge" },
        "signatureIndex": value.signature_index,
        "revocationPolicy": match value.revocation { RevocationPolicy::CacheOnly => "cache_only", RevocationPolicy::Online => "online" },
        "trust": match value.trust { SignatureTrust::OsTrusted => serde_json::json!({"kind": "os_trusted"}), SignatureTrust::OsRefused { status } => serde_json::json!({"kind": "os_refused", "status": status}) },
        "subject": file_observation(&value.subject),
        "signerCertificateSha256": value.signer_certificate_sha256.map(|digest| observation_hex(&digest)),
        "publisherApproval": false,
    })
}

#[test]
#[ignore = "root-controlled exact pinned signed private subject; no publisher approval"]
fn primary_signature_certificate_is_observed_and_unsupported_secondary_refused() {
    let root = fixture_root();
    let subject_input = PathBuf::from(
        std::env::var_os("BRIDGE_TEST_WINDOWS_SIGNED_SUBJECT")
            .expect("explicit root-supplied signed subject required"),
    );
    let root_input = PathBuf::from(std::env::var_os("BRIDGE_TEST_WINDOWS_FIXTURE_ROOT").unwrap());
    assert!(
        subject_input == root.join("signed-subject.exe")
            || subject_input == root_input.join("signed-subject.exe")
    );
    let subject_metadata = fs::symlink_metadata(&subject_input).unwrap();
    assert!(subject_metadata.is_file() && subject_metadata.file_attributes() & 0x400 == 0);
    let subject = subject_input.canonicalize().unwrap();
    assert_eq!(subject, root.join("signed-subject.exe"));
    for selected in [&subject, &root.join("signed-subject.json")] {
        let metadata = fs::symlink_metadata(selected).unwrap();
        assert!(metadata.is_file() && metadata.file_attributes() & 0x400 == 0);
        assert_eq!(selected.canonicalize().unwrap(), *selected);
    }
    let manifest_path = root.join("signed-subject.json");
    let size = fs::metadata(&manifest_path).unwrap().len();
    assert!(
        size > 0 && size <= 4096,
        "bounded root-controlled signature fixture manifest"
    );
    let manifest: serde_json::Value =
        serde_json::from_slice(&fs::read(manifest_path).unwrap()).unwrap();
    let object = manifest.as_object().unwrap();
    assert_eq!(object.len(), 6);
    assert_eq!(
        manifest["schemaVersion"],
        "bridge-windows-signed-subject/v1"
    );
    assert_eq!(manifest["relativePath"], "signed-subject.exe");
    assert_eq!(manifest["revocationPolicy"], "cache_only");
    assert_eq!(manifest["unsupportedSignatureIndex"], 1);
    // This one reviewed test subject is supplied/copied by root. The test never
    // discovers or opens arbitrary system/game binaries or accepts another pin.
    assert_eq!(
        manifest["sha256"],
        "58e74bf02fc5bbacc41dcb8bef089961cd5bddd37830b87784e4fc624d145d1f"
    );
    let expected_bytes = fixture_digest(manifest["sha256"].as_str().unwrap());
    let expected_certificate =
        fixture_digest(manifest["primarySignerCertificateSha256"].as_str().unwrap());
    let observed = capture_file(&subject, Some(256 * 1024 * 1024)).unwrap();
    assert_eq!(observed.observation().disk_sha256, Some(expected_bytes));
    let guard = admit_file(observed.observation()).unwrap();
    for domain in [
        TrustDomain::Mod,
        TrustDomain::OfficialGame,
        TrustDomain::Bridge,
    ] {
        let primary = observe_signature(&guard, domain, RevocationPolicy::CacheOnly, 0).unwrap();
        emit_observation(
            "primary_signature_certificate_is_observed_and_unsupported_secondary_refused",
            signature_observation(&primary),
        );
        assert_eq!(primary.domain, domain);
        assert_eq!(primary.signature_index, 0);
        assert_eq!(primary.revocation, RevocationPolicy::CacheOnly);
        assert_eq!(primary.subject, *guard.observation());
        assert_eq!(primary.trust, SignatureTrust::OsTrusted);
        assert_eq!(
            primary.signer_certificate_sha256,
            Some(expected_certificate)
        );
        // OS signature/certificate observation is distinct from any caller's
        // independent publisher/provider/update policy for these three labels.
        let secondary = observe_signature(&guard, domain, RevocationPolicy::CacheOnly, 1).unwrap();
        emit_observation(
            "primary_signature_certificate_is_observed_and_unsupported_secondary_refused",
            signature_observation(&secondary),
        );
        assert_eq!(secondary.domain, domain);
        assert_eq!(secondary.signature_index, 1);
        assert_eq!(secondary.subject, *guard.observation());
        assert!(matches!(secondary.trust, SignatureTrust::OsRefused { .. }));
        assert!(secondary.signer_certificate_sha256.is_none());
    }
}

struct OwnedChild {
    child: Child,
    fixture: Fixture,
}
impl OwnedChild {
    fn spawn(window: bool) -> Self {
        let fixture = Fixture::new("child");
        let child = Command::new(std::env::current_exe().unwrap())
            .args(["--ignored", "--exact", "fixture_owned_child", "--nocapture"])
            .env("BRIDGE_WINDOWS_CHILD_DIR", &fixture.path)
            .env(
                "BRIDGE_WINDOWS_CHILD_WINDOW",
                if window { "1" } else { "0" },
            )
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        let mut result = Self { child, fixture };
        let started = Instant::now();
        while !result.fixture.path.join("ready").is_file() {
            assert!(
                started.elapsed() < Duration::from_secs(10),
                "owned child readiness deadline"
            );
            assert!(
                result.child.try_wait().unwrap().is_none(),
                "owned child exited before readiness"
            );
            std::thread::sleep(Duration::from_millis(10));
        }
        result
    }
    fn finish(&mut self) {
        let release = self.fixture.path.join("release");
        OpenOptions::new()
            .create_new(true)
            .write(true)
            .open(release)
            .unwrap();
        let started = Instant::now();
        loop {
            if let Some(status) = self.child.try_wait().unwrap() {
                assert!(status.success());
                return;
            }
            assert!(
                started.elapsed() < Duration::from_secs(10),
                "owned child exit deadline"
            );
            std::thread::sleep(Duration::from_millis(10));
        }
    }
}
impl Drop for OwnedChild {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

#[test]
#[ignore = "root-controlled Windows native fixture gate"]
fn exact_process_rejects_generation_change_and_observes_exit() {
    let mut child = OwnedChild::spawn(false);
    let captured = capture_process(child.child.id(), None).unwrap();
    assert_eq!(captured.observation().pid, child.child.id());
    assert_eq!(
        captured.observation().architecture,
        NativeArchitecture::X86_64
    );
    assert_eq!(captured.liveness().unwrap(), ProcessLiveness::Running);
    emit_observation(
        "exact_process_rejects_generation_change_and_observes_exit",
        serde_json::json!({ "phase": "captured", "process": process_observation(captured.observation()), "liveness": "running" }),
    );
    let mut wrong = captured.observation().clone();
    let ProcessStartStamp::Windows {
        ref mut creation_filetime,
    } = wrong.start_stamp
    else {
        panic!("Windows stamp required");
    };
    *creation_filetime += 1;
    assert_eq!(
        open_exact_process(&wrong).err().unwrap().code,
        PlatformErrorCode::IdentityChanged
    );
    assert!(open_exact_process(captured.observation()).is_ok());
    child.finish();
    assert_eq!(captured.liveness().unwrap(), ProcessLiveness::Exited);
    emit_observation(
        "exact_process_rejects_generation_change_and_observes_exit",
        serde_json::json!({ "phase": "released", "process": process_observation(captured.observation()), "liveness": "exited" }),
    );
    assert!(open_exact_process(captured.observation()).is_err());
    assert_eq!(
        focus_exact_process(&captured).unwrap(),
        FocusOutcome::ProcessExited
    );
}

#[test]
#[ignore = "root-controlled owned visible fixture window; foreground policy may refuse"]
fn focus_targets_only_exact_owned_session_and_observes_policy() {
    let mut headless = OwnedChild::spawn(false);
    let headless_process = capture_process(headless.child.id(), None).unwrap();
    let headless_focus = focus_exact_process(&headless_process).unwrap();
    assert_eq!(headless_focus, FocusOutcome::NoWindow);
    emit_observation(
        "focus_targets_only_exact_owned_session_and_observes_policy",
        serde_json::json!({ "phase": "headless", "process": process_observation(headless_process.observation()), "focusOutcome": focus_label(headless_focus) }),
    );
    headless.finish();
    let mut child = OwnedChild::spawn(true);
    let exact = capture_process(child.child.id(), None).unwrap();
    let visible_focus = focus_exact_process(&exact).unwrap();
    assert!(matches!(
        visible_focus,
        FocusOutcome::ForegroundObserved | FocusOutcome::Denied
    ));
    emit_observation(
        "focus_targets_only_exact_owned_session_and_observes_policy",
        serde_json::json!({ "phase": "visible", "process": process_observation(exact.observation()), "focusOutcome": focus_label(visible_focus) }),
    );
    let mut wrong = exact.observation().clone();
    wrong.pid = std::process::id();
    assert!(open_exact_process(&wrong).is_err());
    child.finish();
    let exited_focus = focus_exact_process(&exact).unwrap();
    assert_eq!(exited_focus, FocusOutcome::ProcessExited);
    emit_observation(
        "focus_targets_only_exact_owned_session_and_observes_policy",
        serde_json::json!({ "phase": "released", "process": process_observation(exact.observation()), "focusOutcome": focus_label(exited_focus) }),
    );
}

#[test]
#[ignore = "root-controlled private directory; no Desktop/Start Menu publish"]
fn shortcut_private_publish_preserves_literal_arguments_and_refuses_overwrite() {
    let fixture = Fixture::new("shortcut");
    let parent = fixture.parent();
    let request = ShortcutRequest {
        destination: parent
            .observation()
            .physical_path
            .join("Synthetic Bridge.lnk"),
        executable: std::env::current_exe().unwrap(),
        arguments: vec![
            "".into(),
            "space value".into(),
            "quote\"value".into(),
            "tail\\".into(),
        ],
        working_directory: parent.observation().physical_path.clone(),
        description: "Synthetic fixture only".into(),
    };
    let published = create_shortcut(&parent, &request, "stage.tmp").unwrap();
    assert_eq!(published.kind, FileKind::File);
    assert!(published.byte_len > 0);
    let original = fs::read(&published.physical_path).unwrap();
    assert!(create_shortcut(&parent, &request, "stage2.tmp").is_err());
    assert_eq!(fs::read(&published.physical_path).unwrap(), original);
    unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) }
        .ok()
        .unwrap();
    {
        let link: IShellLinkW =
            unsafe { CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER) }.unwrap();
        let persist: IPersistFile = link.cast().unwrap();
        let path: Vec<u16> = request
            .destination
            .as_os_str()
            .encode_wide()
            .chain(Some(0))
            .collect();
        unsafe { persist.Load(PCWSTR(path.as_ptr()), STGM_READ) }.unwrap();
        let mut argv = [0u16; 1024];
        unsafe { link.GetArguments(&mut argv) }.unwrap();
        let len = argv.iter().position(|c| *c == 0).unwrap();
        assert_eq!(
            OsString::from_wide(&argv[..len]),
            OsStr::new("\"\" \"space value\" \"quote\\\"value\" \"tail\\\\\"")
        );
    }
    unsafe { CoUninitialize() };
}

/// Orchestration helper, not a qualification case. It only responds to the exact
/// fixture-root-bound environment supplied by OwnedChild, with a 25s deadline.
#[test]
#[ignore = "private re-executed fixture helper; not independent qualification"]
fn fixture_owned_child() {
    let root = fixture_root();
    let directory = PathBuf::from(
        std::env::var_os("BRIDGE_WINDOWS_CHILD_DIR").expect("explicit child directory"),
    );
    assert_eq!(directory.parent(), Some(root.as_path()));
    assert_eq!(directory.canonicalize().unwrap(), directory);
    assert!(
        directory
            .file_name()
            .unwrap()
            .to_string_lossy()
            .starts_with("child-")
    );
    let window = match std::env::var("BRIDGE_WINDOWS_CHILD_WINDOW")
        .unwrap()
        .as_str()
    {
        "0" => None,
        "1" => {
            let hwnd = unsafe {
                CreateWindowExW(
                    WS_EX_TOOLWINDOW,
                    w!("STATIC"),
                    w!("Bridge isolated native fixture"),
                    WS_OVERLAPPEDWINDOW,
                    0,
                    0,
                    250,
                    100,
                    None,
                    None,
                    None,
                    None,
                )
            }
            .unwrap();
            let _ = unsafe { ShowWindow(hwnd, SW_SHOWNOACTIVATE) };
            Some(hwnd)
        }
        _ => panic!("closed child mode required"),
    };
    OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(directory.join("ready"))
        .unwrap();
    let started = Instant::now();
    while !directory.join("release").is_file() {
        assert!(
            started.elapsed() < Duration::from_secs(25),
            "owned child bounded lifetime"
        );
        let mut message = MSG::default();
        while unsafe { PeekMessageW(&mut message, None, 0, 0, PM_REMOVE) }.as_bool() {
            unsafe {
                let _ = TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }
        std::thread::sleep(Duration::from_millis(10));
    }
    if let Some(hwnd) = window {
        unsafe { DestroyWindow(hwnd) }.unwrap();
    }
}
