//! Explicit native producer probe. Run only under the owning gate's adopted
//! manifest/fixture packet. Ordinary Cargo suites must report this as unexecuted.
use bridge_native::{
    LoadedModule, ModuleProvenance, NativeComponent, PinnedModuleSpec, current_host,
};
use bridge_profiles::*;
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::{
    path::{Path, PathBuf},
    sync::Arc,
};

fn workspace() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap()
        .to_owned()
}
fn sha256(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}
fn inputs() -> (PinnedModuleSpec, PathBuf) {
    let workspace = workspace().canonicalize().unwrap();
    let manifest = PathBuf::from(
        std::env::var_os("BRIDGE_TEST_NATIVE_MANIFEST").expect("explicit native manifest required"),
    );
    assert_eq!(
        manifest.canonicalize().unwrap(),
        workspace
            .join("dependencies/next-native-inputs.json")
            .canonicalize()
            .unwrap(),
        "only the owning backend's adopted manifest may execute"
    );
    let bytes = std::fs::read(manifest).unwrap();
    assert!(bytes.len() <= 65_536);
    let document: Value = serde_json::from_slice(&bytes).unwrap();
    assert_eq!(document["schemaVersion"], "bridge-native-probe-inputs/v1");
    assert_eq!(document["owningRepository"], "Guffawaffle/stfc-mod-bridge");
    let host = current_host().expect("qualified native host required");
    let selected: Vec<_> = document["modules"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|entry| {
            entry["component"] == "profiles"
                && serde_json::from_value::<bridge_native::NativeHost>(entry["host"].clone()).ok()
                    == Some(host)
        })
        .collect();
    assert_eq!(
        selected.len(),
        1,
        "exactly one native Profiles input required"
    );
    let selected = selected[0];
    assert_eq!(selected["producer"], "Guffawaffle/stfc-profiles");
    assert_eq!(selected["symbolAbi"], 1);
    assert_eq!(selected["jsonApi"], 2);
    let provenance: ModuleProvenance =
        serde_json::from_value(selected["provenance"].clone()).unwrap();
    let receipt = selected["buildReceipt"].as_str().unwrap();
    let relative = Path::new(receipt);
    assert!(
        relative
            .components()
            .all(|p| matches!(p, std::path::Component::Normal(_)))
    );
    let receipt = workspace.join(relative).canonicalize().unwrap();
    assert!(receipt.starts_with(&workspace));
    assert_eq!(
        sha256(&std::fs::read(receipt).unwrap()),
        provenance.build_receipt_sha256
    );
    let spec = PinnedModuleSpec::new(
        NativeComponent::Profiles,
        host,
        workspace.clone(),
        PathBuf::from(selected["relativePath"].as_str().unwrap()),
        selected["sha256"].as_str().unwrap().to_owned(),
        provenance,
    )
    .unwrap();
    let fixture = PathBuf::from(
        std::env::var_os("BRIDGE_TEST_NATIVE_FIXTURE_ROOT")
            .expect("explicit root-owned fixture required"),
    );
    let fixture = fixture.canonicalize().unwrap();
    let allowed = workspace
        .join("artifacts/next/native-ffi")
        .canonicalize()
        .unwrap();
    assert!(fixture.starts_with(&allowed));
    assert_eq!(fixture.file_name().unwrap(), "fixtures");
    assert_eq!(
        fixture.strip_prefix(&allowed).unwrap().components().count(),
        2
    );
    assert!(fixture.is_dir());
    assert!(
        !std::fs::symlink_metadata(&fixture)
            .unwrap()
            .file_type()
            .is_symlink()
    );
    (spec, fixture)
}
fn path(path: &Path) -> NativePath {
    NativePath::new(path.to_str().expect("fixture path must preserve UTF-8")).unwrap()
}

