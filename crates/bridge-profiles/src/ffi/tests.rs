//! Controlled C ABI fixtures allocate real readable buffers and opaque objects.
//! These are allocator/lifetime proofs, not qualification of producer DLL bytes.
use super::*;
use crate::*;
use std::{cell::RefCell, collections::BTreeMap};

#[derive(Default, Debug)]
struct Counts {
    requests: usize,
    acquisitions: usize,
    frees: usize,
    data_releases: usize,
    installation_releases: usize,
    invalid_release: usize,
    after_unload: usize,
    module_drops: usize,
}
pub(super) struct ModuleProbe {
    counts: Rc<RefCell<Counts>>,
}
impl Drop for ModuleProbe {
    fn drop(&mut self) {
        self.counts.borrow_mut().module_drops += 1;
    }
}
#[derive(Clone, Copy, PartialEq, Eq)]
enum LeaseKind {
    Data,
    Installation,
}
struct State {
    status: c_int,
    response: Option<Vec<u8>>,
    error: Option<Vec<u8>>,
    lease: bool,
    allocations: BTreeMap<usize, Vec<u8>>,
    leases: BTreeMap<usize, (Box<u8>, LeaseKind)>,
    counts: Rc<RefCell<Counts>>,
    module: std::rc::Weak<ModuleProbe>,
    last_request: Vec<u8>,
    last_root: Option<Vec<u8>>,
    last_selector: Vec<u8>,
}
thread_local! { static STATE: RefCell<Option<State>> = const { RefCell::new(None) }; }
fn with_state<T>(f: impl FnOnce(&mut State) -> T) -> T {
    STATE.with(|slot| f(slot.borrow_mut().as_mut().unwrap()))
}
fn allocate(state: &mut State, bytes: Vec<u8>) -> *mut c_char {
    let mut bytes = bytes;
    let pointer = bytes.as_mut_ptr();
    state.allocations.insert(pointer as usize, bytes);
    pointer.cast()
}
unsafe extern "C" fn fake_request(input: *const c_char, output: *mut *mut c_char) -> c_int {
    with_state(|state| {
        state.counts.borrow_mut().requests += 1;
        state.last_request = unsafe { std::ffi::CStr::from_ptr(input) }
            .to_bytes()
            .to_vec();
        let pointer = state
            .response
            .clone()
            .map_or(std::ptr::null_mut(), |b| allocate(state, b));
        unsafe { output.write(pointer) };
        state.status
    })
}
unsafe extern "C" fn fake_free(pointer: *mut c_void) {
    with_state(|state| {
        let mut counts = state.counts.borrow_mut();
        if state.module.upgrade().is_none() {
            counts.after_unload += 1;
        }
        if state.allocations.remove(&(pointer as usize)).is_some() {
            counts.frees += 1;
        } else {
            counts.invalid_release += 1;
        }
    });
}
unsafe fn fake_acquire(
    root: *const c_char,
    selector: *const c_char,
    lease: *mut *mut c_void,
    error: *mut *mut c_char,
    kind: LeaseKind,
) -> c_int {
    with_state(|state| {
        state.counts.borrow_mut().acquisitions += 1;
        state.last_root = (!root.is_null()).then(|| {
            unsafe { std::ffi::CStr::from_ptr(root) }
                .to_bytes()
                .to_vec()
        });
        state.last_selector = unsafe { std::ffi::CStr::from_ptr(selector) }
            .to_bytes()
            .to_vec();
        let pointer = if state.lease {
            let mut object = Box::new(42_u8);
            let pointer = (&mut *object as *mut u8).cast();
            state.leases.insert(pointer as usize, (object, kind));
            pointer
        } else {
            std::ptr::null_mut()
        };
        let diagnostic = state
            .error
            .clone()
            .map_or(std::ptr::null_mut(), |b| allocate(state, b));
        unsafe {
            lease.write(pointer);
            error.write(diagnostic)
        };
        state.status
    })
}
unsafe extern "C" fn fake_acquire_data(
    root: *const c_char,
    id: *const c_char,
    lease: *mut *mut c_void,
    error: *mut *mut c_char,
) -> c_int {
    unsafe { fake_acquire(root, id, lease, error, LeaseKind::Data) }
}
unsafe extern "C" fn fake_acquire_installation(
    root: *const c_char,
    game: *const c_char,
    lease: *mut *mut c_void,
    error: *mut *mut c_char,
) -> c_int {
    unsafe { fake_acquire(root, game, lease, error, LeaseKind::Installation) }
}
fn release(pointer: *mut c_void, expected: LeaseKind) {
    with_state(|state| {
        let mut counts = state.counts.borrow_mut();
        if state.module.upgrade().is_none() {
            counts.after_unload += 1;
        }
        match state.leases.remove(&(pointer as usize)) {
            Some((_, actual)) if actual == expected => match expected {
                LeaseKind::Data => counts.data_releases += 1,
                LeaseKind::Installation => counts.installation_releases += 1,
            },
            _ => counts.invalid_release += 1,
        }
    });
}
unsafe extern "C" fn fake_release_data(pointer: *mut c_void) {
    release(pointer, LeaseKind::Data);
}
unsafe extern "C" fn fake_release_installation(pointer: *mut c_void) {
    release(pointer, LeaseKind::Installation);
}

