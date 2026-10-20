use bridge_domain::platform::{
    NativeArchitecture, PhysicalIdentity, PlatformErrorCode, ProcessStartStamp, SecretDomain,
    SecretPurpose,
};
use bridge_platform_macos::{
    format::{self, SecretReference},
    require_macos_arm64,
};

fn purpose(domain: SecretDomain, context: &str) -> SecretPurpose {
    SecretPurpose {
        version: 1,
        domain,
        context: context.into(),
    }
}

#[test]
fn absolute_paths_preserve_case_and_utf8_bytes() {
    let path = "/Volumes/CaseSensitive/Éngine/Foo".as_bytes();
    let components = format::absolute_components(path).unwrap();
    assert_eq!(components[2], "Éngine".as_bytes());
    assert_eq!(components[3], b"Foo");
    assert_ne!(
        format::absolute_components(b"/Foo").unwrap(),
        format::absolute_components(b"/foo").unwrap()
    );
}

#[test]
fn process_path_count_excludes_nul_and_matches_bounded_native_storage() {
    assert_eq!(
        format::pid_path_payload(b"/private/Case\0unused", 13).unwrap(),
        b"/private/Case"
    );
    for (storage, count) in [
        (b"/path\0".as_slice(), 0),
        (b"/path\0".as_slice(), 4),
        (b"/path\0".as_slice(), 6),
        (b"/path".as_slice(), 5),
        (b"/pa\0th\0".as_slice(), 6),
        (b"\0path\0".as_slice(), 5),
    ] {
        assert_eq!(
            format::pid_path_payload(storage, count).unwrap_err().code,
            PlatformErrorCode::UnknownObservation
        );
    }
    assert_eq!(
        format::pid_path_payload(b"relative\0", 8).unwrap_err().code,
        PlatformErrorCode::InvalidInput
    );
}

#[test]
fn path_parser_refuses_lexical_aliases_and_nul() {
    for path in [
        b"relative".as_slice(),
        b"//root",
        b"/a//b",
        b"/a/../b",
        b"/a/./b",
        b"/a/",
        b"/a\0b",
    ] {
        assert_eq!(
            format::absolute_components(path).unwrap_err().code,
            PlatformErrorCode::InvalidInput
        );
    }
}

#[test]
fn filesystem_root_has_no_fabricated_child() {
    assert!(format::absolute_components(b"/").unwrap().is_empty());
}

#[test]
fn path_and_component_limits_are_enforced() {
    let oversized = format!("/{}", "a".repeat(format::MAX_PATH_BYTES));
    assert_eq!(
        format::absolute_components(oversized.as_bytes())
            .unwrap_err()
            .code,
        PlatformErrorCode::TooLarge
    );
    let oversized_leaf = format!("/{}", "a".repeat(256));
    assert_eq!(
        format::absolute_components(oversized_leaf.as_bytes())
            .unwrap_err()
            .code,
        PlatformErrorCode::TooLarge
    );
    let too_deep = format!("/{}", vec!["x"; format::MAX_COMPONENTS + 1].join("/"));
    assert_eq!(
        format::absolute_components(too_deep.as_bytes())
            .unwrap_err()
            .code,
        PlatformErrorCode::TooLarge
    );
}

#[test]
fn relative_mutation_names_cannot_escape_parent() {
    for leaf in [b"".as_slice(), b".", b"..", b"a/b", b"a\0b"] {
        assert_eq!(
            format::validate_leaf(leaf).unwrap_err().code,
            PlatformErrorCode::InvalidInput
        );
    }
    assert!(format::validate_leaf(b".bridge-stage-0123456789abcdef").is_ok());
}

#[test]
fn stage_nonce_names_are_single_components_and_distinct() {
    let first = format::stage_leaf(&[1; 16]);
    let second = format::stage_leaf(&[2; 16]);
    format::validate_leaf(first.as_bytes()).unwrap();
    assert_ne!(first, second);
    assert_eq!(first, ".bridge-stage-01010101010101010101010101010101");
}

#[test]
fn process_start_stamp_keeps_kernel_units_and_bounds() {
    assert_eq!(
        format::start_stamp(99, 999_999).unwrap(),
        ProcessStartStamp::MacOs {
            seconds: 99,
            microseconds: 999_999
        }
    );
    assert_eq!(
        format::start_stamp(99, 1_000_000).unwrap_err().code,
        PlatformErrorCode::UnknownObservation
    );
    assert_eq!(
        format::start_stamp(0, 1).unwrap_err().code,
        PlatformErrorCode::UnknownObservation
    );
}

