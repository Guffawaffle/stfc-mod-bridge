//! A bounded append-only engine journal. This is application transaction custody,
//! not a native catalog or replacement for the canonical owner's own journal.
use super::{KernelFailure, ResourceKey};
use bridge_contracts::v1::*;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    fs::{File, OpenOptions},
    io::{Read, Seek, SeekFrom, Write},
    path::Path,
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

/// An existing regular journal file must be provisioned in an owner-controlled
/// private state directory by the platform adapter. Opening this implementation
/// creates no directory or file, follows no final symlink and holds an exclusive
/// OS file lock until drop. Native packaging must qualify that directory's ACL,
/// mount, durability and alias semantics; this portable layer cannot do that.
pub struct FileJournal {
    file: File,
    records: Vec<JournalRecord>,
    poisoned: bool,
}

impl FileJournal {
    pub fn open_existing(path: &Path) -> Result<Self, KernelFailure> {
        let before = std::fs::symlink_metadata(path).map_err(|_| KernelFailure::Storage)?;
        if !before.is_file() || before.is_symlink() || before.len() > MAX_JOURNAL {
            return Err(KernelFailure::UnsafeJournal);
        }
        let mut file = OpenOptions::new()
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
        if file.metadata().map_err(|_| KernelFailure::Storage)?.len() == 0 {
            file.write_all(MAGIC).map_err(|_| KernelFailure::Storage)?;
            file.sync_all().map_err(|_| KernelFailure::Storage)?;
        }
        let mut bytes = Vec::new();
        file.seek(SeekFrom::Start(0))
            .map_err(|_| KernelFailure::Storage)?;
        file.read_to_end(&mut bytes)
            .map_err(|_| KernelFailure::Storage)?;
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
            file.set_len(offset as u64)
                .map_err(|_| KernelFailure::Storage)?;
            file.sync_all().map_err(|_| KernelFailure::Storage)?;
        }
        file.seek(SeekFrom::End(0))
            .map_err(|_| KernelFailure::Storage)?;
        Ok(Self {
            file,
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
        let current = self
            .file
            .metadata()
            .map_err(|_| KernelFailure::Storage)?
            .len();
        if payload.is_empty()
            || payload.len() > MAX_RECORD
            || current + payload.len() as u64 + 40 > MAX_JOURNAL
        {
            return Err(KernelFailure::Capacity);
        }
        let result = (|| {
            self.file.write_all(&(payload.len() as u32).to_le_bytes())?;
            self.file
                .write_all(&(!(payload.len() as u32)).to_le_bytes())?;
            self.file.write_all(&payload)?;
            self.file.write_all(&Sha256::digest(&payload))?;
            self.file.sync_all()
        })();
        if result.is_err() {
            self.poisoned = true;
            return Err(KernelFailure::Storage);
        }
        self.records.push(record.clone());
        Ok(())
    }
}
