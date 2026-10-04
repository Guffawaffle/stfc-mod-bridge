//! Reviewed native code selection and lifetime, independent of any renderer.
//!
//! Hashes, file headers and export origins are observations, not proofs that
//! arbitrary foreign code implements a safe ABI or that its in-memory image
//! equals current disk bytes. Only the owning backend may adopt a trusted pin.

use libloading::Library;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::ffi::{CStr, c_void};
use std::fs::{File, OpenOptions};
use std::io::{Read, Seek, SeekFrom};
use std::marker::PhantomData;
use std::path::{Component, Path, PathBuf};
use std::rc::Rc;
use std::sync::Arc;

pub const MAX_MODULE_BYTES: u64 = 128 * 1024 * 1024;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum NativeComponent {
    Profiles,
    Toml,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum NativeHost {
    #[serde(rename = "windows-x64")]
    WindowsX64,
    #[serde(rename = "macos-arm64-native")]
    MacOsArm64,
}

pub fn current_host() -> Result<NativeHost, LoadError> {
    if cfg!(all(target_os = "windows", target_arch = "x86_64")) {
        Ok(NativeHost::WindowsX64)
    } else if cfg!(all(target_os = "macos", target_arch = "aarch64")) {
        Ok(NativeHost::MacOsArm64)
    } else {
        Err(LoadError::new("unsupported_native_host"))
    }
}

/// Descriptive provenance. This does not establish recipe or release approval.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ModuleProvenance {
    pub source_revision: String,
    pub source_archive_sha256: String,
    pub build_receipt_sha256: String,
}

#[derive(Clone, Debug)]
pub struct PinnedModuleSpec {
    component: NativeComponent,
    host: NativeHost,
    root: PathBuf,
    relative_path: PathBuf,
    sha256: String,
    provenance: ModuleProvenance,
}

impl PinnedModuleSpec {
    /// Construct an identity request. Foreign-code adoption remains unsafe.
    pub fn new(
        component: NativeComponent,
        host: NativeHost,
        root: PathBuf,
        relative_path: PathBuf,
        sha256: String,
        provenance: ModuleProvenance,
    ) -> Result<Self, LoadError> {
        if !root.is_absolute()
            || relative_path.as_os_str().is_empty()
            || relative_path
                .components()
                .any(|part| !matches!(part, Component::Normal(_)))
            || !hex(&sha256, 64)
            || !hex(&provenance.source_revision, 40)
            || !hex(&provenance.source_archive_sha256, 64)
            || !hex(&provenance.build_receipt_sha256, 64)
        {
            return Err(LoadError::new("invalid_module_pin"));
        }
        Ok(Self {
            component,
            host,
            root,
            relative_path,
            sha256,
            provenance,
        })
    }
}

fn hex(value: &str, length: usize) -> bool {
    value.len() == length
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ModuleIdentity {
    pub component: NativeComponent,
    pub host: NativeHost,
    pub physical_path: PathBuf,
    pub sha256: String,
    pub bytes: u64,
    pub provenance: ModuleProvenance,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct LoadError {
    pub code: &'static str,
}
impl LoadError {
    fn new(code: &'static str) -> Self {
        Self { code }
    }
}
impl std::fmt::Display for LoadError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(self.code)
    }
}
impl std::error::Error for LoadError {}

/// Code and originating file remain retained together. Thread affinity is
/// deliberate: no producer ABI has qualified cross-thread invocation/lifetimes.
///
/// ```compile_fail
/// fn requires_send<T: Send>() {}
/// requires_send::<bridge_native::LoadedModule>();
/// ```
/// ```compile_fail
/// fn requires_sync<T: Sync>() {}
/// requires_sync::<bridge_native::LoadedModule>();
/// ```
pub struct LoadedModule {
    library: Library,
    file: File,
    spec: PinnedModuleSpec,
    identity: ModuleIdentity,
    _thread: PhantomData<Rc<()>>,
}

