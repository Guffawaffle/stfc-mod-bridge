//! Faults at the retained storage boundary, not the operation-owner mock port.
use bridge_contracts::v1::{HostEpoch, StreamId};
use bridge_engine::operations::{DurableJournal, FileJournal, JournalRecord, KernelFailure};
use bridge_journal_io::{JournalStorage, StorageFailure};
use std::{
    cell::{Cell, RefCell},
    fs::{File, OpenOptions},
    io::{self, Read, Seek, SeekFrom, Write},
    path::PathBuf,
    rc::Rc,
    sync::atomic::{AtomicU64, Ordering},
};

const MAGIC: &[u8] = b"BRJWAL01";
const MAX_JOURNAL: u64 = 128 * 1024 * 1024;

#[derive(Default)]
struct State {
    bytes: Vec<u8>,
    position: u64,
    reads: u64,
    read_bytes: u64,
    writes: u64,
    syncs: u64,
    truncates: u64,
    validations: u64,
    drops: u64,
    chunk: Option<usize>,
    interrupt_read: bool,
    interrupt_write: bool,
    write_remaining: Option<usize>,
    fail_custody: bool,
    fail_post_write_custody: bool,
    fail_sync: bool,
    fail_truncate: bool,
    reported_length: Option<u64>,
    generated_length: Option<u64>,
    grow_on_eof: bool,
    lose_on_eof: bool,
    events: Vec<&'static str>,
}

struct MemoryOwner(Rc<RefCell<State>>);
fn memory(bytes: Vec<u8>) -> (MemoryOwner, Rc<RefCell<State>>) {
    let state = Rc::new(RefCell::new(State {
        bytes,
        ..State::default()
    }));
    (MemoryOwner(state.clone()), state)
}
impl Drop for MemoryOwner {
    fn drop(&mut self) {
        self.0.borrow_mut().drops += 1;
    }
}
impl Read for MemoryOwner {
    fn read(&mut self, output: &mut [u8]) -> io::Result<usize> {
        let mut state = self.0.borrow_mut();
        state.reads += 1;
        if std::mem::take(&mut state.interrupt_read) {
            return Err(io::ErrorKind::Interrupted.into());
        }
        let length = state.generated_length.unwrap_or(state.bytes.len() as u64);
        let remaining = length.saturating_sub(state.position);
        let count = output
            .len()
            .min(state.chunk.unwrap_or(usize::MAX))
            .min(usize::try_from(remaining).unwrap());
        for (index, byte) in output[..count].iter_mut().enumerate() {
            *byte = state
                .bytes
                .get(state.position as usize + index)
                .copied()
                .unwrap_or(0);
        }
        state.position += count as u64;
        state.read_bytes += count as u64;
        if count == 0 && std::mem::take(&mut state.grow_on_eof) {
            state.bytes.push(1);
        }
        if count == 0 && std::mem::take(&mut state.lose_on_eof) {
            state.fail_custody = true;
        }
        Ok(count)
    }
}
impl Write for MemoryOwner {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        let mut state = self.0.borrow_mut();
        state.writes += 1;
        state.events.push("write");
        if std::mem::take(&mut state.interrupt_write) {
            return Err(io::ErrorKind::Interrupted.into());
        }
        if state.write_remaining == Some(0) {
            return Err(io::ErrorKind::Other.into());
        }
        let count = bytes
            .len()
            .min(state.chunk.unwrap_or(usize::MAX))
            .min(state.write_remaining.unwrap_or(usize::MAX));
        let position = usize::try_from(state.position).unwrap();
        let end = position.checked_add(count).unwrap();
        let new_length = state.bytes.len().max(end);
        state.bytes.resize(new_length, 0);
        state.bytes[position..end].copy_from_slice(&bytes[..count]);
        state.position = end as u64;
        if let Some(remaining) = &mut state.write_remaining {
            *remaining -= count;
        }
        Ok(count)
    }
    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}
