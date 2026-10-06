//! Retained ordinary-user Apple Silicon WAL storage. This is source awaiting
//! native qualification, not production bootstrap or a power-loss guarantee.
//!
//! The fixed route comes from getpwuid_r, never HOME or a caller path. All names
//! are opened relative to retained descriptors. The BSD lock excludes only
//! cooperating writers; it cannot prevent same-user namespace or byte edits.
//!
//! Constructor protocol, including reopen: leaf fsync + F_FULLFSYNC, every
//! private directory bottom-up through Application Support, leaf fsync +
//! F_FULLFSYNC again, then full custody validation. Failure may leave declared
//! entries and has unknown persistence. Nothing is repaired or removed.
//!
//! Native contracts consulted before authoring:
//! - Apple Libc include/unistd.h and XNU bsd/kern/kern_prot.c (gettid):
//!   https://github.com/apple-oss-distributions/Libc/blob/main/include/unistd.h
//!   https://github.com/apple-oss-distributions/xnu/blob/main/bsd/kern/kern_prot.c
//!   https://github.com/apple-oss-distributions/xnu/blob/main/libsyscall/Platforms/syscall.map
//! - Apple Libc include/sys/acl.h, posix1e/acl_file.c, acl_entry.c and
//!   gen/filesec.c: descriptor extended ACL copy, ENOENT for absent ACL, native
//!   0-success/-1-EINVAL end-of-entry protocol, one acl_free per owned copy.
//!   https://github.com/apple-oss-distributions/Libc/blob/main/include/sys/acl.h
//!   https://github.com/apple-oss-distributions/Libc/blob/main/posix1e/acl_file.c
//!   https://github.com/apple-oss-distributions/Libc/blob/main/posix1e/acl_entry.c
//!   https://github.com/apple-oss-distributions/Libc/blob/main/gen/filesec.c
//! - Apple getpwuid(3), flock(2), fsync(2), fcntl(2), fstatfs(2), write(2).
//!   https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man3/getpwuid.3.html
//!   https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/flock.2.html
//!   https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/fsync.2.html
//! - Apple WWDC19 session 710: firmlinks join System/Data volume ancestry.
//!   https://developer.apple.com/videos/play/wwdc2019/710/
//!
//! libc 0.2.190 supplies passwd/stat/statfs, openat/fstatat, BSD flock,
//! getpwuid_r, __error, fgetxattr, fsync and F_GETPATH/F_FULLFSYNC. Only the
//! opaque ACL and pthread credential declarations missing there are bound here.

#![cfg(all(target_os = "macos", target_arch = "aarch64"))]

use crate::{format, private_journal_policy as policy};
use bridge_journal_io::{JournalStorage, StorageFailure};
use std::{
    cell::RefCell,
    ffi::{CString, c_void},
    fs::File,
    io::{self, Read, Seek, SeekFrom, Write},
    mem::MaybeUninit,
    os::fd::{AsRawFd, FromRawFd},
    ptr,
    sync::atomic::AtomicBool,
    thread::{self, ThreadId},
};

const ACL_TYPE_EXTENDED: libc::c_int = 0x100;
const ACL_MAX_ENTRIES: libc::c_int = 128;
static JOURNAL_RESERVED: AtomicBool = AtomicBool::new(false);

enum Namespace {
    Production,
    #[cfg(test)]
    Fixture(Vec<Vec<u8>>),
}

// Apple declarations use uid_t/gid_t, int, and opaque pointer typedefs. No
// private native layout or variadic ACL argument is reproduced in Rust.
unsafe extern "C" {
    fn pthread_getugid_np(uid: *mut libc::uid_t, gid: *mut libc::gid_t) -> libc::c_int;
    fn acl_get_fd_np(fd: libc::c_int, kind: libc::c_int) -> *mut c_void;
    fn acl_valid(acl: *mut c_void) -> libc::c_int;
    fn acl_get_entry(acl: *mut c_void, index: libc::c_int, entry: *mut *mut c_void) -> libc::c_int;
    fn acl_get_tag_type(entry: *mut c_void, tag: *mut libc::c_int) -> libc::c_int;
    fn acl_free(acl: *mut c_void) -> libc::c_int;
}

fn errno() -> libc::c_int {
    // SAFETY: Darwin returns the calling thread's live errno slot.
    unsafe { *libc::__error() }
}

fn clear_errno() {
    // SAFETY: calling thread owns its errno slot; no credential change.
    unsafe { *libc::__error() = 0 };
}

fn native_failure() -> StorageFailure {
    match errno() {
        libc::ELOOP | libc::ENOTDIR | libc::EINVAL | libc::EPERM | libc::EACCES => {
            StorageFailure::Unsafe
        }
        libc::EWOULDBLOCK => StorageFailure::Busy,
        _ => StorageFailure::Unavailable,
    }
}

