use crate::filesystem::{AdmittedDirectoryGuard, absent, leaf, publish_new_sibling};
use crate::native::{error, path_wide, wide, win_error};
use crate::process::ExactProcessGuard;
use bridge_domain::platform::{
    FocusOutcome, ObservedFile, PlatformError, PlatformErrorCode, ProcessLiveness, ShortcutRequest,
};
use std::ffi::OsStr;
use windows::Win32::Foundation::{HWND, LPARAM};
use windows::Win32::System::Com::{
    CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx,
    CoUninitialize, IPersistStream, STATFLAG_NONAME, STATSTG, STREAM_SEEK_SET,
};
use windows::Win32::UI::Shell::{IShellLinkW, SHCreateMemStream, ShellLink};
use windows::Win32::UI::WindowsAndMessaging::*;
use windows::core::{BOOL, Interface, PCWSTR};

struct WindowSearch {
    pid: u32,
    hwnd: HWND,
}
unsafe extern "system" fn visit_window(hwnd: HWND, parameter: LPARAM) -> BOOL {
    // SAFETY: synchronous EnumWindows receives an exclusive stack state pointer.
    let search = unsafe { &mut *(parameter.0 as *mut WindowSearch) };
    let mut pid = 0;
    unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)) };
    if pid == search.pid && unsafe { IsWindowVisible(hwnd) }.as_bool() {
        search.hwnd = hwnd;
        BOOL(0)
    } else {
        BOOL(1)
    }
}

/// Focus is bound to an already verified PID/start/executable guard. Windows
/// foreground restrictions may deny it. No privilege/input-thread workaround,
/// process enumeration heuristic, launch, or fallback target is used.
pub fn focus_exact_process(process: &ExactProcessGuard) -> Result<FocusOutcome, PlatformError> {
    if process.liveness()? == ProcessLiveness::Exited {
        return Ok(FocusOutcome::ProcessExited);
    }
    let mut search = WindowSearch {
        pid: process.observation().pid,
        hwnd: HWND::default(),
    };
    // SAFETY: bounded synchronous enumeration, callback uses valid stack state.
    let result = unsafe {
        EnumWindows(
            Some(visit_window),
            LPARAM((&mut search as *mut WindowSearch) as isize),
        )
    };
    // Callback cancellation is an expected early stop only after a match.
    if let Err(err) = result
        && search.hwnd.is_invalid()
    {
        return Err(win_error(err));
    }
    if process.liveness()? == ProcessLiveness::Exited {
        return Ok(FocusOutcome::ProcessExited);
    }
    if search.hwnd.is_invalid() {
        return Ok(FocusOutcome::NoWindow);
    }
    let mut current_pid = 0;
    unsafe { GetWindowThreadProcessId(search.hwnd, Some(&mut current_pid)) };
    if current_pid != process.observation().pid {
        return Err(error(PlatformErrorCode::IdentityChanged));
    }
    // No restoration of unrelated/minimized windows is attempted. A denied
    // foreground request is an explicit outcome, not a claimed successful focus.
    let accepted = unsafe { SetForegroundWindow(search.hwnd) }.as_bool();
    if process.liveness()? == ProcessLiveness::Exited {
        return Ok(FocusOutcome::ProcessExited);
    }
    let foreground = unsafe { GetForegroundWindow() };
    let mut foreground_pid = 0;
    unsafe { GetWindowThreadProcessId(foreground, Some(&mut foreground_pid)) };
    if accepted && foreground == search.hwnd && foreground_pid == process.observation().pid {
        Ok(FocusOutcome::ForegroundObserved)
    } else {
        Ok(FocusOutcome::Denied)
    }
}

struct Apartment;
impl Apartment {
    fn enter() -> Result<Self, PlatformError> {
        // SAFETY: COM initialized on this owning thread; every success including
        // S_FALSE receives exactly one matching CoUninitialize.
        unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) }
            .ok()
            .map_err(win_error)?;
        Ok(Self)
    }
}
impl Drop for Apartment {
    fn drop(&mut self) {
        unsafe { CoUninitialize() };
    }
}