#[allow(clippy::arc_with_non_send_sync)] // Deliberate module custody on this test's owning thread.
fn fixture() -> (ProfilesClient, Rc<RefCell<Counts>>) {
    let counts = Rc::new(RefCell::new(Counts::default()));
    let module = Rc::new(ModuleProbe {
        counts: counts.clone(),
    });
    STATE.with(|slot| {
        *slot.borrow_mut() = Some(State {
            status: 0,
            response: None,
            error: None,
            lease: false,
            allocations: BTreeMap::new(),
            leases: BTreeMap::new(),
            counts: counts.clone(),
            module: Rc::downgrade(&module),
            last_request: vec![],
            last_root: None,
            last_selector: vec![],
        })
    });
    (
        ProfilesClient {
            functions: Arc::new(Functions {
                owner: ModuleOwner::Fixture { _module: module },
                request: fake_request,
                free: fake_free,
                acquire_data: fake_acquire_data,
                release_data: fake_release_data,
                acquire_installation: fake_acquire_installation,
                release_installation: fake_release_installation,
            }),
            _thread: PhantomData,
        },
        counts,
    )
}
fn nul(bytes: impl AsRef<[u8]>) -> Vec<u8> {
    let mut bytes = bytes.as_ref().to_vec();
    bytes.push(0);
    bytes
}
fn answer(value: serde_json::Value) {
    with_state(|s| s.response = Some(nul(serde_json::to_vec(&value).unwrap())));
}
fn list() -> CatalogRequest {
    CatalogRequest {
        root: Some(NativePath::new("D:\\Synthetic\\目录").unwrap()),
        operation: CatalogOperation::List { archived: false },
    }
}
fn empty_list() -> serde_json::Value {
    serde_json::json!({"apiVersion":2,"ok":true,"revision":"native-revision:1","profiles":[],"issues":[]})
}
fn id() -> NativeId {
    NativeId::new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").unwrap()
}
fn assert_clean(counts: &Rc<RefCell<Counts>>) {
    let counts = counts.borrow();
    assert_eq!(counts.invalid_release, 0);
    assert_eq!(counts.after_unload, 0);
    with_state(|s| {
        assert!(s.allocations.is_empty());
        assert!(s.leases.is_empty());
    });
}

#[test]
fn binding_handshake_observes_api2_without_catalog_or_lease_authority() {
    let (client, counts) = fixture();
    answer(
        serde_json::json!({"apiVersion":2,"ok":true,"catalogRoot":"D:\\Synthetic\\OSUserCatalog"}),
    );
    client.verify_api2().unwrap();
    with_state(|s| {
        let wire: serde_json::Value = serde_json::from_slice(&s.last_request).unwrap();
        assert_eq!(
            wire,
            serde_json::json!({"apiVersion":2,"operation":"catalog-location"})
        );
    });
    answer(
        serde_json::json!({"apiVersion":1,"ok":true,"catalogRoot":"D:\\Synthetic\\OSUserCatalog"}),
    );
    assert_eq!(
        client.verify_api2(),
        Err(ConsumerError::UnsupportedApiVersion)
    );
    assert_eq!(counts.borrow().acquisitions, 0);
    assert_eq!(counts.borrow().frees, 2);
    drop(client);
    assert_clean(&counts);
}

