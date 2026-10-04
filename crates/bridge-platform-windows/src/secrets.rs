use crate::native::{error, win_error};
use bridge_domain::platform::{PlatformError, PlatformErrorCode, SecretDomain, SecretPurpose};
use sha2::{Digest, Sha256};
use std::marker::PhantomData;
use std::rc::Rc;
use windows::Win32::Foundation::{HLOCAL, LocalFree};
use windows::Win32::Security::Cryptography::{
    CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData, CryptUnprotectData,
};
use windows::core::PCWSTR;

const MAX_SECRET: usize = 1024 * 1024;
const MAX_PROTECTED: usize = MAX_SECRET + 64 * 1024;
const HEADER: &[u8; 8] = b"BRDPAPI1";

/// Owned plaintext intentionally has no Debug or Clone implementation. Drop
/// clears its allocation. Native output is also cleared before LocalFree.
pub struct Plaintext(Vec<u8>, PhantomData<Rc<()>>);
impl Plaintext {
    pub fn as_bytes(&self) -> &[u8] {
        &self.0
    }
}
impl Drop for Plaintext {
    fn drop(&mut self) {
        wipe(&mut self.0);
    }
}

struct LocalBuffer(CRYPT_INTEGER_BLOB);
impl Drop for LocalBuffer {
    fn drop(&mut self) {
        if !self.0.pbData.is_null() {
            // SAFETY: a successful DPAPI output is a LocalAlloc buffer of this
            // size. The guard is created before interpreting the output.
            if self.0.cbData as usize <= MAX_PROTECTED {
                let bytes = unsafe {
                    std::slice::from_raw_parts_mut(self.0.pbData, self.0.cbData as usize)
                };
                wipe(bytes);
            }
            // SAFETY: only the original DPAPI allocation is freed, once.
            unsafe { LocalFree(Some(HLOCAL(self.0.pbData.cast()))) };
        }
    }
}

pub fn protect_secret(purpose: &SecretPurpose, plaintext: &[u8]) -> Result<Vec<u8>, PlatformError> {
    let entropy = entropy(purpose)?;
    if plaintext.is_empty() || plaintext.len() > MAX_SECRET {
        return Err(error(PlatformErrorCode::TooLarge));
    }
    let input = blob(plaintext);
    let purpose_blob = blob(&entropy);
    let mut output = LocalBuffer(CRYPT_INTEGER_BLOB::default());
    // SAFETY: borrowed byte buffers remain valid, output is guarded immediately.
    // Current-user scope and UI_FORBIDDEN; LOCAL_MACHINE is never used.
    unsafe {
        CryptProtectData(
            &input,
            PCWSTR::null(),
            Some(&purpose_blob),
            None,
            None,
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output.0,
        )
    }
    .map_err(win_error)?;
    let bytes = native_bytes(&output, MAX_PROTECTED)?;
    let mut sealed = Vec::with_capacity(HEADER.len() + 32 + bytes.len());
    sealed.extend_from_slice(HEADER);
    sealed.extend_from_slice(&Sha256::digest(&entropy));
    sealed.extend_from_slice(bytes);
    Ok(sealed)
}

pub fn unprotect_secret(
    purpose: &SecretPurpose,
    sealed: &[u8],
) -> Result<Plaintext, PlatformError> {
    let entropy = entropy(purpose)?;
    if sealed.len() <= HEADER.len() + 32 || sealed.len() > MAX_PROTECTED + HEADER.len() + 32 {
        return Err(error(PlatformErrorCode::TooLarge));
    }
    if &sealed[..HEADER.len()] != HEADER
        || sealed[HEADER.len()..HEADER.len() + 32] != Sha256::digest(&entropy)[..]
    {
        return Err(error(PlatformErrorCode::InvalidInput));
    }
    let input = blob(&sealed[HEADER.len() + 32..]);
    let purpose_blob = blob(&entropy);
    let mut output = LocalBuffer(CRYPT_INTEGER_BLOB::default());
    // SAFETY: exact borrowed input/entropy buffers and immediately guarded native
    // output; no description/UI output or machine-wide scope is requested.
    unsafe {
        CryptUnprotectData(
            &input,
            None,
            Some(&purpose_blob),
            None,
            None,
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output.0,
        )
    }
    .map_err(win_error)?;
    Ok(Plaintext(
        native_bytes(&output, MAX_SECRET)?.to_vec(),
        PhantomData,
    ))
}

fn entropy(purpose: &SecretPurpose) -> Result<Vec<u8>, PlatformError> {
    if purpose.version != 1
        || purpose.context.is_empty()
        || purpose.context.len() > 256
        || purpose.context.chars().any(char::is_control)
    {
        return Err(error(PlatformErrorCode::InvalidInput));
    }
    let domain = match purpose.domain {
        SecretDomain::ApplicationPreferences => 1u8,
        SecretDomain::ProfileCompanion => 2u8,
        SecretDomain::UpdateState => 3u8,
    };
    let mut result = b"stfc-bridge/dpapi/current-user\0".to_vec();
    result.extend_from_slice(&purpose.version.to_le_bytes());
    result.push(domain);
    result.extend_from_slice(&(purpose.context.len() as u16).to_le_bytes());
    result.extend_from_slice(purpose.context.as_bytes());
    Ok(result)
}

fn blob(bytes: &[u8]) -> CRYPT_INTEGER_BLOB {
    CRYPT_INTEGER_BLOB {
        cbData: bytes.len() as u32,
        pbData: bytes.as_ptr().cast_mut(),
    }
}

fn native_bytes(buffer: &LocalBuffer, maximum: usize) -> Result<&[u8], PlatformError> {
    let length = buffer.0.cbData as usize;
    if length == 0 || length > maximum || buffer.0.pbData.is_null() {
        return Err(error(PlatformErrorCode::UnknownObservation));
    }
    // SAFETY: DPAPI produced this valid allocated byte range; checked bounds are
    // local policy, not a claim that arbitrary foreign pointers are valid.
    Ok(unsafe { std::slice::from_raw_parts(buffer.0.pbData, length) })
}

fn wipe(bytes: &mut [u8]) {
    for byte in bytes {
        // SAFETY: valid unique byte references; volatile stores prevent elision.
        unsafe { std::ptr::write_volatile(byte, 0) };
    }
    std::sync::atomic::compiler_fence(std::sync::atomic::Ordering::SeqCst);
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn secret_entropy_is_closed_versioned_and_domain_separated() {
        let purpose = SecretPurpose {
            version: 1,
            domain: SecretDomain::ApplicationPreferences,
            context: "synthetic/fixture".into(),
        };
        let mut other = purpose.clone();
        other.domain = SecretDomain::UpdateState;
        assert_ne!(entropy(&purpose).unwrap(), entropy(&other).unwrap());
        other = purpose.clone();
        other.context = "synthetic/other".into();
        assert_ne!(entropy(&purpose).unwrap(), entropy(&other).unwrap());
        other.version = 2;
        assert!(entropy(&other).is_err());
        other.version = 1;
        other.context = "x\0y".into();
        assert!(entropy(&other).is_err());
        other.context = String::new();
        assert!(entropy(&other).is_err());
        other.context = "x".repeat(257);
        assert!(entropy(&other).is_err());
    }
    #[test]
    fn plaintext_wipe_clears_owned_bytes() {
        let mut bytes = [1, 2, 3, 4];
        wipe(&mut bytes);
        assert_eq!(bytes, [0; 4]);
    }
}
