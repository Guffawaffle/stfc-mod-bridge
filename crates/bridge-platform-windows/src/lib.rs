//! Ordinary-user Windows adapters. Captures are observations, not permissions,
//! catalog identities, or operation admission. Handles stay on their owning
//! backend thread. The operation kernel remains the sole recovery journal.
//!
//! These services are available only on the qualified Windows x64 host. A
//! foreign host never substitutes a synthetic successful observation.

#[cfg(all(windows, target_arch = "x86_64"))]
mod filesystem;
#[cfg(all(windows, target_arch = "x86_64"))]
mod native;
#[cfg(all(windows, target_arch = "x86_64"))]
mod process;
#[cfg(all(windows, target_arch = "x86_64"))]
pub mod providers;
#[cfg(all(windows, target_arch = "x86_64"))]
mod secrets;
#[cfg(all(windows, target_arch = "x86_64"))]
mod shell;
#[cfg(all(windows, target_arch = "x86_64"))]
mod signature;

#[cfg(all(windows, target_arch = "x86_64"))]
pub use filesystem::{
    AdmittedDirectoryGuard, AdmittedFileGuard, ReadOnlyFile, StagedReplacement, admit_directory,
    admit_file, capture_directory, capture_file,
};
#[cfg(all(windows, target_arch = "x86_64"))]
pub use process::{ExactProcessGuard, capture_process, open_exact_process};
#[cfg(all(windows, target_arch = "x86_64"))]
pub use secrets::{Plaintext, protect_secret, unprotect_secret};
#[cfg(all(windows, target_arch = "x86_64"))]
pub use shell::{create_shortcut, focus_exact_process};
#[cfg(all(windows, target_arch = "x86_64"))]
pub use signature::observe_signature;

/// No native action is attempted by this host check.
pub fn require_windows_x64() -> Result<(), bridge_domain::platform::PlatformError> {
    if cfg!(all(windows, target_arch = "x86_64")) {
        Ok(())
    } else {
        Err(bridge_domain::platform::PlatformError::new(
            bridge_domain::platform::PlatformErrorCode::UnsupportedHost,
        ))
    }
}