#[test]
fn structured_refusal_is_distinct_from_allocation_abi_failures_and_redacts_diagnostics() {
    let (client, counts) = fixture();
    answer(
        serde_json::json!({"apiVersion":2,"ok":false,"error":{"code":"busy","message":"PRIVATE-NATIVE-DETAIL"}}),
    );
    let reply = client.request(&list()).unwrap();
    assert_eq!(
        reply,
        CatalogReply::Refused(OperationFailure {
            code: NativeErrorCode::Busy,
            process_id: None
        })
    );
    assert!(!format!("{reply:?}").contains("PRIVATE-NATIVE-DETAIL"));
    assert_eq!(counts.borrow().frees, 1);
    for (code, expected) in [
        (1, AbiStatus::InvalidInput),
        (2, AbiStatus::NativeFailure),
        (-1, AbiStatus::Unexpected),
    ] {
        with_state(|s| {
            s.status = code;
            s.response = None;
        });
        assert_eq!(
            client.request(&list()),
            Err(ConsumerError::Abi {
                status: expected,
                diagnostic: DiagnosticDisposition::Absent
            })
        );
    }
    // Even an allocation returned with a nonzero status receives its own free.
    with_state(|s| {
        s.status = 2;
        s.response = Some(nul(b"unexpected ABI allocation"));
    });
    assert_eq!(
        client.request(&list()),
        Err(ConsumerError::Abi {
            status: AbiStatus::NativeFailure,
            diagnostic: DiagnosticDisposition::Suppressed
        })
    );
    assert_eq!(counts.borrow().frees, 2);
    drop(client);
    assert_clean(&counts);
}

#[test]
fn installation_status_refusal_codes_remain_typed_and_native_details_are_suppressed() {
    let (client, counts) = fixture();
    let request = CatalogRequest {
        root: list().root,
        operation: CatalogOperation::InstallationStatus(game_selection()),
    };
    let cases = [
        ("unsafe_path", NativeErrorCode::UnsafePath),
        ("invalid_installation", NativeErrorCode::InvalidInstallation),
        ("root_unavailable", NativeErrorCode::RootUnavailable),
        ("root_redirected", NativeErrorCode::RootRedirected),
        ("lock_failed", NativeErrorCode::LockFailed),
        ("hash_unavailable", NativeErrorCode::HashUnavailable),
        ("hash_failed", NativeErrorCode::HashFailed),
        ("process_check_failed", NativeErrorCode::ProcessCheckFailed),
        (
            "process_check_incomplete",
            NativeErrorCode::ProcessCheckIncomplete,
        ),
        ("PRIVATE-FOREIGN-CODE", NativeErrorCode::Unknown),
    ];
    for (native_code, expected) in cases {
        answer(serde_json::json!({"apiVersion":2,"ok":false,"error":{
            "code":native_code,"message":"PRIVATE-NATIVE-DETAIL"
        }}));
        let reply = client.request(&request).unwrap();
        assert_eq!(
            reply,
            CatalogReply::Refused(OperationFailure {
                code: expected,
                process_id: None
            })
        );
        let debug = format!("{reply:?}");
        assert!(!debug.contains("PRIVATE-NATIVE-DETAIL"));
        assert!(!debug.contains("PRIVATE-FOREIGN-CODE"));
    }
    assert_eq!(counts.borrow().frees, cases.len());
    assert_eq!(counts.borrow().acquisitions, 0);
    drop(client);
    assert_clean(&counts);
}