impl Seek for MemoryOwner {
    fn seek(&mut self, position: SeekFrom) -> io::Result<u64> {
        let mut state = self.0.borrow_mut();
        state.events.push("seek");
        let position = match position {
            SeekFrom::Start(value) => i128::from(value),
            SeekFrom::Current(value) => i128::from(state.position) + i128::from(value),
            SeekFrom::End(value) => state.bytes.len() as i128 + i128::from(value),
        };
        state.position = u64::try_from(position).map_err(|_| io::ErrorKind::InvalidInput)?;
        Ok(state.position)
    }
}
impl JournalStorage for MemoryOwner {
    fn validate_custody(&self) -> Result<u64, StorageFailure> {
        let mut state = self.0.borrow_mut();
        state.validations += 1;
        state.events.push("validate");
        if state.fail_custody || (state.fail_post_write_custody && state.writes > 0) {
            return Err(StorageFailure::Unsafe);
        }
        Ok(state.reported_length.unwrap_or(state.bytes.len() as u64))
    }
    fn truncate(&mut self, length: u64) -> Result<(), StorageFailure> {
        let mut state = self.0.borrow_mut();
        state.truncates += 1;
        state.events.push("truncate");
        if state.fail_truncate {
            return Err(StorageFailure::Unavailable);
        }
        state.bytes.truncate(usize::try_from(length).unwrap());
        Ok(())
    }
    fn sync_durable(&mut self) -> Result<(), StorageFailure> {
        let mut state = self.0.borrow_mut();
        state.syncs += 1;
        state.events.push("sync");
        if state.fail_sync {
            return Err(StorageFailure::Unavailable);
        }
        Ok(())
    }
}

fn record() -> JournalRecord {
    JournalRecord::Host {
        epoch: HostEpoch::new("00000000-0000-4000-8000-000000000001").unwrap(),
        stream: StreamId::new("00000000-0000-4000-8000-000000000002").unwrap(),
    }
}

#[test]
fn retained_short_and_interrupted_io_roundtrips_only_after_sync_and_validation() {
    let (owner, state) = memory(Vec::new());
    {
        let mut s = state.borrow_mut();
        s.chunk = Some(3);
        s.interrupt_read = true;
        s.interrupt_write = true;
    }
    let mut journal = FileJournal::open_retained(owner).unwrap();
    assert_eq!(state.borrow().bytes, MAGIC);
    state.borrow_mut().events.clear();
    journal.append(&record()).unwrap();
    journal.append(&record()).unwrap();
    assert_eq!(journal.records(), &[record(), record()]);
    let events = state.borrow().events.clone();
    assert_eq!(events.last(), Some(&"validate"));
    assert!(events.iter().position(|event| *event == "sync").unwrap() < events.len() - 1);
    assert_eq!(state.borrow().drops, 0);
    drop(journal);
    assert_eq!(state.borrow().drops, 1);
    let (owner, _) = memory(state.borrow().bytes.clone());
    assert_eq!(
        FileJournal::open_retained(owner).unwrap().records(),
        &[record(), record()]
    );
}

#[test]
fn oversized_or_growing_read_is_bounded_and_never_repaired() {
    let (owner, state) = memory(MAGIC.to_vec());
    state.borrow_mut().reported_length = Some(MAX_JOURNAL + 1);
    assert_eq!(
        FileJournal::open_retained(owner).err(),
        Some(KernelFailure::UnsafeJournal)
    );
    assert_eq!(state.borrow().reads, 0);
    let (owner, state) = memory(MAGIC.to_vec());
    state.borrow_mut().generated_length = Some(MAX_JOURNAL + 100);
    assert_eq!(
        FileJournal::open_retained(owner).err(),
        Some(KernelFailure::UnsafeJournal)
    );
    assert_eq!(state.borrow().read_bytes, MAX_JOURNAL + 1);
    assert_eq!(
        (
            state.borrow().writes,
            state.borrow().truncates,
            state.borrow().syncs
        ),
        (0, 0, 0)
    );
}

#[test]
fn changing_length_or_custody_during_read_blocks_parsing_and_repair() {
    for mode in [0, 1, 2] {
        let (owner, state) = memory(MAGIC.to_vec());
        {
            let mut s = state.borrow_mut();
            match mode {
                0 => s.reported_length = Some(9),
                1 => s.grow_on_eof = true,
                _ => s.lose_on_eof = true,
            }
        }
        assert_eq!(
            FileJournal::open_retained(owner).err(),
            Some(KernelFailure::UnsafeJournal)
        );
        assert_eq!(
            (
                state.borrow().writes,
                state.borrow().truncates,
                state.borrow().syncs
            ),
            (0, 0, 0)
        );
    }
}

