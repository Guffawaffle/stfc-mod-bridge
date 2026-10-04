//! A bounded append-only engine journal. This is application transaction custody,
//! not a native catalog or replacement for the canonical owner's own journal.
use super::{KernelFailure, ResourceKey};
use bridge_contracts::v1::*;
use bridge_journal_io::{JournalStorage, StorageFailure};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    fs::{File, OpenOptions},
    io::{Read, Seek, SeekFrom, Write},
    marker::PhantomData,
    path::Path,
    rc::Rc,
};

const MAGIC: &[u8; 8] = b"BRJWAL01";
const MAX_RECORD: usize = 1024 * 1024;
const MAX_JOURNAL: u64 = 128 * 1024 * 1024;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum DurablePhase {
    Admitted,
    Executing,
    Terminal,
    Recovery,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct DurableOperation {
    pub commit: CommitInput,
    pub snapshot: OperationSnapshot,
    pub resources: Vec<ResourceKey>,
    pub recovery: RecoveryRef,
    pub phase: DurablePhase,
    pub cancellation_requested: bool,
    pub cancellable: bool,
    pub safe_owner_boundary: bool,
    pub session_custody: Option<SessionBinding>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", deny_unknown_fields)]
pub enum JournalRecord {
    Host { epoch: HostEpoch, stream: StreamId },
    Operation { value: Box<DurableOperation> },
}

/// One WAL codec over an opaque retained storage owner. Native application
/// composition must supply a qualified private owner through `open_retained`.
/// The legacy `open_existing` entry owns a file lock but cannot establish native
/// ancestry, privacy, race-free namespace custody or namespace durability.
///
/// ```compile_fail
/// use bridge_engine::operations::journal::FileJournal;
/// fn transfer<T: Send>() {}
/// transfer::<FileJournal>();
/// ```
/// ```compile_fail
/// use bridge_engine::operations::journal::FileJournal;
/// fn share<T: Sync>() {}
/// share::<FileJournal>();
/// ```
pub struct FileJournal {
    storage: Box<dyn JournalStorage>,
    expected_len: u64,
    records: Vec<JournalRecord>,
    poisoned: bool,
}

impl FileJournal {
    /// Compatibility for explicitly provisioned portable fixtures. This creates
    /// no file or directory and rejects observed final links. The caller owns
    /// the native provisioning and race-free custody boundary; production uses
    /// `open_retained` instead of reopening a captured path.
    pub fn open_existing(path: &Path) -> Result<Self, KernelFailure> {
        let before = std::fs::symlink_metadata(path).map_err(|_| KernelFailure::Storage)?;
        if !before.is_file() || before.is_symlink() || before.len() > MAX_JOURNAL {
            return Err(KernelFailure::UnsafeJournal);
        }
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .open(path)
            .map_err(|_| KernelFailure::Storage)?;
        file.try_lock().map_err(|_| KernelFailure::JournalBusy)?;
        // Canonical native provisioning owns ancestry and race-free file identity.
        // This check catches static final links/replacement lengths, not that proof.
        let after = std::fs::symlink_metadata(path).map_err(|_| KernelFailure::Storage)?;
        if !after.is_file()
            || after.is_symlink()
            || file.metadata().map_err(|_| KernelFailure::Storage)?.len() != after.len()
        {
            return Err(KernelFailure::UnsafeJournal);
        }
        Self::open_retained(LegacyFileStorage {
            file,
            _local: PhantomData,
        })
    }

    pub fn open_retained(storage: impl JournalStorage + 'static) -> Result<Self, KernelFailure> {
        let mut storage: Box<dyn JournalStorage> = Box::new(storage);
        let mut length = storage.validate_custody().map_err(storage_failure)?;
        if length > MAX_JOURNAL {
            return Err(KernelFailure::UnsafeJournal);
        }
        if length == 0 {
            storage
                .seek(SeekFrom::Start(0))
                .map_err(|_| KernelFailure::Storage)?;
            storage
                .write_all(MAGIC)
                .map_err(|_| KernelFailure::Storage)?;
            storage.sync_durable().map_err(storage_failure)?;
            length = MAGIC.len() as u64;
            require_length(storage.as_ref(), length)?;
        }
        let mut bytes = Vec::new();
        storage
            .seek(SeekFrom::Start(0))
            .map_err(|_| KernelFailure::Storage)?;
        Read::by_ref(&mut storage)
            .take(MAX_JOURNAL + 1)
            .read_to_end(&mut bytes)
            .map_err(|_| KernelFailure::Storage)?;
        if bytes.len() as u64 > MAX_JOURNAL {
            return Err(KernelFailure::UnsafeJournal);
        }
        // Custody and length must stay stable for the complete bounded read.
        // Neither corruption parsing nor tail repair may precede this check.
        require_length(storage.as_ref(), length)?;
        if bytes.len() as u64 != length {
            return Err(KernelFailure::UnsafeJournal);
        }
        if !bytes.starts_with(MAGIC) {
            return Err(KernelFailure::CorruptJournal);
        }
        let mut offset = MAGIC.len();
        let mut records = Vec::new();
        while offset < bytes.len() {
            if bytes.len() - offset < 8 {
                break;
            }
            let length = u32::from_le_bytes(bytes[offset..offset + 4].try_into().unwrap()) as usize;
            let inverse = u32::from_le_bytes(bytes[offset + 4..offset + 8].try_into().unwrap());
            if inverse != !(length as u32) {
                return Err(KernelFailure::CorruptJournal);
            }
            if length == 0 || length > MAX_RECORD {
                return Err(KernelFailure::CorruptJournal);
            }
            let end = offset
                .checked_add(8 + length + 32)
                .ok_or(KernelFailure::CorruptJournal)?;
            if end > bytes.len() {
                break;
            }
            let payload = &bytes[offset + 8..offset + 8 + length];
            let digest = Sha256::digest(payload);
            if digest.as_slice() != &bytes[offset + 8 + length..end] {
                return Err(KernelFailure::CorruptJournal);
            }
            let record =
                serde_json::from_slice(payload).map_err(|_| KernelFailure::CorruptJournal)?;
            records.push(record);
            offset = end;
        }
        if offset != bytes.len() {
            // Only an incomplete final frame can be discarded. No effects may
            // precede a durable executing frame; complete corruption blocks open.
            storage.truncate(offset as u64).map_err(storage_failure)?;
            storage.sync_durable().map_err(storage_failure)?;
            length = offset as u64;
            require_length(storage.as_ref(), length)?;
        }
        storage
            .seek(SeekFrom::Start(length))
            .map_err(|_| KernelFailure::Storage)?;
        Ok(Self {
            storage,
            expected_len: length,
            records,
            poisoned: false,
        })
    }
}

impl super::DurableJournal for FileJournal {
    fn records(&self) -> &[JournalRecord] {
        &self.records
    }
    fn append(&mut self, record: &JournalRecord) -> Result<(), KernelFailure> {
        if self.poisoned {
            return Err(KernelFailure::Poisoned);
        }
        let payload = serde_json::to_vec(record).map_err(|_| KernelFailure::Storage)?;
        if payload.is_empty() || payload.len() > MAX_RECORD {
            return Err(KernelFailure::Capacity);
        }
        let next_len = self
            .expected_len
            .checked_add(payload.len() as u64)
            .and_then(|length| length.checked_add(40))
            .filter(|length| *length <= MAX_JOURNAL)
            .ok_or(KernelFailure::Capacity)?;
        let result = (|| -> Result<(), KernelFailure> {
            require_length(self.storage.as_ref(), self.expected_len)?;
            self.storage
                .seek(SeekFrom::Start(self.expected_len))
                .map_err(|_| KernelFailure::Storage)?;
            self.storage
                .write_all(&(payload.len() as u32).to_le_bytes())
                .map_err(|_| KernelFailure::Storage)?;
            self.storage
                .write_all(&(!(payload.len() as u32)).to_le_bytes())
                .map_err(|_| KernelFailure::Storage)?;
            self.storage
                .write_all(&payload)
                .map_err(|_| KernelFailure::Storage)?;
            self.storage
                .write_all(&Sha256::digest(&payload))
                .map_err(|_| KernelFailure::Storage)?;
            self.storage.sync_durable().map_err(storage_failure)?;
            require_length(self.storage.as_ref(), next_len)
        })();
        if let Err(failure) = result {
            self.poisoned = true;
            return Err(failure);
        }
        self.expected_len = next_len;
        self.records.push(record.clone());
        Ok(())
    }
}

fn storage_failure(failure: StorageFailure) -> KernelFailure {
    match failure {
        StorageFailure::Unsafe => KernelFailure::UnsafeJournal,
        StorageFailure::Busy => KernelFailure::JournalBusy,
        StorageFailure::Unavailable => KernelFailure::Storage,
    }
}

fn require_length(storage: &dyn JournalStorage, expected: u64) -> Result<(), KernelFailure> {
    if storage.validate_custody().map_err(storage_failure)? == expected {
        Ok(())
    } else {
        Err(KernelFailure::UnsafeJournal)
    }
}

struct LegacyFileStorage {
    file: File,
    _local: PhantomData<Rc<()>>,
}
impl Read for LegacyFileStorage {
    fn read(&mut self, output: &mut [u8]) -> std::io::Result<usize> {
        self.file.read(output)
    }
}
impl Write for LegacyFileStorage {
    fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
        self.file.write(bytes)
    }
    fn flush(&mut self) -> std::io::Result<()> {
        self.file.flush()
    }
}
impl Seek for LegacyFileStorage {
    fn seek(&mut self, position: SeekFrom) -> std::io::Result<u64> {
        self.file.seek(position)
    }
}
impl JournalStorage for LegacyFileStorage {
    fn validate_custody(&self) -> Result<u64, StorageFailure> {
        let metadata = self
            .file
            .metadata()
            .map_err(|_| StorageFailure::Unavailable)?;
        if !metadata.is_file() {
            return Err(StorageFailure::Unsafe);
        }
        Ok(metadata.len())
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
