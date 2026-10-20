//! Retained backend journal storage, independent of its codec or native owner.
//! The platform implementation keeps identity, private namespace and exclusion
//! custody; it never exports a path or handle for the engine to reopen.
use std::io::{Read, Seek, Write};

/// Closed, payload-free storage failures. Any failure after a mutation can have
/// uncertain persistence; it must never be interpreted as rollback evidence.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StorageFailure {
    Unsafe,
    Busy,
    Unavailable,
}

/// The implementation retains its writer, ancestry, private security and
/// exclusion until Drop. Own writes may change length and timestamps without
/// changing physical identity. Every I/O operation stays under that custody.
///
/// No transfer, sharing, clone, path or raw-handle capability is required.
/// Implementations may be local to their owning actor thread.
pub trait JournalStorage: Read + Write + Seek {
    /// Revalidate native identity, namespace, privacy and exclusion and return
    /// current length. This observation does not acquire replacement custody.
    fn validate_custody(&self) -> Result<u64, StorageFailure>;
    fn truncate(&mut self, length: u64) -> Result<(), StorageFailure>;
    /// Success acknowledges all prior writes through the platform's qualified
    /// file/namespace flush protocol. Failure has uncertain persistence.
    fn sync_durable(&mut self) -> Result<(), StorageFailure>;
}
