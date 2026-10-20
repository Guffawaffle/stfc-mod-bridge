use bridge_domain::platform::{PlatformError, PlatformErrorCode};
use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::path::Path;
use windows::Win32::Foundation::{CloseHandle, HANDLE};

pub(crate) fn error(code: PlatformErrorCode) -> PlatformError {
    PlatformError::new(code)
}

pub(crate) fn win_error(err: windows::core::Error) -> PlatformError {
    // HRESULT_FROM_WIN32 preserves the low Win32 code. Public diagnostics never
    // retain the native error's formatted text (which can contain private paths).
    let value = err.code().0;
    let raw = if (value as u32 & 0xffff_0000) == 0x8007_0000 {
        i64::from(value as u32 & 0xffff)
    } else {
        i64::from(value)
    };
    let code = match raw {
        2 | 3 => PlatformErrorCode::Missing,
        5 => PlatformErrorCode::AccessDenied,
        32 | 33 => PlatformErrorCode::Busy,
        _ => PlatformErrorCode::NativeFailure,
    };
    PlatformError {
        code,
        native_code: Some(raw),
    }
}

pub(crate) fn io_error(err: std::io::Error) -> PlatformError {
    let code = match err.kind() {
        std::io::ErrorKind::NotFound => PlatformErrorCode::Missing,
        std::io::ErrorKind::PermissionDenied => PlatformErrorCode::AccessDenied,
        std::io::ErrorKind::AlreadyExists => PlatformErrorCode::Busy,
        _ if matches!(err.raw_os_error(), Some(32 | 33)) => PlatformErrorCode::Busy,
        _ => PlatformErrorCode::NativeFailure,
    };
    PlatformError {
        code,
        native_code: err.raw_os_error().map(i64::from),
    }
}

pub(crate) fn wide(text: &OsStr) -> Result<Vec<u16>, PlatformError> {
    let mut value: Vec<u16> = text.encode_wide().collect();
    if value.is_empty() || value.len() > 32_766 || value.contains(&0) {
        return Err(error(PlatformErrorCode::InvalidInput));
    }
    value.push(0);
    Ok(value)
}

pub(crate) fn path_wide(path: &Path) -> Result<Vec<u16>, PlatformError> {
    if !path.is_absolute()
        || path
            .components()
            .any(|c| matches!(c, std::path::Component::ParentDir))
    {
        return Err(error(PlatformErrorCode::InvalidInput));
    }
    // Local drive paths only. Device namespaces, pipes, alternate streams and
    // implicit drive-relative/UNC routes are not ordinary local-file targets.
    match path.components().next() {
        Some(std::path::Component::Prefix(prefix))
            if matches!(
                prefix.kind(),
                std::path::Prefix::Disk(_) | std::path::Prefix::VerbatimDisk(_)
            ) => {}
        _ => return Err(error(PlatformErrorCode::InvalidInput)),
    }
    for component in path.components() {
        if let std::path::Component::Normal(name) = component
            && name.encode_wide().any(|c| c < 32 || c == b':' as u16)
        {
            return Err(error(PlatformErrorCode::InvalidInput));
        }
    }
    wide(path.as_os_str())
}

pub(crate) struct ProcessHandle(pub(crate) HANDLE);
impl Drop for ProcessHandle {
    fn drop(&mut self) {
        // SAFETY: this wrapper owns the successful OpenProcess handle once.
        let _ = unsafe { CloseHandle(self.0) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn explicit_paths_reject_devices_streams_relative_and_parent_routes() {
        for invalid in [
            "relative",
            "C:relative",
            "C:/x/../y",
            "C:/x:stream",
            "\\\\.\\pipe\\name",
            "\\\\?\\GLOBALROOT\\Device\\HarddiskVolume1\\x",
            "\\\\server\\share\\x",
        ] {
            assert!(path_wide(Path::new(invalid)).is_err(), "{invalid:?}");
        }
        assert!(path_wide(Path::new("D:/owned/file.bin")).is_ok());
        assert!(path_wide(Path::new("\\\\?\\D:\\owned\\file.bin")).is_ok());
    }
    #[test]
    fn native_errors_do_not_retain_diagnostic_strings() {
        let denied = win_error(windows::core::Error::new(
            windows::core::HRESULT(0x80070005u32 as i32),
            "private account path",
        ));
        assert_eq!(denied.code, PlatformErrorCode::AccessDenied);
        assert_eq!(denied.native_code, Some(5));
        assert!(!denied.to_string().contains("private"));
        let positive = win_error(windows::core::Error::from_hresult(windows::core::HRESULT(
            123,
        )));
        assert_eq!(positive.code, PlatformErrorCode::NativeFailure);
    }
}