#[test]
fn partial_initial_header_never_constructs_a_usable_journal_or_gets_repaired() {
    let (owner, state) = memory(Vec::new());
    state.borrow_mut().write_remaining = Some(3);
    assert_eq!(
        FileJournal::open_retained(owner).err(),
        Some(KernelFailure::Storage)
    );
    assert_eq!(state.borrow().bytes, &MAGIC[..3]);
    assert_eq!(state.borrow().drops, 1);
    let (owner, state) = memory(state.borrow().bytes.clone());
    assert_eq!(
        FileJournal::open_retained(owner).err(),
        Some(KernelFailure::CorruptJournal)
    );
    assert_eq!(
        (
            state.borrow().writes,
            state.borrow().truncates,
            state.borrow().syncs
        ),
        (0, 0, 0)
    );
}

#[test]
fn partial_append_failure_poison_retains_owner_without_publishing_a_record() {
    let (owner, state) = memory(MAGIC.to_vec());
    let mut journal = FileJournal::open_retained(owner).unwrap();
    state.borrow_mut().write_remaining = Some(5);
    assert_eq!(journal.append(&record()), Err(KernelFailure::Storage));
    assert!(journal.records().is_empty());
    assert_eq!(state.borrow().bytes.len(), MAGIC.len() + 5);
    let events = state.borrow().events.clone();
    assert_eq!(journal.append(&record()), Err(KernelFailure::Poisoned));
    assert_eq!(state.borrow().events, events);
    assert_eq!(state.borrow().drops, 0);
    drop(journal);
    assert_eq!(state.borrow().drops, 1);
    let (owner, reopened) = memory(state.borrow().bytes.clone());
    assert!(
        FileJournal::open_retained(owner)
            .unwrap()
            .records()
            .is_empty()
    );
    assert_eq!(reopened.borrow().bytes, MAGIC);
    assert_eq!(reopened.borrow().syncs, 1);
}

#[test]
fn unacknowledged_complete_frame_survives_sync_or_post_write_custody_failure() {
    for sync_failure in [true, false] {
        let (owner, state) = memory(MAGIC.to_vec());
        let mut journal = FileJournal::open_retained(owner).unwrap();
        {
            let mut s = state.borrow_mut();
            s.fail_sync = sync_failure;
            s.fail_post_write_custody = !sync_failure;
        }
        let expected = if sync_failure {
            KernelFailure::Storage
        } else {
            KernelFailure::UnsafeJournal
        };
        assert_eq!(journal.append(&record()), Err(expected));
        assert!(journal.records().is_empty());
        let events = state.borrow().events.clone();
        assert_eq!(journal.append(&record()), Err(KernelFailure::Poisoned));
        assert_eq!(state.borrow().events, events);
        assert_eq!(state.borrow().drops, 0);
        drop(journal);
        let (owner, reopened) = memory(state.borrow().bytes.clone());
        assert_eq!(
            FileJournal::open_retained(owner).unwrap().records(),
            &[record()]
        );
        assert_eq!(reopened.borrow().truncates, 0);
    }
}

#[test]
fn pre_append_custody_and_length_loss_poison_without_writing() {
    for custody_failure in [true, false] {
        let (owner, state) = memory(MAGIC.to_vec());
        let mut journal = FileJournal::open_retained(owner).unwrap();
        {
            let mut s = state.borrow_mut();
            s.fail_custody = custody_failure;
            if !custody_failure {
                s.bytes.push(1);
            }
        }
        let original = state.borrow().bytes.clone();
        assert_eq!(journal.append(&record()), Err(KernelFailure::UnsafeJournal));
        assert_eq!(state.borrow().bytes, original);
        assert_eq!((state.borrow().writes, state.borrow().syncs), (0, 0));
        assert_eq!(journal.append(&record()), Err(KernelFailure::Poisoned));
        assert_eq!(state.borrow().drops, 0);
    }
}

#[test]
fn failed_tail_truncate_or_sync_never_constructs_a_usable_journal() {
    for truncate_failure in [true, false] {
        let mut bytes = MAGIC.to_vec();
        bytes.extend_from_slice(&[1, 2, 3]);
        let (owner, state) = memory(bytes);
        {
            let mut s = state.borrow_mut();
            s.fail_truncate = truncate_failure;
            s.fail_sync = !truncate_failure;
        }
        assert_eq!(
            FileJournal::open_retained(owner).err(),
            Some(KernelFailure::Storage)
        );
        assert_eq!(state.borrow().truncates, 1);
        assert_eq!(state.borrow().drops, 1);
    }
}