fn io_failure(_: StorageFailure) -> io::Error {
    io::Error::from(io::ErrorKind::Other)
}

#[derive(Clone, Copy, PartialEq, Eq)]
struct Credentials {
    uid: libc::uid_t,
    gid: libc::gid_t,
    pid: libc::pid_t,
}

fn thread_override_absent() -> Result<(u32, u32), StorageFailure> {
    let mut thread_uid = u32::MAX;
    let mut thread_gid = u32::MAX;
    clear_errno();
    // SAFETY: initialized, exactly sized output slots remain live for this
    // synchronous call. Only exact ESRCH means no per-thread override. Outputs
    // from any successful/unknown result are never adopted as process identity.
    let result = unsafe { pthread_getugid_np(&mut thread_uid, &mut thread_gid) };
    let failure = errno();
    if result != -1 || failure != libc::ESRCH || thread_uid != u32::MAX || thread_gid != u32::MAX {
        return Err(StorageFailure::Unsafe);
    }
    Ok((thread_uid, thread_gid))
}

fn credentials() -> Result<Credentials, StorageFailure> {
    let (thread_uid, thread_gid) = thread_override_absent()?;
    // SAFETY: identity observations only; never request or change privileges.
    let (uid, euid, gid, egid, set_id, pid) = unsafe {
        (
            libc::getuid(),
            libc::geteuid(),
            libc::getgid(),
            libc::getegid(),
            libc::issetugid(),
            libc::getpid(),
        )
    };
    if set_id != 0 || pid <= 0 {
        return Err(StorageFailure::Unsafe);
    }
    policy::ordinary_credentials(uid, euid, gid, egid, false, thread_uid, thread_gid)?;
    thread_override_absent()?;
    // SAFETY: repeat scalar identity observations to reject a changed context
    // during the complete current-thread/process credential observation.
    if unsafe {
        (
            libc::getuid(),
            libc::geteuid(),
            libc::getgid(),
            libc::getegid(),
            libc::issetugid(),
            libc::getpid(),
        )
    } != (uid, euid, gid, egid, 0, pid)
    {
        return Err(StorageFailure::Unsafe);
    }
    Ok(Credentials { uid, gid, pid })
}

fn home(selected: Credentials) -> Result<Vec<u8>, StorageFailure> {
    let mut bytes = vec![0_u8; policy::MAX_NATIVE_HOME_BUFFER_BYTES];
    // SAFETY: pinned passwd consists of scalar values and nullable raw pointers;
    // zero initialization is valid and no field is read before native success.
    let mut entry: libc::passwd = unsafe { std::mem::zeroed() };
    let mut result = ptr::null_mut();
    // SAFETY: libc's pinned passwd layout and live bounded buffers; successful
    // return is checked before reading scalar fields or any returned pointer.
    let status = unsafe {
        libc::getpwuid_r(
            selected.uid,
            &mut entry,
            bytes.as_mut_ptr().cast(),
            bytes.len(),
            &mut result,
        )
    };
    if status != 0 {
        return Err(StorageFailure::Unavailable);
    }
    if !ptr::eq(result, &entry) || entry.pw_uid != selected.uid || entry.pw_gid != selected.gid {
        return Err(StorageFailure::Unsafe);
    }
    // No dereference of pw_dir: validate its numeric range in the initialized
    // caller buffer, then form a safe slice from that original buffer.
    let path = policy::bounded_c_string(&bytes, entry.pw_dir as usize, bytes.as_ptr() as usize)?;
    if credentials()? != selected {
        return Err(StorageFailure::Unsafe);
    }
    Ok(path.to_vec())
}

fn stat(file: &File) -> Result<libc::stat, StorageFailure> {
    let mut value = MaybeUninit::uninit();
    // SAFETY: live owned descriptor and exact pinned native stat output.
    if unsafe { libc::fstat(file.as_raw_fd(), value.as_mut_ptr()) } != 0 {
        return Err(native_failure());
    }
    // SAFETY: successful fstat initialized all stat fields.
    Ok(unsafe { value.assume_init() })
}

fn relative_stat(parent: &File, leaf: &CString) -> Result<libc::stat, StorageFailure> {
    let mut value = MaybeUninit::uninit();
    // SAFETY: retained parent and bounded immutable single component; do not
    // follow a final symlink, including during revalidation.
    if unsafe {
        libc::fstatat(
            parent.as_raw_fd(),
            leaf.as_ptr(),
            value.as_mut_ptr(),
            libc::AT_SYMLINK_NOFOLLOW,
        )
    } != 0
    {
        return Err(native_failure());
    }
    // SAFETY: successful fstatat initialized the structure.
    Ok(unsafe { value.assume_init() })
}

