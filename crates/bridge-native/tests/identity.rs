use bridge_native::{
    LoadError, LoadedModule, ModuleProvenance, NativeComponent, NativeHost, PinnedModuleSpec,
    inspect_image,
};
use sha2::{Digest, Sha256};
use std::path::PathBuf;

fn provenance() -> ModuleProvenance {
    ModuleProvenance {
        source_revision: "a".repeat(40),
        source_archive_sha256: "b".repeat(64),
        build_receipt_sha256: "c".repeat(64),
    }
}
fn pe() -> Vec<u8> {
    let mut bytes = vec![0; 512];
    bytes[..2].copy_from_slice(b"MZ");
    bytes[0x3c..0x40].copy_from_slice(&64u32.to_le_bytes());
    bytes[64..68].copy_from_slice(b"PE\0\0");
    bytes[68..70].copy_from_slice(&0x8664u16.to_le_bytes());
    bytes[70..72].copy_from_slice(&1u16.to_le_bytes());
    bytes[84..86].copy_from_slice(&112u16.to_le_bytes());
    bytes[86..88].copy_from_slice(&0x2002u16.to_le_bytes());
    bytes[88..90].copy_from_slice(&0x20bu16.to_le_bytes());
    bytes
}
fn macho() -> Vec<u8> {
    let mut bytes = vec![0; 40];
    bytes[0..4].copy_from_slice(&0xfeedfacfu32.to_le_bytes());
    bytes[4..8].copy_from_slice(&0x0100000cu32.to_le_bytes());
    bytes[12..16].copy_from_slice(&6u32.to_le_bytes());
    bytes[16..20].copy_from_slice(&1u32.to_le_bytes());
    bytes[20..24].copy_from_slice(&8u32.to_le_bytes());
    bytes[36..40].copy_from_slice(&8u32.to_le_bytes());
    bytes
}

#[test]
fn pin_refuses_relative_roots_traversal_empty_paths_and_unreviewed_digest_shapes() {
    let root = std::env::current_dir().unwrap();
    let construct = |root, relative, hash| {
        PinnedModuleSpec::new(
            NativeComponent::Profiles,
            NativeHost::WindowsX64,
            root,
            relative,
            hash,
            provenance(),
        )
    };
    assert!(
        construct(
            root.clone(),
            PathBuf::from("native/producer.dll"),
            "a".repeat(64)
        )
        .is_ok()
    );
    assert!(
        construct(
            PathBuf::from("relative"),
            PathBuf::from("producer.dll"),
            "a".repeat(64)
        )
        .is_err()
    );
    for path in ["", "../producer.dll", "native/../../producer.dll"] {
        assert!(construct(root.clone(), PathBuf::from(path), "a".repeat(64)).is_err());
    }
    for hash in ["A".repeat(64), "a".repeat(63), "g".repeat(64)] {
        assert!(construct(root.clone(), PathBuf::from("producer.dll"), hash).is_err());
    }
}

#[test]
fn pe_observation_refuses_wrong_machine_executable_pe32_and_truncated_headers() {
    let valid = pe();
    assert!(inspect_image(&valid, NativeHost::WindowsX64).is_ok());
    for (offset, value) in [(68, 0xaa64u16), (86, 2), (88, 0x10b), (84, 111)] {
        let mut bytes = valid.clone();
        bytes[offset..offset + 2].copy_from_slice(&value.to_le_bytes());
        assert!(inspect_image(&bytes, NativeHost::WindowsX64).is_err());
    }
    for length in [0, 2, 63, 88, 199] {
        assert!(inspect_image(&valid[..length], NativeHost::WindowsX64).is_err());
    }
    let mut overflow = valid.clone();
    overflow[0x3c..0x40].copy_from_slice(&u32::MAX.to_le_bytes());
    assert!(inspect_image(&overflow, NativeHost::WindowsX64).is_err());
    assert!(inspect_image(&valid, NativeHost::MacOsArm64).is_err());
}

#[test]
fn macho_observation_refuses_intel_fat_non_dylib_and_malformed_command_tables() {
    let valid = macho();
    assert!(inspect_image(&valid, NativeHost::MacOsArm64).is_ok());
    for (offset, value) in [
        (0, 0xcafebabeu32),
        (4, 0x01000007),
        (12, 2),
        (16, 0),
        (20, u32::MAX),
        (36, 7),
        (36, 16),
    ] {
        let mut bytes = valid.clone();
        bytes[offset..offset + 4].copy_from_slice(&value.to_le_bytes());
        assert!(inspect_image(&bytes, NativeHost::MacOsArm64).is_err());
    }
    for length in [0, 4, 31, 39] {
        assert!(inspect_image(&valid[..length], NativeHost::MacOsArm64).is_err());
    }
    assert!(inspect_image(&valid, NativeHost::WindowsX64).is_err());
}

