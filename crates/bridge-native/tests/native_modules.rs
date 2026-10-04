use bridge_native::{
    LoadedModule, ModuleProvenance, NativeComponent, NativeHost, PinnedModuleSpec,
};
use std::ffi::{c_char, c_int, c_void};
use std::sync::Arc;

#[test]
#[ignore = "requires explicit adopted native probe inputs; native gate executes this exact test"]
fn adopted_producer_exports_have_the_selected_physical_origin_and_retained_code() {
    let root = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap()
        .to_path_buf();
    let supplied = std::path::PathBuf::from(
        std::env::var_os("BRIDGE_TEST_NATIVE_MANIFEST").expect("explicit native probe manifest"),
    );
    let manifest = root.join("dependencies/next-native-inputs.json");
    assert_eq!(
        std::fs::canonicalize(supplied).unwrap(),
        std::fs::canonicalize(&manifest).unwrap()
    );
    let bytes = std::fs::read(&manifest).unwrap();
    assert!(bytes.len() <= 128 * 1024);
    let input: serde_json::Value = serde_json::from_slice(&bytes).unwrap();
    assert_eq!(input["schemaVersion"], "bridge-native-probe-inputs/v1");
    assert_eq!(input["owningRepository"], "Guffawaffle/stfc-mod-bridge");
    assert_eq!(input["nativeRuntimeQualified"], false);
    assert_eq!(input["releaseQualified"], false);
    let host = bridge_native::current_host().unwrap();
    let mut identities = Vec::new();
    for selected in input["modules"].as_array().unwrap() {
        let selected_host: NativeHost = serde_json::from_value(selected["host"].clone()).unwrap();
        if selected_host != host {
            continue;
        }
        let component: NativeComponent =
            serde_json::from_value(selected["component"].clone()).unwrap();
        let provenance: ModuleProvenance =
            serde_json::from_value(selected["provenance"].clone()).unwrap();
        let spec = PinnedModuleSpec::new(
            component,
            host,
            root.clone(),
            selected["relativePath"].as_str().unwrap().into(),
            selected["sha256"].as_str().unwrap().into(),
            provenance,
        )
        .unwrap();
        // SAFETY: root's closed manifest explicitly adopts these exact reviewed
        // producer modules for local ABI probes. This is not release approval.
        let module = unsafe { LoadedModule::open(&spec) }.unwrap();
        let weak = Arc::downgrade(&module);
        identities.push(module.identity().clone());
        // SAFETY: these are the exact published producer C declarations. The
        // pointers are only observed here, not invoked or exposed to a renderer.
        unsafe {
            match component {
                NativeComponent::Profiles => {
                    module
                        .resolve::<unsafe extern "C" fn(*const c_char, *mut *mut c_char) -> c_int>(
                            c"stfc_profiles_catalog_request_v1",
                        )
                        .unwrap();
                    module
                        .resolve::<unsafe extern "C" fn(*mut c_void)>(c"stfc_profiles_free_v1")
                        .unwrap();
                    module
                        .resolve::<unsafe extern "C" fn(
                            *const c_char,
                            *const c_char,
                            *mut *mut c_void,
                            *mut *mut c_char,
                        ) -> c_int>(c"stfc_profiles_acquire_data_lease_v1")
                        .unwrap();
                    module
                        .resolve::<unsafe extern "C" fn(*mut c_void)>(
                            c"stfc_profiles_release_data_lease_v1",
                        )
                        .unwrap();
                    module
                        .resolve::<unsafe extern "C" fn(
                            *const c_char,
                            *const c_char,
                            *mut *mut c_void,
                            *mut *mut c_char,
                        ) -> c_int>(
                            c"stfc_profiles_acquire_installation_lease_v1"
                        )
                        .unwrap();
                    module
                        .resolve::<unsafe extern "C" fn(*mut c_void)>(
                            c"stfc_profiles_release_installation_lease_v1",
                        )
                        .unwrap();
                }
                NativeComponent::Toml => {
                    module
                        .resolve::<unsafe extern "C" fn() -> u32>(c"stfc_toml_abi_version")
                        .unwrap();
                    module
                        .resolve::<unsafe extern "C" fn(
                            *const c_char,
                            usize,
                            *mut *mut c_char,
                            *mut usize,
                        ) -> c_int>(c"stfc_toml_execute")
                        .unwrap();
                    module
                        .resolve::<unsafe extern "C" fn(*mut c_void)>(c"stfc_toml_free")
                        .unwrap();
                }
            }
        }
        let retained = module.clone();
        drop(module);
        assert!(weak.upgrade().is_some());
        drop(retained);
        assert!(weak.upgrade().is_none());
    }
    assert_eq!(
        identities.len(),
        2,
        "both actual host producer modules are required"
    );
    println!(
        "BRIDGE_NATIVE_IDENTITIES={}",
        serde_json::to_string(&identities).unwrap()
    );
}
