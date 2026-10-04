//! Descriptor-relative observations and atomic exchange preparation.
//!
//! macOS descriptors do not deny other writers or renames. Revalidation detects
//! observed changes; it is not a mandatory exclusion. The owning transaction
//! must retain its Profiles exclusion and durable recovery record around calls.
use crate::{format, native};
use bridge_domain::platform::{
    FileKind, FileWriteStamp, ObservedFile, PhysicalIdentity, PlatformError, PlatformErrorCode,
    ReplaceOutcome,
};
use sha2::{Digest, Sha256};
use std::{
    ffi::{CString, OsString},
    fs::File,
    io::Write,
    marker::PhantomData,
    os::{
        fd::{AsRawFd, FromRawFd},
        unix::{
            ffi::{OsStrExt, OsStringExt},
            fs::FileExt,
        },
    },
    path::{Path, PathBuf},
    rc::Rc,
};

struct Link {
    parent: File,
    leaf: CString,
    identity: PhysicalIdentity,
    kind: FileKind,
}

struct RetainedObject {
    file: File,
    links: Vec<Link>,
    observed: ObservedFile,
    _local: PhantomData<Rc<()>>,
}

/// Local descriptor custody; deliberately neither Send nor Sync.
/// ```compile_fail
/// use bridge_platform_macos::filesystem::RetainedDirectory;
/// fn transferable<T: Send + Sync>() {}
/// transferable::<RetainedDirectory>();
/// ```
pub struct RetainedDirectory(RetainedObject);

/// Hashes are current disk observations, never a process's mapped executable.
/// ```compile_fail
/// use bridge_platform_macos::filesystem::ReadOnlyFile;
/// fn transferable<T: Send + Sync>() {}
/// transferable::<ReadOnlyFile>();
/// ```
pub struct ReadOnlyFile(RetainedObject);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct VolumeSemantics {
    pub case_sensitive: Option<bool>,
    pub case_preserving: Option<bool>,
    pub atomic_swap: Option<bool>,
}

fn stat(file: &File) -> Result<libc::stat, PlatformError> {
    let mut result = std::mem::MaybeUninit::<libc::stat>::uninit();
    // SAFETY: retained fd and exactly sized writable native stat storage.
    if unsafe { libc::fstat(file.as_raw_fd(), result.as_mut_ptr()) } != 0 {
        return Err(native::errno_error());
    }
    // SAFETY: successful fstat initialized the structure.
    Ok(unsafe { result.assume_init() })
}

fn identity(stat: &libc::stat) -> PhysicalIdentity {
    PhysicalIdentity::MacOs {
        device: stat.st_dev as u32 as u64,
        inode: stat.st_ino,
    }
}

fn kind(stat: &libc::stat) -> Result<FileKind, PlatformError> {
    match stat.st_mode & libc::S_IFMT {
        libc::S_IFREG => Ok(FileKind::File),
        libc::S_IFDIR => Ok(FileKind::Directory),
        libc::S_IFLNK => Err(PlatformError::new(PlatformErrorCode::LinkOrReparsePoint)),
        _ => Err(PlatformError::new(PlatformErrorCode::WrongKind)),
    }
}

fn same_revision(a: &libc::stat, b: &libc::stat) -> bool {
    identity(a) == identity(b)
        && a.st_mode == b.st_mode
        && a.st_uid == b.st_uid
        && a.st_gid == b.st_gid
        && a.st_flags == b.st_flags
        && a.st_gen == b.st_gen
        && a.st_size == b.st_size
        && a.st_nlink == b.st_nlink
        && a.st_mtime == b.st_mtime
        && a.st_mtime_nsec == b.st_mtime_nsec
        && a.st_ctime == b.st_ctime
        && a.st_ctime_nsec == b.st_ctime_nsec
}

fn physical_path(file: &File) -> Result<PathBuf, PlatformError> {
    let mut bytes = [0_u8; libc::MAXPATHLEN as usize];
    // SAFETY: F_GETPATH receives MAXPATHLEN bytes; fd remains owned.
    if unsafe { libc::fcntl(file.as_raw_fd(), libc::F_GETPATH, bytes.as_mut_ptr()) } != 0 {
        return Err(native::errno_error());
    }
    let end = bytes
        .iter()
        .position(|byte| *byte == 0)
        .ok_or_else(|| PlatformError::new(PlatformErrorCode::TooLarge))?;
    format::absolute_components(&bytes[..end])?;
    Ok(PathBuf::from(OsString::from_vec(bytes[..end].to_vec())))
}