#[derive(Clone, Copy, PartialEq, Eq)]
struct Stable {
    device: libc::dev_t,
    inode: libc::ino_t,
    kind: libc::mode_t,
    mode: libc::mode_t,
    uid: libc::uid_t,
    gid: libc::gid_t,
    flags: u32,
    generation: u32,
}

fn stable(value: &libc::stat) -> Stable {
    Stable {
        device: value.st_dev,
        inode: value.st_ino,
        kind: value.st_mode & libc::S_IFMT,
        mode: value.st_mode,
        uid: value.st_uid,
        gid: value.st_gid,
        flags: value.st_flags,
        generation: value.st_gen,
    }
}

fn observation(value: &libc::stat) -> policy::InodeObservation {
    policy::InodeObservation {
        kind: match value.st_mode & libc::S_IFMT {
            libc::S_IFDIR => policy::InodeKind::Directory,
            libc::S_IFREG => policy::InodeKind::RegularFile,
            _ => policy::InodeKind::Other,
        },
        uid: value.st_uid,
        mode: u32::from(value.st_mode),
        links: u64::from(value.st_nlink),
        size: value.st_size,
    }
}

fn physical_path(file: &File) -> Result<Vec<u8>, StorageFailure> {
    let mut bytes = [0_u8; libc::MAXPATHLEN as usize];
    // SAFETY: F_GETPATH's documented MAXPATHLEN buffer and retained fd.
    if unsafe { libc::fcntl(file.as_raw_fd(), libc::F_GETPATH, bytes.as_mut_ptr()) } != 0 {
        return Err(native_failure());
    }
    let end = bytes
        .iter()
        .position(|byte| *byte == 0)
        .ok_or(StorageFailure::Unsafe)?;
    std::str::from_utf8(&bytes[..end]).map_err(|_| StorageFailure::Unsafe)?;
    format::absolute_components(&bytes[..end]).map_err(|_| StorageFailure::Unsafe)?;
    Ok(bytes[..end].to_vec())
}

fn finder_alias(file: &File) -> Result<(), StorageFailure> {
    let mut bytes = [0_u8; 32];
    // SAFETY: fixed attribute name, retained descriptor and exact output cap.
    let count = unsafe {
        libc::fgetxattr(
            file.as_raw_fd(),
            c"com.apple.FinderInfo".as_ptr(),
            bytes.as_mut_ptr().cast(),
            bytes.len(),
            0,
            0,
        )
    };
    if count == -1 && errno() == libc::ENOATTR {
        return Ok(());
    }
    if count != 32 || u16::from_be_bytes([bytes[8], bytes[9]]) & 0x8000 != 0 {
        return Err(StorageFailure::Unsafe);
    }
    Ok(())
}

struct OwnedAcl(*mut c_void);
impl Drop for OwnedAcl {
    fn drop(&mut self) {
        // SAFETY: one owned copy from acl_get_fd_np; entry values are borrowed.
        unsafe { acl_free(self.0) };
    }
}

fn acl(file: &File, private: bool) -> Result<(), StorageFailure> {
    clear_errno();
    // SAFETY: retained descriptor, fixed supported native extended ACL type.
    let raw = unsafe { acl_get_fd_np(file.as_raw_fd(), ACL_TYPE_EXTENDED) };
    if raw.is_null() {
        // Apple acl_file.c/filesec.c: absent FILESEC_ACL is NULL + ENOENT.
        // Any unsupported/denied/allocation/unknown result refuses.
        return if errno() == libc::ENOENT {
            Ok(())
        } else {
            Err(StorageFailure::Unavailable)
        };
    }
    let owned = OwnedAcl(raw);
    // SAFETY: owned native ACL copy, opaque representation never dereferenced.
    if unsafe { acl_valid(owned.0) } != 0 {
        return Err(StorageFailure::Unsafe);
    }
    for index in 0..=ACL_MAX_ENTRIES {
        let mut entry = ptr::null_mut();
        clear_errno();
        // SAFETY: owned ACL, bounded native index and initialized output slot.
        let result = unsafe { acl_get_entry(owned.0, index, &mut entry) };
        // Darwin's implementation returns -1/EINVAL at the first absent index,
        // unlike other POSIX ACL implementations. Valid ACL checked above.
        if result == -1 && errno() == libc::EINVAL && entry.is_null() {
            return if private {
                policy::no_extended_acl(Some(index as usize))
            } else {
                Ok(())
            };
        }
        if result != 0 || entry.is_null() || index == ACL_MAX_ENTRIES || private {
            return Err(StorageFailure::Unsafe);
        }
        let mut tag = 0;
        // SAFETY: entry is borrowed from the still-owned ACL; exact int output.
        if unsafe { acl_get_tag_type(entry, &mut tag) } != 0 {
            return Err(StorageFailure::Unsafe);
        }
        policy::ancestor_acl_tag(Some(tag))?;
    }
    Err(StorageFailure::Unsafe)
}

