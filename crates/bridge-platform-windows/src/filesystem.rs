use crate::native::{error, io_error, path_wide, win_error};
use bridge_domain::platform::{
    FileKind, FileWriteStamp, ObservedFile, PhysicalIdentity, PlatformError, PlatformErrorCode,
    ReplaceOutcome,
};
use sha2::{Digest, Sha256};
use std::ffi::OsString;
use std::fs::{File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::marker::PhantomData;
use std::os::windows::ffi::OsStringExt;
use std::os::windows::fs::{MetadataExt, OpenOptionsExt};
use std::os::windows::io::{AsRawHandle, FromRawHandle};
use std::path::{Path, PathBuf};
use std::rc::Rc;
use windows::Win32::Foundation::HANDLE;
use windows::Win32::Storage::FileSystem::*;
use windows::core::PCWSTR;

pub const MAX_HASH_BYTES: u64 = 256 * 1024 * 1024;
pub const MAX_STAGE_BYTES: usize = 64 * 1024 * 1024;

struct RetainedFile {
    file: File,
    observation: ObservedFile,
    _thread: PhantomData<Rc<()>>,
}

/// Shared read/write/delete capture: it reserves no mutation exclusion. The
/// disk hash describes bounded reads through this handle, not mapped memory or
/// an immutable file snapshot. Metadata changes during hashing are refused.
/// Final and ancestor reparse points are refused. Route checks are short
/// observations around capture, not atomic relative opens or namespace locks.
///
/// ```compile_fail
/// fn require_send<T: Send>() {}
/// require_send::<bridge_platform_windows::ReadOnlyFile>();
/// ```
pub struct ReadOnlyFile(RetainedFile);
impl ReadOnlyFile {
    pub fn observation(&self) -> &ObservedFile {
        &self.0.observation
    }
    pub fn refresh(&mut self, hash_limit: Option<u64>) -> Result<ObservedFile, PlatformError> {
        let observed = observe(&mut self.0.file, hash_limit)?;
        if observed.identity != self.0.observation.identity {
            return Err(error(PlatformErrorCode::IdentityChanged));
        }
        self.0.observation = observed.clone();
        Ok(observed)
    }
}

/// A successful open denies concurrent file writes and deletion while retained.
/// It is distinct from read-only capture and does not own a catalog exclusion.
///
/// ```compile_fail
/// fn require_sync<T: Sync>() {}
/// require_sync::<bridge_platform_windows::AdmittedFileGuard>();
/// ```
pub struct AdmittedFileGuard(RetainedFile);
impl AdmittedFileGuard {
    pub fn observation(&self) -> &ObservedFile {
        &self.0.observation
    }
    pub(crate) fn handle(&self) -> HANDLE {
        handle(&self.0.file)
    }
}

/// Retains the observed parent directory and denies its rename/deletion. This
/// does not exclude changes to children; the owning transaction must hold its
/// canonical installation/configuration lease before mutating a child.
pub struct AdmittedDirectoryGuard(RetainedFile);
impl AdmittedDirectoryGuard {
    pub fn observation(&self) -> &ObservedFile {
        &self.0.observation
    }
    pub(crate) fn revalidate(&self) -> Result<(), PlatformError> {
        let current = metadata(&self.0.file)?;
        if current.identity != self.0.observation.identity
            || current.kind != FileKind::Directory
            || current.physical_path != self.0.observation.physical_path
        {
            Err(error(PlatformErrorCode::IdentityChanged))
        } else {
            Ok(())
        }
    }
}

pub fn capture_file(path: &Path, hash_limit: Option<u64>) -> Result<ReadOnlyFile, PlatformError> {
    Ok(ReadOnlyFile(open(
        path,
        FileKind::File,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        hash_limit,
        FILE_READ_DATA.0 | FILE_READ_ATTRIBUTES.0,
    )?))
}

pub fn capture_directory(path: &Path) -> Result<ReadOnlyFile, PlatformError> {
    Ok(ReadOnlyFile(open(
        path,
        FileKind::Directory,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        None,
        FILE_READ_ATTRIBUTES.0,
    )?))
}

pub fn admit_file(expected: &ObservedFile) -> Result<AdmittedFileGuard, PlatformError> {
    if expected.kind != FileKind::File {
        return Err(error(PlatformErrorCode::WrongKind));
    }
    let retained = open(
        &expected.physical_path,
        FileKind::File,
        FILE_SHARE_READ,
        expected.disk_sha256.map(|_| expected.byte_len),
        FILE_READ_DATA.0 | FILE_READ_ATTRIBUTES.0,
    )?;
    exact_file(expected, &retained.observation)?;
    Ok(AdmittedFileGuard(retained))
}

pub fn admit_directory(expected: &ObservedFile) -> Result<AdmittedDirectoryGuard, PlatformError> {
    if expected.kind != FileKind::Directory {
        return Err(error(PlatformErrorCode::WrongKind));
    }
    let retained = open(
        &expected.physical_path,
        FileKind::Directory,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        None,
        // Attribute-only handles do not participate in sharing exclusion.
        // Listing access makes the retained no-delete share deny rename/delete;
        // an ordinary-user access refusal must remain a refusal.
        FILE_LIST_DIRECTORY.0 | FILE_READ_ATTRIBUTES.0,
    )?;
    if retained.observation.identity != expected.identity {
        return Err(error(PlatformErrorCode::IdentityChanged));
    }
    Ok(AdmittedDirectoryGuard(retained))
}

fn open(
    path: &Path,
    kind: FileKind,
    share: FILE_SHARE_MODE,
    hash_limit: Option<u64>,
    access: u32,
) -> Result<RetainedFile, PlatformError> {
    let native_path = path_wide(path)?;
    refuse_reparse_route(path)?;
    // SAFETY: terminated path and valid output-owning API. OPEN_REPARSE_POINT
    // lets metadata reject the final link rather than silently following it.
    let raw = unsafe {
        CreateFileW(
            PCWSTR(native_path.as_ptr()),
            access,
            share,
            None,
            OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
            None,
        )
    }
    .map_err(win_error)?;
    // SAFETY: successful native handle ownership transfers exactly once.
    let mut file = unsafe { File::from_raw_handle(raw.0) };
    let initial = metadata(&file)?;
    route_matches_handle(path, &initial.physical_path)?;
    let observation = observe(&mut file, hash_limit)?;
    route_matches_handle(path, &observation.physical_path)?;
    if observation.kind != kind {
        return Err(error(PlatformErrorCode::WrongKind));
    }
    Ok(RetainedFile {
        file,
        observation,
        _thread: PhantomData,
    })
}

fn refuse_reparse_route(path: &Path) -> Result<(), PlatformError> {
    let mut selected = PathBuf::new();
    for component in path.components() {
        selected.push(component.as_os_str());
        // A drive prefix alone is drive-relative; inspect only its absolute
        // root and the ordinary descendants already validated by path_wide.
        if matches!(component, std::path::Component::Prefix(_)) {
            continue;
        }
        let metadata = std::fs::symlink_metadata(&selected).map_err(io_error)?;
        if metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT.0 != 0 {
            return Err(error(PlatformErrorCode::LinkOrReparsePoint));
        }
    }
    Ok(())
}

fn route_matches_handle(path: &Path, physical: &Path) -> Result<(), PlatformError> {
    refuse_reparse_route(path)?;
    if std::fs::canonicalize(path).map_err(io_error)? != physical {
        return Err(error(PlatformErrorCode::IdentityChanged));
    }
    refuse_reparse_route(path)
}

pub(crate) fn exact_file(
    expected: &ObservedFile,
    observed: &ObservedFile,
) -> Result<(), PlatformError> {
    if expected.identity != observed.identity
        || expected.kind != observed.kind
        || expected.byte_len != observed.byte_len
        || expected.write_stamp != observed.write_stamp
        || (expected.disk_sha256.is_some() && expected.disk_sha256 != observed.disk_sha256)
    {
        Err(error(PlatformErrorCode::IdentityChanged))
    } else {
        Ok(())
    }
}

fn handle(file: &File) -> HANDLE {
    HANDLE(file.as_raw_handle())
}

fn information<T: Default>(
    file: &File,
    class: FILE_INFO_BY_HANDLE_CLASS,
) -> Result<T, PlatformError> {
    let mut value = T::default();
    // SAFETY: call sites pair each native class with its exact fixed C struct.
    unsafe {
        GetFileInformationByHandleEx(
            handle(file),
            class,
            (&mut value as *mut T).cast(),
            std::mem::size_of::<T>() as u32,
        )
    }
    .map_err(win_error)?;
    Ok(value)
}

fn metadata(file: &File) -> Result<ObservedFile, PlatformError> {
    let basic: FILE_BASIC_INFO = information(file, FileBasicInfo)?;
    if basic.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT.0 != 0 {
        return Err(error(PlatformErrorCode::LinkOrReparsePoint));
    }
    let id: FILE_ID_INFO = information(file, FileIdInfo)?;
    let standard: FILE_STANDARD_INFO = information(file, FileStandardInfo)?;
    if standard.DeletePending || standard.EndOfFile < 0 {
        return Err(error(PlatformErrorCode::IdentityChanged));
    }
    let mut name = vec![0u16; 32_768];
    // SAFETY: valid retained handle and bounded writable UTF-16 output.
    let len = unsafe {
        GetFinalPathNameByHandleW(
            handle(file),
            &mut name,
            GETFINALPATHNAMEBYHANDLE_FLAGS(FILE_NAME_NORMALIZED.0 | VOLUME_NAME_DOS.0),
        )
    } as usize;
    if len == 0 {
        return Err(win_error(windows::core::Error::from_thread()));
    }
    if len >= name.len() {
        return Err(error(PlatformErrorCode::TooLarge));
    }
    name.truncate(len);
    Ok(ObservedFile {
        physical_path: PathBuf::from(OsString::from_wide(&name)),
        identity: PhysicalIdentity::Windows {
            volume_serial: id.VolumeSerialNumber,
            file_id: id.FileId.Identifier,
        },
        kind: if standard.Directory {
            FileKind::Directory
        } else {
            FileKind::File
        },
        byte_len: standard.EndOfFile as u64,
        write_stamp: FileWriteStamp::Windows {
            filetime: basic.LastWriteTime,
        },
        disk_sha256: None,
    })
}

fn observe(file: &mut File, hash_limit: Option<u64>) -> Result<ObservedFile, PlatformError> {
    let mut before = metadata(file)?;
    if let Some(limit) = hash_limit {
        if limit > MAX_HASH_BYTES {
            return Err(error(PlatformErrorCode::TooLarge));
        }
        if before.kind != FileKind::File {
            return Err(error(PlatformErrorCode::WrongKind));
        }
        if before.byte_len > limit {
            return Err(error(PlatformErrorCode::TooLarge));
        }
        file.seek(SeekFrom::Start(0)).map_err(io_error)?;
        let mut digest = Sha256::new();
        let mut read_bytes = 0u64;
        let mut buffer = [0u8; 64 * 1024];
        loop {
            let count = file.read(&mut buffer).map_err(io_error)?;
            if count == 0 {
                break;
            }
            read_bytes = read_bytes
                .checked_add(count as u64)
                .ok_or_else(|| error(PlatformErrorCode::TooLarge))?;
            if read_bytes > limit {
                return Err(error(PlatformErrorCode::TooLarge));
            }
            digest.update(&buffer[..count]);
        }
        let after = metadata(file)?;
        exact_file(&before, &after)?;
        if read_bytes != before.byte_len {
            return Err(error(PlatformErrorCode::IdentityChanged));
        }
        before.disk_sha256 = Some(digest.finalize().into());
    }
    Ok(before)
}

pub(crate) fn leaf(name: &str) -> Result<(), PlatformError> {
    if name.is_empty()
        || name.len() > 200
        || name.ends_with(['.', ' '])
        || name
            .chars()
            .any(|c| c.is_control() || "\\/:*?\"<>|".contains(c))
        || matches!(name, "." | "..")
    {
        return Err(error(PlatformErrorCode::InvalidInput));
    }
    let stem = name
        .split('.')
        .next()
        .unwrap_or_default()
        .to_ascii_uppercase();
    if matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        || (stem.len() == 4
            && (stem.starts_with("COM") || stem.starts_with("LPT"))
            && matches!(stem.as_bytes()[3], b'1'..=b'9'))
    {
        return Err(error(PlatformErrorCode::InvalidInput));
    }
    Ok(())
}

/// A flushed, create-new sibling staging file. Drop closes the handle and keeps
/// the file for the owning transaction's explicit reconciliation; it never
/// deletes a pathname whose identity might have changed.
pub struct StagedReplacement {
    stage: RetainedFile,
    destination: ObservedFile,
    backup_path: PathBuf,
    parent_identity: PhysicalIdentity,
}
impl StagedReplacement {
    pub fn create(
        parent: &AdmittedDirectoryGuard,
        destination: &ObservedFile,
        stage_leaf: &str,
        backup_leaf: &str,
        bytes: &[u8],
    ) -> Result<Self, PlatformError> {
        parent.revalidate()?;
        leaf(stage_leaf)?;
        leaf(backup_leaf)?;
        if bytes.len() > MAX_STAGE_BYTES {
            return Err(error(PlatformErrorCode::TooLarge));
        }
        if destination.kind != FileKind::File
            || destination.physical_path.parent()
                != Some(parent.observation().physical_path.as_path())
        {
            return Err(error(PlatformErrorCode::InvalidInput));
        }
        let parent_path = &parent.observation().physical_path;
        let stage_path = parent_path.join(stage_leaf);
        let backup_path = parent_path.join(backup_leaf);
        if stage_path == backup_path
            || stage_path == destination.physical_path
            || backup_path == destination.physical_path
        {
            return Err(error(PlatformErrorCode::InvalidInput));
        }
        absent(&backup_path)?;
        let current = capture_file(
            &destination.physical_path,
            destination.disk_sha256.map(|_| destination.byte_len),
        )?;
        exact_file(destination, current.observation())?;
        let mut file = OpenOptions::new()
            .create_new(true)
            .read(true)
            .write(true)
            .share_mode(FILE_SHARE_READ.0)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT.0)
            .open(stage_path)
            .map_err(io_error)?;
        file.write_all(bytes).map_err(io_error)?;
        file.sync_all().map_err(io_error)?;
        let observation = observe(&mut file, Some(bytes.len() as u64))?;
        Ok(Self {
            stage: RetainedFile {
                file,
                observation,
                _thread: PhantomData,
            },
            destination: destination.clone(),
            backup_path,
            parent_identity: parent.observation().identity,
        })
    }

    pub fn staged_observation(&self) -> &ObservedFile {
        &self.stage.observation
    }
    pub fn backup_path(&self) -> &Path {
        &self.backup_path
    }

    /// The caller MUST retain its canonical native transaction exclusion for
    /// destination/stage/backup. Windows ReplaceFileW is path based and is not
    /// an exact-file-ID compare-and-swap. Parent custody alone cannot provide
    /// this exclusion. No journal or recovery policy is created here.
    pub fn replace_under_owner_exclusion(self, parent: &AdmittedDirectoryGuard) -> ReplaceOutcome {
        match self.preflight(parent) {
            Err(error) => ReplaceOutcome::RefusedBeforeCall { error },
            Ok(()) => self.replace(),
        }
    }

    fn preflight(&self, parent: &AdmittedDirectoryGuard) -> Result<(), PlatformError> {
        parent.revalidate()?;
        if parent.observation().identity != self.parent_identity {
            return Err(error(PlatformErrorCode::IdentityChanged));
        }
        absent(&self.backup_path)?;
        let current = capture_file(
            &self.destination.physical_path,
            self.destination
                .disk_sha256
                .map(|_| self.destination.byte_len),
        )?;
        exact_file(&self.destination, current.observation())?;
        let staged = metadata(&self.stage.file)?;
        let mut expected = self.stage.observation.clone();
        expected.disk_sha256 = None;
        exact_file(&expected, &staged)
    }

    fn replace(self) -> ReplaceOutcome {
        let destination_path = self.destination.physical_path.clone();
        let stage_expected = self.stage.observation.clone();
        let backup_path = self.backup_path.clone();
        let paths = (
            path_wide(&destination_path),
            path_wide(&stage_expected.physical_path),
            path_wide(&backup_path),
        );
        let (Ok(destination), Ok(stage), Ok(backup)) = paths else {
            return ReplaceOutcome::RefusedBeforeCall {
                error: error(PlatformErrorCode::InvalidInput),
            };
        };
        // ReplaceFileW opens the staging file without sharing. The canonical
        // owning lease must cover this release-to-call interval.
        drop(self.stage);
        // SAFETY: all three terminated paths were validated and sibling-bound.
        let result = unsafe {
            ReplaceFileW(
                PCWSTR(destination.as_ptr()),
                PCWSTR(stage.as_ptr()),
                PCWSTR(backup.as_ptr()),
                REPLACE_FILE_FLAGS(0),
                None,
                None,
            )
        };
        if let Err(err) = result {
            return ReplaceOutcome::AmbiguousAfterCall {
                native_code: win_error(err).native_code,
            };
        }
        let post = (
            capture_file(&destination_path, Some(stage_expected.byte_len)),
            capture_file(
                &backup_path,
                self.destination
                    .disk_sha256
                    .map(|_| self.destination.byte_len),
            ),
        );
        match post {
            (Ok(destination), Ok(backup))
                if destination.observation().identity == stage_expected.identity
                    && destination.observation().disk_sha256 == stage_expected.disk_sha256
                    && backup.observation().identity == self.destination.identity
                    && exact_file(&self.destination, backup.observation()).is_ok() =>
            {
                ReplaceOutcome::Replaced {
                    destination: Box::new(destination.observation().clone()),
                    backup: Box::new(backup.observation().clone()),
                    staged_file_flushed: true,
                }
            }
            _ => ReplaceOutcome::AmbiguousAfterCall { native_code: None },
        }
    }
}

