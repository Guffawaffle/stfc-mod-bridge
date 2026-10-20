//! Exact captured-session focus; no process launch or PID-only fallback.
use crate::{native, process::ExactProcessGuard};
use bridge_domain::platform::{FocusOutcome, PlatformError, PlatformErrorCode, ShortcutRequest};

#[repr(C)]
#[derive(Clone, Copy, Default, PartialEq, Eq)]
struct ProcessSerialNumber {
    high: u32,
    low: u32,
}

// Public deprecated Process Manager API. Native Apple Silicon export/behavior
// must qualify on the supported deployment target before enabling this route.
#[link(name = "ApplicationServices", kind = "framework")]
unsafe extern "C" {
    fn GetProcessForPID(pid: i32, process: *mut ProcessSerialNumber) -> i32;
    fn GetProcessPID(process: *const ProcessSerialNumber, pid: *mut i32) -> i32;
    fn GetFrontProcess(process: *mut ProcessSerialNumber) -> i16;
    fn SetFrontProcess(process: *const ProcessSerialNumber) -> i16;
}

pub fn focus_exact_process(process: &ExactProcessGuard) -> Result<FocusOutcome, PlatformError> {
    // SAFETY: main-thread observation only; no dispatch to an unknown run loop.
    if unsafe { libc::pthread_main_np() } == 0 {
        return Err(PlatformError::new(PlatformErrorCode::FeatureUnavailable));
    }
    if let Err(error) = process.revalidate() {
        return if error.code == PlatformErrorCode::ProcessExited {
            Ok(FocusOutcome::ProcessExited)
        } else {
            Err(error)
        };
    }
    let pid = i32::try_from(process.observation().pid)
        .map_err(|_| PlatformError::new(PlatformErrorCode::InvalidInput))?;
    let mut serial = ProcessSerialNumber::default();
    // SAFETY: exact validated PID, correctly sized writable PSN.
    let status = unsafe { GetProcessForPID(pid, &mut serial) };
    if status == -600 {
        // A missing GUI serial does not prove that the captured process exited.
        // Check the original generation after the failed native observation.
        return observed_missing_gui_process(process);
    } // procNotFound
    if status != 0 {
        return Err(native::status_error(status));
    }
    if serial == ProcessSerialNumber::default() {
        return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
    }
    let mut serial_pid = 0;
    // SAFETY: PSN obtained from OS, exact-sized PID output.
    let status = unsafe { GetProcessPID(&serial, &mut serial_pid) };
    if status != 0 {
        return Err(native::status_error(status));
    }
    if serial_pid != pid {
        return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
    }
    process.revalidate()?;
    // PSN is unique to an application instance during the boot. It closes the
    // PID-reuse selection gap; the OS can still deny foreground transfer.
    // SAFETY: revalidated exact application's captured native serial identity.
    let status = unsafe { SetFrontProcess(&serial) };
    if status == -600 {
        // Process Manager's procNotFound concerns the GUI instance. Process
        // liveness still comes from the exact libproc generation observation.
        return observed_missing_gui_process(process);
    }
    if status != 0 {
        return Ok(FocusOutcome::Denied);
    }
    let mut foreground = ProcessSerialNumber::default();
    // SAFETY: bounded native foreground observation only.
    let status = unsafe { GetFrontProcess(&mut foreground) };
    if status != 0 {
        return Err(native::status_error(i32::from(status)));
    }
    process.revalidate()?;
    Ok(if foreground == serial {
        FocusOutcome::ForegroundObserved
    } else {
        FocusOutcome::Denied
    })
}

fn observed_missing_gui_process(
    process: &ExactProcessGuard,
) -> Result<FocusOutcome, PlatformError> {
    match process.revalidate() {
        Ok(()) => Ok(FocusOutcome::NoWindow),
        Err(error) if error.code == PlatformErrorCode::ProcessExited => {
            Ok(FocusOutcome::ProcessExited)
        }
        Err(error) => Err(error),
    }
}

/// A Finder alias cannot preserve arbitrary argv/working-directory semantics.
/// No shell script, AppleScript, launch or implicit Desktop output is substituted.
pub fn create_shortcut(_request: &ShortcutRequest) -> Result<(), PlatformError> {
    Err(PlatformError::new(PlatformErrorCode::FeatureUnavailable))
}

/// Native menus belong to the host's main-thread application/run-loop custody.
/// This backend has no AppKit host registration yet; report the feature fact.
pub fn register_native_menu() -> Result<(), PlatformError> {
    Err(PlatformError::new(PlatformErrorCode::FeatureUnavailable))
}