fn local_apfs(file: &File) -> Result<(), StorageFailure> {
    let mut value = MaybeUninit::<libc::statfs>::uninit();
    // SAFETY: live retained fd and exactly sized pinned native statfs layout.
    if unsafe { libc::fstatfs(file.as_raw_fd(), value.as_mut_ptr()) } != 0 {
        return Err(native_failure());
    }
    // SAFETY: successful fstatfs initialized the structure.
    let value = unsafe { value.assume_init() };
    let name: Vec<u8> = value.f_fstypename.iter().map(|byte| *byte as u8).collect();
    if name.iter().position(|byte| *byte == 0) != Some(4)
        || &name[..4] != b"apfs"
        || value.f_flags & libc::MNT_LOCAL as u32 == 0
        || value.f_flags & (libc::MNT_RDONLY | libc::MNT_UNION | libc::MNT_IGNORE_OWNERSHIP) as u32
            != 0
    {
        return Err(StorageFailure::Unsafe);
    }
    Ok(())
}

fn security(
    file: &File,
    value: &libc::stat,
    selected: Credentials,
    private: bool,
    directory: bool,
    user_ancestry: bool,
) -> Result<(), StorageFailure> {
    if directory {
        if private {
            policy::private_directory(&observation(value), selected.uid)?;
        } else if value.st_mode & libc::S_IFMT != libc::S_IFDIR
            || value.st_nlink == 0
            || value.st_mode & 0o7022 != 0
            || (user_ancestry && value.st_uid != selected.uid)
            || (!user_ancestry && value.st_uid != 0 && value.st_uid != selected.uid)
        {
            return Err(StorageFailure::Unsafe);
        }
    } else {
        policy::private_leaf(&observation(value), selected.uid)?;
    }
    if private && value.st_flags != 0 {
        return Err(StorageFailure::Unsafe);
    }
    finder_alias(file)?;
    acl(file, private)?;
    let after = stat(file)?;
    if stable(value) != stable(&after)
        || value.st_nlink != after.st_nlink
        || value.st_size != after.st_size
        || value.st_mtime != after.st_mtime
        || value.st_mtime_nsec != after.st_mtime_nsec
        || value.st_ctime != after.st_ctime
        || value.st_ctime_nsec != after.st_ctime_nsec
    {
        return Err(StorageFailure::Unsafe);
    }
    Ok(())
}

fn leaf_name(bytes: &[u8]) -> Result<CString, StorageFailure> {
    format::validate_leaf(bytes).map_err(|_| StorageFailure::Unsafe)?;
    CString::new(bytes).map_err(|_| StorageFailure::Unsafe)
}

fn joined(parent: &[u8], leaf: &[u8]) -> Result<Vec<u8>, StorageFailure> {
    let mut result = parent.to_vec();
    if result != b"/" {
        result.push(b'/');
    }
    result.extend_from_slice(leaf);
    format::absolute_components(&result).map_err(|_| StorageFailure::Unsafe)?;
    Ok(result)
}

struct Directory {
    file: File,
    name: Option<CString>,
    stable: Stable,
    path: Vec<u8>,
    private: bool,
    user_ancestry: bool,
}

fn directory(
    file: File,
    name: Option<CString>,
    path: Vec<u8>,
    selected: Credentials,
    private: bool,
    user_ancestry: bool,
) -> Result<Directory, StorageFailure> {
    let value = stat(&file)?;
    security(&file, &value, selected, private, true, user_ancestry)?;
    if physical_path(&file)? != path {
        return Err(StorageFailure::Unsafe);
    }
    Ok(Directory {
        file,
        name,
        stable: stable(&value),
        path,
        private,
        user_ancestry,
    })
}

