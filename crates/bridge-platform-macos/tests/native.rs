//! Ignored, owner-scoped real-host fixtures. Nothing here is a synthetic native
//! pass. A qualification harness must bind inputs, commands, files and receipts.
#![cfg(all(target_os = "macos", target_arch = "aarch64"))]
use bridge_domain::platform::{
    FocusOutcome, NativeArchitecture, PlatformErrorCode, ProcessLiveness, ProcessStartStamp,
    ReplaceOutcome, RevocationPolicy, SecretDomain, SecretPurpose, SignatureTrust, TrustDomain,
};
use bridge_platform_macos::{
    bundle,
    filesystem::{self, StagedPermissions, StagedReplacement},
    format, process, secrets, shell, signature,
};
use std::{
    fs,
    io::Write,
    os::{
        fd::AsRawFd,
        unix::fs::{DirBuilderExt, MetadataExt, OpenOptionsExt},
    },
    path::PathBuf,
    process::{Command, Stdio},
    sync::atomic::{AtomicU64, Ordering},
    time::{Duration, Instant},
};

static NEXT_FIXTURE: AtomicU64 = AtomicU64::new(1);

struct Fixture {
    path: PathBuf,
}
impl Fixture {
    fn new() -> Self {
        let explicit = std::env::var_os("BRIDGE_MACOS_NATIVE_FIXTURE_ROOT")
            .expect("explicit physical private fixture parent is mandatory; no temp/home fallback");
        let parent = filesystem::capture_directory(&PathBuf::from(explicit)).unwrap();
        let metadata = fs::metadata(&parent.observation().physical_path).unwrap();
        // SAFETY: uid observations only, never an identity change.
        let (real, effective) = unsafe { (libc::getuid(), libc::geteuid()) };
        assert_ne!(real, 0, "ordinary user fixture required");
        assert_eq!(real, effective);
        assert_eq!(metadata.uid(), effective);
        assert_eq!(
            metadata.mode() & 0o077,
            0,
            "fixture parent must be owner-private"
        );
        let leaf = format!(
            ".bridge-br07-native-{}-{}",
            std::process::id(),
            NEXT_FIXTURE.fetch_add(1, Ordering::SeqCst)
        );
        let path = parent.observation().physical_path.join(leaf);
        fs::DirBuilder::new().mode(0o700).create(&path).unwrap();
        let child = filesystem::capture_directory(&path).unwrap();
        assert_eq!(
            fs::metadata(&child.observation().physical_path)
                .unwrap()
                .uid(),
            effective
        );
        Self {
            path: child.observation().physical_path.clone(),
        }
    }

    fn file(&self, name: &str, bytes: &[u8]) -> PathBuf {
        format::validate_leaf(name.as_bytes()).unwrap();
        let path = self.path.join(name);
        let mut output = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .mode(0o600)
            .open(&path)
            .unwrap();
        output.write_all(bytes).unwrap();
        output.sync_all().unwrap();
        path
    }
}

#[test]
#[ignore = "real native Apple Silicon; explicit owner-private physical fixture parent"]
fn native_descriptor_identity_and_parent_replacement_refusal() {
    let fixture = Fixture::new();
    let parent_path = fixture.path.join("parent");
    fs::DirBuilder::new()
        .mode(0o700)
        .create(&parent_path)
        .unwrap();
    let path = parent_path.join("file");
    fs::write(&path, b"identity bytes").unwrap();
    let file = filesystem::capture_file(&path, true).unwrap();
    let directory = filesystem::capture_directory(&parent_path).unwrap();
    assert!(directory.contains_file(&file).unwrap());
    fs::rename(&parent_path, fixture.path.join("retained-parent")).unwrap();
    fs::DirBuilder::new()
        .mode(0o700)
        .create(&parent_path)
        .unwrap();
    fs::write(&path, b"identity bytes").unwrap();
    assert_eq!(
        file.revalidate(true).unwrap_err().code,
        PlatformErrorCode::IdentityChanged
    );
    assert_ne!(
        file.observation().identity,
        filesystem::capture_file(&path, true)
            .unwrap()
            .observation()
            .identity
    );
}

