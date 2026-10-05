//! Ignored fixtures under the harness-owned native private APFS parent. Each
//! nonce tree is retained for inspection; these tests provide no evidence until
//! the selected native suite actually executes with candidate-bound receipts.
use super::{NativePrivateJournalStorage, credentials, policy, stat};
use bridge_journal_io::{JournalStorage, StorageFailure};
use std::{
    ffi::OsString,
    fs,
    io::{Read, Seek, SeekFrom, Write},
    os::{
        fd::AsRawFd,
        unix::{
            ffi::OsStringExt,
            fs::{DirBuilderExt, MetadataExt, OpenOptionsExt},
        },
    },
    path::PathBuf,
    sync::{Mutex, MutexGuard},
};

// These fixtures share the production process reservation. Serializing their
// execution avoids interpreting another selected fixture as native lock drift.
static FIXTURES: Mutex<()> = Mutex::new(());

fn serial() -> MutexGuard<'static, ()> {
    FIXTURES
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

struct Fixture {
    component: String,
}

impl Fixture {
    fn new() -> Self {
        let mut nonce = [0_u8; 16];
        // SAFETY: getentropy's native 256-byte limit exceeds this exact live
        // writable buffer. There is no PID, time, path or environment fallback.
        assert_eq!(
            unsafe { libc::getentropy(nonce.as_mut_ptr().cast(), nonce.len()) },
            0
        );
        assert_ne!(nonce, [0; 16], "refuse a degenerate fixture nonce");
        let encoded = crate::format::hex(&nonce);
        assert_eq!(encoded.len(), 32);
        assert!(
            encoded
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        );
        let component = format!(".bridge-br07-journal-{encoded}");
        crate::format::validate_leaf(component.as_bytes()).unwrap();
        Self { component }
    }

    fn open(
        &self,
        fail_after_flush: Option<usize>,
    ) -> Result<NativePrivateJournalStorage, StorageFailure> {
        // The private cfg(test) constructor selects and validates only the
        // existing BRIDGE_MACOS_NATIVE_FIXTURE_ROOT harness parent. No caller
        // supplies or substitutes a path through this fixture API.
        NativePrivateJournalStorage::construct_fixture(
            &[self.component.as_bytes(), b"v1".as_slice()],
            fail_after_flush,
        )
    }

    fn private_leaf_path(&self, owner: &NativePrivateJournalStorage) -> PathBuf {
        assert!(credentials().unwrap() == owner.custody.borrow().selected);
        let custody = owner.custody.borrow();
        let leaf = stat(&custody.leaf).unwrap();
        assert_eq!(leaf.st_uid, custody.selected.uid);
        assert_eq!(leaf.st_mode & libc::S_IFMT, libc::S_IFREG);
        assert_eq!(leaf.st_mode & 0o7777, 0o600);
        assert_eq!(leaf.st_nlink, 1);
        assert_eq!(
            policy::bounded_length(leaf.st_size).unwrap(),
            custody.expected_len
        );
        super::acl(&custody.leaf, true).unwrap();
        for row in custody.directories.iter().skip(custody.namespace_parent) {
            let observed = stat(&row.file).unwrap();
            assert_eq!(observed.st_uid, custody.selected.uid);
            assert_eq!(observed.st_mode & libc::S_IFMT, libc::S_IFDIR);
            assert_eq!(observed.st_mode & 0o7777, 0o700);
            assert_ne!(observed.st_nlink, 0);
            super::acl(&row.file, true).unwrap();
        }
        // Mutation paths are derived solely from the successfully retained
        // owner's observed route, then confined to this exact nonce subtree.
        let path = PathBuf::from(OsString::from_vec(custody.leaf_path.clone()));
        assert_eq!(path.file_name().unwrap(), "operations.wal");
        let version = path.parent().unwrap();
        assert_eq!(version.file_name().unwrap(), "v1");
        assert_eq!(
            version.parent().unwrap().file_name().unwrap(),
            self.component.as_str()
        );
        eprintln!("retained native journal fixture: {}", path.display());
        path
    }
}