impl LoadedModule {
    /// Load only explicitly adopted producer code on its owning actor thread.
    ///
    /// # Safety
    /// The caller must establish that the pin identifies reviewed code with safe
    /// initializers/terminators, and exclusively coordinate component loading
    /// and calls in this process. The checks here do not make arbitrary code safe.
    /// They also cannot attest preexisting or concurrently mapped memory images.
    #[expect(
        clippy::arc_with_non_send_sync,
        reason = "Arc retains originating code across thread-bound buffers and leases"
    )]
    pub unsafe fn open(spec: &PinnedModuleSpec) -> Result<Arc<Self>, LoadError> {
        if current_host()? != spec.host {
            return Err(LoadError::new("wrong_native_host"));
        }
        let physical_path = confined_file(spec)?;
        let mut options = OpenOptions::new();
        options.read(true);
        #[cfg(windows)]
        {
            use std::os::windows::fs::OpenOptionsExt;
            // Keep normal writers and replacement/delete from changing the
            // selected DLL while its code/buffers/leases remain owned.
            options.share_mode(1);
        }
        let mut file = options
            .open(&physical_path)
            .map_err(|_| LoadError::new("module_open_failed"))?;
        let bytes = verify_open_file(&mut file, spec)?;
        // SAFETY: foreign code adoption is the caller's explicit obligation.
        let library = unsafe { controlled_load(&physical_path)? };
        if confined_file(spec)? != physical_path {
            return Err(LoadError::new("module_path_changed"));
        }
        if verify_open_file(&mut file, spec)? != bytes {
            return Err(LoadError::new("module_file_changed"));
        }
        Ok(Arc::new(Self {
            library,
            file,
            spec: spec.clone(),
            identity: ModuleIdentity {
                component: spec.component,
                host: spec.host,
                physical_path,
                sha256: spec.sha256.clone(),
                bytes,
                provenance: spec.provenance.clone(),
            },
            _thread: PhantomData,
        }))
    }

    pub fn identity(&self) -> &ModuleIdentity {
        &self.identity
    }

    /// Obtain one exact originating export for a consumer-owned function table.
    ///
    /// # Safety
    /// T must be the reviewed ABI's exact function-pointer type. The table must
    /// retain this module through every invocation and originating allocation.
    pub unsafe fn resolve<T: Copy>(&self, export: &CStr) -> Result<T, LoadError> {
        let name = export.to_bytes();
        if name.is_empty()
            || name.len() > 128
            || !name
                .iter()
                .all(|byte| byte.is_ascii_alphanumeric() || *byte == b'_')
            || std::mem::size_of::<T>() != std::mem::size_of::<*mut c_void>()
        {
            return Err(LoadError::new("invalid_export_type_or_name"));
        }
        // SAFETY: libloading returns the symbol's address with this raw pointer
        // representation. It is observed only and never dereferenced as data.
        let address = unsafe { self.library.get::<*mut c_void>(export.to_bytes_with_nul()) }
            .map_err(|_| LoadError::new("missing_native_export"))?;
        let address = *address;
        if address.is_null() {
            return Err(LoadError::new("null_native_export"));
        }
        // SAFETY: the live Library owns this actual symbol address.
        let origin = unsafe { symbol_origin(address)? };
        let origin = std::fs::canonicalize(origin)
            .map_err(|_| LoadError::new("export_origin_unresolved"))?;
        if origin != self.identity.physical_path {
            return Err(LoadError::new("foreign_export_origin"));
        }
        if confined_file(&self.spec)? != self.identity.physical_path {
            return Err(LoadError::new("module_path_changed"));
        }
        let mut file = self
            .file
            .try_clone()
            .map_err(|_| LoadError::new("module_recheck_failed"))?;
        if verify_open_file(&mut file, &self.spec)? != self.identity.bytes {
            return Err(LoadError::new("module_file_changed"));
        }
        // SAFETY: exact T is an explicit caller obligation; code remains owned.
        let symbol = unsafe { self.library.get::<T>(export.to_bytes_with_nul()) }
            .map_err(|_| LoadError::new("missing_native_export"))?;
        Ok(*symbol)
    }
}

fn no_link(path: &Path, directory: bool) -> Result<(), LoadError> {
    let metadata =
        std::fs::symlink_metadata(path).map_err(|_| LoadError::new("module_path_unresolved"))?;
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if metadata.file_attributes() & 0x400 != 0 {
            return Err(LoadError::new("module_path_is_link"));
        }
    }
    if metadata.file_type().is_symlink() {
        return Err(LoadError::new("module_path_is_link"));
    }
    if (directory && !metadata.is_dir()) || (!directory && !metadata.is_file()) {
        return Err(LoadError::new("module_path_kind"));
    }
    Ok(())
}