#[test]
#[ignore = "real native Apple Silicon; private link and FinderInfo fixtures"]
fn native_symlink_ancestor_and_finder_alias_are_refused() {
    let fixture = Fixture::new();
    let actual = fixture.file("actual", b"no follow");
    std::os::unix::fs::symlink(&actual, fixture.path.join("leaf-link")).unwrap();
    assert_eq!(
        filesystem::capture_file(&fixture.path.join("leaf-link"), false)
            .err()
            .unwrap()
            .code,
        PlatformErrorCode::LinkOrReparsePoint
    );
    std::os::unix::fs::symlink(&fixture.path, fixture.path.join("parent-link")).unwrap();
    assert_eq!(
        filesystem::capture_file(&fixture.path.join("parent-link/actual"), false)
            .err()
            .unwrap()
            .code,
        PlatformErrorCode::LinkOrReparsePoint
    );
    let alias = fixture.file("finder-alias", b"private alias-shaped fixture");
    let file = fs::OpenOptions::new()
        .read(true)
        .write(true)
        .open(&alias)
        .unwrap();
    let mut flags = [0_u8; 32];
    flags[8] = 0x80;
    // SAFETY: exact created private fd, fixed attribute and bounded data.
    assert_eq!(
        unsafe {
            libc::fsetxattr(
                file.as_raw_fd(),
                c"com.apple.FinderInfo".as_ptr(),
                flags.as_ptr().cast(),
                flags.len(),
                0,
                0,
            )
        },
        0
    );
    assert_eq!(
        filesystem::capture_file(&alias, false).err().unwrap().code,
        PlatformErrorCode::LinkOrReparsePoint
    );
}

#[test]
#[ignore = "real native Apple Silicon; volume semantics must be actually observed"]
fn native_case_semantics_follow_the_captured_volume() {
    let fixture = Fixture::new();
    let upper = fixture.file("CaseProbe", b"case");
    let directory = filesystem::capture_directory(&fixture.path).unwrap();
    let semantics = directory.volume_semantics().unwrap();
    let upper = filesystem::capture_file(&upper, true).unwrap();
    let lower = filesystem::capture_file(&fixture.path.join("caseprobe"), true);
    match semantics.case_sensitive {
        Some(true) => assert_eq!(lower.err().unwrap().code, PlatformErrorCode::Missing),
        Some(false) => assert_eq!(
            lower.unwrap().observation().identity,
            upper.observation().identity
        ),
        None => panic!("unknown case semantics cannot qualify this case"),
    }
}

#[test]
#[ignore = "real native Apple Silicon; swap and actual flush support required"]
fn native_staged_exchange_retains_backup_and_explicit_permissions() {
    let fixture = Fixture::new();
    let destination_path = fixture.file("destination", b"old");
    let previous = filesystem::capture_file(&destination_path, true)
        .unwrap()
        .observation()
        .clone();
    let parent = filesystem::capture_directory(&fixture.path).unwrap();
    let mut stage =
        StagedReplacement::prepare(parent, [1; 16], b"new", StagedPermissions::PrivateData)
            .unwrap();
    let staged_identity = stage.observation().identity;
    assert_eq!(
        fs::metadata(&stage.observation().physical_path)
            .unwrap()
            .mode()
            & 0o777,
        0o600
    );
    match stage.replace_under_owner_exclusion(b"destination", &previous) {
        ReplaceOutcome::Replaced {
            destination,
            backup,
            staged_file_flushed,
        } => {
            assert!(staged_file_flushed);
            assert_eq!(destination.identity, staged_identity);
            assert_eq!(backup.identity, previous.identity);
            assert_eq!(fs::read(&destination.physical_path).unwrap(), b"new");
            assert_eq!(fs::read(&backup.physical_path).unwrap(), b"old");
            assert_eq!(
                fs::metadata(&destination.physical_path).unwrap().mode() & 0o777,
                0o600
            );
        }
        outcome => panic!("actual swap did not qualify: {outcome:?}"),
    }
}

#[test]
#[ignore = "real native Apple Silicon; stale object and hard-link private fixtures"]
fn native_exchange_refuses_stale_and_hard_link_destinations_before_call() {
    let fixture = Fixture::new();
    let destination_path = fixture.file("destination", b"old");
    let previous = filesystem::capture_file(&destination_path, true)
        .unwrap()
        .observation()
        .clone();
    fs::rename(&destination_path, fixture.path.join("old-object")).unwrap();
    fixture.file("destination", b"old");
    let parent = filesystem::capture_directory(&fixture.path).unwrap();
    let mut stage =
        StagedReplacement::prepare(parent, [2; 16], b"new", StagedPermissions::PrivateData)
            .unwrap();
    assert!(
        matches!(stage.replace_under_owner_exclusion(b"destination", &previous),
        ReplaceOutcome::RefusedBeforeCall { error } if error.code == PlatformErrorCode::IdentityChanged)
    );
    let current = filesystem::capture_file(&destination_path, true)
        .unwrap()
        .observation()
        .clone();
    fs::hard_link(&destination_path, fixture.path.join("another-name")).unwrap();
    assert!(
        matches!(stage.replace_under_owner_exclusion(b"destination", &current),
        ReplaceOutcome::RefusedBeforeCall { error } if error.code == PlatformErrorCode::IdentityChanged)
    );
    assert_eq!(fs::read(&destination_path).unwrap(), b"old");
    assert_eq!(
        fs::read(&stage.observation().physical_path).unwrap(),
        b"new"
    );
}

