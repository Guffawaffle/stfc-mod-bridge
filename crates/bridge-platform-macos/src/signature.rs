//! Static arm64 code-signature observation under the OS code-signing trust
//! settings. Independent publisher allowlists remain with the owning domain.
//!
//! Security reads by path. Retained descriptors and before/after disk hashes do
//! not deny concurrent writes to a bundle. The caller must hold its admitted
//! exclusion; this is an observation, not Gatekeeper/notarization or mapped-code
//! attestation. Native host qualification must exercise concurrent-write refusal.
use crate::{cf, filesystem::ReadOnlyFile, native};
use bridge_domain::platform::{
    PlatformError, PlatformErrorCode, RevocationPolicy, SignatureObservation, SignatureTrust,
    TrustDomain,
};
use sha2::{Digest, Sha256};
use std::ptr;

const CHECK_ALL_ARCHITECTURES: u32 = 1;
const CHECK_NESTED_CODE: u32 = 1 << 3;
const STRICT_VALIDATE: u32 = 1 << 4;
const RESTRICT_SYMLINKS: u32 = 1 << 7;
const ALLOW_NETWORK_ACCESS: u32 = 1 << 16; // macOS 11.3+
const SIGNING_INFORMATION: u32 = 1 << 1;

#[link(name = "Security", kind = "framework")]
unsafe extern "C" {
    fn SecStaticCodeCreateWithPathAndAttributes(
        url: cf::Ref,
        flags: u32,
        attributes: cf::Ref,
        code: *mut cf::Ref,
    ) -> i32;
    fn SecRequirementCreateWithString(text: cf::Ref, flags: u32, requirement: *mut cf::Ref) -> i32;
    fn SecStaticCodeCheckValidity(code: cf::Ref, flags: u32, requirement: cf::Ref) -> i32;
    fn SecCodeCopySigningInformation(code: cf::Ref, flags: u32, information: *mut cf::Ref) -> i32;
    fn SecCertificateGetTypeID() -> usize;
    fn SecCertificateCopyData(certificate: cf::Ref) -> cf::Ref;
    fn SecPolicyCreateWithProperties(identifier: cf::Ref, properties: cf::Ref) -> cf::Ref;
    fn SecPolicyCreateRevocation(flags: usize) -> cf::Ref;
    fn SecTrustCreateWithCertificates(
        certificates: cf::Ref,
        policies: cf::Ref,
        trust: *mut cf::Ref,
    ) -> i32;
    fn SecTrustSetNetworkFetchAllowed(trust: cf::Ref, allow: u8) -> i32;
    fn SecTrustEvaluateWithError(trust: cf::Ref, error: *mut cf::Ref) -> bool;
    static kSecCodeAttributeArchitecture: cf::Ref;
    static kSecCodeInfoCertificates: cf::Ref;
    static kSecPolicyAppleCodeSigning: cf::Ref;
}

pub fn observe_signature(
    subject: &ReadOnlyFile,
    domain: TrustDomain,
    revocation: RevocationPolicy,
    signature_index: u32,
) -> Result<SignatureObservation, PlatformError> {
    if signature_index != 0 {
        return Err(PlatformError::new(PlatformErrorCode::InvalidInput));
    }
    // Security trust evaluation is synchronous and may consult native services
    // or fetch certificates. The owning host must capture/retain this local
    // descriptor on a worker, never stall AppKit's main run loop.
    // SAFETY: current-thread observation only, with no dispatch or state change.
    if unsafe { libc::pthread_main_np() } != 0 {
        return Err(PlatformError::new(PlatformErrorCode::FeatureUnavailable));
    }
    let before = subject.revalidate(true)?;
    let url = cf::Owned::url(&before.physical_path, false)?;
    let mut attributes = cf::Dictionary::new()?;
    // SAFETY: exported fixed key and owned value, both live through Create.
    unsafe {
        attributes.put_owned(kSecCodeAttributeArchitecture, cf::Owned::string("arm64")?);
    }
    let mut code = ptr::null();
    // SAFETY: native Security creates an owned code object for one selected slice.
    let status = unsafe {
        SecStaticCodeCreateWithPathAndAttributes(url.as_ref(), 0, attributes.as_ref(), &mut code)
    };
    if status != 0 {
        return Err(native::status_error(status));
    }
    // SAFETY: successful Create transfers one owned reference.
    let code = unsafe { cf::Owned::from_create(code)? };
    // `anchor trusted` checks OS code-signing Trust Settings; mere signature
    // validity with a null requirement does not check certificate trust.
    let text = cf::Owned::string("anchor trusted")?;
    let mut requirement = ptr::null();
    // SAFETY: fixed requirement text, owned output reference on success.
    let status = unsafe { SecRequirementCreateWithString(text.as_ref(), 0, &mut requirement) };
    if status != 0 {
        return Err(native::status_error(status));
    }
    // SAFETY: successful Create transfers one owned reference.
    let requirement = unsafe { cf::Owned::from_create(requirement)? };
    let flags = CHECK_ALL_ARCHITECTURES
        | CHECK_NESTED_CODE
        | STRICT_VALIDATE
        | RESTRICT_SYMLINKS
        | if revocation == RevocationPolicy::Online {
            ALLOW_NETWORK_ACCESS
        } else {
            0
        };
    // SAFETY: all CF inputs retained and exact fixed validation flags/requirement.
    let validation =
        unsafe { SecStaticCodeCheckValidity(code.as_ref(), flags, requirement.as_ref()) };
    let (trust, signer) = if validation == 0 {
        let (digest, revocation_refusal) = signer_and_revocation(code.as_ref(), revocation)?;
        (
            revocation_refusal.map_or(SignatureTrust::OsTrusted, |status| {
                SignatureTrust::OsRefused { status }
            }),
            Some(digest),
        )
    } else {
        (
            SignatureTrust::OsRefused {
                status: i64::from(validation),
            },
            None,
        )
    };
    if subject.revalidate(true)? != before {
        return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
    }
    Ok(SignatureObservation {
        domain,
        subject: before,
        signature_index,
        revocation,
        trust,
        signer_certificate_sha256: signer,
    })
}