fn refuse_finder_alias(file: &File) -> Result<(), PlatformError> {
    let mut finder_info = [0_u8; 32];
    // SAFETY: fixed xattr name, valid retained fd and exact output capacity.
    let count = unsafe {
        libc::fgetxattr(
            file.as_raw_fd(),
            c"com.apple.FinderInfo".as_ptr(),
            finder_info.as_mut_ptr().cast(),
            finder_info.len(),
            0,
            0,
        )
    };
    if count < 0 {
        let error = native::errno_error();
        if error.native_code == Some(i64::from(libc::ENOATTR)) {
            return Ok(());
        }
        return Err(error);
    }
    if count != 32 {
        return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
    }
    // Finder flags are big-endian at offset 8 in both FileInfo/FolderInfo.
    if u16::from_be_bytes([finder_info[8], finder_info[9]]) & 0x8000 != 0 {
        return Err(PlatformError::new(PlatformErrorCode::LinkOrReparsePoint));
    }
    Ok(())
}

fn observed(file: &File, expected: FileKind, hash: bool) -> Result<ObservedFile, PlatformError> {
    let before = stat(file)?;
    if kind(&before)? != expected || before.st_size < 0 {
        return Err(PlatformError::new(PlatformErrorCode::WrongKind));
    }
    refuse_finder_alias(file)?;
    let path = physical_path(file)?;
    let disk_sha256 = if hash {
        if expected != FileKind::File {
            return Err(PlatformError::new(PlatformErrorCode::WrongKind));
        }
        if before.st_size as u64 > format::MAX_HASH_BYTES {
            return Err(PlatformError::new(PlatformErrorCode::TooLarge));
        }
        let mut digest = Sha256::new();
        let mut buffer = [0_u8; 64 * 1024];
        let mut offset = 0_u64;
        loop {
            let count = match file.read_at(&mut buffer, offset) {
                Ok(count) => count,
                Err(error) if error.kind() == std::io::ErrorKind::Interrupted => continue,
                Err(error) => return Err(native::from_io(error)),
            };
            if count == 0 {
                break;
            }
            offset = offset
                .checked_add(count as u64)
                .ok_or_else(|| PlatformError::new(PlatformErrorCode::TooLarge))?;
            if offset > format::MAX_HASH_BYTES || offset > before.st_size as u64 {
                return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
            }
            digest.update(&buffer[..count]);
        }
        if offset != before.st_size as u64 {
            return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
        }
        Some(digest.finalize().into())
    } else {
        None
    };
    let after = stat(file)?;
    if !same_revision(&before, &after) || physical_path(file)? != path {
        return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
    }
    let nanoseconds = u32::try_from(after.st_mtime_nsec)
        .ok()
        .filter(|value| *value < 1_000_000_000)
        .ok_or_else(|| PlatformError::new(PlatformErrorCode::UnknownObservation))?;
    Ok(ObservedFile {
        physical_path: path,
        identity: identity(&after),
        kind: expected,
        byte_len: after.st_size as u64,
        write_stamp: FileWriteStamp::MacOs {
            seconds: after.st_mtime,
            nanoseconds,
        },
        disk_sha256,
    })
}

fn open_child(
    parent: &File,
    leaf: &CString,
    directory: bool,
    write: bool,
) -> Result<File, PlatformError> {
    let mut before = std::mem::MaybeUninit::<libc::stat>::uninit();
    // SAFETY: parent is retained; leaf is NUL terminated; output is stat sized.
    if unsafe {
        libc::fstatat(
            parent.as_raw_fd(),
            leaf.as_ptr(),
            before.as_mut_ptr(),
            libc::AT_SYMLINK_NOFOLLOW,
        )
    } != 0
    {
        return Err(native::errno_error());
    }
    // SAFETY: successful fstatat initialized the structure.
    let before = unsafe { before.assume_init() };
    let expected = if directory {
        FileKind::Directory
    } else {
        FileKind::File
    };
    if kind(&before)? != expected {
        return Err(PlatformError::new(PlatformErrorCode::WrongKind));
    }
    let flags = (if write { libc::O_RDWR } else { libc::O_RDONLY })
        | libc::O_NOFOLLOW
        | libc::O_CLOEXEC
        | libc::O_NONBLOCK
        | if directory { libc::O_DIRECTORY } else { 0 };
    // SAFETY: read-only/noncreating open of a validated single component.
    let fd = unsafe { libc::openat(parent.as_raw_fd(), leaf.as_ptr(), flags) };
    if fd < 0 {
        return Err(native::errno_error());
    }
    // SAFETY: successful openat yields one new owned fd.
    let file = unsafe { File::from_raw_fd(fd) };
    if !same_revision(&before, &stat(&file)?) {
        return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
    }
    refuse_finder_alias(&file)?;
    Ok(file)
}