#[cfg(windows)]
fn own_package_identity() -> Value {
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetCurrentPackageFullName(length: *mut u32, full_name: *mut u16) -> i32;
    }
    const NO_PACKAGE: i32 = 15_700;
    const INSUFFICIENT_BUFFER: i32 = 122;
    const MAXIMUM_PACKAGE_CHARACTERS: u32 = 256;
    let mut length = 0;
    // SAFETY: Windows' metadata-only ABI receives a live u32 and null output
    // for its size query. It does not change this process's package or token.
    let first = unsafe { GetCurrentPackageFullName(&mut length, std::ptr::null_mut()) };
    if first == NO_PACKAGE {
        return serde_json::json!({
            "sizeQueryReturnCode":first, "packageFullName":null,
            "classification":"no-package", "queryReturnCode":null
        });
    }
    if first != INSUFFICIENT_BUFFER || !(1..=MAXIMUM_PACKAGE_CHARACTERS).contains(&length) {
        return serde_json::json!({
            "sizeQueryReturnCode":first, "requiredCharacters":length,
            "packageFullName":null, "classification":"query-refused",
            "queryReturnCode":null
        });
    }
    let mut buffer = vec![0_u16; length as usize];
    // SAFETY: the writable allocation contains exactly the bounded size from
    // Windows, including its NUL terminator, and remains live for this call.
    let result = unsafe { GetCurrentPackageFullName(&mut length, buffer.as_mut_ptr()) };
    let full_name = if result == 0
        && length > 0
        && length as usize <= buffer.len()
        && buffer[length as usize - 1] == 0
        && !buffer[..length as usize - 1].contains(&0)
    {
        String::from_utf16(&buffer[..length as usize - 1]).ok()
    } else {
        None
    };
    serde_json::json!({
        "sizeQueryReturnCode":first, "queryReturnCode":result,
        "returnedCharacters":length, "packageFullName":full_name,
        "classification":if full_name.is_some() {"packaged"} else {"query-refused"}
    })
}

#[cfg(not(windows))]
fn own_package_identity() -> Value {
    serde_json::json!({"classification":"not-windows"})
}

fn observe_test_context(catalog_location: &CatalogLocation) {
    // canonicalize reads directory metadata only. This diagnostic neither
    // enumerates shared catalog entries nor creates or acquires a lifecycle lock.
    let shared = Path::new(catalog_location.catalog_root.as_str()).canonicalize();
    let (physical_shared_root, canonicalization_error) = match shared {
        Ok(path) => (Some(path), None),
        Err(error) => (None, Some(format!("{:?}", error.kind()))),
    };
    println!(
        "PROFILES_NATIVE_TEST_CONTEXT_JSON={}",
        serde_json::json!({
            "schemaVersion":"bridge-profiles-native-test-context/v1",
            "processId":std::process::id(), "architecture":std::env::consts::ARCH,
            "owningRoot":workspace().canonicalize().unwrap(),
            "nativeCatalogLocationRoot":catalog_location.catalog_root.as_str(),
            "physicalSharedRoot":physical_shared_root,
            "sharedRootCanonicalizationError":canonicalization_error,
            "ownPackageIdentity":own_package_identity(),
            "observation":"test-process and shared-root metadata only"
        })
    );
}

fn success(
    client: &ProfilesClient,
    root: Option<&NativePath>,
    operation: CatalogOperation,
) -> Box<CatalogSuccess> {
    let request = CatalogRequest {
        root: root.cloned(),
        operation,
    };
    // Only the consumer's fixed operation name is included in failures; request
    // paths, user input and producer diagnostic strings are never logged.
    let encoded: Value = serde_json::from_slice(&request.encoded().unwrap()).unwrap();
    let operation_name = encoded["operation"].as_str().unwrap();
    match client.request(&request).unwrap_or_else(|error| {
        panic!("native fixture operation {operation_name} failed at consumer boundary: {error}")
    }) {
        CatalogReply::Success(output) => output,
        CatalogReply::Refused(error) => {
            panic!(
                "native fixture operation {operation_name} refused: {:?}",
                error.code
            )
        }
    }
}

