//! Backend-only native Apple Silicon observations and explicitly scoped services.
//!
//! File descriptors retain objects, not a namespace or a Profiles exclusion.
//! Every mutation needs the owning engine's admitted transaction and recovery
//! record. No value in this crate establishes catalog or publisher authority.
//! Intel hosts, Rosetta host builds and other operating systems are unsupported.
#![deny(unsafe_op_in_unsafe_fn)]

pub mod format;
pub mod providers;

#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
mod private_journal;
#[cfg(any(test, all(target_os = "macos", target_arch = "aarch64")))]
mod private_journal_policy;
#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
pub use private_journal::NativePrivateJournalStorage;

#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
pub mod bundle;
#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
mod cf;
#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
pub mod filesystem;
#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
mod native;
#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
pub mod process;
#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
pub mod secrets;
#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
pub mod shell;
#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
pub mod signature;

pub fn require_macos_arm64() -> Result<(), bridge_domain::platform::PlatformError> {
    if cfg!(all(target_os = "macos", target_arch = "aarch64")) {
        Ok(())
    } else {
        Err(bridge_domain::platform::PlatformError::new(
            bridge_domain::platform::PlatformErrorCode::UnsupportedHost,
        ))
    }
}