/// Create a shortcut at the caller's explicit destination within its retained
/// parent. The caller supplies a distinct absent staging leaf. Shell COM writes
/// only a bounded memory stream; native file output is create-new, flushed and
/// renamed through the retained source handle without overwrite. The owning
/// transaction retains its destination namespace exclusion. An error after
/// file creation/rename requires reconciliation, not an unchanged-state claim.
/// No Desktop/Start Menu discovery or target execution is performed.
pub fn create_shortcut(
    parent: &AdmittedDirectoryGuard,
    request: &ShortcutRequest,
    staging_leaf: &str,
) -> Result<ObservedFile, PlatformError> {
    parent.revalidate()?;
    leaf(staging_leaf)?;
    let destination_name = request
        .destination
        .file_name()
        .and_then(OsStr::to_str)
        .ok_or_else(|| error(PlatformErrorCode::InvalidInput))?;
    leaf(destination_name)?;
    if !destination_name.to_ascii_lowercase().ends_with(".lnk")
        || request.destination.parent() != Some(parent.observation().physical_path.as_path())
        || request.description.len() > 1024
        || request.description.chars().any(char::is_control)
        || request.arguments.len() > 128
    {
        return Err(error(PlatformErrorCode::InvalidInput));
    }
    let destination = request.destination.clone();
    let staging = parent.observation().physical_path.join(staging_leaf);
    if staging == destination {
        return Err(error(PlatformErrorCode::InvalidInput));
    }
    absent(&destination)?;
    let executable = path_wide(&request.executable)?;
    let cwd = path_wide(&request.working_directory)?;
    let description = if request.description.is_empty() {
        vec![0u16]
    } else {
        wide(OsStr::new(&request.description))?
    };
    let arguments = serialize_arguments(&request.arguments)?;
    let arguments = if arguments.is_empty() {
        vec![0u16]
    } else {
        wide(OsStr::new(&arguments))?
    };
    let _apartment = Apartment::enter()?;
    // SAFETY: SDK ShellLink COM object used and dropped on the initialized thread.
    let link: IShellLinkW =
        unsafe { CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER) }.map_err(win_error)?;
    unsafe {
        link.SetPath(PCWSTR(executable.as_ptr()))
            .map_err(win_error)?;
        link.SetWorkingDirectory(PCWSTR(cwd.as_ptr()))
            .map_err(win_error)?;
        link.SetDescription(PCWSTR(description.as_ptr()))
            .map_err(win_error)?;
        link.SetArguments(PCWSTR(arguments.as_ptr()))
            .map_err(win_error)?;
        link.SetShowCmd(SW_SHOWNORMAL).map_err(win_error)?;
    }
    let persist: IPersistStream = link.cast().map_err(win_error)?;
    // SAFETY: Shell-owned in-memory stream with no initial bytes or filesystem.
    let stream = unsafe { SHCreateMemStream(None) }
        .ok_or_else(|| error(PlatformErrorCode::NativeFailure))?;
    unsafe { persist.Save(&stream, true) }.map_err(win_error)?;
    let mut stat = STATSTG::default();
    unsafe { stream.Stat(&mut stat, STATFLAG_NONAME) }.map_err(win_error)?;
    if stat.cbSize == 0 || stat.cbSize > 1024 * 1024 {
        return Err(error(PlatformErrorCode::TooLarge));
    }
    let mut bytes = vec![0u8; stat.cbSize as usize];
    let mut count = 0;
    unsafe { stream.Seek(0, STREAM_SEEK_SET, None) }.map_err(win_error)?;
    unsafe {
        stream.Read(
            bytes.as_mut_ptr().cast(),
            bytes.len() as u32,
            Some(&mut count),
        )
    }
    .ok()
    .map_err(win_error)?;
    if count as usize != bytes.len() {
        return Err(error(PlatformErrorCode::UnknownObservation));
    }
    publish_new_sibling(parent, staging_leaf, destination_name, &bytes)
}

