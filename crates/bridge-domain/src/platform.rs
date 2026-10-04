//! Backend-only platform observations. These values carry no catalog authority,
//! permission, exclusion, or assertion about an executable's mapped memory.
//! Native adapters retain their handles locally; wire clients receive the v1
//! projections after an owning application service resolves the observations.

use std::path::PathBuf;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PlatformErrorCode {
    UnsupportedHost,
    FeatureUnavailable,
    InvalidInput,
    Missing,
    AccessDenied,
    LinkOrReparsePoint,
    WrongKind,
    IdentityChanged,
    ProcessExited,
    Busy,
    TooLarge,
    NativeFailure,
    UnknownObservation,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PlatformError {
    pub code: PlatformErrorCode,
    /// Native error code only; never a path, token, secret, or command string.
    pub native_code: Option<i64>,
}

impl PlatformError {
    pub const fn new(code: PlatformErrorCode) -> Self {
        Self {
            code,
            native_code: None,
        }
    }
}

impl std::fmt::Display for PlatformError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "platform observation {:?}", self.code)
    }
}
impl std::error::Error for PlatformError {}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum PhysicalIdentity {
    Windows {
        volume_serial: u64,
        file_id: [u8; 16],
    },
    MacOs {
        device: u64,
        inode: u64,
    },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FileKind {
    File,
    Directory,
}

/// Timestamp units are part of the host variant; they are not interchangeable.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FileWriteStamp {
    Windows { filetime: i64 },
    MacOs { seconds: i64, nanoseconds: u32 },
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ObservedFile {
    pub physical_path: PathBuf,
    pub identity: PhysicalIdentity,
    pub kind: FileKind,
    pub byte_len: u64,
    pub write_stamp: FileWriteStamp,
    /// Bounded bytes read through the retained file handle, when requested.
    /// This does not identify an executable's in-memory image.
    pub disk_sha256: Option<[u8; 32]>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeArchitecture {
    X86,
    X86_64,
    Arm64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ProcessStartStamp {
    Windows { creation_filetime: u64 },
    MacOs { seconds: u64, microseconds: u32 },
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ExactProcess {
    pub pid: u32,
    pub start_stamp: ProcessStartStamp,
    pub executable: ObservedFile,
    pub architecture: NativeArchitecture,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ProcessLiveness {
    Running,
    Exited,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TrustDomain {
    Mod,
    OfficialGame,
    Bridge,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RevocationPolicy {
    CacheOnly,
    Online,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SignatureTrust {
    OsTrusted,
    OsRefused { status: i64 },
}

/// Publisher approval belongs to the caller's independent trust-domain policy.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SignatureObservation {
    pub domain: TrustDomain,
    pub subject: ObservedFile,
    /// Exact selected signature; dual-signed artifacts cannot substitute it.
    pub signature_index: u32,
    pub revocation: RevocationPolicy,
    pub trust: SignatureTrust,
    pub signer_certificate_sha256: Option<[u8; 32]>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SecretDomain {
    ApplicationPreferences,
    ProfileCompanion,
    UpdateState,
}

/// Purpose is included in the native protected-data entropy/envelope. Adapters
/// reject empty, oversized, control-containing contexts and unknown versions.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SecretPurpose {
    pub version: u16,
    pub domain: SecretDomain,
    pub context: String,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ReplaceOutcome {
    /// No destination replacement API was called. A separately prepared staging
    /// file may remain for its owning transaction to reconcile.
    RefusedBeforeCall { error: PlatformError },
    /// Observed new destination and retained backup, after the mutation call.
    /// Flush completion is recorded separately from filesystem crash durability.
    Replaced {
        destination: Box<ObservedFile>,
        backup: Box<ObservedFile>,
        staged_file_flushed: bool,
    },
    /// The owning transaction must reconcile all paths and retain recovery.
    /// A failed ReplaceFileW call does not prove an unchanged destination.
    AmbiguousAfterCall { native_code: Option<i64> },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FocusOutcome {
    ForegroundObserved,
    Denied,
    NoWindow,
    ProcessExited,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ShortcutRequest {
    /// Explicit private/user-selected output; adapters never choose a Desktop
    /// or Start Menu destination implicitly.
    pub destination: PathBuf,
    pub executable: PathBuf,
    pub arguments: Vec<String>,
    pub working_directory: PathBuf,
    pub description: String,
}