#[test]
#[ignore = "real native Apple Silicon; exact self process does not qualify game architecture"]
fn native_exact_process_observes_architecture_and_rejects_forged_generation() {
    let actual = process::capture_process(std::process::id()).unwrap();
    assert_eq!(actual.observation().architecture, NativeArchitecture::Arm64);
    assert_eq!(actual.liveness().unwrap(), ProcessLiveness::Running);
    let mut forged = actual.observation().clone();
    if let ProcessStartStamp::MacOs {
        seconds,
        microseconds,
    } = forged.start_stamp
    {
        forged.start_stamp = ProcessStartStamp::MacOs {
            seconds: seconds + 1,
            microseconds,
        };
    } else {
        panic!("wrong host stamp");
    }
    assert_eq!(
        process::open_exact_process(&forged).err().unwrap().code,
        PlatformErrorCode::IdentityChanged
    );
}

#[test]
#[ignore = "real native Apple Silicon; separately pinned private owned helper required"]
fn native_owned_child_exit_is_not_a_reusable_pid_binding() {
    let fixture = Fixture::new();
    let explicit_helper = PathBuf::from(
        std::env::var_os("BRIDGE_MACOS_OWNED_CHILD").expect("fixed owned helper path"),
    );
    let helper = filesystem::capture_file(&explicit_helper, true).unwrap();
    let parent = filesystem::capture_directory(&PathBuf::from(
        std::env::var_os("BRIDGE_MACOS_NATIVE_FIXTURE_ROOT").unwrap(),
    ))
    .unwrap();
    assert!(
        parent.contains_file(&helper).unwrap(),
        "helper must remain in the owner-private qualification root"
    );
    let digest = format::hex(&helper.observation().disk_sha256.unwrap());
    assert_eq!(
        digest,
        std::env::var("BRIDGE_MACOS_OWNED_CHILD_SHA256")
            .expect("retained exact helper binary digest")
    );
    let nonce = format!("{:032x}", std::process::id());
    let mut child = Command::new(&helper.observation().physical_path)
        .args(["bridge-native-child-v1", &nonce])
        .current_dir(&fixture.path)
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    let deadline = Instant::now() + Duration::from_secs(5);
    let guard = loop {
        match process::capture_process(child.id()) {
            Ok(guard)
                if guard.observation().executable.identity == helper.observation().identity
                    && guard.observation().executable.disk_sha256
                        == helper.observation().disk_sha256 =>
            {
                break guard;
            }
            Ok(_) => (), // fork/exec boundary has not selected the owned image yet
            Err(error)
                if matches!(
                    error.code,
                    PlatformErrorCode::IdentityChanged | PlatformErrorCode::UnknownObservation
                ) =>
            {
                ()
            }
            Err(error) => panic!("owned child observation refused: {error}"),
        }
        assert!(Instant::now() < deadline, "owned helper readiness deadline");
        std::thread::sleep(Duration::from_millis(10));
    };
    assert_eq!(
        guard.observation().executable.identity,
        helper.observation().identity
    );
    assert_eq!(
        guard.observation().executable.disk_sha256,
        helper.observation().disk_sha256
    );
    child.stdin.take().unwrap().write_all(b"q").unwrap();
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        if let Some(status) = child.try_wait().unwrap() {
            assert!(status.success());
            break;
        }
        assert!(
            Instant::now() < deadline,
            "owned child exit deadline; no arbitrary kill fallback"
        );
        std::thread::sleep(Duration::from_millis(10));
    }
    assert_eq!(guard.liveness().unwrap(), ProcessLiveness::Exited);
}