fn capture(path: &Path, expected: FileKind, hash: bool) -> Result<RetainedObject, PlatformError> {
    let parts = format::absolute_components(path.as_os_str().as_bytes())?;
    // SAFETY: fixed root, noncreating directory open, no followed symlink.
    let root = unsafe {
        libc::open(
            c"/".as_ptr(),
            libc::O_RDONLY | libc::O_DIRECTORY | libc::O_CLOEXEC,
        )
    };
    if root < 0 {
        return Err(native::errno_error());
    }
    // SAFETY: newly returned root descriptor is owned exactly once.
    let mut file = unsafe { File::from_raw_fd(root) };
    let mut links = Vec::with_capacity(parts.len());
    for (index, component) in parts.iter().enumerate() {
        let leaf = native::cstring(component)?;
        let directory = index + 1 < parts.len() || expected == FileKind::Directory;
        let child = open_child(&file, &leaf, directory, false)?;
        let child_stat = stat(&child)?;
        links.push(Link {
            parent: file,
            leaf,
            identity: identity(&child_stat),
            kind: kind(&child_stat)?,
        });
        file = child;
    }
    let object = RetainedObject {
        observed: observed(&file, expected, hash)?,
        file,
        links,
        _local: PhantomData,
    };
    object.revalidate(hash)?;
    Ok(object)
}

impl RetainedObject {
    fn revalidate_links(&self) -> Result<(), PlatformError> {
        for link in &self.links {
            let mut value = std::mem::MaybeUninit::<libc::stat>::uninit();
            // SAFETY: retained parent, immutable leaf and exact stat storage.
            if unsafe {
                libc::fstatat(
                    link.parent.as_raw_fd(),
                    link.leaf.as_ptr(),
                    value.as_mut_ptr(),
                    libc::AT_SYMLINK_NOFOLLOW,
                )
            } != 0
            {
                return Err(native::errno_error());
            }
            // SAFETY: successful fstatat initialized the structure.
            let value = unsafe { value.assume_init() };
            if identity(&value) != link.identity || kind(&value)? != link.kind {
                return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
            }
        }
        Ok(())
    }

    fn revalidate(&self, hash: bool) -> Result<ObservedFile, PlatformError> {
        self.revalidate_links()?;
        let current = observed(&self.file, self.observed.kind, hash)?;
        if current.identity != self.observed.identity
            || current.physical_path != self.observed.physical_path
            || current.byte_len != self.observed.byte_len
            || current.write_stamp != self.observed.write_stamp
            || (hash
                && self.observed.disk_sha256.is_some()
                && current.disk_sha256 != self.observed.disk_sha256)
        {
            return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
        }
        self.revalidate_links()?;
        Ok(current)
    }
}

pub fn capture_directory(path: &Path) -> Result<RetainedDirectory, PlatformError> {
    capture(path, FileKind::Directory, false).map(RetainedDirectory)
}

pub fn capture_file(path: &Path, hash: bool) -> Result<ReadOnlyFile, PlatformError> {
    capture(path, FileKind::File, hash).map(ReadOnlyFile)
}

impl ReadOnlyFile {
    pub fn observation(&self) -> &ObservedFile {
        &self.0.observed
    }
    pub fn revalidate(&self, hash: bool) -> Result<ObservedFile, PlatformError> {
        self.0.revalidate(hash)
    }
}

impl RetainedDirectory {
    pub fn observation(&self) -> &ObservedFile {
        &self.0.observed
    }
    pub fn revalidate(&self) -> Result<ObservedFile, PlatformError> {
        self.0.revalidate(false)
    }

