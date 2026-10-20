use crate::filesystem::{ReadOnlyFile, capture_file, exact_file};
use crate::native::{ProcessHandle, error, win_error};
use bridge_domain::platform::{
    ExactProcess, NativeArchitecture, PlatformError, PlatformErrorCode, ProcessLiveness,
    ProcessStartStamp,
};
use std::ffi::OsString;
use std::marker::PhantomData;
use std::os::windows::ffi::OsStringExt;
use std::path::PathBuf;
use std::rc::Rc;
use windows::Win32::Foundation::{FILETIME, WAIT_FAILED, WAIT_OBJECT_0, WAIT_TIMEOUT};
use windows::Win32::System::SystemInformation::{
    IMAGE_FILE_MACHINE, IMAGE_FILE_MACHINE_AMD64, IMAGE_FILE_MACHINE_ARM64,
    IMAGE_FILE_MACHINE_I386, IMAGE_FILE_MACHINE_UNKNOWN,
};
use windows::Win32::System::Threading::*;
use windows::core::PWSTR;

/// The retained process handle continues to name the captured generation after
/// PID reuse. Executable metadata/hash remains a disk observation, not mapped
/// image attestation. This guard neither launches nor stops a process.
///
/// ```compile_fail
/// fn require_send<T: Send>() {}
/// require_send::<bridge_platform_windows::ExactProcessGuard>();
/// ```
pub struct ExactProcessGuard {
    handle: ProcessHandle,
    observation: ExactProcess,
    _executable: ReadOnlyFile,
    _thread: PhantomData<Rc<()>>,
}

impl ExactProcessGuard {
    pub fn observation(&self) -> &ExactProcess {
        &self.observation
    }
    pub fn liveness(&self) -> Result<ProcessLiveness, PlatformError> {
        // SAFETY: retained SYNCHRONIZE handle, zero-time observational wait.
        match unsafe { WaitForSingleObject(self.handle.0, 0) } {
            WAIT_TIMEOUT => Ok(ProcessLiveness::Running),
            WAIT_OBJECT_0 => Ok(ProcessLiveness::Exited),
            WAIT_FAILED => Err(win_error(windows::core::Error::from_thread())),
            _ => Err(error(PlatformErrorCode::UnknownObservation)),
        }
    }

    pub(crate) fn require_running(&self) -> Result<(), PlatformError> {
        if self.liveness()? == ProcessLiveness::Running {
            Ok(())
        } else {
            Err(error(PlatformErrorCode::ProcessExited))
        }
    }
}

pub fn capture_process(
    pid: u32,
    disk_hash_limit: Option<u64>,
) -> Result<ExactProcessGuard, PlatformError> {
    if pid == 0 {
        return Err(error(PlatformErrorCode::InvalidInput));
    }
    // SAFETY: explicit PID, no inherited handle and query/synchronize rights
    // only. No debug privilege or elevation is requested.
    let handle = ProcessHandle(
        unsafe {
            OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
                false,
                pid,
            )
        }
        .map_err(win_error)?,
    );
    let start_stamp = creation(&handle)?;
    let mut path = vec![0u16; 32_768];
    let mut size = path.len() as u32;
    // SAFETY: retained process and bounded writable native string buffer.
    unsafe {
        QueryFullProcessImageNameW(
            handle.0,
            PROCESS_NAME_FORMAT(0),
            PWSTR(path.as_mut_ptr()),
            &mut size,
        )
    }
    .map_err(win_error)?;
    if size == 0 || size as usize >= path.len() {
        return Err(error(PlatformErrorCode::UnknownObservation));
    }
    path.truncate(size as usize);
    let executable = capture_file(&PathBuf::from(OsString::from_wide(&path)), disk_hash_limit)?;
    let mut process_machine = IMAGE_FILE_MACHINE_UNKNOWN;
    let mut native_machine = IMAGE_FILE_MACHINE_UNKNOWN;
    // SAFETY: retained process and exact native output types.
    unsafe { IsWow64Process2(handle.0, &mut process_machine, Some(&mut native_machine)) }
        .map_err(win_error)?;
    let machine = if process_machine == IMAGE_FILE_MACHINE_UNKNOWN {
        native_machine
    } else {
        process_machine
    };
    let architecture = architecture(machine)?;
    if start_stamp != creation(&handle)? {
        return Err(error(PlatformErrorCode::IdentityChanged));
    }
    let guard = ExactProcessGuard {
        handle,
        observation: ExactProcess {
            pid,
            start_stamp,
            executable: executable.observation().clone(),
            architecture,
        },
        _executable: executable,
        _thread: PhantomData,
    };
    guard.require_running()?;
    Ok(guard)
}

pub fn open_exact_process(expected: &ExactProcess) -> Result<ExactProcessGuard, PlatformError> {
    let guard = capture_process(
        expected.pid,
        expected
            .executable
            .disk_sha256
            .map(|_| expected.executable.byte_len),
    )?;
    let current = guard.observation();
    if expected.start_stamp != current.start_stamp || expected.architecture != current.architecture
    {
        return Err(error(PlatformErrorCode::IdentityChanged));
    }
    exact_file(&expected.executable, &current.executable)?;
    Ok(guard)
}

fn creation(handle: &ProcessHandle) -> Result<ProcessStartStamp, PlatformError> {
    let mut created = FILETIME::default();
    let mut exited = FILETIME::default();
    let mut kernel = FILETIME::default();
    let mut user = FILETIME::default();
    // SAFETY: all fixed outputs are valid and process handle remains live.
    unsafe { GetProcessTimes(handle.0, &mut created, &mut exited, &mut kernel, &mut user) }
        .map_err(win_error)?;
    Ok(ProcessStartStamp::Windows {
        creation_filetime: (u64::from(created.dwHighDateTime) << 32)
            | u64::from(created.dwLowDateTime),
    })
}

fn architecture(machine: IMAGE_FILE_MACHINE) -> Result<NativeArchitecture, PlatformError> {
    match machine {
        IMAGE_FILE_MACHINE_AMD64 => Ok(NativeArchitecture::X86_64),
        IMAGE_FILE_MACHINE_ARM64 => Ok(NativeArchitecture::Arm64),
        IMAGE_FILE_MACHINE_I386 => Ok(NativeArchitecture::X86),
        _ => Err(error(PlatformErrorCode::UnknownObservation)),
    }
}