fn signer_and_revocation(
    code: cf::Ref,
    revocation: RevocationPolicy,
) -> Result<([u8; 32], Option<i64>), PlatformError> {
    let mut information = ptr::null();
    // SAFETY: validated live code and one Copy-owned dictionary output.
    let status =
        unsafe { SecCodeCopySigningInformation(code, SIGNING_INFORMATION, &mut information) };
    if status != 0 {
        return Err(native::status_error(status));
    }
    // SAFETY: successful Copy transfers one owned reference.
    let information = unsafe { cf::Owned::from_create(information)? };
    // SAFETY: type check before dictionary/array calls; all borrowed values remain
    // alive through their owning information dictionary.
    unsafe {
        if cf::CFGetTypeID(information.as_ref()) != cf::CFDictionaryGetTypeID() {
            return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
        }
        let certificates = cf::CFDictionaryGetValue(information.as_ref(), kSecCodeInfoCertificates);
        if certificates.is_null() || cf::CFGetTypeID(certificates) != cf::CFArrayGetTypeID() {
            return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
        }
        let count = cf::CFArrayGetCount(certificates);
        if !(1..=16).contains(&count) {
            return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
        }
        for index in 0..count {
            let value = cf::CFArrayGetValueAtIndex(certificates, index);
            if value.is_null() || cf::CFGetTypeID(value) != SecCertificateGetTypeID() {
                return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
            }
        }
        let certificate = cf::CFArrayGetValueAtIndex(certificates, 0);
        if certificate.is_null() || cf::CFGetTypeID(certificate) != SecCertificateGetTypeID() {
            return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
        }
        let data = cf::Owned::from_create(SecCertificateCopyData(certificate))?;
        let bytes = cf::checked_data(data.as_ref(), 64 * 1024)?;
        if bytes.is_empty() {
            return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
        }
        // Build a separate trust object. SigningInformation's live trust object
        // must never be mutated. Require positive OCSP/CRL evidence; a missing
        // cache response refuses CacheOnly rather than silently going online.
        let signing_policy = cf::Owned::from_create(SecPolicyCreateWithProperties(
            kSecPolicyAppleCodeSigning,
            ptr::null(),
        ))?;
        let revocation_flags = 3
            | (1 << 3)
            | if revocation == RevocationPolicy::CacheOnly {
                1 << 4
            } else {
                0
            };
        let revocation_policy =
            cf::Owned::from_create(SecPolicyCreateRevocation(revocation_flags))?;
        let policies = cf::Owned::array(&[&signing_policy, &revocation_policy])?;
        let mut trust = ptr::null();
        let status = SecTrustCreateWithCertificates(certificates, policies.as_ref(), &mut trust);
        if status != 0 {
            return Err(native::status_error(status));
        }
        let trust = cf::Owned::from_create(trust)?;
        let status = SecTrustSetNetworkFetchAllowed(
            trust.as_ref(),
            u8::from(revocation == RevocationPolicy::Online),
        );
        if status != 0 {
            return Err(native::status_error(status));
        }
        let mut error = ptr::null();
        let trusted = SecTrustEvaluateWithError(trust.as_ref(), &mut error);
        let refusal = if trusted {
            if !error.is_null() {
                let _error = cf::Owned::from_create(error)?;
                return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
            }
            None
        } else {
            let error = cf::Owned::from_create(error)?;
            if cf::CFGetTypeID(error.as_ref()) != cf::CFErrorGetTypeID() {
                return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
            }
            Some(cf::CFErrorGetCode(error.as_ref()) as i64)
        };
        Ok((Sha256::digest(&bytes).into(), refusal))
    }
}