    /// Actual volume capability bits; invalid/unavailable bits stay unknown.
    pub fn volume_semantics(&self) -> Result<VolumeSemantics, PlatformError> {
        self.0.revalidate_links()?;
        // SAFETY: attrlist is a C integer-only POD structure.
        let mut attributes: libc::attrlist = unsafe { std::mem::zeroed() };
        attributes.bitmapcount = libc::ATTR_BIT_MAP_COUNT as u16;
        attributes.volattr = libc::ATTR_VOL_CAPABILITIES;
        let mut output = [0_u8; 36]; // length + capabilities[4] + valid[4]
        // SAFETY: retained directory and correctly sized packed result buffer.
        if unsafe {
            libc::fgetattrlist(
                self.0.file.as_raw_fd(),
                (&raw mut attributes).cast(),
                output.as_mut_ptr().cast(),
                output.len(),
                0,
            )
        } != 0
        {
            let error = native::errno_error();
            if error.code == PlatformErrorCode::FeatureUnavailable {
                return Ok(VolumeSemantics {
                    case_sensitive: None,
                    case_preserving: None,
                    atomic_swap: None,
                });
            }
            return Err(error);
        }
        let word =
            |offset| u32::from_ne_bytes(output[offset..offset + 4].try_into().expect("fixed word"));
        if word(0) != 36 {
            return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
        }
        Ok(VolumeSemantics {
            case_sensitive: format::capability_bit(
                word(4),
                word(20),
                libc::VOL_CAP_FMT_CASE_SENSITIVE,
            ),
            case_preserving: format::capability_bit(
                word(4),
                word(20),
                libc::VOL_CAP_FMT_CASE_PRESERVING,
            ),
            atomic_swap: format::capability_bit(word(8), word(24), libc::VOL_CAP_INT_RENAME_SWAP),
        })
    }

    /// Descriptor ancestry, not a lexical prefix comparison.
    pub fn contains_file(&self, file: &ReadOnlyFile) -> Result<bool, PlatformError> {
        self.0.revalidate_links()?;
        file.0.revalidate_links()?;
        for link in &file.0.links {
            if identity(&stat(&link.parent)?) == self.0.observed.identity {
                return Ok(true);
            }
        }
        Ok(false)
    }
}

/// Prepared file remains on disk on drop, including all error paths. The owning
/// journal records its path before a replacement call and reconciles recovery.
/// ```compile_fail
/// use bridge_platform_macos::filesystem::StagedReplacement;
/// fn transferable<T: Send + Sync>() {}
/// transferable::<StagedReplacement>();
/// ```
pub struct StagedReplacement {
    parent: RetainedDirectory,
    file: File,
    leaf: CString,
    staged: ObservedFile,
    flushed: bool,
    _local: PhantomData<Rc<()>>,
}

/// The transaction chooses the resulting owner permissions explicitly. No ACL,
/// xattr, original mode or executable bit is silently copied from destination.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StagedPermissions {
    PrivateData,
    OwnerExecutable,
}

impl StagedReplacement {
    /// Consumes descriptor custody of an explicitly chosen parent. Before this
    /// creating call, the owning transaction must persist the exact stage-name
    /// intent and nonce under its exclusion. Errors may leave a partial stage,
    /// so recording only a successful returned observation is insufficient.
    /// The transaction generates the nonce; create-exclusive never overwrites.
    pub fn prepare(
        parent: RetainedDirectory,
        nonce: [u8; 16],
        bytes: &[u8],
        permissions: StagedPermissions,
    ) -> Result<Self, PlatformError> {
        if nonce == [0; 16] {
            return Err(PlatformError::new(PlatformErrorCode::InvalidInput));
        }
        if bytes.len() > format::MAX_STAGE_BYTES {
            return Err(PlatformError::new(PlatformErrorCode::TooLarge));
        }
        parent.0.revalidate_links()?;
        if parent.volume_semantics()?.atomic_swap != Some(true) {
            return Err(PlatformError::new(PlatformErrorCode::FeatureUnavailable));
        }
        let leaf = native::cstring(format::stage_leaf(&nonce).as_bytes())?;
        // SAFETY: exact retained parent, bounded single component and exclusive
        // creation with owner-only permissions. No existing item is followed.
        let fd = unsafe {
            libc::openat(
                parent.0.file.as_raw_fd(),
                leaf.as_ptr(),
                libc::O_RDWR | libc::O_CREAT | libc::O_EXCL | libc::O_NOFOLLOW | libc::O_CLOEXEC,
                0o600,
            )
        };
        if fd < 0 {
            return Err(native::errno_error());
        }
        // SAFETY: successful openat yields one owned descriptor.
        let mut file = unsafe { File::from_raw_fd(fd) };
        file.write_all(bytes).map_err(native::from_io)?;
        let mode = match permissions {
            StagedPermissions::PrivateData => 0o600,
            StagedPermissions::OwnerExecutable => 0o700,
        };
        // SAFETY: exact created descriptor and explicitly chosen owner-only mode.
        if unsafe { libc::fchmod(file.as_raw_fd(), mode) } != 0 {
            return Err(native::errno_error());
        }
        file.sync_all().map_err(native::from_io)?;
        // SAFETY: retained regular-file fd, no variadic argument for this command.
        if unsafe { libc::fcntl(file.as_raw_fd(), libc::F_FULLFSYNC) } != 0 {
            return Err(native::errno_error());
        }
        let staged = observed(&file, FileKind::File, true)?;
        parent.0.revalidate_links()?;
        Ok(Self {
            parent,
            file,
            leaf,
            staged,
            flushed: true,
            _local: PhantomData,
        })
    }

