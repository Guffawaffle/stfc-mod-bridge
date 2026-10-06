use bridge_contracts::v1::*;
use sha2::{Digest, Sha256 as Hasher};

pub type ConfigurationResult<T> = Result<T, ConfigurationFailure>;

/// Closed errors cannot accidentally format a native path, source or secret.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ConfigurationFailure {
    InvalidInput,
    Stale,
    HostMismatch,
    Capacity,
    UnsupportedSchema,
    InvalidDocument,
    UnsupportedSyntax,
    NativeUnavailable,
    ProtectedRefInvalid,
    Busy,
    BackupUnavailable,
    PersistenceFailed,
    RecoveryRequired,
    InvalidOwnerResult,
}
impl ConfigurationFailure {
    pub fn bridge_error(self) -> Box<BridgeError> {
        let (code, retry_disposition) = match self {
            Self::Stale => (ErrorCode::StaleRevision, RetryDisposition::AfterResnapshot),
            Self::HostMismatch => (
                ErrorCode::PlanHostMismatch,
                RetryDisposition::AfterResnapshot,
            ),
            Self::UnsupportedSchema => (
                ErrorCode::UnsupportedSchema,
                RetryDisposition::AfterUserChoice,
            ),
            Self::InvalidDocument => (
                ErrorCode::InvalidConfiguration,
                RetryDisposition::AfterUserChoice,
            ),
            Self::UnsupportedSyntax => (
                ErrorCode::UnsupportedPreservationSyntax,
                RetryDisposition::AfterUserChoice,
            ),
            Self::NativeUnavailable => (
                ErrorCode::NativeUnavailable,
                RetryDisposition::AfterUserChoice,
            ),
            Self::Busy | Self::Capacity => {
                (ErrorCode::OperationBusy, RetryDisposition::AfterUserChoice)
            }
            Self::BackupUnavailable => (
                ErrorCode::BackupUnavailable,
                RetryDisposition::AfterUserChoice,
            ),
            Self::PersistenceFailed => (
                ErrorCode::PersistenceFailed,
                RetryDisposition::AfterRecovery,
            ),
            Self::RecoveryRequired | Self::InvalidOwnerResult => {
                (ErrorCode::RecoveryRequired, RetryDisposition::AfterRecovery)
            }
            Self::InvalidInput | Self::ProtectedRefInvalid => {
                (ErrorCode::InvalidRequest, RetryDisposition::Never)
            }
        };
        Box::new(BridgeError {
            code,
            retry_disposition,
            supported_versions: None,
            violations: BoundedList::new(vec![]).expect("empty bound"),
            expected_revision: None,
            observed_revision: None,
            recovery: None,
        })
    }
}

pub const MAX_DOCUMENT_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_DRAFTS: usize = 32;
pub const MAX_PROTECTED_ENTRIES: usize = 1024;

pub(crate) fn bounded<T, const N: usize>(value: Vec<T>) -> ConfigurationResult<BoundedList<T, N>> {
    BoundedList::new(value).map_err(|_| ConfigurationFailure::Capacity)
}
pub(crate) fn digest(bytes: &[u8]) -> Sha256 {
    let hex = Hasher::digest(bytes)
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect::<String>();
    Sha256::new(format!("sha256:{hex}")).expect("SHA256 format")
}
fn codec_request_id() -> RequestId {
    // UUID correlation has fixed wire length. This local validation identity
    // never becomes an accepted operation or an observed request.
    RequestId::new("00000000-0000-4000-8000-000000000000").expect("static UUID")
}
pub(crate) fn validate_command(command: Command) -> ConfigurationResult<()> {
    let request = Request {
        protocol_version: ProtocolVersion,
        request_id: codec_request_id(),
        body: RequestBody::Command { command },
    };
    let bytes = serde_json::to_vec(&request).map_err(|_| ConfigurationFailure::InvalidInput)?;
    decode_request(&bytes)
        .map(|_| ())
        .map_err(|_| ConfigurationFailure::InvalidInput)
}
pub(crate) fn validate_result(command: CommandResult) -> ConfigurationResult<()> {
    let reply = Reply {
        protocol_version: ProtocolVersion,
        request_id: ReplyRequestId::new(Some(codec_request_id())),
        body: ReplyBody::Result {
            result: ResultPayload::Command {
                command: Box::new(command),
            },
        },
    };
    let bytes = serde_json::to_vec(&reply).map_err(|_| ConfigurationFailure::InvalidInput)?;
    if bytes.len() > MAX_MESSAGE_BYTES {
        return Err(ConfigurationFailure::Capacity);
    }
    decode_reply(&bytes)
        .map(|_| ())
        .map_err(|_| ConfigurationFailure::InvalidInput)
}

