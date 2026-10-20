//! Portable parsing and bounds checks. These functions are not native evidence.
use bridge_domain::platform::{
    NativeArchitecture, PhysicalIdentity, PlatformError, PlatformErrorCode, ProcessStartStamp,
    SecretDomain, SecretPurpose,
};
use sha2::{Digest, Sha256};

pub const MAX_PATH_BYTES: usize = 4095;
pub const MAX_COMPONENTS: usize = 128;
pub const MAX_HASH_BYTES: u64 = 256 * 1024 * 1024;
pub const MAX_STAGE_BYTES: usize = 64 * 1024 * 1024;
pub const MAX_SECRET_BYTES: usize = 64 * 1024;
pub const MAX_CONTEXT_BYTES: usize = 1024;

fn invalid() -> PlatformError {
    PlatformError::new(PlatformErrorCode::InvalidInput)
}

/// No lexical normalization, Unicode folding or host-independent case guessing.
pub fn absolute_components(path: &[u8]) -> Result<Vec<&[u8]>, PlatformError> {
    if path.is_empty() || path[0] != b'/' || path.contains(&0) {
        return Err(invalid());
    }
    if path.len() > MAX_PATH_BYTES {
        return Err(PlatformError::new(PlatformErrorCode::TooLarge));
    }
    if path == b"/" {
        return Ok(Vec::new());
    }
    let parts: Vec<_> = path[1..].split(|byte| *byte == b'/').collect();
    if parts.len() > MAX_COMPONENTS {
        return Err(PlatformError::new(PlatformErrorCode::TooLarge));
    }
    for part in &parts {
        validate_leaf(part)?;
    }
    Ok(parts)
}

pub fn validate_leaf(name: &[u8]) -> Result<(), PlatformError> {
    if name.is_empty() || name == b"." || name == b".." || name.contains(&0) || name.contains(&b'/')
    {
        return Err(invalid());
    }
    if name.len() > 255 {
        return Err(PlatformError::new(PlatformErrorCode::TooLarge));
    }
    Ok(())
}

/// libproc's proc_pidpath returns strlen, excluding the terminal NUL. Require
/// agreement between that observed count and the returned bounded storage.
pub fn pid_path_payload(storage: &[u8], byte_count: usize) -> Result<&[u8], PlatformError> {
    if byte_count == 0 || byte_count >= storage.len() {
        return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
    }
    if storage.iter().position(|byte| *byte == 0) != Some(byte_count) {
        return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
    }
    let path = &storage[..byte_count];
    absolute_components(path)?;
    Ok(path)
}

pub fn stage_leaf(nonce: &[u8; 16]) -> String {
    format!(".bridge-stage-{}", hex(nonce))
}

pub fn start_stamp(seconds: u64, microseconds: u64) -> Result<ProcessStartStamp, PlatformError> {
    if seconds == 0 || microseconds >= 1_000_000 {
        return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
    }
    Ok(ProcessStartStamp::MacOs {
        seconds,
        microseconds: microseconds as u32,
    })
}

/// Observing x86_64 does not grant translated runtime support.
pub fn architecture(cpu_type: i32) -> Result<NativeArchitecture, PlatformError> {
    match cpu_type {
        7 => Ok(NativeArchitecture::X86),
        0x0100_0007 => Ok(NativeArchitecture::X86_64),
        0x0100_000c => Ok(NativeArchitecture::Arm64),
        _ => Err(PlatformError::new(PlatformErrorCode::UnknownObservation)),
    }
}

pub fn capability_bit(capabilities: u32, valid: u32, bit: u32) -> Option<bool> {
    (valid & bit == bit).then_some(capabilities & bit == bit)
}

pub fn exchanged_identities(
    previous: PhysicalIdentity,
    prepared: PhysicalIdentity,
    destination_after: PhysicalIdentity,
    backup_after: PhysicalIdentity,
) -> bool {
    previous != prepared && destination_after == prepared && backup_after == previous
}

pub fn purpose_digest(purpose: &SecretPurpose) -> Result<[u8; 32], PlatformError> {
    if purpose.version != 1
        || purpose.context.is_empty()
        || purpose.context.chars().any(char::is_control)
    {
        return Err(invalid());
    }
    if purpose.context.len() > MAX_CONTEXT_BYTES {
        return Err(PlatformError::new(PlatformErrorCode::TooLarge));
    }
    let domain = match purpose.domain {
        SecretDomain::ApplicationPreferences => 1_u8,
        SecretDomain::ProfileCompanion => 2,
        SecretDomain::UpdateState => 3,
    };
    let mut digest = Sha256::new();
    digest.update(b"bridge.macos.keychain-purpose\0");
    digest.update(purpose.version.to_be_bytes());
    digest.update([domain]);
    digest.update((purpose.context.len() as u32).to_be_bytes());
    digest.update(purpose.context.as_bytes());
    Ok(digest.finalize().into())
}

/// Opaque reference to one created item. It contains no plaintext or authority.
#[derive(Clone, PartialEq, Eq)]
pub struct SecretReference {
    pub(crate) purpose: [u8; 32],
    pub(crate) nonce: [u8; 16],
}

impl SecretReference {
    pub fn encode(&self) -> String {
        format!(
            "bridge-keychain-v1:{}:{}",
            hex(&self.purpose),
            hex(&self.nonce)
        )
    }

    pub fn decode(value: &str) -> Result<Self, PlatformError> {
        let mut parts = value.split(':');
        if parts.next() != Some("bridge-keychain-v1") {
            return Err(invalid());
        }
        let purpose = decode_hex::<32>(parts.next().ok_or_else(invalid)?)?;
        let nonce = decode_hex::<16>(parts.next().ok_or_else(invalid)?)?;
        if parts.next().is_some() || nonce == [0; 16] {
            return Err(invalid());
        }
        Ok(Self { purpose, nonce })
    }

    pub fn matches(&self, purpose: &SecretPurpose) -> Result<bool, PlatformError> {
        Ok(self.purpose == purpose_digest(purpose)?)
    }

    #[cfg(all(target_os = "macos", target_arch = "aarch64"))]
    pub(crate) fn account(&self) -> String {
        format!("v1.{}.{}", hex(&self.purpose), hex(&self.nonce))
    }
}

pub fn hex(bytes: &[u8]) -> String {
    const DIGITS: &[u8; 16] = b"0123456789abcdef";
    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        output.push(DIGITS[(byte >> 4) as usize] as char);
        output.push(DIGITS[(byte & 15) as usize] as char);
    }
    output
}

fn decode_hex<const N: usize>(value: &str) -> Result<[u8; N], PlatformError> {
    if value.len() != N * 2 {
        return Err(invalid());
    }
    let mut result = [0; N];
    for (index, pair) in value.as_bytes().as_chunks::<2>().0.iter().enumerate() {
        let digit = |byte: u8| match byte {
            b'0'..=b'9' => Ok(byte - b'0'),
            b'a'..=b'f' => Ok(byte - b'a' + 10),
            _ => Err(invalid()),
        };
        result[index] = (digit(pair[0])? << 4) | digit(pair[1])?;
    }
    Ok(result)
}