#[test]
fn wrong_host_is_refused_before_file_lookup_or_any_initializer() {
    let host = bridge_native::current_host().unwrap();
    let foreign = match host {
        NativeHost::WindowsX64 => NativeHost::MacOsArm64,
        NativeHost::MacOsArm64 => NativeHost::WindowsX64,
    };
    let spec = PinnedModuleSpec::new(
        NativeComponent::Profiles,
        foreign,
        std::env::current_dir().unwrap(),
        PathBuf::from("absent.dll"),
        "a".repeat(64),
        provenance(),
    )
    .unwrap();
    // SAFETY: wrong host is refused before filesystem or foreign-code execution.
    assert_eq!(
        unsafe { LoadedModule::open(&spec) }.err(),
        Some(LoadError {
            code: "wrong_native_host"
        })
    );
}

#[test]
fn changed_bytes_refuse_before_foreign_code_is_loaded() {
    let directory = std::env::temp_dir().join(format!(
        "bridge-native-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ));
    std::fs::create_dir(&directory).unwrap();
    // The fixture's physical root is the pin, independent of TEMP redirection
    // (including macOS /var). Loader refusal of lexical links remains strict.
    let physical_directory = std::fs::canonicalize(&directory).unwrap();
    let original = if bridge_native::current_host().unwrap() == NativeHost::WindowsX64 {
        pe()
    } else {
        macho()
    };
    let hash: String = Sha256::digest(&original)
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect();
    let mut changed = original;
    changed[30] ^= 1;
    std::fs::write(directory.join("producer.bin"), changed).unwrap();
    let spec = PinnedModuleSpec::new(
        NativeComponent::Profiles,
        bridge_native::current_host().unwrap(),
        physical_directory.clone(),
        PathBuf::from("producer.bin"),
        hash,
        provenance(),
    )
    .unwrap();
    // SAFETY: a differing digest refuses before loading the inert test bytes.
    assert_eq!(
        unsafe { LoadedModule::open(&spec) }.err(),
        Some(LoadError {
            code: "module_hash_mismatch"
        })
    );
    std::fs::remove_file(directory.join("producer.bin")).unwrap();
    std::fs::remove_dir(directory).unwrap();
}

#[test]
fn lexical_root_and_relative_child_links_refuse_before_foreign_code_is_loaded() {
    let directory = std::env::temp_dir().join(format!(
        "bridge-native-links-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ));
    std::fs::create_dir(&directory).unwrap();
    let directory = std::fs::canonicalize(directory).unwrap();
    let physical = directory.join("physical");
    let linked = directory.join("linked");
    std::fs::create_dir(&physical).unwrap();
    let bytes = if bridge_native::current_host().unwrap() == NativeHost::WindowsX64 {
        pe()
    } else {
        macho()
    };
    let hash: String = Sha256::digest(&bytes)
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect();
    std::fs::write(physical.join("producer.bin"), bytes).unwrap();
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        let powershell = PathBuf::from(std::env::var_os("SystemRoot").unwrap())
            .join("System32/WindowsPowerShell/v1.0/powershell.exe");
        // Fixed script, with paths supplied as data. A private junction needs
        // no symlink privilege; no other fixture or global setting is touched.
        let output = std::process::Command::new(powershell)
            .args([
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "New-Item -ItemType Junction -Path $env:BRIDGE_NATIVE_TEST_LINK -Value $env:BRIDGE_NATIVE_TEST_TARGET -ErrorAction Stop | Out-Null",
            ])
            .env("BRIDGE_NATIVE_TEST_LINK", &linked)
            .env("BRIDGE_NATIVE_TEST_TARGET", &physical)
            .creation_flags(0x0800_0000)
            .output()
            .unwrap();
        assert!(output.status.success(), "private junction creation failed");
    }
    #[cfg(target_os = "macos")]
    std::os::unix::fs::symlink(&physical, &linked).unwrap();
    for (root, relative) in [
        (linked.clone(), PathBuf::from("producer.bin")),
        (directory.clone(), PathBuf::from("linked/producer.bin")),
    ] {
        let spec = PinnedModuleSpec::new(
            NativeComponent::Profiles,
            bridge_native::current_host().unwrap(),
            root,
            relative,
            hash.clone(),
            provenance(),
        )
        .unwrap();
        // SAFETY: lexical link refusal precedes loading the inert image bytes.
        assert_eq!(
            unsafe { LoadedModule::open(&spec) }.err(),
            Some(LoadError {
                code: "module_path_is_link"
            })
        );
    }
    // Remove only the link itself, then this test's exact private contents.
    #[cfg(windows)]
    std::fs::remove_dir(linked).unwrap();
    #[cfg(target_os = "macos")]
    std::fs::remove_file(linked).unwrap();
    std::fs::remove_file(physical.join("producer.bin")).unwrap();
    std::fs::remove_dir(physical).unwrap();
    std::fs::remove_dir(directory).unwrap();
}