fn open_directory(
    parent: &Directory,
    name: &CString,
    create: bool,
) -> Result<File, StorageFailure> {
    if create {
        // SAFETY: retained parent and one validated fixed component. mkdirat's
        // mode never grants group/other access, even under umask. No subsequent
        // chmod is used because mkdirat does not return created-object custody.
        let result = unsafe { libc::mkdirat(parent.file.as_raw_fd(), name.as_ptr(), 0o700) };
        if result != 0 && errno() != libc::EEXIST {
            return Err(native_failure());
        }
    }
    let before = relative_stat(&parent.file, name)?;
    if before.st_mode & libc::S_IFMT != libc::S_IFDIR {
        return Err(StorageFailure::Unsafe);
    }
    // SAFETY: noncreating, nonfollowing exact retained-parent open.
    let fd = unsafe {
        libc::openat(
            parent.file.as_raw_fd(),
            name.as_ptr(),
            libc::O_RDONLY
                | libc::O_DIRECTORY
                | libc::O_NOFOLLOW
                | libc::O_CLOEXEC
                | libc::O_NONBLOCK,
        )
    };
    if fd < 0 {
        return Err(native_failure());
    }
    // SAFETY: successful openat transfers exactly one fresh owned descriptor.
    let file = unsafe { File::from_raw_fd(fd) };
    if stable(&before) != stable(&stat(&file)?) {
        return Err(StorageFailure::Unsafe);
    }
    Ok(file)
}

fn open_leaf(parent: &Directory, name: &CString) -> Result<File, StorageFailure> {
    let flags = libc::O_RDWR | libc::O_NOFOLLOW | libc::O_CLOEXEC | libc::O_NONBLOCK;
    // SAFETY: exact retained parent, exclusive new-file creation, owner mode.
    let created = unsafe {
        libc::openat(
            parent.file.as_raw_fd(),
            name.as_ptr(),
            flags | libc::O_CREAT | libc::O_EXCL,
            0o600,
        )
    };
    if created >= 0 {
        // SAFETY: successful exclusive create owns this exact new file once.
        let file = unsafe { File::from_raw_fd(created) };
        // Initialize only the exclusive-created fd, never an existing object.
        // SAFETY: retained newly-created writable regular-file descriptor.
        if unsafe { libc::fchmod(file.as_raw_fd(), 0o600) } != 0 {
            return Err(native_failure());
        }
        return Ok(file);
    }
    if errno() != libc::EEXIST {
        return Err(native_failure());
    }
    let before = relative_stat(&parent.file, name)?;
    if before.st_mode & libc::S_IFMT != libc::S_IFREG {
        return Err(StorageFailure::Unsafe);
    }
    // SAFETY: existing exact component, never create/truncate/follow a link.
    let fd = unsafe { libc::openat(parent.file.as_raw_fd(), name.as_ptr(), flags) };
    if fd < 0 {
        return Err(native_failure());
    }
    // SAFETY: successful openat yields one new owned descriptor.
    let file = unsafe { File::from_raw_fd(fd) };
    let after = stat(&file)?;
    if stable(&before) != stable(&after)
        || before.st_nlink != after.st_nlink
        || before.st_size != after.st_size
    {
        return Err(StorageFailure::Unsafe);
    }
    Ok(file)
}

fn full_sync(file: &File) -> Result<(), StorageFailure> {
    // SAFETY: synchronous operations on the retained fd, no borrowed outputs.
    if unsafe { libc::fsync(file.as_raw_fd()) } != 0 {
        return Err(native_failure());
    }
    // SAFETY: F_FULLFSYNC ignores the optional argument. Never fall back to a
    // weaker flush if the native operation is denied/unsupported/interrupted.
    if unsafe { libc::fcntl(file.as_raw_fd(), libc::F_FULLFSYNC) } != 0 {
        return Err(native_failure());
    }
    Ok(())
}

struct Custody {
    // Drop order closes the leaf (and its advisory lock) before ancestors.
    leaf: File,
    directories: Vec<Directory>,
    leaf_name: CString,
    leaf_stable: Stable,
    leaf_path: Vec<u8>,
    expected_len: u64,
    selected: Credentials,
    thread: ThreadId,
    home_index: usize,
    namespace_parent: usize,
    device: libc::dev_t,
    lock_admitted: bool,
}

fn validate_directories(
    directories: &[Directory],
    selected: Credentials,
    home_volume: Option<(usize, libc::dev_t)>,
) -> Result<(), StorageFailure> {
    if credentials()? != selected {
        return Err(StorageFailure::Unsafe);
    }
    for (index, row) in directories.iter().enumerate() {
        let current = stat(&row.file)?;
        if stable(&current) != row.stable || physical_path(&row.file)? != row.path {
            return Err(StorageFailure::Unsafe);
        }
        security(
            &row.file,
            &current,
            selected,
            row.private,
            true,
            row.user_ancestry,
        )?;
        if let Some((home_index, device)) = home_volume
            && index >= home_index
        {
            local_apfs(&row.file)?;
            if current.st_dev != device {
                return Err(StorageFailure::Unsafe);
            }
        }
        if index != 0 {
            let parent = &directories[index - 1];
            let name = row.name.as_ref().ok_or(StorageFailure::Unsafe)?;
            if stable(&relative_stat(&parent.file, name)?) != row.stable {
                return Err(StorageFailure::Unsafe);
            }
        }
    }
    if credentials()? != selected {
        return Err(StorageFailure::Unsafe);
    }
    Ok(())
}

