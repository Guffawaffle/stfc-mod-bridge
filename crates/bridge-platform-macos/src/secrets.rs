//! One purpose-bound generic-password item in the ordinary user's Data
//! Protection Keychain. No search-list/default-keychain changes, access-group
//! enumeration, synchronisation, prompts, bulk updates or delete-on-drop.
use crate::{
    cf,
    format::{self, SecretReference},
    native,
};
use bridge_domain::platform::{PlatformError, PlatformErrorCode, SecretPurpose};
use std::{
    marker::PhantomData,
    ptr,
    rc::Rc,
    sync::atomic::{Ordering, compiler_fence},
};

const SERVICE: &str = "org.stfc-mod-bridge.platform-secrets.v1";
const MAGIC: &[u8; 8] = b"BRKEY001";
const HEADER_LEN: usize = 56;

#[link(name = "Security", kind = "framework")]
unsafe extern "C" {
    fn SecItemAdd(attributes: cf::Ref, result: *mut cf::Ref) -> i32;
    fn SecItemCopyMatching(query: cf::Ref, result: *mut cf::Ref) -> i32;
    fn SecItemDelete(query: cf::Ref) -> i32;
    fn SecRandomCopyBytes(random: cf::Ref, count: usize, bytes: *mut u8) -> i32;
    static kSecClass: cf::Ref;
    static kSecClassGenericPassword: cf::Ref;
    static kSecAttrService: cf::Ref;
    static kSecAttrAccount: cf::Ref;
    static kSecAttrSynchronizable: cf::Ref;
    static kSecAttrAccessible: cf::Ref;
    static kSecAttrAccessibleWhenUnlockedThisDeviceOnly: cf::Ref;
    static kSecUseDataProtectionKeychain: cf::Ref;
    static kSecUseAuthenticationUI: cf::Ref;
    static kSecUseAuthenticationUIFail: cf::Ref;
    static kSecValueData: cf::Ref;
    static kSecReturnData: cf::Ref;
    static kSecMatchLimit: cf::Ref;
    static kSecMatchLimitOne: cf::Ref;
}

/// Rust-owned plaintext is overwritten on drop. No Clone/Debug/Display/serde.
/// Native framework buffers and caller-owned input are outside this guarantee.
/// ```compile_fail
/// use bridge_platform_macos::secrets::Plaintext;
/// fn transferable<T: Send + Sync>() {}
/// transferable::<Plaintext>();
/// ```
pub struct Plaintext {
    bytes: Vec<u8>,
    _local: PhantomData<Rc<()>>,
}

impl Plaintext {
    pub fn expose(&self) -> &[u8] {
        &self.bytes
    }
    fn new(bytes: Vec<u8>) -> Self {
        Self {
            bytes,
            _local: PhantomData,
        }
    }
}

impl Drop for Plaintext {
    fn drop(&mut self) {
        for byte in &mut self.bytes {
            // SAFETY: exclusive live bytes; volatile prevents dead-store removal.
            unsafe { ptr::write_volatile(byte, 0) };
        }
        compiler_fence(Ordering::SeqCst);
    }
}

fn ordinary_user() -> Result<(), PlatformError> {
    // SAFETY: uid observation only, no identity change or privilege request.
    let (real, effective) = unsafe { (libc::getuid(), libc::geteuid()) };
    if real == 0 || effective == 0 || real != effective {
        return Err(PlatformError::new(PlatformErrorCode::AccessDenied));
    }
    Ok(())
}

fn item_query(reference: &SecretReference) -> Result<cf::Dictionary, PlatformError> {
    ordinary_user()?;
    let mut query = cf::Dictionary::new()?;
    // SAFETY: framework-exported CF keys/values are static borrowed references.
    unsafe {
        query.put_static(kSecClass, kSecClassGenericPassword);
        query.put_owned(kSecAttrService, cf::Owned::string(SERVICE)?);
        query.put_owned(kSecAttrAccount, cf::Owned::string(&reference.account())?);
        query.put_static(kSecAttrSynchronizable, cf::kCFBooleanFalse);
        query.put_static(kSecUseDataProtectionKeychain, cf::kCFBooleanTrue);
        query.put_static(kSecUseAuthenticationUI, kSecUseAuthenticationUIFail);
    }
    Ok(query)
}