#[test]
fn process_architecture_is_not_inferred_from_host() {
    assert_eq!(
        format::architecture(0x0100_000c).unwrap(),
        NativeArchitecture::Arm64
    );
    assert_eq!(
        format::architecture(0x0100_0007).unwrap(),
        NativeArchitecture::X86_64
    );
    assert_eq!(format::architecture(7).unwrap(), NativeArchitecture::X86);
    for raw in [0, 12, -1, 0x0200_000c] {
        assert_eq!(
            format::architecture(raw).unwrap_err().code,
            PlatformErrorCode::UnknownObservation
        );
    }
}

#[test]
fn unvalidated_volume_capability_stays_unknown() {
    assert_eq!(format::capability_bit(4, 0, 4), None);
    assert_eq!(format::capability_bit(0, 4, 4), Some(false));
    assert_eq!(format::capability_bit(4, 4, 4), Some(true));
}

#[test]
fn atomic_exchange_requires_two_distinct_exact_objects() {
    let old = PhysicalIdentity::MacOs {
        device: 1,
        inode: 10,
    };
    let new = PhysicalIdentity::MacOs {
        device: 1,
        inode: 20,
    };
    let outsider = PhysicalIdentity::MacOs {
        device: 2,
        inode: 10,
    };
    assert!(format::exchanged_identities(old, new, new, old));
    assert!(!format::exchanged_identities(old, new, old, new));
    assert!(!format::exchanged_identities(old, new, new, outsider));
    assert!(!format::exchanged_identities(old, old, old, old));
}

#[test]
fn keychain_purpose_domains_and_context_bytes_never_alias() {
    let first = purpose(SecretDomain::ApplicationPreferences, "installation:abc");
    let second = purpose(SecretDomain::ProfileCompanion, "installation:abc");
    let third = purpose(SecretDomain::ApplicationPreferences, "installation:ABC");
    assert_ne!(
        format::purpose_digest(&first).unwrap(),
        format::purpose_digest(&second).unwrap()
    );
    assert_ne!(
        format::purpose_digest(&first).unwrap(),
        format::purpose_digest(&third).unwrap()
    );
}

#[test]
fn keychain_purpose_refuses_unknown_versions_empty_and_control_contexts() {
    for context in ["", "a\0b", "a\nb", "a\u{85}b"] {
        assert_eq!(
            format::purpose_digest(&purpose(SecretDomain::UpdateState, context))
                .unwrap_err()
                .code,
            PlatformErrorCode::InvalidInput
        );
    }
    let mut unknown = purpose(SecretDomain::UpdateState, "valid");
    unknown.version = 2;
    assert_eq!(
        format::purpose_digest(&unknown).unwrap_err().code,
        PlatformErrorCode::InvalidInput
    );
    assert_eq!(
        format::purpose_digest(&purpose(SecretDomain::UpdateState, &"x".repeat(1025)))
            .unwrap_err()
            .code,
        PlatformErrorCode::TooLarge
    );
}

#[test]
fn keychain_reference_has_closed_canonical_grammar() {
    let valid = format!("bridge-keychain-v1:{}:{}", "a".repeat(64), "b".repeat(32));
    assert_eq!(SecretReference::decode(&valid).unwrap().encode(), valid);
    for invalid in [
        format!("{valid}:extra"),
        valid.replace("v1", "v2"),
        valid.to_uppercase(),
        format!("bridge-keychain-v1:{}:{}", "a".repeat(63), "b".repeat(32)),
        format!("bridge-keychain-v1:{}:{}", "a".repeat(64), "0".repeat(32)),
        format!("bridge-keychain-v1:{}:{}", "g".repeat(64), "b".repeat(32)),
    ] {
        assert!(SecretReference::decode(&invalid).is_err());
    }
}

#[test]
fn keychain_reference_does_not_authorize_another_purpose() {
    let correct = purpose(SecretDomain::ProfileCompanion, "fixture:one");
    let digest = format::hex(&format::purpose_digest(&correct).unwrap());
    let reference =
        SecretReference::decode(&format!("bridge-keychain-v1:{digest}:{}", "1".repeat(32)))
            .unwrap();
    assert!(reference.matches(&correct).unwrap());
    assert!(
        !reference
            .matches(&purpose(SecretDomain::ProfileCompanion, "fixture:two"))
            .unwrap()
    );
    assert!(
        !reference
            .matches(&purpose(SecretDomain::UpdateState, "fixture:one"))
            .unwrap()
    );
}

#[test]
fn host_gate_never_admits_intel_or_non_macos() {
    if cfg!(all(target_os = "macos", target_arch = "aarch64")) {
        assert!(require_macos_arm64().is_ok());
    } else {
        assert_eq!(
            require_macos_arm64().unwrap_err().code,
            PlatformErrorCode::UnsupportedHost
        );
    }
}