#[test]
#[ignore = "real native Apple Silicon; harness-owned private APFS fixture parent; retain nonce tree"]
fn native_private_journal_roundtrip_reopen_and_refused_owner_reservation() {
    let _serial = serial();
    let fixture = Fixture::new();
    let mut owner = fixture
        .open(None)
        .expect("fresh native fixture construction");
    assert_eq!(owner.validate_custody(), Ok(0));
    let path = fixture.private_leaf_path(&owner);
    let payload: &[u8] = b"native retained journal roundtrip";
    owner.write_all(payload).unwrap();
    owner.sync_durable().unwrap();
    assert_eq!(owner.validate_custody(), Ok(payload.len() as u64));
    owner
        .seek(SeekFrom::Start(payload.len() as u64 + 3))
        .unwrap();
    assert_eq!(owner.write(&[]).unwrap(), 0);
    assert_eq!(owner.validate_custody(), Ok(payload.len() as u64));
    assert_eq!(owner.seek(SeekFrom::Start(0)).unwrap(), 0);
    let mut observed = vec![0; payload.len()];
    owner.read_exact(&mut observed).unwrap();
    assert_eq!(observed.as_slice(), payload);
    owner.truncate(9).unwrap();
    owner.sync_durable().unwrap();
    owner.seek(SeekFrom::Start(0)).unwrap();
    observed.clear();
    owner.read_to_end(&mut observed).unwrap();
    assert_eq!(observed.as_slice(), &payload[..9]);
    assert_eq!(fixture.open(None).err(), Some(StorageFailure::Busy));
    drop(owner);

    let mut reopened = fixture
        .open(None)
        .expect("durable fixture reopen after observed drop");
    assert_eq!(reopened.validate_custody(), Ok(9));
    assert_eq!(fixture.private_leaf_path(&reopened), path);
    let before = fs::symlink_metadata(&path).unwrap();
    observed.clear();
    reopened.read_to_end(&mut observed).unwrap();
    assert_eq!(observed.as_slice(), &payload[..9]);
    assert!(
        reopened
            .seek(SeekFrom::Start(policy::MAX_JOURNAL_BYTES + 1))
            .is_err()
    );
    assert_eq!(reopened.validate_custody(), Err(StorageFailure::Unsafe));
    assert!(reopened.read(&mut [0_u8; 1]).is_err());
    assert!(reopened.write(b"refused").is_err());
    assert_eq!(reopened.truncate(0), Err(StorageFailure::Unsafe));
    assert_eq!(reopened.sync_durable(), Err(StorageFailure::Unsafe));
    assert_eq!(fixture.open(None).err(), Some(StorageFailure::Busy));
    let after = fs::symlink_metadata(&path).unwrap();
    assert_eq!(
        (after.dev(), after.ino(), after.len()),
        (before.dev(), before.ino(), before.len())
    );
    drop(reopened);

    let mut final_owner = fixture
        .open(None)
        .expect("poisoned owner releases only on drop");
    assert_eq!(final_owner.validate_custody(), Ok(9));
    observed.clear();
    final_owner.read_to_end(&mut observed).unwrap();
    assert_eq!(observed.as_slice(), &payload[..9]);
}

#[test]
#[ignore = "real native Apple Silicon; owned descriptor privacy mutation; retain nonce tree"]
fn native_private_journal_mode_change_refuses_without_existing_permission_repair() {
    let _serial = serial();
    let fixture = Fixture::new();
    let mut owner = fixture.open(None).unwrap();
    let payload: &[u8] = b"preserved after native privacy refusal";
    owner.write_all(payload).unwrap();
    owner.sync_durable().unwrap();
    let path = fixture.private_leaf_path(&owner);
    let before = fs::symlink_metadata(&path).unwrap();
    {
        let custody = owner.custody.borrow();
        // SAFETY: exact test-owned retained leaf descriptor in this nonce tree;
        // deliberately widen its permissions to exercise custody refusal.
        assert_eq!(unsafe { libc::fchmod(custody.leaf.as_raw_fd(), 0o640) }, 0);
        assert_eq!(stat(&custody.leaf).unwrap().st_mode & 0o7777, 0o640);
    }
    assert_eq!(owner.validate_custody(), Err(StorageFailure::Unsafe));
    assert_eq!(owner.sync_durable(), Err(StorageFailure::Unsafe));
    assert!(owner.write(b"refused").is_err());
    drop(owner);
    assert_eq!(fixture.open(None).err(), Some(StorageFailure::Unsafe));
    let after = fs::symlink_metadata(&path).unwrap();
    assert_eq!(
        after.mode() & 0o7777,
        0o640,
        "reopen must not repair existing permissions"
    );
    assert_eq!(
        (after.dev(), after.ino(), after.len()),
        (before.dev(), before.ino(), before.len())
    );
    assert_eq!(fs::read(&path).unwrap(), payload);
}