/// Quote literal argv elements using the Microsoft CRT backslash/quote rule.
/// This is an executable argument string, not shell command syntax.
fn serialize_arguments(arguments: &[String]) -> Result<String, PlatformError> {
    // Check the fully escaped UTF-16 size before any quoted/joined output is
    // allocated. Caller-owned input may be large even when its argv count is small.
    let _bounded_units = escaped_argument_units(arguments)?;
    let mut result = Vec::with_capacity(arguments.len());
    for argument in arguments {
        let mut quoted = String::from("\"");
        let mut slashes = 0;
        for character in argument.chars() {
            if character == '\\' {
                slashes += 1;
                continue;
            }
            if character == '"' {
                quoted.extend(std::iter::repeat_n('\\', slashes * 2 + 1));
            } else {
                quoted.extend(std::iter::repeat_n('\\', slashes));
            }
            slashes = 0;
            quoted.push(character);
        }
        quoted.extend(std::iter::repeat_n('\\', slashes * 2));
        quoted.push('"');
        result.push(quoted);
    }
    Ok(result.join(" "))
}

fn escaped_argument_units(arguments: &[String]) -> Result<usize, PlatformError> {
    const MAX_UNITS: usize = 16_384;
    if arguments.len() > 128 {
        return Err(error(PlatformErrorCode::InvalidInput));
    }
    let add = |current: usize, amount: usize| {
        current
            .checked_add(amount)
            .filter(|value| *value <= MAX_UNITS)
            .ok_or_else(|| error(PlatformErrorCode::TooLarge))
    };
    let mut units = arguments.len().saturating_sub(1); // Separating spaces.
    for argument in arguments {
        units = add(units, 2)?; // Opening and closing quotes.
        let mut slashes = 0;
        for character in argument.chars() {
            if matches!(character, '\0' | '\r' | '\n') {
                return Err(error(PlatformErrorCode::InvalidInput));
            }
            if character == '\\' {
                units = add(units, 1)?;
                slashes += 1; // The checked unit bound also bounds this run.
                continue;
            }
            units = if character == '"' {
                // Each preceding slash needs one additional escape; the quote
                // itself consumes its escape and literal UTF-16 code unit.
                add(units, slashes + 2)?
            } else {
                add(units, character.len_utf16())?
            };
            slashes = 0;
        }
        units = add(units, slashes)?; // Double trailing slashes before closing quote.
    }
    Ok(units)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn literal_arguments_preserve_quotes_empty_and_trailing_slashes() {
        assert_eq!(
            serialize_arguments(&["".into(), "a b".into(), "x\"y".into(), "C:\\dir\\".into()])
                .unwrap(),
            "\"\" \"a b\" \"x\\\"y\" \"C:\\dir\\\\\""
        );
        assert!(serialize_arguments(&["a\0b".into()]).is_err());
        assert!(serialize_arguments(&["a\nb".into()]).is_err());
    }

    #[test]
    fn argument_preflight_bounds_escaped_utf16_before_output_allocation() {
        for accepted in ["a".repeat(16_382), "\\".repeat(8_191), "😀".repeat(8_191)] {
            let arguments = [accepted];
            assert_eq!(escaped_argument_units(&arguments).unwrap(), 16_384);
            assert_eq!(
                serialize_arguments(&arguments)
                    .unwrap()
                    .encode_utf16()
                    .count(),
                16_384
            );
        }
        for rejected in [
            "a".repeat(16_383),
            "\\".repeat(8_192),
            "😀".repeat(8_192),
            "\"".repeat(8_192),
        ] {
            assert_eq!(
                escaped_argument_units(&[rejected]).unwrap_err().code,
                PlatformErrorCode::TooLarge
            );
        }
        assert_eq!(
            escaped_argument_units(&["a".repeat(16_380), String::new()])
                .unwrap_err()
                .code,
            PlatformErrorCode::TooLarge
        );
        assert_eq!(
            escaped_argument_units(&vec![String::new(); 129])
                .unwrap_err()
                .code,
            PlatformErrorCode::InvalidInput
        );
        assert_eq!(
            escaped_argument_units(&["bad\0input".into()])
                .unwrap_err()
                .code,
            PlatformErrorCode::InvalidInput
        );
        assert_eq!(escaped_argument_units(&[]).unwrap(), 0);
    }
}
