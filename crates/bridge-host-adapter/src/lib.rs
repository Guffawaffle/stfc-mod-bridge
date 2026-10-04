//! Frontend-independent host observation adapters over the shared engine handle.
//!
//! This crate owns neither the local engine owner nor native GUI authority.
//! Platform composition supplies caller/document admission, bounded workers,
//! independent expiry, response publication and original-thread servicing.

pub mod registration;
