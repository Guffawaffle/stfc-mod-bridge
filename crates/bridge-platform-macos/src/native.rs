use bridge_domain::platform::{PlatformError, PlatformErrorCode};
use std::{ffi::CString, io};

pub(crate) fn errno_error() -> PlatformError {
    from_io(io::Error::last_os_error())
}

pub(crate) fn from_io(error: io::Error) -> PlatformError {
    let native_code = error.raw_os_error();
    let code = match native_code {
        Some(libc::ENOENT) => PlatformErrorCode::Missing,
        Some(libc::ESRCH) => PlatformErrorCode::ProcessExited,
        Some(libc::EACCES | libc::EPERM) => PlatformErrorCode::AccessDenied,
        Some(libc::ELOOP) => PlatformErrorCode::LinkOrReparsePoint,
        Some(libc::EEXIST | libc::EBUSY | libc::ETXTBSY) => PlatformErrorCode::Busy,
        Some(libc::ENOTDIR | libc::EISDIR) => PlatformErrorCode::WrongKind,
        Some(libc::ENAMETOOLONG | libc::EFBIG) => PlatformErrorCode::TooLarge,
        Some(libc::ENOTSUP | libc::ENOSYS) => PlatformErrorCode::FeatureUnavailable,
        _ => PlatformErrorCode::NativeFailure,
    };
    PlatformError {
        code,
        native_code: native_code.map(i64::from),
    }
}

pub(crate) fn status_error(status: i32) -> PlatformError {
    let code = match status {
        -25300 => PlatformErrorCode::Missing, // errSecItemNotFound
        -25299 => PlatformErrorCode::Busy,    // errSecDuplicateItem
        -25293 | -25308 | -34018 => PlatformErrorCode::AccessDenied,
        _ => PlatformErrorCode::NativeFailure,
    };
    PlatformError {
        code,
        native_code: Some(i64::from(status)),
    }
}

pub(crate) fn cstring(bytes: &[u8]) -> Result<CString, PlatformError> {
    CString::new(bytes).map_err(|_| PlatformError::new(PlatformErrorCode::InvalidInput))
}