    pub fn observation(&self) -> &ObservedFile {
        &self.staged
    }

    /// Exchange stage/destination atomically; the old destination is retained
    /// at the stage path. Every error after entering renameatx_np is ambiguous.
    /// This does not implement a journal or promise hardware crash durability.
    pub fn replace_under_owner_exclusion(
        &mut self,
        destination_leaf: &[u8],
        expected: &ObservedFile,
    ) -> ReplaceOutcome {
        let prepare = || -> Result<(CString, File), PlatformError> {
            format::validate_leaf(destination_leaf)?;
            let destination = native::cstring(destination_leaf)?;
            if destination == self.leaf
                || expected.kind != FileKind::File
                || expected.disk_sha256.is_none()
            {
                return Err(PlatformError::new(PlatformErrorCode::InvalidInput));
            }
            self.parent.0.revalidate_links()?;
            let stage_at_name = open_child(&self.parent.0.file, &self.leaf, false, false)?;
            if observed(&stage_at_name, FileKind::File, true)? != self.staged
                || observed(&self.file, FileKind::File, true)? != self.staged
                || stat(&self.file)?.st_nlink != 1
            {
                return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
            }
            let previous = open_child(&self.parent.0.file, &destination, false, false)?;
            if observed(&previous, FileKind::File, true)? != *expected
                || stat(&previous)?.st_nlink != 1
                || expected.identity == self.staged.identity
            {
                return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
            }
            self.parent.0.revalidate_links()?;
            Ok((destination, previous))
        };
        let (destination, _previous_custody) = match prepare() {
            Ok(value) => value,
            Err(error) => return ReplaceOutcome::RefusedBeforeCall { error },
        };
        // SAFETY: both exact names relative to one retained parent. RENAME_SWAP
        // never creates a missing destination. External exclusion belongs to caller.
        let status = unsafe {
            libc::renameatx_np(
                self.parent.0.file.as_raw_fd(),
                self.leaf.as_ptr(),
                self.parent.0.file.as_raw_fd(),
                destination.as_ptr(),
                libc::RENAME_SWAP,
            )
        };
        if status != 0 {
            return ReplaceOutcome::AmbiguousAfterCall {
                native_code: native::errno_error().native_code,
            };
        }
        let verify = || -> Result<(ObservedFile, ObservedFile), PlatformError> {
            self.parent.0.revalidate_links()?;
            self.parent.0.file.sync_all().map_err(native::from_io)?;
            let new_file = open_child(&self.parent.0.file, &destination, false, false)?;
            let old_file = open_child(&self.parent.0.file, &self.leaf, false, false)?;
            let new = observed(&new_file, FileKind::File, true)?;
            let old = observed(&old_file, FileKind::File, true)?;
            if !format::exchanged_identities(
                expected.identity,
                self.staged.identity,
                new.identity,
                old.identity,
            ) || new.disk_sha256 != self.staged.disk_sha256
                || old.disk_sha256 != expected.disk_sha256
            {
                return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
            }
            self.parent.0.revalidate_links()?;
            Ok((new, old))
        };
        match verify() {
            Ok((destination, backup)) => ReplaceOutcome::Replaced {
                destination: Box::new(destination),
                backup: Box::new(backup),
                staged_file_flushed: self.flushed,
            },
            Err(error) => ReplaceOutcome::AmbiguousAfterCall {
                native_code: error.native_code,
            },
        }
    }
}