impl Custody {
    fn validate(&self) -> Result<u64, StorageFailure> {
        if !self.lock_admitted
            || thread::current().id() != self.thread
            || credentials()? != self.selected
        {
            return Err(StorageFailure::Unsafe);
        }
        validate_directories(
            &self.directories,
            self.selected,
            Some((self.home_index, self.device)),
        )?;
        let parent = self.directories.last().ok_or(StorageFailure::Unsafe)?;
        let current = stat(&self.leaf)?;
        let route = relative_stat(&parent.file, &self.leaf_name)?;
        if stable(&current) != self.leaf_stable
            || stable(&route) != self.leaf_stable
            || route.st_nlink != 1
            || current.st_dev != self.device
            || physical_path(&self.leaf)? != self.leaf_path
            || policy::bounded_length(current.st_size)? != self.expected_len
            || route.st_size != current.st_size
        {
            return Err(StorageFailure::Unsafe);
        }
        security(&self.leaf, &current, self.selected, true, false, true)?;
        local_apfs(&self.leaf)?;
        // BSD flock remains associated with this privately owned open file
        // until its last close. There is no unlock/dup/clone surface. Validation
        // never reissues flock: it must not acquire replacement custody. Unsafe
        // external duplication/unlock or inherited-fd fork interference cannot
        // be ruled out by observations and is not a mandatory exclusion claim.
        if credentials()? != self.selected {
            return Err(StorageFailure::Unsafe);
        }
        validate_directories(
            &self.directories,
            self.selected,
            Some((self.home_index, self.device)),
        )?;
        let final_current = stat(&self.leaf)?;
        let final_route = relative_stat(&parent.file, &self.leaf_name)?;
        if stable(&final_current) != self.leaf_stable
            || stable(&final_route) != self.leaf_stable
            || final_current.st_nlink != 1
            || final_route.st_nlink != 1
            || policy::bounded_length(final_current.st_size)? != self.expected_len
            || final_route.st_size != final_current.st_size
            || physical_path(&self.leaf)? != self.leaf_path
        {
            return Err(StorageFailure::Unsafe);
        }
        Ok(self.expected_len)
    }
}

/// One backend-owned native Apple Silicon journal. No path injection, raw-fd
/// extraction, cloning, sharing or actor-thread transfer is exposed.
/// Native ACL, APFS route, lock and flush behavior remain unqualified.
///
/// ```compile_fail
/// fn require_send<T: Send>() {}
/// require_send::<bridge_platform_macos::NativePrivateJournalStorage>();
/// ```
/// ```compile_fail
/// fn require_sync<T: Sync>() {}
/// require_sync::<bridge_platform_macos::NativePrivateJournalStorage>();
/// ```
pub struct NativePrivateJournalStorage {
    // All descriptors close before the process reservation releases.
    custody: RefCell<Custody>,
    _reservation: policy::Reservation,
    refusal: policy::Refusal,
}

impl NativePrivateJournalStorage {
    /// Provision only the fixed ordinary-user backend namespace. Foreign or
    /// unknown observations refuse, and failed construction leaves its entries.
    pub fn open() -> Result<Self, StorageFailure> {
        Self::construct(Namespace::Production, None)
    }