#[test]
#[ignore = "real native Apple Silicon; private actual CFBundle fixture"]
fn native_bundle_executable_requires_descriptor_ancestry() {
    let fixture = Fixture::new();
    let root = fixture.path.join("Fixture.app");
    fs::create_dir_all(root.join("Contents/MacOS")).unwrap();
    fs::write(root.join("Contents/Info.plist"), b"<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>CFBundleExecutable</key><string>fixture</string><key>CFBundlePackageType</key><string>APPL</string></dict></plist>").unwrap();
    fs::write(
        root.join("Contents/MacOS/fixture"),
        b"private bytes, never launched",
    )
    .unwrap();
    let captured = bundle::capture_bundle(&root).unwrap();
    assert_eq!(
        captured.executable().physical_path,
        root.join("Contents/MacOS/fixture")
    );
    captured.revalidate().unwrap();
    fs::remove_file(root.join("Contents/MacOS/fixture")).unwrap();
    let outside = fixture.file("outside", b"private outside");
    std::os::unix::fs::symlink(&outside, root.join("Contents/MacOS/fixture")).unwrap();
    assert_eq!(
        bundle::capture_bundle(&root).err().unwrap().code,
        PlatformErrorCode::LinkOrReparsePoint
    );
}

#[test]
#[ignore = "ephemeral ordinary-user Data Protection Keychain fixture; never developer setup"]
fn native_keychain_roundtrip_wrong_purpose_and_exact_delete() {
    let fixture = Fixture::new();
    assert_eq!(
        std::env::var("BRIDGE_MACOS_KEYCHAIN_FIXTURE").unwrap(),
        "ephemeral-ordinary-user-data-protection"
    );
    let purpose = SecretPurpose {
        version: 1,
        domain: SecretDomain::ProfileCompanion,
        context: format!(
            "br07-native-fixture:{}",
            fixture.path.file_name().unwrap().to_string_lossy()
        ),
    };
    let reference = secrets::protect_secret(&purpose, b"private fixture secret").unwrap();
    // Delete this created exact reference even if the assertions fail. No store reset.
    let assertions = std::panic::catch_unwind(|| {
        assert_eq!(
            secrets::unprotect_secret(&purpose, &reference)
                .unwrap()
                .expose(),
            b"private fixture secret"
        );
        let mut wrong = purpose.clone();
        wrong.domain = SecretDomain::UpdateState;
        assert_eq!(
            secrets::unprotect_secret(&wrong, &reference)
                .err()
                .unwrap()
                .code,
            PlatformErrorCode::InvalidInput
        );
    });
    secrets::delete_secret(&purpose, &reference).unwrap();
    assert_eq!(
        secrets::unprotect_secret(&purpose, &reference)
            .err()
            .unwrap()
            .code,
        PlatformErrorCode::Missing
    );
    assertions.unwrap();
}

#[test]
#[ignore = "real native Apple Silicon; private unsigned code fixture, no trust setup"]
fn native_unsigned_signature_cannot_grant_os_trust_or_publisher_policy() {
    let fixture = Fixture::new();
    let unsigned = fixture.file("unsigned-script", b"#!/bin/sh\nexit 0\n");
    let subject = filesystem::capture_file(&unsigned, true).unwrap();
    for domain in [
        TrustDomain::Mod,
        TrustDomain::OfficialGame,
        TrustDomain::Bridge,
    ] {
        let observation =
            signature::observe_signature(&subject, domain, RevocationPolicy::CacheOnly, 0).unwrap();
        assert_eq!(observation.domain, domain);
        assert!(matches!(
            observation.trust,
            SignatureTrust::OsRefused { .. }
        ));
        assert!(observation.signer_certificate_sha256.is_none());
    }
    assert_eq!(
        signature::observe_signature(&subject, TrustDomain::Mod, RevocationPolicy::CacheOnly, 1)
            .unwrap_err()
            .code,
        PlatformErrorCode::InvalidInput
    );
}

#[test]
#[ignore = "real native Apple Silicon; non-GUI exact session, no arbitrary foreground target"]
fn native_non_gui_focus_and_unavailable_shell_features_are_explicit() {
    let actual = process::capture_process(std::process::id()).unwrap();
    let result = shell::focus_exact_process(&actual);
    // Test harness threads are not AppKit's main thread. It must refuse rather
    // than switch by PID. A GUI foreground success needs a separate real fixture.
    assert!(
        matches!(result, Err(error) if error.code == PlatformErrorCode::FeatureUnavailable)
            || matches!(result, Ok(FocusOutcome::NoWindow | FocusOutcome::Denied))
    );
    assert_eq!(
        shell::register_native_menu().unwrap_err().code,
        PlatformErrorCode::FeatureUnavailable
    );
}
