//! Consumer of STFC Profiles' v1 allocation ABI with typed JSON API 2.
//!
//! Profiles remains the catalog, storage, preference and lifecycle authority.
//! This crate has no catalog cache, writer lock or cancellation API. Native calls
//! are synchronous and serialized on the client's owning thread. Losing an
//! observer does not cancel a call or release a retained lease.
//!
//! A data lease is shared `BrowserLease` access, not exclusive configuration
//! writer authority. An installation lease exposes no authoritative physical
//! installation or process identity; its caller still owns exact launch custody.

mod ffi;
mod model;
mod response;

pub use ffi::{DataLease, InstallationLease, ProfilesClient};
pub use model::*;
pub use response::*;

pub const API_VERSION: u32 = 2;
pub const MAX_REQUEST_BYTES: usize = 65_536;
pub const MAX_PATH_BYTES: usize = 32_768;
pub const MAX_RESPONSE_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_LEASE_ERROR_BYTES: usize = 65_536;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AbiStatus {
    InvalidInput,
    NativeFailure,
    Unexpected,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DiagnosticDisposition {
    Absent,
    Suppressed,
    InvalidUtf8,
    TooLarge,
}

/// Errors contain stable classifications, never producer diagnostic text.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ConsumerError {
    InvalidInput,
    RequestTooLarge,
    WrongComponent,
    BindingFailure,
    Abi {
        status: AbiStatus,
        diagnostic: DiagnosticDisposition,
    },
    ContradictoryOutput,
    MissingOutput,
    ResponseTooLarge,
    InvalidUtf8,
    InvalidJson,
    UnsupportedApiVersion,
    InvalidResponse,
}

impl std::fmt::Display for ConsumerError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "Profiles consumer error: {self:?}")
    }
}
impl std::error::Error for ConsumerError {}