    #[cfg(test)]
    fn construct_fixture(
        components: &[&[u8]],
        fail_after_flush: Option<usize>,
    ) -> Result<Self, StorageFailure> {
        const PREFIX: &[u8] = b".bridge-br07-journal-";
        if components.len() != 2 || components[1] != b"v1" {
            return Err(StorageFailure::Unsafe);
        }
        let nonce = components[0]
            .strip_prefix(PREFIX)
            .ok_or(StorageFailure::Unsafe)?;
        if nonce.len() != 32
            || !nonce
                .iter()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(byte))
            || nonce.iter().all(|byte| *byte == b'0')
        {
            return Err(StorageFailure::Unsafe);
        }
        Self::construct(
            Namespace::Fixture(components.iter().map(|part| part.to_vec()).collect()),
            fail_after_flush,
        )
    }

    fn construct(
        namespace: Namespace,
        #[allow(unused_variables)] fail_after_flush: Option<usize>,
    ) -> Result<Self, StorageFailure> {
        let reservation = policy::Reservation::acquire(&JOURNAL_RESERVED)?;
        let selected = credentials()?;
        let (home, fixture, private_components) = match namespace {
            Namespace::Production => (
                home(selected)?,
                false,
                vec![b"STFCModBridgeNext".to_vec(), b"v1".to_vec()],
            ),
            #[cfg(test)]
            Namespace::Fixture(components) => {
                // Only the existing native harness injects this fixture parent.
                // This branch and namespace grammar do not exist in normal builds.
                let parent = std::env::var("BRIDGE_MACOS_NATIVE_FIXTURE_ROOT")
                    .map_err(|_| StorageFailure::Unsafe)?;
                let parent = parent.into_bytes();
                format::absolute_components(&parent).map_err(|_| StorageFailure::Unsafe)?;
                (parent, true, components)
            }
        };
        let parts = format::absolute_components(&home).map_err(|_| StorageFailure::Unsafe)?;
        if parts.is_empty() {
            return Err(StorageFailure::Unsafe);
        }
        // Bound the complete fixed route before any creating call. F_GETPATH's
        // native buffer is stricter than the portable path grammar.
        let mut route = home.clone();
        if !fixture {
            for part in [b"Library".as_slice(), b"Application Support".as_slice()] {
                route = joined(&route, part)?;
            }
        }
        for part in &private_components {
            route = joined(&route, part)?;
        }
        route = joined(&route, policy::JOURNAL_LEAF)?;
        if route.len() >= libc::MAXPATHLEN as usize {
            return Err(StorageFailure::Unsafe);
        }
        // SAFETY: fixed filesystem root, owned noncreating/nonfollowing fd.
        let fd = unsafe {
            libc::open(
                c"/".as_ptr(),
                libc::O_RDONLY | libc::O_DIRECTORY | libc::O_CLOEXEC,
            )
        };
        if fd < 0 {
            return Err(native_failure());
        }
        // SAFETY: successful root open owns one descriptor.
        let file = unsafe { File::from_raw_fd(fd) };
        let mut directories = vec![directory(
            file,
            None,
            b"/".to_vec(),
            selected,
            false,
            false,
        )?];
        for (index, part) in parts.iter().enumerate() {
            validate_directories(&directories, selected, None)?;
            let parent = directories.last().ok_or(StorageFailure::Unsafe)?;
            let name = leaf_name(part)?;
            let file = open_directory(parent, &name, false)?;
            let path = joined(&parent.path, part)?;
            directories.push(directory(
                file,
                Some(name),
                path,
                selected,
                fixture && index + 1 == parts.len(),
                index + 1 == parts.len(),
            )?);
            validate_directories(&directories, selected, None)?;
        }
        let home_index = directories.len() - 1;
        let home_row = directories.last().ok_or(StorageFailure::Unsafe)?;
        local_apfs(&home_row.file)?;
        let device = home_row.stable.device;
        // Existing user Library/Application Support only. Bridge never creates
        // or repairs OS-managed ancestry and never follows an alternative route.
        for part in if fixture {
            Vec::new()
        } else {
            vec![b"Library".as_slice(), b"Application Support".as_slice()]
        } {
            validate_directories(&directories, selected, Some((home_index, device)))?;
            let parent = directories.last().ok_or(StorageFailure::Unsafe)?;
            let name = leaf_name(part)?;
            let file = open_directory(parent, &name, false)?;
            let path = joined(&parent.path, part)?;
            let row = directory(file, Some(name), path, selected, false, true)?;
            local_apfs(&row.file)?;
            if row.stable.device != device {
                return Err(StorageFailure::Unsafe);
            }
            directories.push(row);
            validate_directories(&directories, selected, Some((home_index, device)))?;
        }
        let namespace_parent = directories.len() - 1;
        for part in &private_components {
            validate_directories(&directories, selected, Some((home_index, device)))?;
            let parent = directories.last().ok_or(StorageFailure::Unsafe)?;
            let name = leaf_name(part)?;
            let file = open_directory(parent, &name, true)?;
            let path = joined(&parent.path, part)?;
            let row = directory(file, Some(name), path, selected, true, true)?;
            local_apfs(&row.file)?;
            if row.stable.device != device {
                return Err(StorageFailure::Unsafe);
            }
            directories.push(row);
            validate_directories(&directories, selected, Some((home_index, device)))?;
        }
        let parent = directories.last().ok_or(StorageFailure::Unsafe)?;
        validate_directories(&directories, selected, Some((home_index, device)))?;
        let leaf_name = leaf_name(policy::JOURNAL_LEAF)?;
        let leaf = open_leaf(parent, &leaf_name)?;
        let leaf_path = joined(&parent.path, policy::JOURNAL_LEAF)?;
        let value = stat(&leaf)?;
        security(&leaf, &value, selected, true, false, true)?;
        local_apfs(&leaf)?;
        if value.st_dev != device || physical_path(&leaf)? != leaf_path {
            return Err(StorageFailure::Unsafe);
        }
        // SAFETY: retained leaf, nonblocking lifetime cooperative exclusion.
        if unsafe { libc::flock(leaf.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) } != 0 {
            return Err(native_failure());
        }
        let custody = Custody {
            leaf,
            directories,
            leaf_name,
            leaf_stable: stable(&value),
            leaf_path,
            expected_len: policy::bounded_length(value.st_size)?,
            selected,
            thread: thread::current().id(),
            home_index,
            namespace_parent,
            device,
            lock_admitted: true,
        };
        custody.validate()?;
        #[cfg(test)]
        let mut flush_count = 0;
        policy::constructor_flush(
            custody.namespace_parent,
            custody.directories.len(),
            |index| {
                custody.validate()?;
                match index {
                    None => full_sync(&custody.leaf)?,
                    Some(index) => {
                        // SAFETY: retained directory; no weaker flush fallback.
                        if unsafe { libc::fsync(custody.directories[index].file.as_raw_fd()) } != 0
                        {
                            return Err(native_failure());
                        }
                    }
                }
                custody.validate()?;
                #[cfg(test)]
                {
                    flush_count += 1;
                    if fail_after_flush == Some(flush_count) {
                        return Err(StorageFailure::Unavailable);
                    }
                }
                Ok(())
            },
        )?;
        custody.validate()?;
        Ok(Self {
            custody: RefCell::new(custody),
            _reservation: reservation,
            refusal: policy::Refusal::default(),
        })
    }

    fn with<T>(
        &self,
        operation: impl FnOnce(&mut Custody) -> Result<T, StorageFailure>,
    ) -> Result<T, StorageFailure> {
        self.refusal.run(|| {
            let mut custody = self
                .custody
                .try_borrow_mut()
                .map_err(|_| StorageFailure::Unsafe)?;
            operation(&mut custody)
        })
    }
}

