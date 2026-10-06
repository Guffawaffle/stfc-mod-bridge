//! libproc observations: PID + kernel start stamp + physical executable + actual
//! process architecture. No task_for_pid, process enumeration or lifecycle call.
use crate::{
    filesystem::{self, ReadOnlyFile},
    format, native,
};
use bridge_domain::platform::{ExactProcess, PlatformError, PlatformErrorCode, ProcessLiveness};
use std::{ffi::OsString, marker::PhantomData, os::unix::ffi::OsStringExt, path::PathBuf, rc::Rc};

const PROC_PIDARCHINFO: i32 = 19;
#[repr(C)]
struct ArchInfo {
    cpu_type: i32,
    cpu_subtype: i32,
}

fn pid_native(pid: u32) -> Result<i32, PlatformError> {
    i32::try_from(pid)
        .ok()
        .filter(|pid| *pid > 0)
        .ok_or_else(|| PlatformError::new(PlatformErrorCode::InvalidInput))
}

fn bsd(pid: i32) -> Result<libc::proc_bsdinfo, PlatformError> {
    let mut output = std::mem::MaybeUninit::<libc::proc_bsdinfo>::uninit();
    let expected = std::mem::size_of::<libc::proc_bsdinfo>();
    // SAFETY: exact libproc flavor, argument zero, correctly sized output.
    let count = unsafe {
        libc::proc_pidinfo(
            pid,
            libc::PROC_PIDTBSDINFO,
            0,
            output.as_mut_ptr().cast(),
            expected as i32,
        )
    };
    if count <= 0 {
        return Err(native::errno_error());
    }
    if count != expected as i32 {
        return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
    }
    // SAFETY: libproc returned exactly the requested structure size.
    let output = unsafe { output.assume_init() };
    if output.pbi_pid != pid as u32 {
        return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
    }
    if output.pbi_status == libc::SZOMB {
        return Err(PlatformError::new(PlatformErrorCode::ProcessExited));
    }
    format::start_stamp(output.pbi_start_tvsec, output.pbi_start_tvusec)?;
    Ok(output)
}

fn executable_path(pid: i32) -> Result<PathBuf, PlatformError> {
    let mut output = [0_u8; 4096]; // PROC_PIDPATHINFO_MAXSIZE from Apple proc_info.h
    // SAFETY: libproc writes at most the exact provided capacity.
    let count = unsafe { libc::proc_pidpath(pid, output.as_mut_ptr().cast(), output.len() as u32) };
    if count <= 0 {
        return Err(native::errno_error());
    }
    let bytes = format::pid_path_payload(&output, count as usize)?;
    Ok(PathBuf::from(OsString::from_vec(bytes.to_vec())))
}

fn process_architecture(
    pid: i32,
) -> Result<bridge_domain::platform::NativeArchitecture, PlatformError> {
    let mut output = std::mem::MaybeUninit::<ArchInfo>::uninit();
    // SAFETY: Apple PROC_PIDARCHINFO ABI is two 32-bit CPU fields.
    let count = unsafe {
        libc::proc_pidinfo(
            pid,
            PROC_PIDARCHINFO,
            0,
            output.as_mut_ptr().cast(),
            std::mem::size_of::<ArchInfo>() as i32,
        )
    };
    if count <= 0 {
        return Err(native::errno_error());
    }
    if count != std::mem::size_of::<ArchInfo>() as i32 {
        return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
    }
    // SAFETY: libproc returned exactly the requested ABI structure size.
    format::architecture(unsafe { output.assume_init() }.cpu_type)
}

/// A revalidated observation guard, not a Mach task or kernel lifetime lease.
/// ```compile_fail
/// use bridge_platform_macos::process::ExactProcessGuard;
/// fn transferable<T: Send + Sync>() {}
/// transferable::<ExactProcessGuard>();
/// ```
pub struct ExactProcessGuard {
    observation: ExactProcess,
    executable: ReadOnlyFile,
    _local: PhantomData<Rc<()>>,
}

pub fn capture_process(pid: u32) -> Result<ExactProcessGuard, PlatformError> {
    let native_pid = pid_native(pid)?;
    let before = bsd(native_pid)?;
    let path = executable_path(native_pid)?;
    let executable = filesystem::capture_file(&path, true)?;
    let architecture = process_architecture(native_pid)?;
    let after = bsd(native_pid)?;
    let start_stamp = format::start_stamp(before.pbi_start_tvsec, before.pbi_start_tvusec)?;
    if start_stamp != format::start_stamp(after.pbi_start_tvsec, after.pbi_start_tvusec)?
        || executable_path(native_pid)? != path
    {
        return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
    }
    let guard = ExactProcessGuard {
        observation: ExactProcess {
            pid,
            start_stamp,
            executable: executable.observation().clone(),
            architecture,
        },
        executable,
        _local: PhantomData,
    };
    guard.revalidate()?;
    Ok(guard)
}

pub fn open_exact_process(expected: &ExactProcess) -> Result<ExactProcessGuard, PlatformError> {
    let current = capture_process(expected.pid)?;
    if current.observation != *expected {
        return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
    }
    Ok(current)
}

impl ExactProcessGuard {
    pub fn observation(&self) -> &ExactProcess {
        &self.observation
    }

    pub fn revalidate(&self) -> Result<(), PlatformError> {
        let pid = pid_native(self.observation.pid)?;
        let before = bsd(pid)?;
        if format::start_stamp(before.pbi_start_tvsec, before.pbi_start_tvusec)?
            != self.observation.start_stamp
        {
            return Err(PlatformError::new(PlatformErrorCode::ProcessExited));
        }
        let current = filesystem::capture_file(&executable_path(pid)?, true)?;
        let architecture = process_architecture(pid)?;
        let after = bsd(pid)?;
        if format::start_stamp(after.pbi_start_tvsec, after.pbi_start_tvusec)?
            != self.observation.start_stamp
        {
            return Err(PlatformError::new(PlatformErrorCode::ProcessExited));
        }
        if current.observation() != &self.observation.executable
            || architecture != self.observation.architecture
            || self.executable.revalidate(true)? != self.observation.executable
        {
            return Err(PlatformError::new(PlatformErrorCode::IdentityChanged));
        }
        Ok(())
    }

    pub fn liveness(&self) -> Result<ProcessLiveness, PlatformError> {
        match self.revalidate() {
            Ok(()) => Ok(ProcessLiveness::Running),
            Err(error) if error.code == PlatformErrorCode::ProcessExited => {
                Ok(ProcessLiveness::Exited)
            }
            Err(error) => Err(error),
        }
    }
}
