//! CFBundle executable discovery with retained descriptor ancestry verification.
use crate::{
    cf,
    filesystem::{self, ReadOnlyFile, RetainedDirectory},
};
use bridge_domain::platform::{ObservedFile, PlatformError, PlatformErrorCode};
use std::{path::Path, ptr};

#[link(name = "CoreFoundation", kind = "framework")]
unsafe extern "C" {
    fn CFBundleCreate(allocator: cf::Ref, url: cf::Ref) -> cf::Ref;
    fn CFBundleCopyExecutableURL(bundle: cf::Ref) -> cf::Ref;
}

/// No alias resolution, guessed Contents/MacOS name, bundle-ID policy or launch.
pub struct CapturedBundle {
    directory: RetainedDirectory,
    executable: ReadOnlyFile,
}

impl CapturedBundle {
    pub fn directory(&self) -> &ObservedFile {
        self.directory.observation()
    }
    pub fn executable(&self) -> &ObservedFile {
        self.executable.observation()
    }
    pub fn revalidate(&self) -> Result<(), PlatformError> {
        self.directory.revalidate()?;
        self.executable.revalidate(true)?;
        if !self.directory.contains_file(&self.executable)? {
            return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
        }
        Ok(())
    }
}

pub fn capture_bundle(path: &Path) -> Result<CapturedBundle, PlatformError> {
    let directory = filesystem::capture_directory(path)?;
    let url = cf::Owned::url(&directory.observation().physical_path, true)?;
    // SAFETY: retained live URL; CFBundleCreate returns one owned reference.
    let bundle = unsafe { cf::Owned::from_create(CFBundleCreate(ptr::null(), url.as_ref()))? };
    // SAFETY: valid bundle, Copy creates one owned URL or returns null.
    let executable_url =
        unsafe { cf::Owned::from_create(CFBundleCopyExecutableURL(bundle.as_ref())) }
            .map_err(|_| PlatformError::new(PlatformErrorCode::Missing))?;
    let executable = filesystem::capture_file(&cf::url_path(executable_url.as_ref())?, true)?;
    let captured = CapturedBundle {
        directory,
        executable,
    };
    captured.revalidate()?;
    Ok(captured)
}