impl JournalStorage for NativePrivateJournalStorage {
    fn validate_custody(&self) -> Result<u64, StorageFailure> {
        self.with(|custody| custody.validate())
    }

    fn truncate(&mut self, length: u64) -> Result<(), StorageFailure> {
        self.with(|custody| {
            custody.validate()?;
            if length > policy::MAX_JOURNAL_BYTES {
                return Err(StorageFailure::Unsafe);
            }
            custody
                .leaf
                .set_len(length)
                .map_err(|_| StorageFailure::Unavailable)?;
            custody.expected_len = length;
            custody.validate()?;
            Ok(())
        })
    }

    fn sync_durable(&mut self) -> Result<(), StorageFailure> {
        self.with(|custody| {
            custody.validate()?;
            full_sync(&custody.leaf)?;
            custody.validate()?;
            Ok(())
        })
    }
}

impl Read for NativePrivateJournalStorage {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        self.with(|custody| {
            custody.validate()?;
            let count = loop {
                match custody.leaf.read(buffer) {
                    Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
                    result => break result.map_err(|_| StorageFailure::Unavailable)?,
                }
            };
            custody.validate()?;
            Ok(count)
        })
        .map_err(io_failure)
    }
}

impl Write for NativePrivateJournalStorage {
    fn write(&mut self, buffer: &[u8]) -> io::Result<usize> {
        self.with(|custody| {
            let length = custody.validate()?;
            let position = custody
                .leaf
                .stream_position()
                .map_err(|_| StorageFailure::Unavailable)?;
            policy::checked_write_end(position, buffer.len())?;
            let count = loop {
                match custody.leaf.write(buffer) {
                    Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
                    result => break result.map_err(|_| StorageFailure::Unavailable)?,
                }
            };
            // A zero-byte write beyond EOF does not extend the native file.
            custody.expected_len = if count == 0 {
                length
            } else {
                length.max(policy::checked_write_end(position, count)?)
            };
            custody.validate()?;
            Ok(count)
        })
        .map_err(io_failure)
    }

    fn flush(&mut self) -> io::Result<()> {
        self.sync_durable().map_err(io_failure)
    }
}

impl Seek for NativePrivateJournalStorage {
    fn seek(&mut self, target: SeekFrom) -> io::Result<u64> {
        self.with(|custody| {
            let length = custody.validate()?;
            let position = custody
                .leaf
                .stream_position()
                .map_err(|_| StorageFailure::Unavailable)?;
            let expected = policy::checked_seek(position, length, target)?;
            let result = custody
                .leaf
                .seek(SeekFrom::Start(expected))
                .map_err(|_| StorageFailure::Unavailable)?;
            if result != expected {
                return Err(StorageFailure::Unsafe);
            }
            custody.validate()?;
            Ok(result)
        })
        .map_err(io_failure)
    }
}

#[cfg(test)]
mod native_fixtures;
