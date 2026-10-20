//! Portable private-journal policy. These decisions are not native evidence.
use bridge_journal_io::StorageFailure;
use std::{
    cell::Cell,
    io::SeekFrom,
    marker::PhantomData,
    rc::Rc,
    sync::atomic::{AtomicBool, Ordering},
};

pub(crate) const MAX_JOURNAL_BYTES: u64 = 128 * 1024 * 1024;
pub(crate) const MAX_NATIVE_HOME_BUFFER_BYTES: usize = 1024 * 1024;
pub(crate) const NAMESPACE_DIRECTORIES: [&[u8]; 4] = [
    b"Library",
    b"Application Support",
    b"STFCModBridgeNext",
    b"v1",
];
pub(crate) const JOURNAL_LEAF: &[u8] = b"operations.wal";

/// Read only the initialized buffer supplied to getpwuid_r. No native pointer
/// is dereferenced here, and a caller-supplied base must identify that buffer.
pub(crate) fn bounded_c_string(
    buffer: &[u8],
    native_address: usize,
    base: usize,
) -> Result<&[u8], StorageFailure> {
    if buffer.is_empty()
        || buffer.len() > MAX_NATIVE_HOME_BUFFER_BYTES
        || base != buffer.as_ptr() as usize
    {
        return Err(StorageFailure::Unsafe);
    }
    let end = base
        .checked_add(buffer.len())
        .ok_or(StorageFailure::Unsafe)?;
    if native_address < base || native_address >= end {
        return Err(StorageFailure::Unsafe);
    }
    let offset = native_address
        .checked_sub(base)
        .ok_or(StorageFailure::Unsafe)?;
    let suffix = buffer.get(offset..).ok_or(StorageFailure::Unsafe)?;
    let nul = suffix
        .iter()
        .position(|byte| *byte == 0)
        .ok_or(StorageFailure::Unsafe)?;
    let home = &suffix[..nul];
    std::str::from_utf8(home).map_err(|_| StorageFailure::Unsafe)?;
    let components =
        crate::format::absolute_components(home).map_err(|_| StorageFailure::Unsafe)?;
    let suffix_bytes = NAMESPACE_DIRECTORIES
        .iter()
        .chain(std::iter::once(&JOURNAL_LEAF))
        .try_fold(0_usize, |count, name| count.checked_add(name.len() + 1))
        .ok_or(StorageFailure::Unsafe)?;
    let path_bytes = home
        .len()
        .checked_add(suffix_bytes)
        .and_then(|length| length.checked_sub(usize::from(home == b"/")))
        .ok_or(StorageFailure::Unsafe)?;
    let path_components = components
        .len()
        .checked_add(NAMESPACE_DIRECTORIES.len() + 1)
        .ok_or(StorageFailure::Unsafe)?;
    if path_bytes > crate::format::MAX_PATH_BYTES || path_components > crate::format::MAX_COMPONENTS
    {
        return Err(StorageFailure::Unsafe);
    }
    Ok(home)
}