#[test]
fn malformed_utf8_json_version_and_duplicate_members_are_refused_and_freed_once() {
    let (client, counts) = fixture();
    for (bytes, error) in [
        (vec![0xff, 0], ConsumerError::InvalidUtf8),
        (nul(b"{"), ConsumerError::InvalidJson),
        (nul(br#"{"apiVersion":1,"ok":true}"#), ConsumerError::UnsupportedApiVersion),
        (nul(br#"{"apiVersion":2,"ok":true,"ok":false}"#), ConsumerError::InvalidJson),
        (nul(br#"{"apiVersion":2,"ok":true,"revision":"r","profiles":[],"issues":[{"id":"i","message":"a","message":"b"}]}"#), ConsumerError::InvalidJson),
        (nul(br#"{"apiVersion":2,"ok":true,"revision":"r","profiles":[],"issues":[],"error":{"code":"busy","message":"hidden"}}"#), ConsumerError::InvalidResponse),
    ] {
        with_state(|s| s.response = Some(bytes));
        let before = counts.borrow().frees;
        assert_eq!(client.request(&list()), Err(error)); assert_eq!(counts.borrow().frees, before + 1);
    }
    with_state(|s| s.response = None);
    assert_eq!(client.request(&list()), Err(ConsumerError::MissingOutput));
    drop(client);
    assert_clean(&counts);
}

#[test]
fn bounded_scanner_accepts_exact_cap_and_refuses_larger_owned_readable_allocation() {
    let (client, counts) = fixture();
    let json = serde_json::to_vec(&empty_list()).unwrap();
    let mut exact = json.clone();
    exact.resize(MAX_RESPONSE_BYTES, b' ');
    exact.push(0);
    with_state(|s| s.response = Some(exact));
    assert!(matches!(
        client.request(&list()),
        Ok(CatalogReply::Success(output)) if matches!(*output, CatalogSuccess::Profiles(_))
    ));
    let mut oversize = json;
    oversize.resize(MAX_RESPONSE_BYTES + 1, b' ');
    oversize.push(0);
    with_state(|s| s.response = Some(oversize));
    assert_eq!(
        client.request(&list()),
        Err(ConsumerError::ResponseTooLarge)
    );
    assert_eq!(counts.borrow().frees, 2);
    drop(client);
    assert_clean(&counts);
}

#[test]
fn non_ascii_inputs_preserve_utf8_and_limits_refuse_before_native_invocation() {
    let (client, counts) = fixture();
    answer(empty_list());
    client.request(&list()).unwrap();
    with_state(|s| {
        let wire: serde_json::Value = serde_json::from_slice(&s.last_request).unwrap();
        assert_eq!(wire["apiVersion"], 2);
        assert_eq!(wire["root"], "D:\\Synthetic\\目录");
    });
    let oversized = CatalogRequest {
        root: None,
        operation: CatalogOperation::RegisterInstallation {
            name: "x".repeat(MAX_REQUEST_BYTES),
            game_directory: NativePath::new("D:\\Synthetic\\Game").unwrap(),
        },
    };
    assert_eq!(
        client.request(&oversized),
        Err(ConsumerError::RequestTooLarge)
    );
    let nul_input = CatalogRequest {
        root: None,
        operation: CatalogOperation::RegisterInstallation {
            name: "invalid\0name".into(),
            game_directory: NativePath::new("D:\\Synthetic\\Game").unwrap(),
        },
    };
    assert_eq!(client.request(&nul_input), Err(ConsumerError::InvalidInput));
    assert_eq!(counts.borrow().requests, 1);
    assert!(NativeId::new("A".repeat(32)).is_err());
    assert!(NativeId::new("a".repeat(33)).is_err());
    assert!(NativePath::new("D:\\bad\0path").is_err());
    assert!(NativePath::new("D:\\".to_owned() + &"x".repeat(MAX_PATH_BYTES)).is_err());
    with_state(|s| s.lease = true);
    let data = client
        .acquire_data_lease(list().root.as_ref(), &id())
        .unwrap();
    with_state(|s| {
        assert_eq!(
            s.last_root.as_ref().unwrap(),
            "D:\\Synthetic\\目录".as_bytes()
        )
    });
    drop(data);
    drop(client);
    assert_clean(&counts);
}

#[test]
fn every_lease_and_native_buffer_keeps_its_originating_table_alive_after_client_drop() {
    let (client, counts) = fixture();
    with_state(|s| s.lease = true);
    let data = client.acquire_data_lease(None, &id()).unwrap();
    let installation = client
        .acquire_installation_lease(None, &NativePath::new("D:\\Synthetic\\Game").unwrap())
        .unwrap();
    let raw = with_state(|s| allocate(s, nul(b"held allocation")));
    let buffer = NativeBuffer::new(raw, client.functions.clone()).unwrap();
    drop(client);
    assert_eq!(counts.borrow().module_drops, 0);
    assert_eq!(buffer.bytes(64).unwrap(), b"held allocation");
    drop(data);
    assert_eq!(counts.borrow().data_releases, 1);
    assert_eq!(counts.borrow().module_drops, 0);
    drop(installation);
    assert_eq!(counts.borrow().installation_releases, 1);
    assert_eq!(counts.borrow().module_drops, 0);
    drop(buffer);
    assert_eq!(counts.borrow().frees, 1);
    assert_eq!(counts.borrow().module_drops, 1);
    assert_clean(&counts);
}

#[test]
fn contradictory_lease_outputs_release_both_allocations_with_matching_owners() {
    let (client, counts) = fixture();
    for (status, has_lease, has_error, expected) in [
        (0, false, false, ConsumerError::MissingOutput),
        (0, true, true, ConsumerError::ContradictoryOutput),
        (0, false, true, ConsumerError::ContradictoryOutput),
        (1, true, false, ConsumerError::ContradictoryOutput),
        (2, true, true, ConsumerError::ContradictoryOutput),
    ] {
        with_state(|s| {
            s.status = status;
            s.lease = has_lease;
            s.error = has_error.then(|| nul(b"PRIVATE-NATIVE-DETAIL"));
        });
        let before = (counts.borrow().frees, counts.borrow().installation_releases);
        let result = client
            .acquire_installation_lease(None, &NativePath::new("D:\\Synthetic\\Game").unwrap());
        assert!(matches!(result, Err(error) if error == expected));
        assert_eq!(counts.borrow().frees, before.0 + usize::from(has_error));
        assert_eq!(
            counts.borrow().installation_releases,
            before.1 + usize::from(has_lease)
        );
    }
    drop(client);
    assert_clean(&counts);
}

#[test]
fn lease_error_diagnostics_are_bounded_suppressed_and_freed_for_both_abi_statuses() {
    let (client, counts) = fixture();
    for (status, text, diagnostic) in [
        (1, None, DiagnosticDisposition::Absent),
        (
            2,
            Some(nul(b"PRIVATE-NATIVE-DETAIL")),
            DiagnosticDisposition::Suppressed,
        ),
        (2, Some(vec![0xff, 0]), DiagnosticDisposition::InvalidUtf8),
        (
            2,
            Some(nul(vec![b'x'; MAX_LEASE_ERROR_BYTES + 1])),
            DiagnosticDisposition::TooLarge,
        ),
    ] {
        let expected_status = abi_status(status);
        let has_text = text.is_some();
        with_state(|s| {
            s.status = status;
            s.error = text;
        });
        let before = counts.borrow().frees;
        let result = client.acquire_data_lease(None, &id());
        assert!(
            matches!(result, Err(ConsumerError::Abi {status, diagnostic: d}) if status == expected_status && d == diagnostic)
        );
        assert_eq!(counts.borrow().frees, before + usize::from(has_text));
    }
    drop(client);
    assert_clean(&counts);
}

#[test]
fn response_shape_and_same_request_identity_are_checked_before_success() {
    let (client, counts) = fixture();
    for invalid in [
        serde_json::json!({"apiVersion":2,"ok":true,"revision":"r","profiles":{},"issues":[]}),
        serde_json::json!({"apiVersion":2,"ok":false,"error":{"code":"busy","message":"hidden"},"profiles":[]}),
        serde_json::json!({"apiVersion":2,"ok":true,"revision":"r","profiles":[],"issues":[],"extra":true}),
    ] {
        answer(invalid);
        assert_eq!(client.request(&list()), Err(ConsumerError::InvalidResponse));
    }
    let request = CatalogRequest {
        root: None,
        operation: CatalogOperation::Delete {
            id: id(),
            expected_revision: NativeRevision::new("r").unwrap(),
            archived: true,
            permanent: true,
        },
    };
    answer(
        serde_json::json!({"apiVersion":2,"ok":true,"id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","deleted":true}),
    );
    assert_eq!(
        client.request(&request),
        Err(ConsumerError::InvalidResponse)
    );
    answer(serde_json::json!({"apiVersion":2,"ok":true,"id":id().as_str(),"deleted":true}));
    assert!(matches!(
        client.request(&request),
        Ok(CatalogReply::Success(output)) if matches!(*output, CatalogSuccess::Deleted(_))
    ));
    drop(client);
    assert_clean(&counts);
}

fn profile(ordinary: bool, archived: bool) -> serde_json::Value {
    let mut value = serde_json::json!({
        "id":id().as_str(), "name":if ordinary {"Default"} else {"Synthetic"},
        "gameDirectory":"", "directory":"D:\\Synthetic\\Catalog\\profiles\\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "revision":"native-profile-revision:1", "state":if archived {"archived"} else {"active"},
        "kind":if ordinary {"windows-user"} else {"isolated"}, "builtIn":ordinary,
        "preferenceScope":if ordinary {"windows-user"} else {"profile"},
        "configurationScope":if ordinary {"installation"} else {"profile"}, "preferredInstallationId":""
    });
    if ordinary {
        value["ownerUserId"] = "S-1-5-21-1000".into();
    } else {
        value["configPath"] =
            "D:\\Synthetic\\Catalog\\profiles\\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\\config.toml"
                .into();
        value["logPath"] =
            "D:\\Synthetic\\Catalog\\profiles\\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\\logs\\Player.log"
                .into();
        value["preferencesInitialized"] = false.into();
    }
    value
}
fn game_selection() -> GameSelection {
    GameSelection {
        installation_id: None,
        game_directory: Some(NativePath::new("D:\\Synthetic\\Game").unwrap()),
    }
}
fn launch_selection() -> LaunchSelection {
    LaunchSelection {
        id: id(),
        installation_id: None,
        game_directory: Some(NativePath::new("D:\\Synthetic\\Game").unwrap()),
    }
}
fn import_selection() -> ImportSelection {
    ImportSelection {
        source_user_sid: "S-1-5-21-1000".into(),
        name: "Synthetic".into(),
        preferred_installation_id: None,
        game_directory: None,
    }
}
fn edit_selection() -> ProfileEdit {
    ProfileEdit {
        id: id(),
        expected_revision: NativeRevision::new("native-profile-revision:1").unwrap(),
        archived: false,
        name: Some("Synthetic".into()),
        preferred_installation_id: None,
        game_directory: None,
    }
}

#[test]
fn all_24_producer_operation_requests_and_response_families_keep_native_api2_shape() {
    let (client, counts) = fixture();
    let isolated = profile(false, false);
    let ordinary = profile(true, false);
    let profile_reply =
        serde_json::json!({"apiVersion":2,"ok":true,"profile":isolated,"revision":"catalog:1"});
    let default_reply =
        serde_json::json!({"apiVersion":2,"ok":true,"profile":ordinary,"revision":"catalog:1"});
    let installation = serde_json::json!({"id":id().as_str(),"name":"Synthetic","gameDirectory":"D:\\Synthetic\\Game","physicalIdentity":"native-physical:1","revision":"installation:1","state":"available"});
    let game = serde_json::json!({"gameDirectory":"D:\\Synthetic\\Game","state":"ready","phase":"idle","requiresRecovery":false,"runningProcessIds":[],"installedVersion":null});
    let game_reply = serde_json::json!({"apiVersion":2,"ok":true,"installation":game});
    let mut checked_game = game.clone();
    checked_game["availableVersion"] = 1.into();
    checked_game["updateAvailable"] = true.into();
    checked_game["downloadBytes"] = 128.into();
    checked_game["extractedTotalBytes"] = 256.into();
    let checked_reply = serde_json::json!({"apiVersion":2,"ok":true,"installation":checked_game});
    let cases = vec![
        (
            "catalog-location",
            CatalogOperation::CatalogLocation,
            serde_json::json!({"apiVersion":2,"ok":true,"catalogRoot":"D:\\Synthetic\\Catalog"}),
        ),
        (
            "list",
            CatalogOperation::List { archived: false },
            serde_json::json!({"apiVersion":2,"ok":true,"profiles":[isolated],"issues":[],"revision":"catalog:1"}),
        ),
        (
            "paths",
            CatalogOperation::Paths {
                id: id(),
                archived: false,
            },
            serde_json::json!({"apiVersion":2,"ok":true,"profile":isolated}),
        ),
        (
            "sessions",
            CatalogOperation::Sessions,
            serde_json::json!({"apiVersion":2,"ok":true,"sessions":[{"apiVersion":1,"id":id().as_str(),"processId":7,"started":"synthetic-generation","executable":"D:\\Synthetic\\Game\\prime.exe","phase":"ready","readiness":"ready"}],"issues":[]}),
        ),
        (
            "installations",
            CatalogOperation::Installations,
            serde_json::json!({"apiVersion":2,"ok":true,"installations":[installation],"issues":[],"revision":"catalog:1"}),
        ),
        (
            "register-installation",
            CatalogOperation::RegisterInstallation {
                name: "Synthetic".into(),
                game_directory: NativePath::new("D:\\Synthetic\\Game").unwrap(),
            },
            serde_json::json!({"apiVersion":2,"ok":true,"installation":installation,"created":false}),
        ),
        (
            "installation-paths",
            CatalogOperation::InstallationPaths {
                installation_id: id(),
            },
            serde_json::json!({"apiVersion":2,"ok":true,"installation":installation}),
        ),
        (
            "ensure-default",
            CatalogOperation::EnsureDefault,
            default_reply.clone(),
        ),
        (
            "resolve-default",
            CatalogOperation::ResolveDefault,
            default_reply,
        ),
        (
            "create",
            CatalogOperation::Create(NewProfile {
                name: "Synthetic".into(),
                preferred_installation_id: None,
                game_directory: None,
            }),
            profile_reply.clone(),
        ),
        (
            "edit",
            CatalogOperation::Edit(edit_selection()),
            profile_reply.clone(),
        ),
        (
            "rename",
            CatalogOperation::Rename(edit_selection()),
            profile_reply.clone(),
        ),
        (
            "archive",
            CatalogOperation::Archive {
                id: id(),
                expected_revision: NativeRevision::new("r").unwrap(),
            },
            serde_json::json!({"apiVersion":2,"ok":true,"profile":profile(false,true),"revision":"catalog:2"}),
        ),
        (
            "restore",
            CatalogOperation::Restore {
                id: id(),
                expected_revision: NativeRevision::new("r").unwrap(),
            },
            profile_reply.clone(),
        ),
        (
            "delete",
            CatalogOperation::Delete {
                id: id(),
                expected_revision: NativeRevision::new("r").unwrap(),
                archived: true,
                permanent: true,
            },
            serde_json::json!({"apiVersion":2,"ok":true,"id":id().as_str(),"deleted":true}),
        ),
        (
            "launch",
            CatalogOperation::Launch(launch_selection()),
            serde_json::json!({"apiVersion":2,"ok":true,"profile":isolated,"processId":7,"readiness":"ready"}),
        ),
        (
            "launch-ordinary",
            CatalogOperation::LaunchOrdinary(launch_selection()),
            serde_json::json!({"apiVersion":2,"ok":true,"profile":ordinary,"processId":7,"started":"synthetic-generation","executable":"D:\\Synthetic\\Game\\prime.exe","readiness":"ordinary","session":{"processId":7,"started":"synthetic-generation","executable":"D:\\Synthetic\\Game\\prime.exe"}}),
        ),
        (
            "import-sources",
            CatalogOperation::ImportSources {
                allow_elevation: false,
                expected_destination_sid: None,
            },
            serde_json::json!({"apiVersion":2,"ok":true,"users":[],"destinationUser":{"sid":"S-1-5-21-1000","name":"Synthetic"},"requiresElevation":false,"unavailableUsers":2}),
        ),
        (
            "prepare-user-import",
            CatalogOperation::PrepareUserImport(import_selection()),
            serde_json::json!({"apiVersion":2,"ok":true,"importPlan":{"sourceUserSid":"S-1-5-21-1000","sourceUserName":"Synthetic source","destinationUserSid":"S-1-5-21-2000","destinationUserName":"Synthetic destination","name":"Synthetic","gameDirectory":"","preferredInstallationId":"","installationRevision":"","requiresElevation":false,"reason":""}}),
        ),
        (
            "import-user",
            CatalogOperation::ImportUser(ImportCommit {
                selection: import_selection(),
                expected_destination_sid: "S-1-5-21-2000".into(),
                expected_installation_revision: "".into(),
                allow_elevation: false,
            }),
            profile_reply,
        ),
        (
            "installation-status",
            CatalogOperation::InstallationStatus(game_selection()),
            game_reply.clone(),
        ),
        (
            "check-game-update",
            CatalogOperation::CheckGameUpdate(game_selection()),
            checked_reply,
        ),
        (
            "update-game",
            CatalogOperation::UpdateGame {
                selection: game_selection(),
                expected_version: 1,
            },
            serde_json::json!({"apiVersion":2,"ok":true,"installation":game,"updated":false}),
        ),
        (
            "recover-game-update",
            CatalogOperation::RecoverGameUpdate(game_selection()),
            serde_json::json!({"apiVersion":2,"ok":true,"installation":game,"recovered":false}),
        ),
    ];
    assert_eq!(cases.len(), 24);
    for (name, operation, response) in cases {
        answer(response);
        assert!(
            matches!(
                client.request(&CatalogRequest {
                    root: None,
                    operation
                }),
                Ok(CatalogReply::Success(_))
            ),
            "producer shape refused: {name}"
        );
        with_state(|s| {
            let request: serde_json::Value = serde_json::from_slice(&s.last_request).unwrap();
            assert_eq!(request["apiVersion"], 2);
            assert_eq!(request["operation"], name);
            assert!(
                request.get("input").is_none(),
                "native ABI has no renderer input wrapper"
            );
            assert!(
                request.get("selection").is_none(),
                "native operation fields remain at the root"
            );
        });
    }
    assert_eq!(counts.borrow().requests, 24);
    assert_eq!(counts.borrow().frees, 24);
    assert_eq!(counts.borrow().acquisitions, 0);
    drop(client);
    assert_clean(&counts);
}

#[test]
fn contradictory_profile_kind_state_and_ordinary_process_receipts_never_publish_success() {
    let (client, counts) = fixture();
    let archive = CatalogRequest {
        root: None,
        operation: CatalogOperation::Archive {
            id: id(),
            expected_revision: NativeRevision::new("r").unwrap(),
        },
    };
    answer(
        serde_json::json!({"apiVersion":2,"ok":true,"profile":profile(false,false),"revision":"r"}),
    );
    assert_eq!(
        client.request(&archive),
        Err(ConsumerError::InvalidResponse)
    );
    let launch = CatalogRequest {
        root: None,
        operation: CatalogOperation::Launch(launch_selection()),
    };
    answer(
        serde_json::json!({"apiVersion":2,"ok":true,"profile":profile(true,false),"processId":7,"readiness":"ready"}),
    );
    assert_eq!(client.request(&launch), Err(ConsumerError::InvalidResponse));
    let ordinary = CatalogRequest {
        root: None,
        operation: CatalogOperation::LaunchOrdinary(launch_selection()),
    };
    answer(
        serde_json::json!({"apiVersion":2,"ok":true,"profile":profile(true,false),"processId":7,"started":"a","executable":"D:\\Synthetic\\Game\\prime.exe","readiness":"ordinary","session":{"processId":8,"started":"a","executable":"D:\\Synthetic\\Game\\prime.exe"}}),
    );
    assert_eq!(
        client.request(&ordinary),
        Err(ConsumerError::InvalidResponse)
    );
    answer(
        serde_json::json!({"apiVersion":2,"ok":false,"processId":7,"error":{"code":"readiness_timeout","message":"PRIVATE-NATIVE-DETAIL"}}),
    );
    assert_eq!(
        client.request(&launch),
        Ok(CatalogReply::Refused(OperationFailure {
            code: NativeErrorCode::ReadinessTimeout,
            process_id: Some(7)
        }))
    );
    assert_eq!(counts.borrow().frees, 4);
    drop(client);
    assert_clean(&counts);
}

#[test]
fn request_byte_boundary_and_explicit_preference_clear_preserve_producer_fields() {
    let (client, counts) = fixture();
    let mut request = CatalogRequest {
        root: None,
        operation: CatalogOperation::RegisterInstallation {
            name: String::new(),
            game_directory: NativePath::new("D:\\Synthetic\\Game").unwrap(),
        },
    };
    let overhead = request.encoded().unwrap().len();
    let CatalogOperation::RegisterInstallation { name, .. } = &mut request.operation else {
        unreachable!()
    };
    *name = "x".repeat(MAX_REQUEST_BYTES - overhead);
    assert_eq!(request.encoded().unwrap().len(), MAX_REQUEST_BYTES);
    answer(
        serde_json::json!({"apiVersion":2,"ok":false,"error":{"code":"invalid_name","message":"bounded refusal"}}),
    );
    assert!(matches!(
        client.request(&request),
        Ok(CatalogReply::Refused(_))
    ));
    with_state(|s| assert_eq!(s.last_request.len(), MAX_REQUEST_BYTES));
    let mut edit = edit_selection();
    edit.preferred_installation_id = Some(InstallationPreference::Clear);
    let wire: serde_json::Value = serde_json::from_slice(
        &CatalogRequest {
            root: None,
            operation: CatalogOperation::Edit(edit),
        }
        .encoded()
        .unwrap(),
    )
    .unwrap();
    assert_eq!(wire["preferredInstallationId"], "");
    assert_eq!(wire["expectedRevision"], "native-profile-revision:1");
    assert_eq!(counts.borrow().requests, 1);
    drop(client);
    assert_clean(&counts);
}