#[test]
#[ignore = "real native Apple Silicon; exact nonce leaf hardlink mutation; retain both names"]
fn native_private_journal_multiple_links_refuse_without_unlinking() {
    let _serial = serial();
    let fixture = Fixture::new();
    let mut owner = fixture.open(None).unwrap();
    let payload: &[u8] = b"retained hardlink fixture bytes";
    owner.write_all(payload).unwrap();
    owner.sync_durable().unwrap();
    let path = fixture.private_leaf_path(&owner);
    let alias = path.parent().unwrap().join("retained-hardlink");
    fs::hard_link(&path, &alias).unwrap();
    let original = fs::symlink_metadata(&path).unwrap();
    let linked = fs::symlink_metadata(&alias).unwrap();
    assert_eq!(original.nlink(), 2);
    assert_eq!(
        (original.dev(), original.ino()),
        (linked.dev(), linked.ino())
    );
    assert_eq!(owner.validate_custody(), Err(StorageFailure::Unsafe));
    assert!(owner.read(&mut [0_u8; 1]).is_err());
    assert_eq!(owner.sync_durable(), Err(StorageFailure::Unsafe));
    drop(owner);
    assert_eq!(fixture.open(None).err(), Some(StorageFailure::Unsafe));
    assert_eq!(fs::symlink_metadata(&path).unwrap().nlink(), 2);
    assert_eq!(fs::symlink_metadata(&alias).unwrap().nlink(), 2);
    assert_eq!(fs::read(&path).unwrap(), payload);
    assert_eq!(fs::read(&alias).unwrap(), payload);
}

#[test]
#[ignore = "real native Apple Silicon; same-byte ancestor replacement; retain both directories"]
fn native_private_journal_same_byte_version_directory_replacement_refuses() {
    let _serial = serial();
    let fixture = Fixture::new();
    let mut owner = fixture.open(None).unwrap();
    let payload: &[u8] = b"same bytes do not restore retained physical custody";
    owner.write_all(payload).unwrap();
    owner.sync_durable().unwrap();
    let path = fixture.private_leaf_path(&owner);
    let before = fs::symlink_metadata(&path).unwrap();
    let version = path.parent().unwrap();
    let moved = version.parent().unwrap().join("retained-v1");
    fs::rename(version, &moved).unwrap();
    fs::DirBuilder::new().mode(0o700).create(version).unwrap();
    let mut replacement = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .mode(0o600)
        .open(&path)
        .unwrap();
    replacement.write_all(payload).unwrap();
    replacement.sync_all().unwrap();
    let after = fs::symlink_metadata(&path).unwrap();
    assert_eq!(
        fs::symlink_metadata(version).unwrap().mode() & 0o7777,
        0o700
    );
    assert_eq!(after.mode() & 0o7777, 0o600);
    assert_ne!((after.dev(), after.ino()), (before.dev(), before.ino()));
    assert_eq!(fs::read(&path).unwrap(), payload);
    assert_eq!(fs::read(moved.join("operations.wal")).unwrap(), payload);
    assert_eq!(owner.validate_custody(), Err(StorageFailure::Unsafe));
    assert!(owner.seek(SeekFrom::Start(0)).is_err());
    assert!(owner.write(b"refused").is_err());
    assert_eq!(owner.sync_durable(), Err(StorageFailure::Unsafe));
}

#[test]
#[ignore = "real native Apple Silicon; completed constructor flush faults and clean retries; retain all trees"]
fn native_private_journal_completed_constructor_flush_faults_retry_the_full_protocol() {
    let _serial = serial();
    // Two private directories plus the harness parent, bracketed by two leaf
    // flushes. Each fault is injected only after that native flush completes.
    for ordinal in 1..=5 {
        let fixture = Fixture::new();
        assert_eq!(
            fixture.open(Some(ordinal)).err(),
            Some(StorageFailure::Unavailable)
        );
        let mut retry = fixture
            .open(None)
            .expect("full constructor retry after completed flush fault");
        assert_eq!(retry.validate_custody(), Ok(0));
        let path = fixture.private_leaf_path(&retry);
        let payload = [ordinal as u8];
        retry.write_all(&payload).unwrap();
        retry.sync_durable().unwrap();
        drop(retry);
        let mut reopened = fixture
            .open(None)
            .expect("retry leaves a durably reopenable fixture");
        assert_eq!(fixture.private_leaf_path(&reopened), path);
        assert_eq!(reopened.validate_custody(), Ok(1));
        let mut observed = [0_u8; 1];
        reopened.read_exact(&mut observed).unwrap();
        assert_eq!(observed, payload);
    }
}