/// Not Debug/Serialize/Clone: protected TOML source stays local. Drop clears
/// the allocation before release; adapters must apply their native protections.
pub struct ProtectedValue(Vec<u8>);
impl ProtectedValue {
    pub fn new(encoded_toml: Vec<u8>) -> ConfigurationResult<Self> {
        if encoded_toml.len() > MAX_DOCUMENT_BYTES || std::str::from_utf8(&encoded_toml).is_err() {
            return Err(ConfigurationFailure::InvalidInput);
        }
        Ok(Self(encoded_toml))
    }
    pub(crate) fn source(&self) -> ConfigurationResult<&str> {
        std::str::from_utf8(&self.0).map_err(|_| ConfigurationFailure::InvalidInput)
    }
}
impl Drop for ProtectedValue {
    fn drop(&mut self) {
        // Volatile writes prevent dead-store elimination. This does not promise
        // erasure of native/OS copies, nor put plaintext in a durable journal.
        for byte in &mut self.0 {
            unsafe { std::ptr::write_volatile(byte, 0) };
        }
        std::sync::atomic::compiler_fence(std::sync::atomic::Ordering::SeqCst);
    }
}

/// One allocation attempt per requested identity, with closed refusal instead
/// of retry or fabricated identity. Already issued IDs remain burned when the
/// enclosing operation refuses.
pub trait ConfigurationIds {
    fn draft_id(&mut self) -> ConfigurationResult<DraftId>;
    fn private_id(&mut self) -> ConfigurationResult<PrivateValueId>;
    fn secret_id(&mut self) -> ConfigurationResult<SecretRefId>;
}

/// The owner must have resolved the canonical route and verified the full
/// document binding against retained identity. Missing means virtual bytes.
pub struct DocumentRead {
    pub binding: DocumentBinding,
    pub bytes: Vec<u8>,
}
impl DocumentRead {
    pub(crate) fn text(&self) -> ConfigurationResult<&str> {
        if self.bytes.len() > MAX_DOCUMENT_BYTES {
            return Err(ConfigurationFailure::Capacity);
        }
        match &self.binding.baseline {
            DocumentBaseline::Missing if !self.bytes.is_empty() => {
                return Err(ConfigurationFailure::InvalidOwnerResult);
            }
            DocumentBaseline::Existing { content_digest, .. }
                if &digest(&self.bytes) != content_digest =>
            {
                return Err(ConfigurationFailure::InvalidOwnerResult);
            }
            _ => {}
        }
        std::str::from_utf8(&self.bytes).map_err(|_| ConfigurationFailure::InvalidDocument)
    }
}

/// Trusted owner inspection, never caller supplied file paths.
#[derive(Clone, Debug)]
pub enum OwnerOutcome {
    Pending {
        phase: ConfigurationPhase,
        cancellable: bool,
    },
    Committed {
        document: Box<DocumentBinding>,
        backup: Option<Box<BackupReceiptRef>>,
    },
    CancelledBeforeCommit,
    RolledBack,
    RecoveryRequired,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum ConfigurationPhase {
    Admitted,
    StageWritten,
    StageFlushed,
    BackupRetained,
    PromotionInvoked,
}