pub fn protect_secret(
    purpose: &SecretPurpose,
    bytes: &[u8],
) -> Result<SecretReference, PlatformError> {
    ordinary_user()?;
    let digest = format::purpose_digest(purpose)?;
    if bytes.is_empty() {
        return Err(PlatformError::new(PlatformErrorCode::InvalidInput));
    }
    if bytes.len() > format::MAX_SECRET_BYTES {
        return Err(PlatformError::new(PlatformErrorCode::TooLarge));
    }
    let mut nonce = [0; 16];
    // SAFETY: Security's default random source, exactly sized writable nonce.
    let status = unsafe { SecRandomCopyBytes(ptr::null(), nonce.len(), nonce.as_mut_ptr()) };
    if status != 0 {
        return Err(native::status_error(status));
    }
    if nonce == [0; 16] {
        return Err(PlatformError::new(PlatformErrorCode::NativeFailure));
    }
    let reference = SecretReference {
        purpose: digest,
        nonce,
    };
    let mut envelope = Plaintext::new(Vec::with_capacity(HEADER_LEN + bytes.len()));
    envelope.bytes.extend_from_slice(MAGIC);
    envelope.bytes.extend_from_slice(&digest);
    envelope.bytes.extend_from_slice(&nonce);
    envelope.bytes.extend_from_slice(bytes);
    let mut query = item_query(&reference)?;
    // SAFETY: static keys/values borrowed; owned data lives through synchronous Add.
    unsafe {
        query.put_static(
            kSecAttrAccessible,
            kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
        );
        query.put_owned(kSecValueData, cf::Owned::data(envelope.expose())?);
    }
    // SAFETY: complete immutable purpose-scoped attributes; no result requested.
    let status = unsafe { SecItemAdd(query.as_ref(), ptr::null_mut()) };
    if status != 0 {
        return Err(native::status_error(status));
    }
    Ok(reference)
}

pub fn unprotect_secret(
    purpose: &SecretPurpose,
    reference: &SecretReference,
) -> Result<Plaintext, PlatformError> {
    if !reference.matches(purpose)? {
        return Err(PlatformError::new(PlatformErrorCode::InvalidInput));
    }
    let mut query = item_query(reference)?;
    // SAFETY: static framework keys/values; request one exact item's data only.
    unsafe {
        query.put_static(kSecReturnData, cf::kCFBooleanTrue);
        query.put_static(kSecMatchLimit, kSecMatchLimitOne);
    }
    let mut result = ptr::null();
    // SAFETY: synchronous query, live immutable dictionary, one owned result out.
    let status = unsafe { SecItemCopyMatching(query.as_ref(), &mut result) };
    if status != 0 {
        return Err(native::status_error(status));
    }
    // SAFETY: success transfers one owned CF result, validated before use below.
    let result = unsafe { cf::Owned::from_create(result)? };
    let mut envelope = Plaintext::new(cf::checked_data(
        result.as_ref(),
        format::MAX_SECRET_BYTES + HEADER_LEN,
    )?);
    if envelope.bytes.len() <= HEADER_LEN
        || &envelope.bytes[..8] != MAGIC
        || envelope.bytes[8..40] != reference.purpose
        || envelope.bytes[40..56] != reference.nonce
    {
        return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
    }
    // Copy only the validated payload; the full temporary envelope is wiped.
    let payload = envelope.bytes[HEADER_LEN..].to_vec();
    envelope.bytes.fill(0);
    Ok(Plaintext::new(payload))
}

/// Explicit deletion of this exact created reference. No bulk selector accepted.
pub fn delete_secret(
    purpose: &SecretPurpose,
    reference: &SecretReference,
) -> Result<(), PlatformError> {
    if !reference.matches(purpose)? {
        return Err(PlatformError::new(PlatformErrorCode::InvalidInput));
    }
    let query = item_query(reference)?;
    // SAFETY: exact service/account/purpose; no match-all or search-list mutation.
    let status = unsafe { SecItemDelete(query.as_ref()) };
    if status != 0 {
        return Err(native::status_error(status));
    }
    Ok(())
}