fn confined_file(spec: &PinnedModuleSpec) -> Result<PathBuf, LoadError> {
    no_link(&spec.root, true)?;
    let root =
        std::fs::canonicalize(&spec.root).map_err(|_| LoadError::new("module_root_unresolved"))?;
    // Inspect every parent of the approved root as well as every relative child.
    let mut ancestor = spec.root.parent();
    while let Some(parent) = ancestor {
        no_link(parent, true)?;
        ancestor = parent.parent();
    }
    let parts: Vec<_> = spec.relative_path.components().collect();
    let mut selected = spec.root.clone();
    for (index, part) in parts.iter().enumerate() {
        selected.push(part.as_os_str());
        no_link(&selected, index + 1 < parts.len())?;
    }
    let physical =
        std::fs::canonicalize(selected).map_err(|_| LoadError::new("module_path_unresolved"))?;
    if !physical.starts_with(&root) || physical == root {
        return Err(LoadError::new("module_path_escape"));
    }
    Ok(physical)
}

fn verify_open_file(file: &mut File, spec: &PinnedModuleSpec) -> Result<u64, LoadError> {
    let before = file
        .metadata()
        .map_err(|_| LoadError::new("module_metadata_failed"))?;
    if !before.is_file() || before.len() < 32 || before.len() > MAX_MODULE_BYTES {
        return Err(LoadError::new("module_byte_bound"));
    }
    file.seek(SeekFrom::Start(0))
        .map_err(|_| LoadError::new("module_read_failed"))?;
    let mut bytes = Vec::with_capacity(before.len() as usize);
    (&mut *file)
        .take(MAX_MODULE_BYTES + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| LoadError::new("module_read_failed"))?;
    let after = file
        .metadata()
        .map_err(|_| LoadError::new("module_metadata_failed"))?;
    if before.len() != after.len()
        || bytes.len() as u64 != before.len()
        || before.modified().ok() != after.modified().ok()
    {
        return Err(LoadError::new("module_file_changed"));
    }
    let digest: String = Sha256::digest(&bytes)
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect();
    if digest != spec.sha256 {
        return Err(LoadError::new("module_hash_mismatch"));
    }
    inspect_image(&bytes, spec.host)?;
    Ok(before.len())
}

fn word(bytes: &[u8], offset: usize) -> Option<u16> {
    Some(u16::from_le_bytes(
        bytes.get(offset..offset.checked_add(2)?)?.try_into().ok()?,
    ))
}
fn dword(bytes: &[u8], offset: usize) -> Option<u32> {
    Some(u32::from_le_bytes(
        bytes.get(offset..offset.checked_add(4)?)?.try_into().ok()?,
    ))
}

/// Bounded architecture/file-kind observation. The OS loader validates the rest.
pub fn inspect_image(bytes: &[u8], host: NativeHost) -> Result<(), LoadError> {
    let valid = match host {
        NativeHost::WindowsX64 => {
            let offset = dword(bytes, 0x3c).map(|value| value as usize);
            offset.is_some_and(|offset| {
                let Some(header) = offset.checked_add(24) else {
                    return false;
                };
                let Some(length) = word(bytes, offset + 20).map(usize::from) else {
                    return false;
                };
                let Some(end) = header.checked_add(length) else {
                    return false;
                };
                bytes.get(..2) == Some(b"MZ")
                    && bytes.get(offset..offset + 4) == Some(b"PE\0\0")
                    && word(bytes, offset + 4) == Some(0x8664)
                    && word(bytes, offset + 6).is_some_and(|count| count > 0)
                    && word(bytes, offset + 22).is_some_and(|flags| flags & 0x2002 == 0x2002)
                    && length >= 112
                    && end <= bytes.len()
                    && word(bytes, header) == Some(0x20b)
            })
        }
        NativeHost::MacOsArm64 => {
            if dword(bytes, 0) != Some(0xfeedfacf)
                || dword(bytes, 4) != Some(0x0100000c)
                || dword(bytes, 12) != Some(6)
            {
                false
            } else {
                let count = dword(bytes, 16).unwrap_or(0) as usize;
                let length = dword(bytes, 20).unwrap_or(u32::MAX) as usize;
                let end = 32usize.checked_add(length);
                if count == 0 || count > 65_536 || end.is_none_or(|end| end > bytes.len()) {
                    false
                } else {
                    let end = end.unwrap_or(0);
                    let mut cursor = 32usize;
                    let mut valid = true;
                    for _ in 0..count {
                        let size = dword(bytes, cursor + 4).unwrap_or(0) as usize;
                        if size < 8
                            || !size.is_multiple_of(8)
                            || cursor.checked_add(size).is_none_or(|next| next > end)
                        {
                            valid = false;
                            break;
                        }
                        cursor += size;
                    }
                    valid && cursor == end
                }
            }
        }
    };
    if valid {
        Ok(())
    } else {
        Err(LoadError::new("wrong_or_malformed_native_image"))
    }
}