#[test]
fn complete_corruption_remains_untouched_through_retained_storage() {
    let (owner, state) = memory(MAGIC.to_vec());
    let mut journal = FileJournal::open_retained(owner).unwrap();
    journal.append(&record()).unwrap();
    drop(journal);
    let mut bytes = state.borrow().bytes.clone();
    *bytes.last_mut().unwrap() ^= 1;
    let (owner, state) = memory(bytes.clone());
    assert_eq!(
        FileJournal::open_retained(owner).err(),
        Some(KernelFailure::CorruptJournal)
    );
    assert_eq!(state.borrow().bytes, bytes);
    assert_eq!(
        (
            state.borrow().writes,
            state.borrow().truncates,
            state.borrow().syncs
        ),
        (0, 0, 0)
    );
}

struct LockedOwner {
    file: File,
    lose_custody: Rc<Cell<bool>>,
}
impl Read for LockedOwner {
    fn read(&mut self, bytes: &mut [u8]) -> io::Result<usize> {
        self.file.read(bytes)
    }
}
impl Write for LockedOwner {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        self.file.write(bytes)
    }
    fn flush(&mut self) -> io::Result<()> {
        self.file.flush()
    }
}
impl Seek for LockedOwner {
    fn seek(&mut self, position: SeekFrom) -> io::Result<u64> {
        self.file.seek(position)
    }
}
impl JournalStorage for LockedOwner {
    fn validate_custody(&self) -> Result<u64, StorageFailure> {
        if self.lose_custody.get() {
            return Err(StorageFailure::Unsafe);
        }
        self.file
            .metadata()
            .map(|m| m.len())
            .map_err(|_| StorageFailure::Unavailable)
    }
    fn truncate(&mut self, length: u64) -> Result<(), StorageFailure> {
        self.file
            .set_len(length)
            .map_err(|_| StorageFailure::Unavailable)
    }
    fn sync_durable(&mut self) -> Result<(), StorageFailure> {
        self.file
            .sync_all()
            .map_err(|_| StorageFailure::Unavailable)
    }
}
struct Fixture(PathBuf);
impl Fixture {
    fn new() -> Self {
        static NEXT: AtomicU64 = AtomicU64::new(0);
        let parent = std::fs::canonicalize(std::env::temp_dir()).unwrap();
        let root = parent.join(format!(
            "bridge-retained-wal-{}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        std::fs::create_dir(&root).unwrap();
        Self(std::fs::canonicalize(root).unwrap())
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let parent = std::fs::canonicalize(std::env::temp_dir()).unwrap();
        let actual = std::fs::canonicalize(&self.0).unwrap();
        assert_eq!(actual.parent(), Some(parent.as_path()));
        assert!(
            actual
                .file_name()
                .unwrap()
                .to_str()
                .unwrap()
                .starts_with("bridge-retained-wal-")
        );
        std::fs::remove_dir_all(actual).unwrap();
    }
}

#[test]
fn retained_file_poison_keeps_actual_lock_until_drop_and_preserves_legacy_codec() {
    let fixture = Fixture::new();
    let path = fixture.0.join("journal.wal");
    let file = OpenOptions::new()
        .read(true)
        .write(true)
        .create_new(true)
        .open(&path)
        .unwrap();
    file.try_lock().unwrap();
    let lose_custody = Rc::new(Cell::new(false));
    let mut journal = FileJournal::open_retained(LockedOwner {
        file,
        lose_custody: lose_custody.clone(),
    })
    .unwrap();
    journal.append(&record()).unwrap();
    lose_custody.set(true);
    assert_eq!(journal.append(&record()), Err(KernelFailure::UnsafeJournal));
    let competitor = OpenOptions::new()
        .read(true)
        .write(true)
        .open(&path)
        .unwrap();
    assert!(competitor.try_lock().is_err());
    drop(journal);
    competitor.try_lock().unwrap();
    drop(competitor);
    let mut legacy = FileJournal::open_existing(&path).unwrap();
    assert_eq!(legacy.records(), &[record()]);
    legacy.append(&record()).unwrap();
    drop(legacy);
    let file = OpenOptions::new()
        .read(true)
        .write(true)
        .open(&path)
        .unwrap();
    file.try_lock().unwrap();
    let retained = FileJournal::open_retained(LockedOwner {
        file,
        lose_custody: Rc::new(Cell::new(false)),
    })
    .unwrap();
    assert_eq!(retained.records(), &[record(), record()]);
    drop(retained);
}
