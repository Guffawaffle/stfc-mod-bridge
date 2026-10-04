use crate::filesystem::AdmittedFileGuard;
use crate::native::{error, path_wide, win_error};
use bridge_domain::platform::{
    PlatformError, PlatformErrorCode, RevocationPolicy, SignatureObservation, SignatureTrust,
    TrustDomain,
};
use windows::Win32::Foundation::HWND;
use windows::Win32::Security::Cryptography::{
    CERT_SHA256_HASH_PROP_ID, CertGetCertificateContextProperty,
};
use windows::Win32::Security::WinTrust::*;
use windows::core::PCWSTR;

struct TrustState(WINTRUST_DATA);
impl Drop for TrustState {
    fn drop(&mut self) {
        self.0.dwStateAction = WTD_STATEACTION_CLOSE;
        let mut action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
        // SAFETY: state and referenced input structures remain in the enclosing
        // call's scope through this close; only native provider state is freed.
        unsafe {
            WinVerifyTrust(
                HWND::default(),
                &mut action,
                (&mut self.0 as *mut WINTRUST_DATA).cast(),
            )
        };
    }
}

/// Verify one explicit signature using the operating system's Authenticode
/// policy. This does not approve a publisher, provider, release, or trust domain.
/// Domain-specific application services must compare the signer against their
/// own independent reviewed policy. Cache-only revocation failure is a refusal,
/// never silently retried online. Online retrieval requires an explicit call.
pub fn observe_signature(
    subject: &AdmittedFileGuard,
    domain: TrustDomain,
    revocation: RevocationPolicy,
    signature_index: u32,
) -> Result<SignatureObservation, PlatformError> {
    if signature_index > 15 {
        return Err(error(PlatformErrorCode::InvalidInput));
    }
    let path = path_wide(&subject.observation().physical_path)?;
    let mut file = WINTRUST_FILE_INFO {
        cbStruct: std::mem::size_of::<WINTRUST_FILE_INFO>() as u32,
        pcwszFilePath: PCWSTR(path.as_ptr()),
        hFile: subject.handle(),
        ..Default::default()
    };
    let mut settings = WINTRUST_SIGNATURE_SETTINGS {
        cbStruct: std::mem::size_of::<WINTRUST_SIGNATURE_SETTINGS>() as u32,
        dwIndex: signature_index,
        dwFlags: WSS_VERIFY_SPECIFIC,
        ..Default::default()
    };
    let mut state = TrustState(WINTRUST_DATA {
        cbStruct: std::mem::size_of::<WINTRUST_DATA>() as u32,
        dwUIChoice: WTD_UI_NONE,
        fdwRevocationChecks: WTD_REVOKE_WHOLECHAIN,
        dwUnionChoice: WTD_CHOICE_FILE,
        Anonymous: WINTRUST_DATA_0 { pFile: &mut file },
        dwStateAction: WTD_STATEACTION_VERIFY,
        dwProvFlags: WTD_REVOCATION_CHECK_CHAIN
            | if revocation == RevocationPolicy::CacheOnly {
                WTD_CACHE_ONLY_URL_RETRIEVAL
            } else {
                WINTRUST_DATA_PROVIDER_FLAGS(0)
            },
        pSignatureSettings: &mut settings,
        ..Default::default()
    });
    let mut action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    // SAFETY: exact initialized C structures, stable path and retained read-only
    // file guard. WTD_UI_NONE prevents modal interaction.
    let status = unsafe {
        WinVerifyTrust(
            HWND::default(),
            &mut action,
            (&mut state.0 as *mut WINTRUST_DATA).cast(),
        )
    };
    let mut certificate = None;
    // WinVerifyTrust returns LONG. Only zero is success; HRESULT success macros
    // would incorrectly admit positive refusal values.
    let trust = if status == 0 {
        if settings.dwVerifiedSigIndex != signature_index || state.0.hWVTStateData.is_invalid() {
            return Err(error(PlatformErrorCode::UnknownObservation));
        }
        // SAFETY: provider state was created by the successful guarded WinTrust
        // call; these helpers expose state-owned signer/certificate references.
        let provider = unsafe { WTHelperProvDataFromStateData(state.0.hWVTStateData) };
        if provider.is_null() {
            return Err(error(PlatformErrorCode::UnknownObservation));
        }
        let signer = unsafe { WTHelperGetProvSignerFromChain(provider, 0, false, 0) };
        if signer.is_null() {
            return Err(error(PlatformErrorCode::UnknownObservation));
        }
        let cert = unsafe { WTHelperGetProvCertFromChain(signer, 0) };
        if cert.is_null() {
            return Err(error(PlatformErrorCode::UnknownObservation));
        }
        let context = unsafe { (*cert).pCert };
        if context.is_null() {
            return Err(error(PlatformErrorCode::UnknownObservation));
        }
        let mut digest = [0u8; 32];
        let mut size = digest.len() as u32;
        // SAFETY: provider-owned certificate and bounded writable digest output.
        unsafe {
            CertGetCertificateContextProperty(
                context,
                CERT_SHA256_HASH_PROP_ID,
                Some(digest.as_mut_ptr().cast()),
                &mut size,
            )
        }
        .map_err(win_error)?;
        if size != digest.len() as u32 {
            return Err(error(PlatformErrorCode::UnknownObservation));
        }
        certificate = Some(digest);
        SignatureTrust::OsTrusted
    } else {
        SignatureTrust::OsRefused {
            status: i64::from(status),
        }
    };
    Ok(SignatureObservation {
        domain,
        signature_index,
        subject: subject.observation().clone(),
        revocation,
        trust,
        signer_certificate_sha256: certificate,
    })
}
