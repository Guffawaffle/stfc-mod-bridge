//! Read-only use of the canonical Profiles catalog. No discovery catalog,
//! registration, lease acquisition, game update or launch is implemented here.
use bridge_native::{
    LoadedModule, ModuleProvenance, NativeComponent, PinnedModuleSpec, current_host,
};
use bridge_profiles::{
    CatalogOperation, CatalogReply, CatalogRequest, CatalogSuccess, ConsumerError, ProfilesClient,
};
use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::io::Read;
use std::path::{Component, Path, PathBuf};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Inputs {
    modules: Vec<Module>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Module {
    component: NativeComponent,
    host: bridge_native::NativeHost,
    relative_path: PathBuf,
    sha256: String,
    provenance: ModuleProvenance,
    build_receipt: PathBuf,
}

pub struct NativeCatalog {
    client: ProfilesClient,
}
impl NativeCatalog {
    /// The root is selected by the native host at startup, never by a renderer.
    /// Pins are compiled from the reviewed repository manifest, not loaded from
    /// a replaceable file beside an arbitrary DLL. These are development probe
    /// inputs; this does not qualify or package a production native distribution.
    pub fn open(root: &Path) -> Result<Self, ConsumerError> {
        let host = current_host().map_err(|_| ConsumerError::BindingFailure)?;
        let inputs: Inputs = serde_json::from_str(include_str!(
            "../../../../dependencies/next-native-inputs.json"
        ))
        .map_err(|_| ConsumerError::BindingFailure)?;
        let pin = inputs
            .modules
            .into_iter()
            .find(|pin| pin.component == NativeComponent::Profiles && pin.host == host)
            .ok_or(ConsumerError::BindingFailure)?;
        if !pin
            .build_receipt
            .components()
            .all(|part| matches!(part, Component::Normal(_)))
        {
            return Err(ConsumerError::BindingFailure);
        }
        let root = root
            .canonicalize()
            .map_err(|_| ConsumerError::BindingFailure)?;
        let receipt = root
            .join(&pin.build_receipt)
            .canonicalize()
            .map_err(|_| ConsumerError::BindingFailure)?;
        if !receipt.starts_with(&root) {
            return Err(ConsumerError::BindingFailure);
        }
        let mut receipt_bytes = Vec::new();
        std::fs::File::open(receipt)
            .map_err(|_| ConsumerError::BindingFailure)?
            .take(1024 * 1024 + 1)
            .read_to_end(&mut receipt_bytes)
            .map_err(|_| ConsumerError::BindingFailure)?;
        if receipt_bytes.len() > 1024 * 1024 {
            return Err(ConsumerError::BindingFailure);
        }
        let receipt = receipt_bytes;
        let receipt_hash: String = Sha256::digest(receipt)
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect();
        if receipt_hash != pin.provenance.build_receipt_sha256 {
            return Err(ConsumerError::BindingFailure);
        }
        let spec = PinnedModuleSpec::new(
            NativeComponent::Profiles,
            host,
            root,
            pin.relative_path,
            pin.sha256,
            pin.provenance,
        )
        .map_err(|_| ConsumerError::BindingFailure)?;
        // SAFETY: this is the same exact producer/pin and six-export allocation
        // ABI adopted by the existing consumer probe. Hash, host, controlled DLL
        // loading, symbol origin and API2 are checked before any catalog read.
        let module =
            unsafe { LoadedModule::open(&spec) }.map_err(|_| ConsumerError::BindingFailure)?;
        let client = unsafe { ProfilesClient::bind(module) }?;
        Ok(Self { client })
    }
    pub fn query(&self, operation: CatalogOperation) -> Result<Box<CatalogSuccess>, ConsumerError> {
        if !matches!(
            operation,
            CatalogOperation::Installations
                | CatalogOperation::List { .. }
                | CatalogOperation::ResolveDefault
                | CatalogOperation::Sessions
                | CatalogOperation::InstallationStatus(_)
        ) {
            return Err(ConsumerError::InvalidInput);
        }
        match self.client.request(&CatalogRequest {
            root: None,
            operation,
        })? {
            CatalogReply::Success(value) => Ok(value),
            CatalogReply::Refused(_) => Err(ConsumerError::InvalidResponse),
        }
    }
}