#[test]
#[ignore = "requires explicit adopted native manifest, physical fixture and owning gate authorization"]
fn exact_native_profiles_api2_allocations_shared_data_and_installation_custody() {
    let (spec, fixture) = inputs();
    // SAFETY: this explicit ignored test executes only the owning gate's closed,
    // receipt-backed producer inputs, on the selected supported native host.
    let module = unsafe { LoadedModule::open(&spec) }.unwrap();
    let identity = module.identity().clone();
    let module_alive = Arc::downgrade(&module);
    let client = unsafe { ProfilesClient::bind(module.clone()) }.unwrap();
    let CatalogSuccess::CatalogLocation(location) =
        *success(&client, None, CatalogOperation::CatalogLocation)
    else {
        panic!("catalog location missing");
    };

    let catalog = fixture.join("profiles-目录");
    assert!(
        !catalog.exists(),
        "fixture catalog must be fresh; preserve any existing state"
    );
    let catalog = path(&catalog);
    let CatalogSuccess::Profiles(initial) = *success(
        &client,
        Some(&catalog),
        CatalogOperation::List { archived: false },
    ) else {
        panic!("list missing");
    };
    assert!(initial.profiles.is_empty());
    assert!(initial.issues.is_empty());
    let CatalogSuccess::Profile(created) = *success(
        &client,
        Some(&catalog),
        CatalogOperation::Create(NewProfile {
            name: "Synthetic 目录".into(),
            preferred_installation_id: None,
            game_directory: None,
        }),
    ) else {
        panic!("created profile missing");
    };
    assert_eq!(created.profile.kind, ProfileKind::Isolated);
    assert_eq!(created.profile.preferences_initialized, Some(false));
    assert_eq!(created.profile.game_directory, "");
    let CatalogSuccess::Profile(observed) = *success(
        &client,
        Some(&catalog),
        CatalogOperation::Paths {
            id: created.profile.id.clone(),
            archived: false,
        },
    ) else {
        panic!("profile paths missing");
    };
    assert_eq!(observed.profile.id, created.profile.id);
    assert_eq!(observed.profile.name, "Synthetic 目录");
    let data = client
        .acquire_data_lease(Some(&catalog), &created.profile.id)
        .unwrap();
    let second_shared_data = client
        .acquire_data_lease(Some(&catalog), &created.profile.id)
        .unwrap();
    assert_eq!(
        client
            .request(&CatalogRequest {
                root: Some(catalog.clone()),
                operation: CatalogOperation::Archive {
                    id: created.profile.id.clone(),
                    expected_revision: created.profile.revision.clone()
                }
            })
            .unwrap(),
        CatalogReply::Refused(OperationFailure {
            code: NativeErrorCode::Busy,
            process_id: None
        })
    );
    drop(second_shared_data);

    let game = fixture.join("profiles-empty-installation");
    assert!(
        !game.exists(),
        "physical installation fixture must be unique"
    );
    std::fs::create_dir(&game).unwrap();
    assert!(
        game.to_str().unwrap().is_ascii(),
        "ASCII fixture permits exact producer invariant-fold prediction"
    );
    observe_test_context(&location);
    let CatalogSuccess::Game(status) = *success(
        &client,
        Some(&catalog),
        CatalogOperation::InstallationStatus(GameSelection {
            installation_id: None,
            game_directory: Some(path(&game)),
        }),
    ) else {
        panic!("installation status missing");
    };
    assert_eq!(status.installation.state, GameState::Ready);
    assert!(!status.installation.requires_recovery);
    assert!(status.installation.running_process_ids.is_empty());
    let canonical_game = status.installation.game_directory;
    assert!(canonical_game.as_str().is_ascii());
    let lock_key = sha256(canonical_game.as_str().to_ascii_lowercase().as_bytes());
    let shared_lock = Path::new(location.catalog_root.as_str())
        .join(".locks")
        .join(format!("install-{lock_key}.lock"));
    assert!(
        shared_lock.is_file(),
        "producer status must establish this exact canonical coordination lock"
    );
    let installation = client
        .acquire_installation_lease(Some(&catalog), &canonical_game)
        .unwrap();
    drop(client);
    drop(module);
    assert!(
        module_alive.upgrade().is_some(),
        "native leases retain the original module after client loss"
    );
    drop(data);
    assert!(module_alive.upgrade().is_some());
    drop(installation);
    assert!(module_alive.upgrade().is_none());
    assert!(
        shared_lock.is_file(),
        "shared coordination file remains intact after release"
    );
    // No shared catalog enumeration, cleanup, accounts, game binaries, launch,
    // update or configuration edits. The private fixture is retained as evidence.
    println!(
        "PROFILES_NATIVE_PROBE_JSON={}",
        serde_json::json!({
            "schemaVersion":"bridge-profiles-native-probe/v1", "module":identity,
            "apiVersion":API_VERSION, "fixtureRoot":fixture, "privateCatalog":catalog.as_str(),
            "privatePhysicalInstallation":canonical_game.as_str(), "sharedInstallationLock":shared_lock,
            "sharedInstallationLockRetained":true, "sharedDataLeasesObserved":2,
            "directoryMutationRefusedWhileDataLeaseHeld":true, "moduleRetainedAfterClientDrop":true,
            "moduleReleasedAfterFinalLease":true, "nativeRuntimeQualified":false, "releaseQualified":false
        })
    );
}