#[cfg(windows)]
unsafe fn controlled_load(path: &Path) -> Result<Library, LoadError> {
    use libloading::os::windows::Library as WindowsLibrary;
    use windows_sys::Win32::System::LibraryLoader::{
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR, LOAD_LIBRARY_SEARCH_SYSTEM32,
    };
    if let Ok(existing) = WindowsLibrary::open_already_loaded(path) {
        drop(existing);
        return Err(LoadError::new("component_already_loaded"));
    }
    // SAFETY: full physical path and trusted initializer contract from open.
    unsafe {
        WindowsLibrary::load_with_flags(
            path,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32,
        )
    }
    .map(Into::into)
    .map_err(|_| LoadError::new("native_load_failed"))
}

#[cfg(target_os = "macos")]
unsafe fn controlled_load(path: &Path) -> Result<Library, LoadError> {
    use libloading::os::unix::Library as UnixLibrary;
    // SAFETY: full physical path; NOLOAD observes an already mapped component.
    if let Ok(existing) = unsafe {
        UnixLibrary::open(
            Some(path),
            libc::RTLD_NOW | libc::RTLD_LOCAL | libc::RTLD_NOLOAD | libc::RTLD_FIRST,
        )
    } {
        drop(existing);
        return Err(LoadError::new("component_already_loaded"));
    }
    // SAFETY: trusted initializer contract; FIRST prevents dependency lookup.
    unsafe {
        UnixLibrary::open(
            Some(path),
            libc::RTLD_NOW | libc::RTLD_LOCAL | libc::RTLD_FIRST,
        )
    }
    .map(Into::into)
    .map_err(|_| LoadError::new("native_load_failed"))
}

#[cfg(not(any(windows, target_os = "macos")))]
unsafe fn controlled_load(_: &Path) -> Result<Library, LoadError> {
    Err(LoadError::new("unsupported_native_host"))
}

#[cfg(windows)]
unsafe fn symbol_origin(address: *mut c_void) -> Result<PathBuf, LoadError> {
    use std::os::windows::ffi::OsStringExt;
    use windows_sys::Win32::System::LibraryLoader::{
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        GetModuleFileNameW, GetModuleHandleExW,
    };
    let mut handle = std::ptr::null_mut();
    // SAFETY: FROM_ADDRESS interprets this pointer as a live symbol address,
    // not a UTF-16 string. Library ownership retains the containing module.
    let ok = unsafe {
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            address.cast(),
            &mut handle,
        )
    };
    if ok == 0 || handle.is_null() {
        return Err(LoadError::new("export_origin_unresolved"));
    }
    let mut output = vec![0u16; 32_768];
    // SAFETY: valid retained module handle and writable bounded UTF-16 buffer.
    let length =
        unsafe { GetModuleFileNameW(handle, output.as_mut_ptr(), output.len() as u32) } as usize;
    if length == 0 || length >= output.len() {
        return Err(LoadError::new("export_origin_unresolved"));
    }
    Ok(PathBuf::from(std::ffi::OsString::from_wide(
        &output[..length],
    )))
}

#[cfg(target_os = "macos")]
unsafe fn symbol_origin(address: *mut c_void) -> Result<PathBuf, LoadError> {
    use std::os::unix::ffi::OsStrExt;
    let mut info = std::mem::MaybeUninit::<libc::Dl_info>::zeroed();
    // SAFETY: live symbol address and writable correctly sized OS record.
    if unsafe { libc::dladdr(address.cast_const(), info.as_mut_ptr()) } == 0 {
        return Err(LoadError::new("export_origin_unresolved"));
    }
    // SAFETY: successful dladdr initialized the record and its retained module
    // supplies a valid NUL-terminated pathname. This is an OS ABI assumption.
    let info = unsafe { info.assume_init() };
    if info.dli_fname.is_null() {
        return Err(LoadError::new("export_origin_unresolved"));
    }
    let name = unsafe { CStr::from_ptr(info.dli_fname) }.to_bytes();
    if name.is_empty() || name.len() > 32_768 {
        return Err(LoadError::new("export_origin_unresolved"));
    }
    Ok(PathBuf::from(std::ffi::OsStr::from_bytes(name)))
}

#[cfg(not(any(windows, target_os = "macos")))]
unsafe fn symbol_origin(_: *mut c_void) -> Result<PathBuf, LoadError> {
    Err(LoadError::new("unsupported_native_host"))
}