/// Darwin's no-thread-override UID/GID sentinels are both u32::MAX. Even an
/// override equal to the process identity is refused rather than normalized.
pub(crate) fn ordinary_credentials(
    uid: u32,
    euid: u32,
    gid: u32,
    egid: u32,
    issetugid: bool,
    thread_uid: u32,
    thread_gid: u32,
) -> Result<(), StorageFailure> {
    if uid == 0
        || uid == u32::MAX
        || uid != euid
        || gid == 0
        || gid == u32::MAX
        || gid != egid
        || issetugid
        || thread_uid != u32::MAX
        || thread_gid != u32::MAX
    {
        return Err(StorageFailure::Unsafe);
    }
    Ok(())
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum InodeKind {
    Directory,
    RegularFile,
    Other,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct InodeObservation {
    pub(crate) kind: InodeKind,
    pub(crate) uid: u32,
    pub(crate) mode: u32,
    pub(crate) links: u64,
    pub(crate) size: i64,
}

fn owner_and_mode(
    observed: &InodeObservation,
    expected_uid: u32,
    permissions: u32,
) -> Result<(), StorageFailure> {
    if expected_uid == 0
        || expected_uid == u32::MAX
        || observed.uid != expected_uid
        || observed.mode & 0o7777 != permissions
    {
        return Err(StorageFailure::Unsafe);
    }
    Ok(())
}

pub(crate) fn private_directory(
    observed: &InodeObservation,
    expected_uid: u32,
) -> Result<(), StorageFailure> {
    owner_and_mode(observed, expected_uid, 0o700)?;
    if observed.kind != InodeKind::Directory || observed.links == 0 {
        return Err(StorageFailure::Unsafe);
    }
    Ok(())
}

pub(crate) fn private_leaf(
    observed: &InodeObservation,
    expected_uid: u32,
) -> Result<u64, StorageFailure> {
    owner_and_mode(observed, expected_uid, 0o600)?;
    if observed.kind != InodeKind::RegularFile || observed.links != 1 {
        return Err(StorageFailure::Unsafe);
    }
    bounded_length(observed.size)
}

/// Only an observed absence is accepted. Unavailable ACL observations cannot
/// be treated as private permissions, and existing ACLs are never repaired.
pub(crate) fn no_extended_acl(entries: Option<usize>) -> Result<(), StorageFailure> {
    match entries {
        Some(0) => Ok(()),
        Some(_) | None => Err(StorageFailure::Unsafe),
    }
}

/// OS-managed ancestors may retain DENY entries. Unknown tags and every other
/// observed tag are refused; private namespace entries still require no ACL.
pub(crate) fn ancestor_acl_tag(tag: Option<i32>) -> Result<(), StorageFailure> {
    match tag {
        Some(2) => Ok(()),
        Some(_) | None => Err(StorageFailure::Unsafe),
    }
}

pub(crate) fn bounded_length(length: i64) -> Result<u64, StorageFailure> {
    let length = u64::try_from(length).map_err(|_| StorageFailure::Unsafe)?;
    if length > MAX_JOURNAL_BYTES {
        return Err(StorageFailure::Unsafe);
    }
    Ok(length)
}

pub(crate) fn checked_seek(
    position: u64,
    length: u64,
    seek: SeekFrom,
) -> Result<u64, StorageFailure> {
    if position > MAX_JOURNAL_BYTES || length > MAX_JOURNAL_BYTES {
        return Err(StorageFailure::Unsafe);
    }
    let next = match seek {
        SeekFrom::Start(next) => Some(next),
        SeekFrom::Current(offset) => position.checked_add_signed(offset),
        SeekFrom::End(offset) => length.checked_add_signed(offset),
    }
    .filter(|next| *next <= MAX_JOURNAL_BYTES)
    .ok_or(StorageFailure::Unsafe)?;
    Ok(next)
}

pub(crate) fn checked_write_end(position: u64, byte_count: usize) -> Result<u64, StorageFailure> {
    let byte_count = u64::try_from(byte_count).map_err(|_| StorageFailure::Unsafe)?;
    position
        .checked_add(byte_count)
        .filter(|end| *end <= MAX_JOURNAL_BYTES)
        .ok_or(StorageFailure::Unsafe)
}

/// A process-local reservation supplements the native cooperative lock. This
/// owner cannot be sent or shared; Drop releases exactly its admitted claim.
pub(crate) struct Reservation {
    occupied: &'static AtomicBool,
    _local: PhantomData<Rc<()>>,
}

impl Reservation {
    pub(crate) fn acquire(occupied: &'static AtomicBool) -> Result<Self, StorageFailure> {
        occupied
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .map_err(|_| StorageFailure::Busy)?;
        Ok(Self {
            occupied,
            _local: PhantomData,
        })
    }
}

impl Drop for Reservation {
    fn drop(&mut self) {
        self.occupied.store(false, Ordering::Release);
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum RefusalState {
    Ready,
    Entered,
    Refused,
}

/// Admission is refused before calling an operation. Success alone restores
/// admission, so an error or unwind cannot permit later journal use.
pub(crate) struct Refusal {
    state: Cell<RefusalState>,
}

impl Refusal {
    pub(crate) fn new() -> Self {
        Self {
            state: Cell::new(RefusalState::Ready),
        }
    }

    #[cfg(test)]
    pub(crate) fn is_refused(&self) -> bool {
        self.state.get() != RefusalState::Ready
    }

    pub(crate) fn run<T>(
        &self,
        operation: impl FnOnce() -> Result<T, StorageFailure>,
    ) -> Result<T, StorageFailure> {
        if self.state.replace(RefusalState::Entered) != RefusalState::Ready {
            self.state.set(RefusalState::Refused);
            return Err(StorageFailure::Unsafe);
        }
        match operation() {
            Ok(result) if self.state.get() == RefusalState::Entered => {
                self.state.set(RefusalState::Ready);
                Ok(result)
            }
            Ok(_) => {
                self.state.set(RefusalState::Refused);
                Err(StorageFailure::Unsafe)
            }
            Err(failure) => {
                self.state.set(RefusalState::Refused);
                Err(failure)
            }
        }
    }
}

impl Default for Refusal {
    fn default() -> Self {
        Self::new()
    }
}

/// None is the retained leaf; Some(index) is a retained directory. Every
/// construction, including a retry, flushes the leaf, all namespace ancestors
/// bottom-up through the supplied parent, then the leaf again.
pub(crate) fn constructor_flush(
    base_index: usize,
    directory_count: usize,
    mut flush: impl FnMut(Option<usize>) -> Result<(), StorageFailure>,
) -> Result<(), StorageFailure> {
    if directory_count == 0
        || directory_count > crate::format::MAX_COMPONENTS + 1
        || base_index >= directory_count
    {
        return Err(StorageFailure::Unsafe);
    }
    flush(None)?;
    for index in (base_index..directory_count).rev() {
        flush(Some(index))?;
    }
    flush(None)
}

#[cfg(test)]
mod tests {
    use super::*;

    const UID: u32 = 501;

    fn home(buffer: &[u8], offset: usize) -> Result<&[u8], StorageFailure> {
        let base = buffer.as_ptr() as usize;
        bounded_c_string(buffer, base + offset, base)
    }

    fn inode(kind: InodeKind, mode: u32) -> InodeObservation {
        InodeObservation {
            kind,
            uid: UID,
            mode,
            links: 1,
            size: 0,
        }
    }

    #[test]
    fn native_home_is_bounded_by_the_initialized_native_buffer() {
        let buffer = b"unrelated\0/Users/example\0unused";
        assert_eq!(home(buffer, 10), Ok(b"/Users/example".as_slice()));
        let base = buffer.as_ptr() as usize;
        for address in [base - 1, base + buffer.len(), usize::MAX] {
            assert_eq!(
                bounded_c_string(buffer, address, base),
                Err(StorageFailure::Unsafe)
            );
        }
        assert_eq!(
            bounded_c_string(buffer, base + 10, base + 1),
            Err(StorageFailure::Unsafe)
        );
        assert_eq!(home(b"/Users/example", 0), Err(StorageFailure::Unsafe));
        assert_eq!(home(b"\0", 0), Err(StorageFailure::Unsafe));
        assert_eq!(home(b"/Users/\xff\0", 0), Err(StorageFailure::Unsafe));
        let oversized = vec![0; MAX_NATIVE_HOME_BUFFER_BYTES + 1];
        assert_eq!(home(&oversized, 0), Err(StorageFailure::Unsafe));
    }

    #[test]
    fn native_home_rejects_lexical_redirection_without_normalization() {
        for buffer in [
            b"Users/example\0".as_slice(),
            b"/Users/../example\0".as_slice(),
            b"/Users/./example\0".as_slice(),
            b"/Users//example\0".as_slice(),
            b"/Users/example/\0".as_slice(),
        ] {
            assert_eq!(home(buffer, 0), Err(StorageFailure::Unsafe));
        }
    }

    #[test]
    fn fixed_route_must_fit_path_and_component_limits() {
        let suffix_len = NAMESPACE_DIRECTORIES
            .iter()
            .map(|name| name.len() + 1)
            .sum::<usize>()
            + JOURNAL_LEAF.len()
            + 1;
        let target_home_len = crate::format::MAX_PATH_BYTES - suffix_len;
        let mut longest = vec![b'/'];
        while target_home_len - longest.len() > 255 {
            longest.extend(std::iter::repeat_n(b'a', 254));
            longest.push(b'/');
        }
        longest.extend(std::iter::repeat_n(b'a', target_home_len - longest.len()));
        let mut terminated = longest.clone();
        terminated.push(0);
        assert_eq!(home(&terminated, 0), Ok(longest.as_slice()));
        terminated.insert(terminated.len() - 1, b'a');
        assert_eq!(home(&terminated, 0), Err(StorageFailure::Unsafe));

        let home_components = crate::format::MAX_COMPONENTS - NAMESPACE_DIRECTORIES.len() - 1;
        let mut components = b"/a".repeat(home_components);
        components.push(0);
        assert!(home(&components, 0).is_ok());
        components.splice(components.len() - 1..components.len() - 1, *b"/a");
        assert_eq!(home(&components, 0), Err(StorageFailure::Unsafe));
    }

    #[test]
    fn only_ordinary_unmodified_process_credentials_are_accepted() {
        let permitted = (UID, UID, 20, 20, false, u32::MAX, u32::MAX);
        assert_eq!(
            ordinary_credentials(
                permitted.0,
                permitted.1,
                permitted.2,
                permitted.3,
                permitted.4,
                permitted.5,
                permitted.6
            ),
            Ok(())
        );
        for rejected in [
            (0, 0, 20, 20, false, u32::MAX, u32::MAX),
            (UID, 0, 20, 20, false, u32::MAX, u32::MAX),
            (UID, UID + 1, 20, 20, false, u32::MAX, u32::MAX),
            (UID, UID, 20, 21, false, u32::MAX, u32::MAX),
            (UID, UID, 0, 0, false, u32::MAX, u32::MAX),
            (u32::MAX, u32::MAX, 20, 20, false, u32::MAX, u32::MAX),
            (UID, UID, u32::MAX, u32::MAX, false, u32::MAX, u32::MAX),
            (UID, UID, 20, 20, true, u32::MAX, u32::MAX),
            (UID, UID, 20, 20, false, UID, u32::MAX),
            (UID, UID, 20, 20, false, u32::MAX, 20),
        ] {
            assert_eq!(
                ordinary_credentials(
                    rejected.0, rejected.1, rejected.2, rejected.3, rejected.4, rejected.5,
                    rejected.6
                ),
                Err(StorageFailure::Unsafe)
            );
        }
    }

    #[test]
    fn private_entries_require_exact_owner_modes_and_native_kinds() {
        let directory = inode(InodeKind::Directory, 0o700);
        let leaf = inode(InodeKind::RegularFile, 0o600);
        assert_eq!(private_directory(&directory, UID), Ok(()));
        assert_eq!(private_leaf(&leaf, UID), Ok(0));
        for mode in [0o000, 0o400, 0o640, 0o666, 0o1600, 0o2600, 0o4600] {
            assert_eq!(
                private_leaf(&InodeObservation { mode, ..leaf }, UID),
                Err(StorageFailure::Unsafe)
            );
        }
        for mode in [0o600, 0o750, 0o777, 0o1700, 0o2700, 0o4700] {
            assert_eq!(
                private_directory(&InodeObservation { mode, ..directory }, UID),
                Err(StorageFailure::Unsafe)
            );
        }
        for uid in [0, UID + 1, u32::MAX] {
            assert_eq!(
                private_leaf(&InodeObservation { uid, ..leaf }, UID),
                Err(StorageFailure::Unsafe)
            );
            assert_eq!(
                private_directory(&InodeObservation { uid, ..directory }, UID),
                Err(StorageFailure::Unsafe)
            );
        }
        for kind in [InodeKind::Directory, InodeKind::Other] {
            assert_eq!(
                private_leaf(&InodeObservation { kind, ..leaf }, UID),
                Err(StorageFailure::Unsafe)
            );
        }
        for kind in [InodeKind::RegularFile, InodeKind::Other] {
            assert_eq!(
                private_directory(&InodeObservation { kind, ..directory }, UID),
                Err(StorageFailure::Unsafe)
            );
        }
        for links in [0, 2, u64::MAX] {
            assert_eq!(
                private_leaf(&InodeObservation { links, ..leaf }, UID),
                Err(StorageFailure::Unsafe)
            );
        }
        assert_eq!(
            private_directory(
                &InodeObservation {
                    links: 0,
                    ..directory
                },
                UID
            ),
            Err(StorageFailure::Unsafe)
        );
        for uid in [0, u32::MAX] {
            assert_eq!(
                private_leaf(&InodeObservation { uid, ..leaf }, uid),
                Err(StorageFailure::Unsafe)
            );
        }
    }

    #[test]
    fn acl_absence_must_be_observed_and_never_inferred() {
        assert_eq!(no_extended_acl(Some(0)), Ok(()));
        for entries in [None, Some(1), Some(usize::MAX)] {
            assert_eq!(no_extended_acl(entries), Err(StorageFailure::Unsafe));
        }
    }

    #[test]
    fn ancestors_allow_only_observed_deny_acl_tags() {
        assert_eq!(ancestor_acl_tag(Some(2)), Ok(()));
        for tag in [
            None,
            Some(0),
            Some(1),
            Some(-1),
            Some(i32::MIN),
            Some(i32::MAX),
        ] {
            assert_eq!(ancestor_acl_tag(tag), Err(StorageFailure::Unsafe));
        }
        assert_eq!(no_extended_acl(Some(1)), Err(StorageFailure::Unsafe));
    }

    #[test]
    fn file_lengths_and_seek_offsets_have_checked_closed_bounds() {
        assert_eq!(bounded_length(0), Ok(0));
        assert_eq!(
            bounded_length(MAX_JOURNAL_BYTES as i64),
            Ok(MAX_JOURNAL_BYTES)
        );
        for length in [-1, MAX_JOURNAL_BYTES as i64 + 1, i64::MAX] {
            assert_eq!(bounded_length(length), Err(StorageFailure::Unsafe));
        }
        assert_eq!(checked_seek(2, 5, SeekFrom::Current(-2)), Ok(0));
        assert_eq!(checked_seek(2, 5, SeekFrom::End(-1)), Ok(4));
        assert_eq!(
            checked_seek(0, 0, SeekFrom::Start(MAX_JOURNAL_BYTES)),
            Ok(MAX_JOURNAL_BYTES)
        );
        for seek in [
            SeekFrom::Start(u64::MAX),
            SeekFrom::Current(i64::MIN),
            SeekFrom::Current(i64::MAX),
            SeekFrom::End(-6),
            SeekFrom::End(i64::MAX),
        ] {
            assert_eq!(checked_seek(2, 5, seek), Err(StorageFailure::Unsafe));
        }
        assert_eq!(
            checked_seek(u64::MAX, 5, SeekFrom::Start(0)),
            Err(StorageFailure::Unsafe)
        );
        assert_eq!(
            checked_seek(0, u64::MAX, SeekFrom::Start(0)),
            Err(StorageFailure::Unsafe)
        );
        assert_eq!(
            checked_write_end(MAX_JOURNAL_BYTES - 2, 2),
            Ok(MAX_JOURNAL_BYTES)
        );
        assert_eq!(
            checked_write_end(MAX_JOURNAL_BYTES, 1),
            Err(StorageFailure::Unsafe)
        );
        assert_eq!(checked_write_end(u64::MAX, 1), Err(StorageFailure::Unsafe));
        assert_eq!(
            checked_write_end(0, usize::MAX),
            Err(StorageFailure::Unsafe)
        );
        let leaf = inode(InodeKind::RegularFile, 0o600);
        assert_eq!(
            private_leaf(&InodeObservation { size: -1, ..leaf }, UID),
            Err(StorageFailure::Unsafe)
        );
        assert_eq!(
            private_leaf(
                &InodeObservation {
                    size: MAX_JOURNAL_BYTES as i64 + 1,
                    ..leaf
                },
                UID
            ),
            Err(StorageFailure::Unsafe)
        );
    }

    #[test]
    fn reservation_blocks_other_threads_until_observed_drop() {
        static OCCUPIED: AtomicBool = AtomicBool::new(false);
        let owner = Reservation::acquire(&OCCUPIED).expect("first reservation");
        assert_eq!(
            std::thread::spawn(|| Reservation::acquire(&OCCUPIED).err())
                .join()
                .unwrap(),
            Some(StorageFailure::Busy)
        );
        assert!(OCCUPIED.load(Ordering::Acquire));
        drop(owner);
        assert!(!OCCUPIED.load(Ordering::Acquire));
        assert!(
            std::thread::spawn(|| Reservation::acquire(&OCCUPIED).is_ok())
                .join()
                .unwrap()
        );
        assert!(!OCCUPIED.load(Ordering::Acquire));
    }

    #[test]
    fn failed_reservation_does_not_release_the_existing_owner() {
        static OCCUPIED: AtomicBool = AtomicBool::new(false);
        let owner = Reservation::acquire(&OCCUPIED).unwrap();
        assert_eq!(
            Reservation::acquire(&OCCUPIED).err(),
            Some(StorageFailure::Busy)
        );
        assert!(OCCUPIED.load(Ordering::Acquire));
        drop(owner);
        assert!(!OCCUPIED.load(Ordering::Acquire));
    }

    #[test]
    fn refusal_releases_only_successful_operations() {
        let refusal = Refusal::new();
        assert_eq!(refusal.run(|| Ok(42)), Ok(42));
        assert!(!refusal.is_refused());
        assert_eq!(
            refusal.run::<()>(|| Err(StorageFailure::Unavailable)),
            Err(StorageFailure::Unavailable)
        );
        assert!(refusal.is_refused());
        let called = Cell::new(false);
        assert_eq!(
            refusal.run(|| {
                called.set(true);
                Ok(())
            }),
            Err(StorageFailure::Unsafe)
        );
        assert!(!called.get());
    }

    #[test]
    fn refusal_remains_latched_after_unwind_or_reentrant_admission() {
        let refusal = Refusal::new();
        let unwind = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let _ = refusal.run::<()>(|| panic!("simulated native operation unwind"));
        }));
        assert!(unwind.is_err());
        assert!(refusal.is_refused());
        assert_eq!(refusal.run(|| Ok(())), Err(StorageFailure::Unsafe));

        let refusal = Refusal::new();
        assert_eq!(
            refusal.run(|| {
                assert_eq!(refusal.run(|| Ok(())), Err(StorageFailure::Unsafe));
                Ok(())
            }),
            Err(StorageFailure::Unsafe)
        );
        assert!(refusal.is_refused());
    }

    #[test]
    fn constructor_flush_always_runs_the_complete_order_including_reopen() {
        let expected = vec![None, Some(4), Some(3), Some(2), None];
        for _ in 0..2 {
            let mut events = Vec::new();
            assert_eq!(
                constructor_flush(2, 5, |event| {
                    events.push(event);
                    Ok(())
                }),
                Ok(())
            );
            assert_eq!(events, expected);
        }
        let mut events = Vec::new();
        assert_eq!(
            constructor_flush(0, 1, |event| {
                events.push(event);
                Ok(())
            }),
            Ok(())
        );
        assert_eq!(events, [None, Some(0), None]);
    }

    #[test]
    fn constructor_fault_stops_and_retry_starts_at_the_first_leaf_flush() {
        let expected = [None, Some(4), Some(3), Some(2), None];
        for failure_at in 0..expected.len() {
            let mut events = Vec::new();
            assert_eq!(
                constructor_flush(2, 5, |event| {
                    events.push(event);
                    if events.len() - 1 == failure_at {
                        Err(StorageFailure::Unavailable)
                    } else {
                        Ok(())
                    }
                }),
                Err(StorageFailure::Unavailable)
            );
            assert_eq!(events, expected[..=failure_at]);
            events.clear();
            assert_eq!(
                constructor_flush(2, 5, |event| {
                    events.push(event);
                    Ok(())
                }),
                Ok(())
            );
            assert_eq!(events, expected);
        }
    }

    #[test]
    fn invalid_flush_bounds_never_call_native_flush() {
        for (base, count) in [(0, 0), (2, 2), (3, 2), (0, usize::MAX)] {
            let called = Cell::new(false);
            assert_eq!(
                constructor_flush(base, count, |_| {
                    called.set(true);
                    Ok(())
                }),
                Err(StorageFailure::Unsafe)
            );
            assert!(!called.get());
        }
    }
}