pub(crate) fn absent(path: &Path) -> Result<(), PlatformError> {
    match std::fs::symlink_metadata(path) {
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(err) => Err(io_error(err)),
        Ok(_) => Err(error(PlatformErrorCode::Busy)),
    }
}

/// Own the newly created source handle through a no-overwrite rename. The
/// caller's canonical exclusion still owns the destination namespace; no file
/// handle can exclude every independently writable ancestor or sibling.
pub(crate) fn publish_new_sibling(
    parent: &AdmittedDirectoryGuard,
    stage_leaf: &str,
    destination_leaf: &str,
    bytes: &[u8],
) -> Result<ObservedFile, PlatformError> {
    parent.revalidate()?;
    leaf(stage_leaf)?;
    leaf(destination_leaf)?;
    if stage_leaf.eq_ignore_ascii_case(destination_leaf) || bytes.len() > 1024 * 1024 {
        return Err(error(PlatformErrorCode::InvalidInput));
    }
    let stage = parent.observation().physical_path.join(stage_leaf);
    let destination = parent.observation().physical_path.join(destination_leaf);
    absent(&destination)?;
    let mut file = OpenOptions::new()
        .create_new(true)
        .read(true)
        .write(true)
        .access_mode(FILE_GENERIC_READ.0 | FILE_GENERIC_WRITE.0 | DELETE.0)
        .share_mode(FILE_SHARE_READ.0)
        .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT.0)
        .open(&stage)
        .map_err(io_error)?;
    file.write_all(bytes).map_err(io_error)?;
    file.sync_all().map_err(io_error)?;
    let before = observe(&mut file, Some(bytes.len() as u64))?;
    parent.revalidate()?;
    let mut name = path_wide(&destination)?;
    name.pop();
    let byte_count = name.len() * 2;
    let offset = std::mem::offset_of!(FILE_RENAME_INFO, FileName);
    let size = offset + byte_count;
    let mut storage = vec![0u64; size.div_ceil(std::mem::size_of::<u64>())];
    // SAFETY: u64 allocation has the native structure's alignment on x64, zero
    // initialized flexible structure has enough bytes, and filename copy fits.
    let info = storage.as_mut_ptr().cast::<FILE_RENAME_INFO>();
    unsafe {
        (*info).Anonymous.ReplaceIfExists = false;
        (*info).RootDirectory = HANDLE::default();
        (*info).FileNameLength = byte_count as u32;
        std::ptr::copy_nonoverlapping(
            name.as_ptr(),
            storage.as_mut_ptr().cast::<u8>().add(offset).cast::<u16>(),
            name.len(),
        );
        SetFileInformationByHandle(handle(&file), FileRenameInfo, info.cast(), size as u32)
    }
    .map_err(win_error)?;
    let after = observe(&mut file, Some(bytes.len() as u64))?;
    if after.identity != before.identity
        || after.disk_sha256 != before.disk_sha256
        || after.physical_path != destination
    {
        return Err(error(PlatformErrorCode::IdentityChanged));
    }
    Ok(after)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn child_names_reject_aliases_streams_and_reserved_devices() {
        for invalid in [
            "", ".", "..", "x/y", "x\\y", "a:stream", "name.", "name ", "CON", "NUL.txt",
            "COM1.lnk", "LPT9.txt", "a\0b",
        ] {
            assert!(leaf(invalid).is_err(), "{invalid:?}");
        }
        for valid in ["stage-123.tmp", "STFC Bridge.lnk", "com10.data"] {
            assert!(leaf(valid).is_ok());
        }
    }
}
