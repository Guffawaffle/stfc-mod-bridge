//! Closed ordinary-user synthetic fixtures. Every tree is retained; these tests
//! do not qualify the full production owner, installed game or a release.
use super::*;
use bridge_contracts::v1::{HostEpoch, StreamId};
use bridge_engine::operations::{
    DurableJournal, KernelFailure,
    journal::{FileJournal, JournalRecord},
};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::os::windows::{ffi::OsStrExt, process::CommandExt};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicBool, AtomicUsize},
};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};
use windows::Win32::Foundation::FILETIME;
use windows::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobObjectExtendedLimitInformation,
    SetInformationJobObject,
};
use windows::Win32::System::SystemInformation::{
    IMAGE_FILE_MACHINE_AMD64, IMAGE_FILE_MACHINE_UNKNOWN,
};
use windows::Win32::System::Threading::{
    CREATE_NO_WINDOW, GetProcessTimes, IsWow64Process2, PROCESS_NAME_FORMAT,
    QueryFullProcessImageNameW,
};

const CHILD_TEST: &str = "private_journal::native_fixtures::fixture_private_journal_child";
const CHILD_ROLE: &str = "bridge-private-journal-child-v1";
const ROW_PREFIX: &str = "BRIDGE_PRIVATE_JOURNAL_CHILD ";
const STARTUP_BOUND: usize = 128;
const LINE_BOUND: usize = 16 * 1024;
const PIPE_BOUND: usize = 64 * 1024;
const EVENT_BOUND: usize = 128;
const CHILD_DEADLINE: Duration = Duration::from_secs(10);
static CHILD_SLOTS: AtomicUsize = AtomicUsize::new(0);
static CHILD_POISONED: AtomicBool = AtomicBool::new(false);
pub(super) fn fixture_poisoned() -> bool {
    CHILD_POISONED.load(Ordering::Acquire)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum ChildPhase {
    TryOpen,
    HoldOwner,
    OpenAndRelease,
    DurableAppend,
    TornAppend,
    CorruptComplete,
}
impl ChildPhase {
    fn spelling(self) -> &'static str {
        match self {
            Self::TryOpen => "try-open",
            Self::HoldOwner => "hold-owner",
            Self::OpenAndRelease => "open-and-release",
            Self::DurableAppend => "durable-append",
            Self::TornAppend => "torn-append",
            Self::CorruptComplete => "corrupt-complete",
        }
    }
    fn parse(input: &str) -> Result<Self, StorageFailure> {
        match input {
            "try-open" => Ok(Self::TryOpen),
            "hold-owner" => Ok(Self::HoldOwner),
            "open-and-release" => Ok(Self::OpenAndRelease),
            "durable-append" => Ok(Self::DurableAppend),
            "torn-append" => Ok(Self::TornAppend),
            "corrupt-complete" => Ok(Self::CorruptComplete),
            _ => Err(StorageFailure::Unsafe),
        }
    }
    fn holds(self) -> bool {
        matches!(
            self,
            Self::HoldOwner | Self::DurableAppend | Self::CorruptComplete
        )
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct ChildStartup {
    nonce: FixtureNonce,
    phase: ChildPhase,
}
impl ChildStartup {
    fn parse(frame: &[u8]) -> Result<Self, StorageFailure> {
        if frame.is_empty()
            || frame.len() > STARTUP_BOUND
            || !frame.ends_with(b"\n")
            || frame[..frame.len() - 1].contains(&b'\n')
            || !frame.is_ascii()
            || frame.contains(&b'\r')
        {
            return Err(StorageFailure::Unsafe);
        }
        let text =
            std::str::from_utf8(&frame[..frame.len() - 1]).map_err(|_| StorageFailure::Unsafe)?;
        let mut parts = text.split(' ');
        if parts.next() != Some(CHILD_ROLE) {
            return Err(StorageFailure::Unsafe);
        }
        let nonce = FixtureNonce::parse(parts.next().ok_or(StorageFailure::Unsafe)?.as_bytes())?;
        let phase = ChildPhase::parse(parts.next().ok_or(StorageFailure::Unsafe)?)?;
        if parts.next().is_some() {
            return Err(StorageFailure::Unsafe);
        }
        Ok(Self { nonce, phase })
    }
    fn frame(self) -> Vec<u8> {
        format!(
            "{CHILD_ROLE} {} {}\n",
            self.nonce.component(),
            self.phase.spelling()
        )
        .into_bytes()
    }
    fn read(input: &mut impl Read) -> Result<Self, StorageFailure> {
        let mut frame = [0; STARTUP_BOUND];
        for end in 0..STARTUP_BOUND {
            input
                .read_exact(&mut frame[end..end + 1])
                .map_err(|_| StorageFailure::Unsafe)?;
            if frame[end] == b'\n' {
                return Self::parse(&frame[..=end]);
            }
        }
        Err(StorageFailure::Unsafe)
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum CleanupState {
    Prepared,
    ReapedAndReadersJoined,
    UnknownQuarantined,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct CleanupObservation {
    state: CleanupState,
    kill_error: bool,
    wait_error: bool,
    reader_error: bool,
    exit_success: Option<bool>,
    exit_code: Option<i32>,
}
impl CleanupObservation {
    fn prepared() -> Self {
        Self {
            state: CleanupState::Prepared,
            kill_error: false,
            wait_error: false,
            reader_error: false,
            exit_success: None,
            exit_code: None,
        }
    }
}
fn cleanup_row(observed: CleanupObservation) -> Value {
    json!({"state":format!("{:?}",observed.state),"killError":observed.kill_error,"waitError":observed.wait_error,
        "readerError":observed.reader_error,"exitSuccess":observed.exit_success,"exitCode":observed.exit_code,
        "actualExitObserved":observed.exit_success.is_some()&&observed.state==CleanupState::ReapedAndReadersJoined})
}
struct ChildReservation;
impl ChildReservation {
    fn acquire() -> Result<Self, StorageFailure> {
        if fixture_poisoned() {
            return Err(StorageFailure::Unavailable);
        }
        CHILD_SLOTS
            .try_update(Ordering::AcqRel, Ordering::Acquire, |n| {
                (n < 2).then_some(n + 1)
            })
            .map_err(|_| StorageFailure::Busy)?;
        if fixture_poisoned() {
            CHILD_SLOTS.fetch_sub(1, Ordering::AcqRel);
            return Err(StorageFailure::Unavailable);
        }
        Ok(Self)
    }
}
impl Drop for ChildReservation {
    fn drop(&mut self) {
        CHILD_SLOTS.fetch_sub(1, Ordering::AcqRel);
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct ObjectStamp {
    volume: u64,
    id: [u8; 16],
    length: u64,
    directory: bool,
}
fn information<T: Default>(
    file: &File,
    class: FILE_INFO_BY_HANDLE_CLASS,
) -> Result<T, StorageFailure> {
    let mut value = T::default();
    // SAFETY: each private call site supplies the exact fixed structure/class.
    unsafe {
        GetFileInformationByHandleEx(
            file_handle(file),
            class,
            (&mut value as *mut T).cast(),
            size_of::<T>() as u32,
        )
    }
    .map_err(win_failure)?;
    Ok(value)
}
fn stamp(file: &File) -> Result<ObjectStamp, StorageFailure> {
    let basic: FILE_BASIC_INFO = information(file, FileBasicInfo)?;
    let mut raw = [0u64; 8];
    // Native BOOLEAN bytes are checked before materializing Rust bool fields.
    unsafe {
        GetFileInformationByHandleEx(
            file_handle(file),
            FileStandardInfo,
            raw.as_mut_ptr().cast(),
            size_of::<FILE_STANDARD_INFO>() as u32,
        )
    }
    .map_err(win_failure)?;
    let bytes = unsafe { std::slice::from_raw_parts(raw.as_ptr().cast::<u8>(), size_of_val(&raw)) };
    if bytes[std::mem::offset_of!(FILE_STANDARD_INFO, DeletePending)] > 1
        || bytes[std::mem::offset_of!(FILE_STANDARD_INFO, Directory)] > 1
    {
        return Err(StorageFailure::Unsafe);
    }
    let standard = unsafe { ptr::read(raw.as_ptr().cast::<FILE_STANDARD_INFO>()) };
    let id: FILE_ID_INFO = information(file, FileIdInfo)?;
    if unsafe { GetFileType(file_handle(file)) } != FILE_TYPE_DISK
        || basic.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT.0 != 0
        || standard.DeletePending
        || standard.EndOfFile < 0
        || (!standard.Directory && standard.NumberOfLinks != 1)
        || id.FileId.Identifier == [0; 16]
    {
        return Err(StorageFailure::Unsafe);
    }
    Ok(ObjectStamp {
        volume: id.VolumeSerialNumber,
        id: id.FileId.Identifier,
        length: standard.EndOfFile as u64,
        directory: standard.Directory,
    })
}
fn wide_path(path: &std::path::Path) -> Vec<u16> {
    path.as_os_str().encode_wide().chain(Some(0)).collect()
}
fn disk_hash(file: &mut File, bound: u64) -> Result<[u8; 32], StorageFailure> {
    let before = stamp(file)?;
    if before.directory || before.length > bound {
        return Err(StorageFailure::Unsafe);
    }
    file.seek(SeekFrom::Start(0))
        .map_err(|_| StorageFailure::Unavailable)?;
    let mut hash = Sha256::new();
    let mut block = [0; 16 * 1024];
    let mut total = 0u64;
    loop {
        let n = file
            .read(&mut block)
            .map_err(|_| StorageFailure::Unavailable)?;
        if n == 0 {
            break;
        }
        total = total
            .checked_add(n as u64)
            .filter(|n| *n <= bound)
            .ok_or(StorageFailure::Unsafe)?;
        hash.update(&block[..n]);
    }
    if before != stamp(file)? || before.length != total {
        return Err(StorageFailure::Unsafe);
    }
    Ok(hash.finalize().into())
}
struct RetainedTestExecutable {
    file: File,
    path: std::path::PathBuf,
    identity: ObjectStamp,
    hash: [u8; 32],
}
impl RetainedTestExecutable {
    fn capture() -> Result<Self, StorageFailure> {
        let path = std::env::current_exe().map_err(|_| StorageFailure::Unavailable)?;
        let wide = wide_path(&path);
        // SAFETY: only the actual current test artifact; read/execute with no writer/delete sharing.
        let handle = unsafe {
            CreateFileW(
                PCWSTR(wide.as_ptr()),
                (FILE_GENERIC_READ | FILE_GENERIC_EXECUTE).0,
                FILE_SHARE_READ,
                None,
                OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT,
                None,
            )
        }
        .map_err(win_failure)?;
        let mut file = unsafe { File::from_raw_handle(handle.0) };
        let identity = stamp(&file)?;
        let hash = disk_hash(&mut file, 256 * 1024 * 1024)?;
        Ok(Self {
            file,
            path,
            identity,
            hash,
        })
    }
    fn revalidate(&mut self) -> Result<(), StorageFailure> {
        if stamp(&self.file)? != self.identity
            || disk_hash(&mut self.file, 256 * 1024 * 1024)? != self.hash
        {
            return Err(StorageFailure::Unsafe);
        }
        Ok(())
    }
}
#[derive(Clone, Debug, PartialEq, Eq)]
struct ProcessIdentity {
    creation: u64,
    image: Vec<u16>,
}
#[derive(Clone, Copy)]
struct ChildBinding {
    creation: u64,
    volume: u64,
    id: [u8; 16],
    hash: [u8; 32],
    bytes: u64,
}
fn process_identity(
    child: &Child,
    subject: &RetainedTestExecutable,
) -> Result<ProcessIdentity, StorageFailure> {
    let handle = HANDLE(child.as_raw_handle());
    let mut created = FILETIME::default();
    let mut exited = FILETIME::default();
    let mut kernel = FILETIME::default();
    let mut user = FILETIME::default();
    unsafe { GetProcessTimes(handle, &mut created, &mut exited, &mut kernel, &mut user) }
        .map_err(win_failure)?;
    let mut image = vec![0u16; MAX_PATH_UNITS + 1];
    let mut count = MAX_PATH_UNITS as u32;
    unsafe {
        QueryFullProcessImageNameW(
            handle,
            PROCESS_NAME_FORMAT(0),
            PWSTR(image.as_mut_ptr()),
            &mut count,
        )
    }
    .map_err(win_failure)?;
    if count == 0 || count as usize > MAX_PATH_UNITS {
        return Err(StorageFailure::Unsafe);
    }
    image.truncate(count as usize);
    let expected = physical_name(file_handle(&subject.file))?;
    if !ordinal_equal(&image, &expected) {
        return Err(StorageFailure::Unsafe);
    }
    let mut process_machine = IMAGE_FILE_MACHINE_UNKNOWN;
    let mut native_machine = IMAGE_FILE_MACHINE_UNKNOWN;
    unsafe { IsWow64Process2(handle, &mut process_machine, Some(&mut native_machine)) }
        .map_err(win_failure)?;
    if native_machine != IMAGE_FILE_MACHINE_AMD64 || process_machine != IMAGE_FILE_MACHINE_UNKNOWN {
        return Err(StorageFailure::Unsafe);
    }
    Ok(ProcessIdentity {
        creation: (u64::from(created.dwHighDateTime) << 32) | u64::from(created.dwLowDateTime),
        image,
    })
}
struct PipeState {
    bytes: Mutex<Vec<u8>>,
    eof: AtomicBool,
    failed: AtomicBool,
    started: AtomicBool,
    required: AtomicBool,
}
impl PipeState {
    fn new() -> Result<Arc<Self>, StorageFailure> {
        let mut bytes = Vec::new();
        bytes
            .try_reserve_exact(PIPE_BOUND)
            .map_err(|_| StorageFailure::Unavailable)?;
        Ok(Arc::new(Self {
            bytes: Mutex::new(bytes),
            eof: AtomicBool::new(false),
            failed: AtomicBool::new(false),
            started: AtomicBool::new(false),
            required: AtomicBool::new(false),
        }))
    }
    fn snapshot(&self) -> Result<Vec<u8>, StorageFailure> {
        if self.failed.load(Ordering::Acquire) {
            return Err(StorageFailure::Unsafe);
        }
        self.bytes
            .lock()
            .map(|bytes| bytes.clone())
            .map_err(|_| StorageFailure::Unsafe)
    }
    fn cleanup_known(&self) -> bool {
        !self.failed.load(Ordering::Acquire)
            && (!self.required.load(Ordering::Acquire) || self.eof.load(Ordering::Acquire))
    }
}
fn reader(mut pipe: impl Read, state: Arc<PipeState>) {
    let mut block = [0; 1024];
    loop {
        match pipe.read(&mut block) {
            Ok(0) => {
                state.eof.store(true, Ordering::Release);
                return;
            }
            Ok(n) => {
                let Ok(mut bytes) = state.bytes.lock() else {
                    state.failed.store(true, Ordering::Release);
                    return;
                };
                if bytes
                    .len()
                    .checked_add(n)
                    .is_none_or(|end| end > PIPE_BOUND)
                {
                    state.failed.store(true, Ordering::Release);
                    return;
                }
                bytes.extend_from_slice(&block[..n]);
                let mut events = 0;
                let mut line = 0;
                for byte in bytes.iter() {
                    if *byte == b'\n' {
                        events += 1;
                        line = 0;
                    } else {
                        line += 1;
                    }
                    if line > LINE_BOUND || events > EVENT_BOUND {
                        state.failed.store(true, Ordering::Release);
                        return;
                    }
                }
            }
            Err(_) => {
                state.failed.store(true, Ordering::Release);
                return;
            }
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum ChildLifecycle {
    Prepared,
    SpawnedStartupBlocked,
    Assigned,
    StartupSent,
    Ready,
    Releasing,
    Terminating,
    ReapedAndReadersJoined,
    UnknownQuarantined,
}
struct ChildCapsule {
    subject: RetainedTestExecutable,
    job: Option<OwnedHandle>,
    child: Option<Child>,
    stdin: Option<ChildStdin>,
    stdout_reader: Option<JoinHandle<()>>,
    stderr_reader: Option<JoinHandle<()>>,
    stdout: Arc<PipeState>,
    stderr: Arc<PipeState>,
    process: Option<ProcessIdentity>,
    lifecycle: ChildLifecycle,
}
struct OwnedFixtureChild {
    capsule: Option<Box<ChildCapsule>>,
    cleanup: Rc<Cell<CleanupObservation>>,
    reservation: ManuallyDrop<ChildReservation>,
    startup: ChildStartup,
    startup_sent: bool,
    final_stdout: Option<Vec<u8>>,
    final_stderr: Option<Vec<u8>>,
    binding: Option<ChildBinding>,
}
impl OwnedFixtureChild {
    fn prepared(startup: ChildStartup) -> Result<Self, StorageFailure> {
        let reservation = ChildReservation::acquire()?;
        let stdout = PipeState::new()?;
        let stderr = PipeState::new()?;
        let subject = RetainedTestExecutable::capture()?;
        Ok(Self {
            capsule: Some(Box::new(ChildCapsule {
                subject,
                job: None,
                child: None,
                stdin: None,
                stdout_reader: None,
                stderr_reader: None,
                stdout,
                stderr,
                process: None,
                lifecycle: ChildLifecycle::Prepared,
            })),
            cleanup: Rc::new(Cell::new(CleanupObservation::prepared())),
            reservation: ManuallyDrop::new(reservation),
            startup,
            startup_sent: false,
            final_stdout: None,
            final_stderr: None,
            binding: None,
        })
    }
    fn spawn(&mut self, send_startup: bool) -> Result<(), StorageFailure> {
        let capsule = self.capsule.as_mut().ok_or(StorageFailure::Unavailable)?;
        if capsule.lifecycle != ChildLifecycle::Prepared || fixture_poisoned() {
            return Err(StorageFailure::Unavailable);
        }
        // SAFETY: unnamed noninheritable job with no externally supplied object or security attributes.
        capsule.job = Some(OwnedHandle(
            unsafe { CreateJobObjectW(None, PCWSTR::null()) }.map_err(win_failure)?,
        ));
        let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        unsafe {
            SetInformationJobObject(
                capsule.job.as_ref().unwrap().0,
                JobObjectExtendedLimitInformation,
                (&limits as *const JOBOBJECT_EXTENDED_LIMIT_INFORMATION).cast(),
                size_of_val(&limits) as u32,
            )
        }
        .map_err(win_failure)?;
        let child = Command::new(&capsule.subject.path)
            .args([
                "--ignored",
                "--exact",
                CHILD_TEST,
                "--nocapture",
                "--test-threads=1",
            ])
            .creation_flags(CREATE_NO_WINDOW.0)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|_| StorageFailure::Unavailable)?;
        // Complete prepared custody receives Child immediately, before any fallible setup.
        capsule.child = Some(child);
        // Both pipes were created by this exact spawn. Their EOF obligations
        // precede every fallible setup or transfer into a reader thread.
        capsule.stdout.required.store(true, Ordering::Release);
        capsule.stderr.required.store(true, Ordering::Release);
        capsule.lifecycle = ChildLifecycle::SpawnedStartupBlocked;
        let retained = capsule.child.as_mut().unwrap();
        unsafe {
            AssignProcessToJobObject(
                capsule.job.as_ref().unwrap().0,
                HANDLE(retained.as_raw_handle()),
            )
        }
        .map_err(win_failure)?;
        capsule.lifecycle = ChildLifecycle::Assigned;
        capsule.process = Some(process_identity(retained, &capsule.subject)?);
        capsule.subject.revalidate()?;
        self.binding = Some(ChildBinding {
            creation: capsule.process.as_ref().unwrap().creation,
            volume: capsule.subject.identity.volume,
            id: capsule.subject.identity.id,
            hash: capsule.subject.hash,
            bytes: capsule.subject.identity.length,
        });
        capsule.stdin = retained.stdin.take();
        let stdout = retained.stdout.take().ok_or(StorageFailure::Unavailable)?;
        let state = capsule.stdout.clone();
        capsule.stdout_reader = Some(
            std::thread::Builder::new()
                .name("bridge-journal-fixture-out".into())
                .spawn(move || reader(stdout, state))
                .map_err(|_| {
                    capsule.stdout.failed.store(true, Ordering::Release);
                    StorageFailure::Unavailable
                })?,
        );
        capsule.stdout.started.store(true, Ordering::Release);
        let stderr = retained.stderr.take().ok_or(StorageFailure::Unavailable)?;
        let state = capsule.stderr.clone();
        capsule.stderr_reader = Some(
            std::thread::Builder::new()
                .name("bridge-journal-fixture-err".into())
                .spawn(move || reader(stderr, state))
                .map_err(|_| {
                    capsule.stderr.failed.store(true, Ordering::Release);
                    StorageFailure::Unavailable
                })?,
        );
        capsule.stderr.started.store(true, Ordering::Release);
        if send_startup {
            self.startup_sent = true;
            let input = capsule.stdin.as_mut().ok_or(StorageFailure::Unavailable)?;
            input
                .write_all(&self.startup.frame())
                .and_then(|_| input.flush())
                .map_err(|_| StorageFailure::Unavailable)?;
            capsule.lifecycle = ChildLifecycle::StartupSent;
            if !self.startup.phase.holds() {
                capsule.stdin.take();
            }
        }
        Ok(())
    }
    fn rows(&self) -> Result<Vec<Value>, StorageFailure> {
        let bytes = if let Some(capsule) = &self.capsule {
            if capsule.stderr.failed.load(Ordering::Acquire) {
                return Err(StorageFailure::Unsafe);
            }
            capsule.stdout.snapshot()?
        } else {
            self.final_stdout
                .clone()
                .ok_or(StorageFailure::Unavailable)?
        };
        let text = std::str::from_utf8(&bytes).map_err(|_| StorageFailure::Unsafe)?;
        let mut rows = Vec::new();
        let exact_prefix = format!("test {CHILD_TEST} ... ");
        for line in text.split_inclusive('\n') {
            if !line.ends_with('\n') {
                continue;
            }
            let line = line.trim_end_matches('\n');
            let line = line.strip_prefix(&exact_prefix).unwrap_or(line);
            let Some(data) = line.strip_prefix(ROW_PREFIX) else {
                continue;
            };
            let row: Value = serde_json::from_str(data).map_err(|_| StorageFailure::Unsafe)?;
            let object = row.as_object().ok_or(StorageFailure::Unsafe)?;
            if object.len() != 6
                || !["schemaVersion", "nonce", "phase", "seq", "event", "details"]
                    .iter()
                    .all(|key| object.contains_key(*key))
                || row["schemaVersion"] != 1
                || row["nonce"] != self.startup.nonce.component()
                || row["phase"] != self.startup.phase.spelling()
                || row["seq"].as_u64() != Some(rows.len() as u64 + 1)
                || !row["details"].is_object()
                || !matches!(
                    row["event"].as_str(),
                    Some("Started" | "OpenRefused" | "Opened" | "Ready" | "Released")
                )
            {
                return Err(StorageFailure::Unsafe);
            }
            rows.push(row);
            if rows.len() > EVENT_BOUND {
                return Err(StorageFailure::Unsafe);
            }
        }
        if !rows.is_empty() && rows[0]["event"] != "Started" {
            return Err(StorageFailure::Unsafe);
        }
        Ok(rows)
    }
    fn ready(&mut self) -> Result<Value, StorageFailure> {
        if self
            .capsule
            .as_ref()
            .is_none_or(|capsule| capsule.lifecycle != ChildLifecycle::StartupSent)
        {
            return Err(StorageFailure::Unavailable);
        }
        let deadline = Instant::now() + CHILD_DEADLINE;
        loop {
            let rows = self.rows()?;
            if rows.len() >= 3 && rows[2]["event"] == "Ready" && rows[1]["event"] == "Opened" {
                let expected = match self.startup.phase {
                    ChildPhase::HoldOwner => json!({"heldOwner":true}),
                    ChildPhase::DurableAppend => {
                        json!({"appendReturned":true,"syncAcknowledged":true})
                    }
                    ChildPhase::TornAppend => {
                        json!({"partialTailFlushed":true,"appendReturned":false})
                    }
                    ChildPhase::CorruptComplete => json!({"completeCorruptionFlushed":true}),
                    _ => return Err(StorageFailure::Unsafe),
                };
                if rows[2]["details"] != expected || rows.len() != 3 {
                    return Err(StorageFailure::Unsafe);
                }
                self.capsule.as_mut().unwrap().lifecycle = ChildLifecycle::Ready;
                return Ok(rows[2].clone());
            }
            let capsule = self.capsule.as_mut().ok_or(StorageFailure::Unavailable)?;
            if capsule
                .child
                .as_mut()
                .ok_or(StorageFailure::Unavailable)?
                .try_wait()
                .map_err(|_| StorageFailure::Unavailable)?
                .is_some()
                || Instant::now() >= deadline
            {
                return Err(StorageFailure::Unavailable);
            }
            std::thread::sleep(Duration::from_millis(10));
        }
    }
    fn release(&mut self) -> Result<(), StorageFailure> {
        if !self.startup.phase.holds() {
            return Err(StorageFailure::Unsafe);
        }
        let capsule = self.capsule.as_mut().ok_or(StorageFailure::Unavailable)?;
        if capsule.lifecycle != ChildLifecycle::Ready {
            return Err(StorageFailure::Unsafe);
        }
        let input = capsule.stdin.as_mut().ok_or(StorageFailure::Unavailable)?;
        input
            .write_all(b"R")
            .and_then(|_| input.flush())
            .map_err(|_| StorageFailure::Unavailable)?;
        capsule.lifecycle = ChildLifecycle::Releasing;
        capsule.stdin.take();
        Ok(())
    }
    fn close_startup(&mut self) {
        if let Some(capsule) = self.capsule.as_mut() {
            capsule.stdin.take();
        }
    }
    fn binding(&self) -> Value {
        let binding = self.binding.expect("actual retained child binding");
        json!({"creationFiletime":binding.creation,"executableVolume":binding.volume,
            "executableFileId":binding.id.iter().map(|b|format!("{b:02x}")).collect::<String>(),
            "executableSha256":binding.hash.iter().map(|b|format!("{b:02x}")).collect::<String>(),"executableBytes":binding.bytes})
    }
    fn finish(
        &mut self,
        terminate: bool,
        require_success: bool,
    ) -> Result<CleanupObservation, StorageFailure> {
        let observed = self.clean(terminate);
        if observed.state != CleanupState::ReapedAndReadersJoined
            || observed.kill_error
            || observed.wait_error
            || observed.reader_error
            || (require_success && observed.exit_success != Some(true))
        {
            return Err(StorageFailure::Unavailable);
        }
        if self.startup_sent {
            let rows = self.rows()?;
            let events: Vec<_> = rows
                .iter()
                .map(|row| row["event"].as_str().unwrap())
                .collect();
            let allowed = match self.startup.phase {
                ChildPhase::TryOpen => {
                    events == ["Started", "OpenRefused"]
                        || events == ["Started", "Opened", "Released"]
                }
                ChildPhase::OpenAndRelease => events == ["Started", "Opened", "Released"],
                _ if terminate => events == ["Started", "Opened", "Ready"],
                _ => events == ["Started", "Opened", "Ready", "Released"],
            };
            if !allowed {
                return Err(StorageFailure::Unsafe);
            }
        } else {
            if !self.rows()?.is_empty()
                || !self.final_stderr.as_ref().is_some_and(|bytes| {
                    bytes
                        .windows(b"BRIDGE_PRIVATE_JOURNAL_STARTUP_REFUSED".len())
                        .any(|slice| slice == b"BRIDGE_PRIVATE_JOURNAL_STARTUP_REFUSED")
                })
            {
                return Err(StorageFailure::Unsafe);
            }
        }
        Ok(observed)
    }
    fn clean(&mut self, terminate: bool) -> CleanupObservation {
        let Some(capsule) = self.capsule.as_mut() else {
            return self.cleanup.get();
        };
        let mut result = CleanupObservation::prepared();
        // Setup failures still own the returned Child and pipes. Start any
        // remaining readers before termination so actual EOF can be observed.
        if let Some(child) = capsule.child.as_mut() {
            if let Some(pipe) = child.stdout.take() {
                let state = capsule.stdout.clone();
                match std::thread::Builder::new()
                    .name("bridge-fixture-cleanup-out".into())
                    .spawn(move || reader(pipe, state))
                {
                    Ok(reader) => {
                        capsule.stdout_reader = Some(reader);
                        capsule.stdout.started.store(true, Ordering::Release);
                    }
                    Err(_) => {
                        capsule.stdout.failed.store(true, Ordering::Release);
                    }
                }
            }
            if let Some(pipe) = child.stderr.take() {
                let state = capsule.stderr.clone();
                match std::thread::Builder::new()
                    .name("bridge-fixture-cleanup-err".into())
                    .spawn(move || reader(pipe, state))
                {
                    Ok(reader) => {
                        capsule.stderr_reader = Some(reader);
                        capsule.stderr.started.store(true, Ordering::Release);
                    }
                    Err(_) => {
                        capsule.stderr.failed.store(true, Ordering::Release);
                    }
                }
            }
        }
        capsule.stdin.take();
        capsule.lifecycle = ChildLifecycle::Terminating;
        if terminate
            && let Some(child) = capsule.child.as_mut()
            && child.kill().is_err()
        {
            result.kill_error = true;
        }
        let deadline = Instant::now() + CHILD_DEADLINE;
        let mut exited = capsule.child.is_none();
        while !exited && Instant::now() < deadline {
            match capsule.child.as_mut().unwrap().try_wait() {
                Ok(Some(status)) => {
                    result.exit_success = Some(status.success());
                    result.exit_code = status.code();
                    exited = true;
                }
                Ok(None) => std::thread::sleep(Duration::from_millis(10)),
                Err(_) => {
                    result.wait_error = true;
                    break;
                }
            }
        }
        while exited
            && Instant::now() < deadline
            && [
                capsule.stdout_reader.as_ref(),
                capsule.stderr_reader.as_ref(),
            ]
            .iter()
            .flatten()
            .any(|reader| !reader.is_finished())
        {
            std::thread::sleep(Duration::from_millis(10));
        }
        let readers_done = [
            capsule.stdout_reader.as_ref(),
            capsule.stderr_reader.as_ref(),
        ]
        .iter()
        .flatten()
        .all(|reader| reader.is_finished());
        if exited && readers_done {
            for reader in [capsule.stdout_reader.take(), capsule.stderr_reader.take()]
                .into_iter()
                .flatten()
            {
                if reader.join().is_err() {
                    result.reader_error = true;
                }
            }
            // Every pipe created by spawn owes EOF even if reader launch failed
            // or unwound; absence of a reader cannot erase that obligation.
            let pipes_known = capsule.stdout.cleanup_known() && capsule.stderr.cleanup_known();
            if !pipes_known {
                result.reader_error = true;
            }
            if capsule.subject.revalidate().is_err() {
                result.reader_error = true;
            }
            if let (Some(child), Some(expected)) = (&capsule.child, &capsule.process)
                && process_identity(child, &capsule.subject).as_ref() != Ok(expected)
            {
                result.wait_error = true;
            }
            if pipes_known {
                capsule.lifecycle = ChildLifecycle::ReapedAndReadersJoined;
                result.state = CleanupState::ReapedAndReadersJoined;
                self.final_stdout = capsule.stdout.snapshot().ok();
                self.final_stderr = capsule.stderr.snapshot().ok();
                // Retained executable, exited Child and unique Job are destroyed before reservation release.
                drop(self.capsule.take());
                unsafe { ManuallyDrop::drop(&mut self.reservation) };
                self.cleanup.set(result);
                return result;
            }
        }
        result.state = CleanupState::UnknownQuarantined;
        capsule.lifecycle = ChildLifecycle::UnknownQuarantined;
        CHILD_POISONED.store(true, Ordering::Release);
        if let Some(capsule) = self.capsule.take() {
            std::mem::forget(capsule);
        }
        // Reservation intentionally remains held with unknown capsule; no later admission.
        self.cleanup.set(result);
        result
    }
}
impl Drop for OwnedFixtureChild {
    fn drop(&mut self) {
        let _ = self.clean(true);
    }
}

// Test-only replacement for the existing ordinary_host block. Shared by its
// seed/child guards and by exactly one context emission at each native entry.
const NATIVE_CONTEXT_PREFIX: &str = "BRIDGE_WINDOWS_JOURNAL_CONTEXT ";
const NATIVE_CONTEXT_JSON_BOUND: usize = 4096;
const NATIVE_CONTEXT_TESTS: [&str; 9] = [
    "private_journal::native_fixtures::native_private_journal_fresh_reopen_owner_acl_volume_and_namespace",
    "private_journal::native_fixtures::native_private_journal_dacl_variants_and_live_drift_refuse_without_repair",
    "private_journal::native_fixtures::native_private_journal_leaf_and_private_directory_sharing_are_retained",
    "private_journal::native_fixtures::native_private_journal_cross_process_owner_refusal_and_release",
    "private_journal::native_fixtures::native_private_journal_every_completed_constructor_failure_repeats_full_flush",
    "private_journal::native_fixtures::native_private_journal_codec_durable_append_survives_owned_child_kill",
    "private_journal::native_fixtures::native_private_journal_codec_torn_append_repairs_after_owned_child_kill",
    "private_journal::native_fixtures::native_private_journal_codec_complete_corruption_refuses_without_repair",
    "private_journal::native_fixtures::native_private_journal_child_custody_failure_cleanup_is_observed",
];

#[derive(Clone, Copy)]
struct NativeJournalContext {
    pid: u32,
    creation: u64,
    token_type: u32,
    elevated: bool,
    elevation_type: u32,
    integrity_rid: u32,
    integrity_attributes: u32,
    thread_token_absent: bool,
    process_machine: u16,
    native_machine: u16,
}

impl NativeJournalContext {
    fn ordinary_medium(self) -> bool {
        self.token_type == 1
            && !self.elevated
            && matches!(self.elevation_type, 1 | 3)
            && self.integrity_rid == 0x2000
            && self.integrity_attributes & 0x20 != 0
            && self.thread_token_absent
            && self.process_machine == IMAGE_FILE_MACHINE_UNKNOWN.0
            && self.native_machine == IMAGE_FILE_MACHINE_AMD64.0
    }
    fn require_ordinary_medium(self) -> Result<(), StorageFailure> {
        if self.ordinary_medium() {
            Ok(())
        } else {
            Err(StorageFailure::Unsafe)
        }
    }
}

fn context_token_dword(
    token: HANDLE,
    class: TOKEN_INFORMATION_CLASS,
) -> Result<u32, StorageFailure> {
    let mut output = 0u32;
    let mut returned = 0u32;
    // SAFETY: synchronous own-token query into one initialized four-byte output;
    // the independent returned extent is checked before interpreting it.
    unsafe {
        GetTokenInformation(
            token,
            class,
            Some((&mut output as *mut u32).cast()),
            size_of_val(&output) as u32,
            &mut returned,
        )
    }
    .map_err(win_failure)?;
    if returned as usize != size_of_val(&output) {
        return Err(StorageFailure::Unsafe);
    }
    Ok(output)
}

fn context_token_integrity(token: HANDLE) -> Result<(u32, u32), StorageFailure> {
    // Fixed aligned stack allocation. No caller path, SID, handle or length;
    // insufficient native output capacity refuses without allocation/fallback.
    let mut buffer = [0u64; 512];
    let mut returned = 0u32;
    // SAFETY: synchronous query into a live aligned 4096-byte initialized buffer.
    unsafe {
        GetTokenInformation(
            token,
            TokenIntegrityLevel,
            Some(buffer.as_mut_ptr().cast()),
            size_of_val(&buffer) as u32,
            &mut returned,
        )
    }
    .map_err(win_failure)?;
    let length = returned as usize;
    if length < size_of::<TOKEN_MANDATORY_LABEL>() || length > size_of_val(&buffer) {
        return Err(StorageFailure::Unsafe);
    }
    // SAFETY: the aligned complete header precedes any interior pointer access.
    let label = unsafe { ptr::read(buffer.as_ptr().cast::<TOKEN_MANDATORY_LABEL>()) };
    let header_end = (buffer.as_ptr() as usize)
        .checked_add(size_of::<TOKEN_MANDATORY_LABEL>())
        .ok_or(StorageFailure::Unsafe)?;
    if (label.Label.Sid.0 as usize) < header_end {
        return Err(StorageFailure::Unsafe);
    }
    // Existing owner helper checks pointer/null/alignment/header/whole extent
    // before IsValidSid/GetLengthSid. Never use unchecked native SID helpers.
    let extent = sid_extent(label.Label.Sid, buffer.as_ptr().cast(), length)?;
    if extent != 12 || label.Label.Attributes & 0x20 == 0 {
        return Err(StorageFailure::Unsafe);
    }
    // SAFETY: all 12 bytes are checked inside this live stack allocation above.
    let sid = unsafe { std::slice::from_raw_parts(label.Label.Sid.0.cast::<u8>(), extent) };
    if sid[..8] != [1, 1, 0, 0, 0, 0, 0, 16] {
        return Err(StorageFailure::Unsafe);
    }
    let rid = u32::from_le_bytes([sid[8], sid[9], sid[10], sid[11]]);
    Ok((rid, label.Label.Attributes))
}

fn context_thread_token_absent() -> Result<bool, StorageFailure> {
    let mut token = HANDLE::default();
    // SAFETY: own current-thread pseudohandle, TOKEN_QUERY only, initialized
    // output. A successfully returned token is closed without any mutation.
    match unsafe { OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, true, &mut token) } {
        Ok(()) => {
            if token.is_invalid() {
                return Err(StorageFailure::Unsafe);
            }
            drop(OwnedHandle(token));
            Ok(false)
        }
        Err(error) if error.code().0 as u32 & 0xffff == ERROR_NO_TOKEN.0 => Ok(true),
        Err(error) => Err(win_failure(error)),
    }
}

fn observe_native_journal_context() -> Result<NativeJournalContext, StorageFailure> {
    // SAFETY: these APIs obtain only this process's pseudohandle and PID.
    let process = unsafe { GetCurrentProcess() };
    let pid = unsafe { windows::Win32::System::Threading::GetCurrentProcessId() };
    if pid == 0 {
        return Err(StorageFailure::Unsafe);
    }
    let mut created = FILETIME::default();
    let mut exited = FILETIME::default();
    let mut kernel = FILETIME::default();
    let mut user = FILETIME::default();
    // SAFETY: own process handle and four distinct initialized complete outputs.
    unsafe { GetProcessTimes(process, &mut created, &mut exited, &mut kernel, &mut user) }
        .map_err(win_failure)?;
    let creation = (u64::from(created.dwHighDateTime) << 32) | u64::from(created.dwLowDateTime);
    if creation == 0 || exited.dwHighDateTime != 0 || exited.dwLowDateTime != 0 {
        return Err(StorageFailure::Unsafe);
    }
    let mut token = HANDLE::default();
    // SAFETY: own process handle, TOKEN_QUERY only and initialized token output.
    unsafe { OpenProcessToken(process, TOKEN_QUERY, &mut token) }.map_err(win_failure)?;
    if token.is_invalid() {
        return Err(StorageFailure::Unsafe);
    }
    let token = OwnedHandle(token);
    let token_type = context_token_dword(token.0, TokenType)?;
    // TOKEN_ELEVATION's DWORD has documented nonzero/zero boolean semantics.
    let elevated = context_token_dword(token.0, TokenElevation)? != 0;
    let elevation_type = context_token_dword(token.0, TokenElevationType)?;
    let (integrity_rid, integrity_attributes) = context_token_integrity(token.0)?;
    let thread_token_absent = context_thread_token_absent()?;
    let mut process_machine = IMAGE_FILE_MACHINE_UNKNOWN;
    let mut native_machine = IMAGE_FILE_MACHINE_UNKNOWN;
    // SAFETY: own handle and distinct initialized architecture outputs.
    unsafe { IsWow64Process2(process, &mut process_machine, Some(&mut native_machine)) }
        .map_err(win_failure)?;
    Ok(NativeJournalContext {
        pid,
        creation,
        token_type,
        elevated,
        elevation_type,
        integrity_rid,
        integrity_attributes,
        thread_token_absent,
        process_machine: process_machine.0,
        native_machine: native_machine.0,
    })
}

// Retain the already-existing direct own-process/thread guard for seed and
// child_execute; enrich the same source of truth with type/integrity checks.
fn ordinary_host() -> Result<(), StorageFailure> {
    observe_native_journal_context()?.require_ordinary_medium()
}

fn native_journal_preflight(test_name: &'static str) -> Result<(), StorageFailure> {
    if !NATIVE_CONTEXT_TESTS.contains(&test_name) {
        return Err(StorageFailure::Unsafe);
    }
    let observed = observe_native_journal_context()?;
    let row = json!({
        "schemaVersion": "bridge-windows-journal-context/v1",
        "testName": test_name,
        "pid": observed.pid,
        "creationFiletime": observed.creation.to_string(),
        "tokenType": observed.token_type,
        "elevated": observed.elevated,
        "elevationType": observed.elevation_type,
        "integrityRid": observed.integrity_rid,
        "integrityAttributes": observed.integrity_attributes,
        "threadTokenAbsent": observed.thread_token_absent,
        "processMachine": observed.process_machine,
        "nativeMachine": observed.native_machine,
        "beforeJournalEffects": true,
    });
    let encoded = serde_json::to_string(&row).map_err(|_| StorageFailure::Unavailable)?;
    if encoded.len() > NATIVE_CONTEXT_JSON_BOUND {
        return Err(StorageFailure::Unsafe);
    }
    {
        // Emit and flush the actual observation BEFORE ordinary-context refusal.
        // The only call sites are the first statements of the nine fixed cases.
        let mut output = io::stdout().lock();
        output
            .write_all(NATIVE_CONTEXT_PREFIX.as_bytes())
            .map_err(|_| StorageFailure::Unavailable)?;
        output
            .write_all(encoded.as_bytes())
            .map_err(|_| StorageFailure::Unavailable)?;
        output
            .write_all(b"\n")
            .map_err(|_| StorageFailure::Unavailable)?;
        output.flush().map_err(|_| StorageFailure::Unavailable)?;
    }
    observed.require_ordinary_medium()
}

fn context(
    nonce: FixtureNonce,
    admission: FixtureAdmission,
    fault: FixtureFault,
) -> FixtureContext {
    FixtureContext::new(nonce, admission, fault).expect("closed fresh fixture control")
}
fn open(context: &FixtureContext) -> Result<NativePrivateJournalStorage, StorageFailure> {
    NativePrivateJournalStorage::open_namespace(Namespace::Fixture(context.clone()))
}
fn assert_validated(control: &FixtureControl) {
    assert!(!control.trace_failed.get());
    assert_eq!(control.terminal.get(), None);
    assert!(!control.fired.get());
    let events = control.events.borrow();
    let accepted: Vec<_> = events
        .iter()
        .filter_map(|event| match event {
            FixtureEvent::NativeFlushAccepted(stage, primary) => Some((*stage, *primary)),
            _ => None,
        })
        .collect();
    assert_eq!(
        accepted.iter().map(|(stage, _)| *stage).collect::<Vec<_>>(),
        FIXTURE_FLUSH_STAGES
    );
    assert!(
        accepted
            .iter()
            .all(|(_, primary)| *primary >= 0 && *primary != PENDING.0)
    );
    assert_eq!(events.last(), Some(&FixtureEvent::ConstructorValidated));
}
fn phase_name(phase: FixturePhase) -> String {
    format!("{phase:?}")
}
fn trace(control: &FixtureControl) -> Value {
    let events: Vec<_> = control.events.borrow().iter().map(|event| match event {
        FixtureEvent::CreateReturned(phase, primary) => json!({"event":"CreateReturned","phase":phase_name(*phase),"primary":primary}),
        FixtureEvent::FlushStarted(stage) => json!({"event":"FlushStarted","stage":format!("{stage:?}")}),
        FixtureEvent::NativeFlushReturned(stage, primary) => json!({"event":"NativeFlushReturned","stage":format!("{stage:?}"),"primary":primary}),
        FixtureEvent::NativeFlushAccepted(stage, primary) => json!({"event":"NativeFlushAccepted","stage":format!("{stage:?}"),"primary":primary}),
        FixtureEvent::FaultInjected(stage, primary) => json!({"event":"FaultInjected","stage":format!("{stage:?}"),"primary":primary}),
        FixtureEvent::ConstructorValidated => json!({"event":"ConstructorValidated"}),
    }).collect();
    json!({"events":events,"traceFailed":control.trace_failed.get(),"fired":control.fired.get(),
        "terminal":control.terminal.get().map(|terminal| json!({"phase":phase_name(terminal.phase),"kind":format!("{:?}",terminal.kind),"primary":terminal.primary}))})
}
fn report(case: &str, details: Value) {
    let row = json!({"schemaVersion":1,"case":case,"details":details,
        "foreignOwner":{"status":"not_observed","reason":"foreign_owner_seed_not_declared"},
        "deferred":["reparse_hard_link","writable_mapping","broader_bounds_custody_loss","known_folder_configuration_redirection"],
        "privateJournalOwnerQualified":false,"installedGameQualified":false,"releaseQualified":false});
    let encoded = serde_json::to_string(&row).unwrap();
    assert!(encoded.len() <= LINE_BOUND);
    println!("BRIDGE_PRIVATE_JOURNAL_FIXTURE {encoded}");
}
fn child_row(
    startup: ChildStartup,
    sequence: &Cell<u64>,
    event: &str,
    details: Value,
) -> Result<(), StorageFailure> {
    let seq = sequence
        .get()
        .checked_add(1)
        .filter(|n| *n <= EVENT_BOUND as u64)
        .ok_or(StorageFailure::Unsafe)?;
    let row = json!({"schemaVersion":1,"nonce":startup.nonce.component(),"phase":startup.phase.spelling(),"seq":seq,"event":event,"details":details});
    let encoded = serde_json::to_vec(&row).map_err(|_| StorageFailure::Unavailable)?;
    if encoded.len() + ROW_PREFIX.len() + 1 > LINE_BOUND {
        return Err(StorageFailure::Unsafe);
    }
    let mut output = std::io::stdout().lock();
    output
        .write_all(ROW_PREFIX.as_bytes())
        .and_then(|_| output.write_all(&encoded))
        .and_then(|_| output.write_all(b"\n"))
        .and_then(|_| output.flush())
        .map_err(|_| StorageFailure::Unavailable)?;
    sequence.set(seq);
    Ok(())
}
fn release_frame(input: &mut impl Read) -> Result<(), StorageFailure> {
    let mut value = [0];
    input
        .read_exact(&mut value)
        .map_err(|_| StorageFailure::Unsafe)?;
    if value != *b"R" {
        return Err(StorageFailure::Unsafe);
    }
    if input.read(&mut value).map_err(|_| StorageFailure::Unsafe)? != 0 {
        return Err(StorageFailure::Unsafe);
    }
    Ok(())
}
fn host_record(second: bool) -> JournalRecord {
    JournalRecord::Host {
        epoch: HostEpoch::new(if second {
            "22222222-2222-4222-8222-222222222222"
        } else {
            "11111111-1111-4111-8111-111111111111"
        })
        .unwrap(),
        stream: StreamId::new(if second {
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
        } else {
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
        })
        .unwrap(),
    }
}
fn records_length(count: usize) -> u64 {
    8 + (0..count)
        .map(|i| serde_json::to_vec(&host_record(i != 0)).unwrap().len() as u64 + 40)
        .sum::<u64>()
}
struct CodecControl {
    synced_length: Cell<u64>,
    torn: Cell<bool>,
    frame_write: Cell<usize>,
    startup: ChildStartup,
    sequence: Rc<Cell<u64>>,
}
struct FixtureCodecStorage {
    inner: NativePrivateJournalStorage,
    control: Rc<CodecControl>,
}
impl Read for FixtureCodecStorage {
    fn read(&mut self, out: &mut [u8]) -> io::Result<usize> {
        self.inner.read(out)
    }
}
impl Seek for FixtureCodecStorage {
    fn seek(&mut self, from: SeekFrom) -> io::Result<u64> {
        self.inner.seek(from)
    }
}
impl Write for FixtureCodecStorage {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        if self.control.torn.get() {
            let call = self.control.frame_write.get();
            self.control.frame_write.set(call + 1);
            if call == 2 {
                let expected = serde_json::to_vec(&host_record(true)).unwrap();
                if bytes != expected.as_slice() || bytes.len() <= 7 {
                    return Err(io::ErrorKind::InvalidData.into());
                }
                self.inner.write_all(&bytes[..7])?;
                self.inner.sync_durable().map_err(io_failure)?;
                let length = self.inner.validate_custody().map_err(io_failure)?;
                if length != records_length(1) + 8 + 7 {
                    return Err(io::ErrorKind::InvalidData.into());
                }
                child_row(
                    self.control.startup,
                    &self.control.sequence,
                    "Ready",
                    json!({"partialTailFlushed":true,"appendReturned":false}),
                )
                .map_err(io_failure)?;
                // Deliberately remains inside actual append's Write until retained-child termination.
                loop {
                    std::thread::sleep(Duration::from_millis(50));
                }
            }
            let payload_length = serde_json::to_vec(&host_record(true)).unwrap().len() as u32;
            let expected = if call == 0 {
                payload_length.to_le_bytes()
            } else {
                (!payload_length).to_le_bytes()
            };
            if call > 2 || bytes != expected {
                return Err(io::ErrorKind::InvalidData.into());
            }
            self.inner.write_all(bytes)?;
            return Ok(bytes.len());
        }
        self.inner.write(bytes)
    }
    fn flush(&mut self) -> io::Result<()> {
        self.inner.flush()
    }
}
impl JournalStorage for FixtureCodecStorage {
    fn validate_custody(&self) -> Result<u64, StorageFailure> {
        self.inner.validate_custody()
    }
    fn truncate(&mut self, length: u64) -> Result<(), StorageFailure> {
        self.inner.truncate(length)
    }
    fn sync_durable(&mut self) -> Result<(), StorageFailure> {
        self.inner.sync_durable()?;
        self.control
            .synced_length
            .set(self.inner.validate_custody()?);
        Ok(())
    }
}
fn child_execute(startup: ChildStartup, input: &mut impl Read) -> Result<(), StorageFailure> {
    ordinary_host()?;
    let sequence = Rc::new(Cell::new(0));
    child_row(startup, &sequence, "Started", json!({}))?;
    let ctx = FixtureContext::new(startup.nonce, FixtureAdmission::Reopen, FixtureFault::None)?;
    let storage = match open(&ctx) {
        Ok(storage) => storage,
        Err(error) => {
            child_row(
                startup,
                &sequence,
                "OpenRefused",
                json!({"failure":format!("{error:?}"),"trace":trace(&ctx.control)}),
            )?;
            return if startup.phase == ChildPhase::TryOpen {
                Ok(())
            } else {
                Err(error)
            };
        }
    };
    assert_validated(&ctx.control);
    child_row(
        startup,
        &sequence,
        "Opened",
        json!({"trace":trace(&ctx.control)}),
    )?;
    match startup.phase {
        ChildPhase::TryOpen | ChildPhase::OpenAndRelease => {
            drop(storage);
            child_row(startup, &sequence, "Released", json!({}))?;
        }
        ChildPhase::HoldOwner => {
            child_row(startup, &sequence, "Ready", json!({"heldOwner":true}))?;
            release_frame(input)?;
            drop(storage);
            child_row(startup, &sequence, "Released", json!({}))?;
        }
        ChildPhase::DurableAppend | ChildPhase::TornAppend => {
            let control = Rc::new(CodecControl {
                synced_length: Cell::new(0),
                torn: Cell::new(false),
                frame_write: Cell::new(0),
                startup,
                sequence: sequence.clone(),
            });
            let mut journal = FileJournal::open_retained(FixtureCodecStorage {
                inner: storage,
                control: control.clone(),
            })
            .map_err(|_| StorageFailure::Unsafe)?;
            journal
                .append(&host_record(false))
                .map_err(|_| StorageFailure::Unsafe)?;
            if control.synced_length.get() != records_length(1) {
                return Err(StorageFailure::Unsafe);
            }
            if startup.phase == ChildPhase::TornAppend {
                control.torn.set(true);
            }
            journal
                .append(&host_record(true))
                .map_err(|_| StorageFailure::Unsafe)?;
            if startup.phase == ChildPhase::TornAppend
                || control.synced_length.get() != records_length(2)
            {
                return Err(StorageFailure::Unsafe);
            }
            child_row(
                startup,
                &sequence,
                "Ready",
                json!({"appendReturned":true,"syncAcknowledged":true}),
            )?;
            release_frame(input)?;
            drop(journal);
            child_row(startup, &sequence, "Released", json!({}))?;
        }
        ChildPhase::CorruptComplete => {
            let mut journal =
                FileJournal::open_retained(storage).map_err(|_| StorageFailure::Unsafe)?;
            journal
                .append(&host_record(false))
                .map_err(|_| StorageFailure::Unsafe)?;
            journal
                .append(&host_record(true))
                .map_err(|_| StorageFailure::Unsafe)?;
            drop(journal);
            let next =
                FixtureContext::new(startup.nonce, FixtureAdmission::Reopen, FixtureFault::None)?;
            let mut storage = open(&next)?;
            assert_validated(&next.control);
            if storage.validate_custody()? != records_length(2) {
                return Err(StorageFailure::Unsafe);
            }
            storage
                .seek(SeekFrom::Start(16))
                .map_err(|_| StorageFailure::Unavailable)?;
            let mut first = [0];
            storage
                .read_exact(&mut first)
                .map_err(|_| StorageFailure::Unavailable)?;
            if first != *b"{" {
                return Err(StorageFailure::Unsafe);
            }
            storage
                .seek(SeekFrom::Start(16))
                .map_err(|_| StorageFailure::Unavailable)?;
            storage
                .write_all(b"!")
                .map_err(|_| StorageFailure::Unavailable)?;
            storage.sync_durable()?;
            if storage.validate_custody()? != records_length(2) {
                return Err(StorageFailure::Unsafe);
            }
            child_row(
                startup,
                &sequence,
                "Ready",
                json!({"completeCorruptionFlushed":true}),
            )?;
            release_frame(input)?;
            drop(storage);
            child_row(startup, &sequence, "Released", json!({}))?;
        }
    }
    Ok(())
}
#[test]
#[ignore = "internal exact child; selected only by native fixture controller"]
fn fixture_private_journal_child() {
    let mut input = std::io::stdin().lock();
    let startup = match ChildStartup::read(&mut input) {
        Ok(startup) => startup,
        Err(_) => {
            eprintln!("BRIDGE_PRIVATE_JOURNAL_STARTUP_REFUSED");
            panic!("closed startup refused before namespace effects");
        }
    };
    if !startup.phase.holds() {
        let mut tail = [0];
        if input.read(&mut tail).expect("closed startup EOF") != 0 {
            eprintln!("BRIDGE_PRIVATE_JOURNAL_STARTUP_REFUSED");
            panic!("non-held phase requires startup EOF before namespace effects");
        }
    }
    child_execute(startup, &mut input).expect("closed child execution");
}

fn seed() -> FixtureNonce {
    ordinary_host().expect("ordinary native Windows x64");
    let nonce = FixtureNonce::fresh().unwrap();
    let ctx = context(nonce, FixtureAdmission::Fresh, FixtureFault::None);
    let owner = open(&ctx).expect("fresh native private fixture namespace");
    assert_validated(&ctx.control);
    assert_eq!(owner.validate_custody().unwrap(), 0);
    drop(owner);
    nonce
}
fn child(nonce: FixtureNonce, phase: ChildPhase) -> OwnedFixtureChild {
    let mut child = OwnedFixtureChild::prepared(ChildStartup { nonce, phase }).unwrap();
    child.spawn(true).unwrap();
    child
}
fn wait_rows(child: &mut OwnedFixtureChild, count: usize) -> Vec<Value> {
    let deadline = Instant::now() + CHILD_DEADLINE;
    loop {
        let rows = child.rows().unwrap();
        if rows.len() >= count {
            return rows;
        }
        assert!(
            Instant::now() < deadline,
            "bounded closed child protocol deadline"
        );
        std::thread::sleep(Duration::from_millis(10));
    }
}
fn bytes(nonce: FixtureNonce) -> Vec<u8> {
    let ctx = context(nonce, FixtureAdmission::Reopen, FixtureFault::None);
    let mut storage = open(&ctx).unwrap();
    assert_validated(&ctx.control);
    let length = storage.validate_custody().unwrap();
    assert!(length <= 1024 * 1024);
    storage.seek(SeekFrom::Start(0)).unwrap();
    let mut bytes = Vec::new();
    Read::by_ref(&mut storage)
        .take(1024 * 1024 + 1)
        .read_to_end(&mut bytes)
        .unwrap();
    assert_eq!(bytes.len() as u64, length);
    assert_eq!(storage.validate_custody().unwrap(), length);
    bytes
}
fn reopen_codec(nonce: FixtureNonce, expected_count: usize) {
    let ctx = context(nonce, FixtureAdmission::Reopen, FixtureFault::None);
    let journal = FileJournal::open_retained(open(&ctx).unwrap()).unwrap();
    assert_validated(&ctx.control);
    let expected: Vec<_> = (0..expected_count).map(|i| host_record(i != 0)).collect();
    assert_eq!(journal.records(), expected.as_slice());
}
#[derive(Clone, Copy, Debug)]
enum FixtureObject {
    Nonce,
    Version,
    Wal,
}
struct SealedFixtureObject {
    file: File,
    identity: ObjectStamp,
    directory: bool,
}
fn sealed_object(
    owner: &NativePrivateJournalStorage,
    object: FixtureObject,
) -> Result<SealedFixtureObject, StorageFailure> {
    owner.with(|guard, capsule| {
        validate(guard, capsule)?;
        let context = capsule.fixture.as_ref().ok_or(StorageFailure::Unsafe)?;
        if context.admission != FixtureAdmission::Fresh {
            return Err(StorageFailure::Unsafe);
        }
        let known = capsule
            .selected
            .as_ref()
            .ok_or(StorageFailure::Unsafe)?
            .components
            .len();
        let observation = match object {
            FixtureObject::Nonce => capsule.directories[known + 2].observation.as_ref(),
            FixtureObject::Version => capsule.directories[known + 3].observation.as_ref(),
            FixtureObject::Wal => capsule.leaf_observation.as_ref(),
        }
        .ok_or(StorageFailure::Unsafe)?;
        let directory = !matches!(object, FixtureObject::Wal);
        let path: Vec<_> = observation.path.iter().copied().chain(Some(0)).collect();
        let handle = unsafe {
            CreateFileW(
                PCWSTR(path.as_ptr()),
                (READ_CONTROL | WRITE_DAC | FILE_READ_ATTRIBUTES).0,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                None,
                OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT
                    | if directory {
                        FILE_FLAG_BACKUP_SEMANTICS
                    } else {
                        FILE_FLAGS_AND_ATTRIBUTES(0)
                    },
                None,
            )
        }
        .map_err(win_failure)?;
        let file = unsafe { File::from_raw_handle(handle.0) };
        let identity = stamp(&file)?;
        if identity.volume != observation.identity.volume
            || identity.id != observation.identity.file
            || identity.directory != directory
        {
            return Err(StorageFailure::Unsafe);
        }
        private_security(
            file_handle(&file),
            capsule.sid.as_ref().ok_or(StorageFailure::Unsafe)?,
            directory,
        )?;
        Ok(SealedFixtureObject {
            file,
            identity,
            directory,
        })
    })
}
fn fixture_descriptor(file: &File) -> Result<LocalAllocation, StorageFailure> {
    let mut descriptor = PSECURITY_DESCRIPTOR::default();
    let status = unsafe {
        GetSecurityInfo(
            file_handle(file),
            SE_FILE_OBJECT,
            OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
            None,
            None,
            None,
            None,
            Some(&mut descriptor),
        )
    };
    let allocation = LocalAllocation(descriptor.0);
    if status.0 != 0 || allocation.0.is_null() {
        return Err(StorageFailure::Unavailable);
    }
    let length = unsafe { GetSecurityDescriptorLength(descriptor) } as usize;
    if !(20..=4096).contains(&length) {
        return Err(StorageFailure::Unsafe);
    }
    if !(unsafe { IsValidSecurityDescriptor(descriptor) }).as_bool() {
        return Err(StorageFailure::Unsafe);
    }
    Ok(allocation)
}
fn descriptor_bytes(file: &File) -> Result<Vec<u8>, StorageFailure> {
    let allocation = fixture_descriptor(file)?;
    let length =
        unsafe { GetSecurityDescriptorLength(PSECURITY_DESCRIPTOR(allocation.0)) } as usize;
    // SAFETY: the owned native descriptor was validated and bounded above.
    Ok(unsafe { std::slice::from_raw_parts(allocation.0.cast::<u8>(), length) }.to_vec())
}
fn fixture_dacl_bytes(allocation: &LocalAllocation) -> Result<Vec<u8>, StorageFailure> {
    let descriptor = PSECURITY_DESCRIPTOR(allocation.0);
    let length = unsafe { GetSecurityDescriptorLength(descriptor) } as usize;
    let mut present = BOOL::default();
    let mut defaulted = BOOL::default();
    let mut dacl = ptr::null_mut();
    unsafe { GetSecurityDescriptorDacl(descriptor, &mut present, &mut dacl, &mut defaulted) }
        .map_err(win_failure)?;
    if !present.as_bool()
        || defaulted.as_bool()
        || !within(dacl.cast(), size_of::<ACL>(), allocation.0, length)
    {
        return Err(StorageFailure::Unsafe);
    }
    // SAFETY: the complete ACL header lies in the owned descriptor allocation.
    let header = unsafe { ptr::read_unaligned(dacl) };
    let acl_length = header.AclSize as usize;
    if acl_length < size_of::<ACL>()
        || !within(dacl.cast(), acl_length, allocation.0, length)
        || !(unsafe { IsValidAcl(dacl) }).as_bool()
    {
        return Err(StorageFailure::Unsafe);
    }
    // SAFETY: the valid ACL's full declared extent is bounded by its owner.
    Ok(unsafe { std::slice::from_raw_parts(dacl.cast::<u8>(), acl_length) }.to_vec())
}
fn sealed_contents(object: &SealedFixtureObject) -> Result<Vec<u8>, StorageFailure> {
    if object.directory || stamp(&object.file)? != object.identity {
        return Err(StorageFailure::Unsafe);
    }
    let path: Vec<_> = physical_name(file_handle(&object.file))?
        .into_iter()
        .chain(Some(0))
        .collect();
    let handle = unsafe {
        CreateFileW(
            PCWSTR(path.as_ptr()),
            FILE_GENERIC_READ.0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            None,
            OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT,
            None,
        )
    }
    .map_err(win_failure)?;
    let mut file = unsafe { File::from_raw_handle(handle.0) };
    if stamp(&file)? != object.identity || object.identity.length > 1024 * 1024 {
        return Err(StorageFailure::Unsafe);
    }
    let mut contents = Vec::new();
    Read::by_ref(&mut file)
        .take(1024 * 1024 + 1)
        .read_to_end(&mut contents)
        .map_err(|_| StorageFailure::Unavailable)?;
    if contents.len() as u64 != object.identity.length || stamp(&file)? != object.identity {
        return Err(StorageFailure::Unsafe);
    }
    Ok(contents)
}
#[derive(Clone, Copy, Debug)]
enum DaclVariant {
    Unprotected,
    WorldRead,
    Reduced,
    WrongInheritance,
}
fn change_dacl(object: &SealedFixtureObject, variant: DaclVariant) -> Result<(), StorageFailure> {
    if stamp(&object.file)? != object.identity {
        return Err(StorageFailure::Unsafe);
    }
    let sid = process_sid()?;
    private_security(file_handle(&object.file), &sid, object.directory)?;
    let user = sid_string(&sid)?;
    let flags = if object.directory { "OICI" } else { "" };
    let flags = if matches!(variant, DaclVariant::WrongInheritance) {
        if object.directory { "" } else { "OICI" }
    } else {
        flags
    };
    let protection = if matches!(variant, DaclVariant::Unprotected) {
        ""
    } else {
        "P"
    };
    let mask = if matches!(variant, DaclVariant::Reduced) {
        "FR"
    } else {
        "FA"
    };
    let world = if matches!(variant, DaclVariant::WorldRead) {
        "(A;;FR;;;WD)"
    } else {
        ""
    };
    let allocation = descriptor_from_sddl(&format!(
        "O:{user}D:{protection}(A;{flags};{mask};;;{user}){world}"
    ))?;
    let mut present = BOOL::default();
    let mut defaulted = BOOL::default();
    let mut dacl = ptr::null_mut();
    unsafe {
        GetSecurityDescriptorDacl(
            PSECURITY_DESCRIPTOR(allocation.0),
            &mut present,
            &mut dacl,
            &mut defaulted,
        )
    }
    .map_err(win_failure)?;
    if !present.as_bool() || dacl.is_null() {
        return Err(StorageFailure::Unsafe);
    }
    let flags = DACL_SECURITY_INFORMATION
        | if matches!(variant, DaclVariant::Unprotected) {
            UNPROTECTED_DACL_SECURITY_INFORMATION
        } else {
            PROTECTED_DACL_SECURITY_INFORMATION
        };
    let raw_leaf_inheritance =
        !object.directory && matches!(variant, DaclVariant::WrongInheritance);
    if raw_leaf_inheritance {
        // SetSecurityInfo normalizes inheritance flags on a leaf. The closed
        // negative fixture needs those exact invalid flags to persist, not an
        // API-success observation followed by a canonical descriptor.
        // SAFETY: this sealed fixture handle retains WRITE_DAC, and the complete
        // owned descriptor remains live through the synchronous native call.
        let status = unsafe {
            windows::Wdk::Storage::FileSystem::NtSetSecurityObject(
                file_handle(&object.file),
                flags.0,
                PSECURITY_DESCRIPTOR(allocation.0),
            )
        };
        if status.0 != 0 {
            return Err(StorageFailure::Unavailable);
        }
    } else {
        let status = unsafe {
            windows::Win32::Security::Authorization::SetSecurityInfo(
                file_handle(&object.file),
                SE_FILE_OBJECT,
                flags,
                None,
                None,
                Some(dacl),
                None,
            )
        };
        if status.0 != 0 {
            return Err(StorageFailure::Unavailable);
        }
    }
    let observed = fixture_descriptor(&object.file)?;
    if raw_leaf_inheritance && fixture_dacl_bytes(&observed)? != fixture_dacl_bytes(&allocation)? {
        return Err(StorageFailure::Unsafe);
    }
    // Every declared negative seed must actually be noncanonical before the
    // reopen/live-drift operation can establish refusal without repair.
    if inspect_descriptor(&observed, &sid, object.directory).is_ok() {
        return Err(StorageFailure::Unsafe);
    }
    if stamp(&object.file)? != object.identity {
        return Err(StorageFailure::Unsafe);
    }
    Ok(())
}

#[test]
fn fixture_nonce_requires_canonical_v4_and_refuses_all_aliases() {
    let valid = b"01234567-89ab-4cde-8fab-0123456789ab";
    assert!(FixtureNonce::parse(valid).is_ok());
    for index in 0..36 {
        for alias in [b'G', b'/', b' ', 0, b'\n'] {
            let mut changed = *valid;
            changed[index] = alias;
            assert!(FixtureNonce::parse(&changed).is_err());
        }
    }
    for alias in [
        "01234567-89AB-4cde-8fab-0123456789ab",
        "01234567-89ab-1cde-8fab-0123456789ab",
        "01234567-89ab-4cde-7fab-0123456789ab",
        "{01234567-89ab-4cde-8fab-0123456789ab}",
        "0123456789ab4cde8fab0123456789ab",
        "01234567-89ab-4cde-8fab-0123456789ab ",
    ] {
        assert!(FixtureNonce::parse(alias.as_bytes()).is_err());
    }
}
#[test]
fn fixture_child_protocol_has_closed_phase_and_bounded_frame() {
    let pipe = PipeState::new().unwrap();
    assert!(
        pipe.cleanup_known(),
        "an uncreated pipe has no EOF obligation"
    );
    pipe.required.store(true, Ordering::Release);
    assert!(!pipe.started.load(Ordering::Acquire));
    assert!(
        !pipe.cleanup_known(),
        "a created pipe needs EOF even without a reader"
    );
    pipe.failed.store(true, Ordering::Release);
    pipe.eof.store(true, Ordering::Release);
    assert!(
        !pipe.cleanup_known(),
        "a failed reader cannot manufacture known cleanup"
    );
    pipe.failed.store(false, Ordering::Release);
    assert!(
        pipe.cleanup_known(),
        "successful EOF settles the actual pipe obligation"
    );
    let nonce = FixtureNonce::parse(b"01234567-89ab-4cde-8fab-0123456789ab").unwrap();
    for phase in [
        ChildPhase::TryOpen,
        ChildPhase::HoldOwner,
        ChildPhase::OpenAndRelease,
        ChildPhase::DurableAppend,
        ChildPhase::TornAppend,
        ChildPhase::CorruptComplete,
    ] {
        let input = ChildStartup { nonce, phase };
        assert_eq!(ChildStartup::parse(&input.frame()).unwrap(), input);
        let mut extended = input.frame();
        extended.extend_from_slice(b"x");
        assert!(ChildStartup::parse(&extended).is_err());
        assert!(ChildStartup::parse(&input.frame()[..input.frame().len() - 1]).is_err());
    }
    for frame in [
        format!("{CHILD_ROLE} {} unknown\n", nonce.component()),
        format!("{CHILD_ROLE}  {} try-open\n", nonce.component()),
        format!("{CHILD_ROLE} {} try-open\r\n", nonce.component()),
        format!("{CHILD_ROLE} {} try-open extra\n", nonce.component()),
        "x".repeat(129),
    ] {
        assert!(ChildStartup::parse(frame.as_bytes()).is_err());
    }
    assert!(ChildStartup::read(&mut io::Cursor::new(vec![b'x'; 129])).is_err());
    assert!(release_frame(&mut io::Cursor::new(b"Rx")).is_err());
    assert!(release_frame(&mut io::Cursor::new(b"R")).is_ok());
}
#[test]
fn fixture_extension_bounds_are_checked_before_namespace_effects() {
    let nonce = FixtureNonce::parse(b"01234567-89ab-4cde-8fab-0123456789ab").unwrap();
    let namespace = Namespace::Fixture(context(nonce, FixtureAdmission::Fresh, FixtureFault::None));
    assert_eq!(namespace.directory_limit(), MAX_COMPONENTS + 4);
    let prepared = Capsule::empty()
        .prepare_fixture(context(nonce, FixtureAdmission::Fresh, FixtureFault::None))
        .unwrap();
    assert_eq!(prepared.directories.len(), 0);
    assert!(prepared.directories.capacity() >= namespace.directory_limit());
    assert!(prepared.fixture.is_some());
    let components = namespace.components();
    assert_eq!(components.len(), 3);
    assert_eq!(
        components[0],
        "STFCModBridgeNextFixtures"
            .encode_utf16()
            .collect::<Vec<_>>()
    );
    assert_eq!(
        components[1],
        nonce.component().encode_utf16().collect::<Vec<_>>()
    );
    let mut selected = Route {
        root: [67, 58, 92, 0],
        components: vec![Box::from([65])],
        full: vec![65; MAX_PATH_UNITS],
    };
    assert!(namespace.extension_bounds(&selected).is_err());
    selected.full = vec![67, 58, 92, 65];
    selected.components = (0..=MAX_COMPONENTS).map(|_| Box::from([65u16])).collect();
    assert!(namespace.extension_bounds(&selected).is_err());
    selected.components = (0..MAX_COMPONENTS).map(|_| Box::from([65u16])).collect();
    assert!(namespace.extension_bounds(&selected).is_ok());
    selected.components = vec![Box::from([65])];
    assert!(namespace.extension_bounds(&selected).is_ok());
}

#[test]
#[ignore = "selected native ordinary-user fixture gate only"]
fn native_private_journal_fresh_reopen_owner_acl_volume_and_namespace() {
    native_journal_preflight("private_journal::native_fixtures::native_private_journal_fresh_reopen_owner_acl_volume_and_namespace")
        .expect("actual own-process/thread ordinary medium context before journal effects");
    let nonce = FixtureNonce::fresh().unwrap();
    let fresh = context(nonce, FixtureAdmission::Fresh, FixtureFault::None);
    let owner = open(&fresh).unwrap();
    assert_validated(&fresh.control);
    let (identity, count) = owner
        .with(|guard, capsule| {
            validate(guard, capsule)?;
            let observation = capsule.leaf_observation.as_ref().unwrap();
            Ok((
                ObjectStamp {
                    volume: observation.identity.volume,
                    id: observation.identity.file,
                    length: observation.length,
                    directory: false,
                },
                capsule.directories.len(),
            ))
        })
        .unwrap();
    drop(owner);
    let reopen = context(nonce, FixtureAdmission::Reopen, FixtureFault::None);
    let owner = open(&reopen).unwrap();
    assert_validated(&reopen.control);
    let reopened = owner
        .with(|guard, capsule| {
            validate(guard, capsule)?;
            stamp(capsule.leaf.as_ref().unwrap())
        })
        .unwrap();
    assert_eq!(identity, reopened);
    report(
        "fresh_reopen",
        json!({"nonce":nonce.component(),"sameIdentity":true,"directoryCount":count,"currentUserOwner":true,"exactPrivateDacl":true,
        "localWritableAclNtfs":true,"leafLinks":1,"fresh":trace(&fresh.control),"reopen":trace(&reopen.control)}),
    );
}
#[test]
#[ignore = "selected native ordinary-user fixture gate only"]
fn native_private_journal_dacl_variants_and_live_drift_refuse_without_repair() {
    native_journal_preflight("private_journal::native_fixtures::native_private_journal_dacl_variants_and_live_drift_refuse_without_repair")
        .expect("actual own-process/thread ordinary medium context before journal effects");
    let mut observations = Vec::new();
    for object_kind in [
        FixtureObject::Nonce,
        FixtureObject::Version,
        FixtureObject::Wal,
    ] {
        for variant in [
            DaclVariant::Unprotected,
            DaclVariant::WorldRead,
            DaclVariant::Reduced,
            DaclVariant::WrongInheritance,
        ] {
            let nonce = FixtureNonce::fresh().unwrap();
            let fresh = context(nonce, FixtureAdmission::Fresh, FixtureFault::None);
            let owner = open(&fresh).unwrap();
            assert_validated(&fresh.control);
            let object = sealed_object(&owner, object_kind).unwrap();
            drop(owner);
            change_dacl(&object, variant).unwrap();
            let before = descriptor_bytes(&object.file).unwrap();
            let before_stamp = stamp(&object.file).unwrap();
            let next = context(nonce, FixtureAdmission::Reopen, FixtureFault::None);
            assert!(open(&next).is_err());
            assert_eq!(descriptor_bytes(&object.file).unwrap(), before);
            assert_eq!(stamp(&object.file).unwrap(), before_stamp);
            report(
                "dacl_variant",
                json!({"nonce":nonce.component(),"object":format!("{object_kind:?}"),"variant":format!("{variant:?}"),"descriptorUnchanged":true,"identityLengthUnchanged":true,"trace":trace(&next.control)}),
            );
            observations.push(json!({"nonce":nonce.component(),"object":format!("{object_kind:?}"),"variant":format!("{variant:?}")}));
        }
    }
    let nonce = FixtureNonce::fresh().unwrap();
    let fresh = context(nonce, FixtureAdmission::Fresh, FixtureFault::None);
    let mut owner = open(&fresh).unwrap();
    assert_validated(&fresh.control);
    owner.write_all(b"fixture-live-drift").unwrap();
    owner.sync_durable().unwrap();
    let object = sealed_object(&owner, FixtureObject::Wal).unwrap();
    // Length is captured after the known fixture write, before the closed DACL change.
    change_dacl(&object, DaclVariant::WorldRead).unwrap();
    let descriptor = descriptor_bytes(&object.file).unwrap();
    let before = stamp(&object.file).unwrap();
    assert!(owner.write_all(b"must-not-write").is_err());
    assert!(owner.validate_custody().is_err());
    assert_eq!(stamp(&object.file).unwrap(), before);
    assert_eq!(descriptor_bytes(&object.file).unwrap(), descriptor);
    drop(owner);
    assert_eq!(sealed_contents(&object).unwrap(), b"fixture-live-drift");
    report(
        "dacl_refusal",
        json!({"variants":observations,"liveBytesUnchanged":true,"liveWriteRefusedBeforeLengthChange":true,"latchedRefusal":true}),
    );
}
fn sharing_attempt(path: &[u16], directory: bool, access: FILE_ACCESS_RIGHTS) -> Result<(), u32> {
    let path: Vec<_> = path.iter().copied().chain(Some(0)).collect();
    let result = unsafe {
        CreateFileW(
            PCWSTR(path.as_ptr()),
            access.0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            None,
            OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT
                | if directory {
                    FILE_FLAG_BACKUP_SEMANTICS
                } else {
                    FILE_FLAGS_AND_ATTRIBUTES(0)
                },
            None,
        )
    };
    match result {
        Ok(handle) => {
            drop(unsafe { File::from_raw_handle(handle.0) });
            Ok(())
        }
        Err(error) => Err(error.code().0 as u32 & 0xffff),
    }
}
#[test]
#[ignore = "selected native ordinary-user fixture gate only"]
fn native_private_journal_leaf_and_private_directory_sharing_are_retained() {
    native_journal_preflight("private_journal::native_fixtures::native_private_journal_leaf_and_private_directory_sharing_are_retained")
        .expect("actual own-process/thread ordinary medium context before journal effects");
    let nonce = FixtureNonce::fresh().unwrap();
    let ctx = context(nonce, FixtureAdmission::Fresh, FixtureFault::None);
    let owner = open(&ctx).unwrap();
    assert_validated(&ctx.control);
    let paths = owner
        .with(|guard, capsule| {
            validate(guard, capsule)?;
            let known = capsule.selected.as_ref().unwrap().components.len();
            let mut paths: Vec<_> = capsule.directories[known + 1..]
                .iter()
                .map(|directory| (directory.observation.as_ref().unwrap().path.clone(), true))
                .collect();
            paths.push((
                capsule.leaf_observation.as_ref().unwrap().path.clone(),
                false,
            ));
            Ok(paths)
        })
        .unwrap();
    for (path, directory) in &paths {
        for access in [FILE_WRITE_DATA, DELETE] {
            assert_eq!(sharing_attempt(path, *directory, access), Err(32));
        }
    }
    drop(owner);
    for (path, directory) in &paths {
        for access in [FILE_WRITE_DATA, DELETE] {
            sharing_attempt(path, *directory, access)
                .expect("writer/delete admission after release");
        }
    }
    report(
        "sharing",
        json!({"nonce":nonce.component(),"independentPrivateObjects":paths.len(),"win32SharingViolation":32,"writerAndDeleteRefused":true,"sameAdmissionsAfterRelease":true,"trace":trace(&ctx.control)}),
    );
}
#[test]
#[ignore = "selected native ordinary-user fixture gate only"]
fn native_private_journal_cross_process_owner_refusal_and_release() {
    native_journal_preflight("private_journal::native_fixtures::native_private_journal_cross_process_owner_refusal_and_release")
        .expect("actual own-process/thread ordinary medium context before journal effects");
    let nonce = seed();
    let mut first = child(nonce, ChildPhase::HoldOwner);
    let ready = first.ready().unwrap();
    let mut second = child(nonce, ChildPhase::TryOpen);
    let rows = wait_rows(&mut second, 2);
    assert_eq!(rows.len(), 2);
    assert_eq!(rows[1]["event"], "OpenRefused");
    let events = rows[1]["details"]["trace"]["events"].as_array().unwrap();
    let last = events
        .iter()
        .rev()
        .find(|event| event["event"] == "CreateReturned")
        .unwrap();
    assert_eq!(last["primary"].as_i64().unwrap() as i32 as u32, 0xc0000043);
    assert_eq!(last["phase"], "FixturesRoot");
    let second_cleanup = second.finish(false, true).unwrap();
    first.release().unwrap();
    let first_cleanup = first.finish(false, true).unwrap();
    let mut next = child(nonce, ChildPhase::OpenAndRelease);
    let reopened = wait_rows(&mut next, 3);
    assert_eq!(reopened[1]["event"], "Opened");
    assert_eq!(reopened[2]["event"], "Released");
    let next_cleanup = next.finish(false, true).unwrap();
    report(
        "cross_process",
        json!({"nonce":nonce.component(),"ready":ready,"refusal":rows,"reopen":reopened,
        "childBindings":[first.binding(),second.binding(),next.binding()],
        "cleanup":[cleanup_row(first_cleanup),cleanup_row(second_cleanup),cleanup_row(next_cleanup)]}),
    );
}
#[test]
#[ignore = "selected native ordinary-user fixture gate only"]
fn native_private_journal_every_completed_constructor_failure_repeats_full_flush() {
    native_journal_preflight("private_journal::native_fixtures::native_private_journal_every_completed_constructor_failure_repeats_full_flush")
        .expect("actual own-process/thread ordinary medium context before journal effects");
    let mut observations = Vec::new();
    for (index, stage) in FIXTURE_FLUSH_STAGES.into_iter().enumerate() {
        let nonce = FixtureNonce::fresh().unwrap();
        let first = context(
            nonce,
            FixtureAdmission::Fresh,
            FixtureFault::AfterFlush(stage),
        );
        assert!(open(&first).is_err());
        assert!(!first.control.trace_failed.get());
        assert!(first.control.fired.get());
        let terminal = first.control.terminal.get().unwrap();
        assert_eq!(
            terminal.kind,
            FixtureFailureKind::InjectedAfterNativeSuccess
        );
        assert_eq!(terminal.phase, FixturePhase::Flush(stage));
        assert!(
            terminal
                .primary
                .is_some_and(|primary| primary >= 0 && primary != PENDING.0)
        );
        let events = first.control.events.borrow();
        let accepted: Vec<_> = events
            .iter()
            .filter_map(|event| {
                if let FixtureEvent::NativeFlushAccepted(stage, primary) = event {
                    Some((*stage, *primary))
                } else {
                    None
                }
            })
            .collect();
        assert_eq!(
            accepted.iter().map(|(stage, _)| *stage).collect::<Vec<_>>(),
            FIXTURE_FLUSH_STAGES[..=index]
        );
        assert!(
            accepted
                .iter()
                .all(|(_, primary)| *primary >= 0 && *primary != PENDING.0)
        );
        assert_eq!(
            events.last(),
            Some(&FixtureEvent::FaultInjected(
                stage,
                terminal.primary.unwrap()
            ))
        );
        drop(events);
        let next = context(nonce, FixtureAdmission::Reopen, FixtureFault::None);
        let owner = open(&next).unwrap();
        assert_validated(&next.control);
        drop(owner);
        report(
            "constructor_failure_stage",
            json!({"nonce":nonce.component(),"stage":format!("{stage:?}"),"first":trace(&first.control),"reopen":trace(&next.control)}),
        );
        observations.push(json!({"nonce":nonce.component(),"stage":format!("{stage:?}")}));
    }
    report(
        "completed_constructor_failures",
        json!({"attempts":observations}),
    );
}
#[test]
#[ignore = "selected native ordinary-user fixture gate only"]
fn native_private_journal_codec_durable_append_survives_owned_child_kill() {
    native_journal_preflight("private_journal::native_fixtures::native_private_journal_codec_durable_append_survives_owned_child_kill")
        .expect("actual own-process/thread ordinary medium context before journal effects");
    let nonce = seed();
    let mut child = child(nonce, ChildPhase::DurableAppend);
    let ready = child.ready().unwrap();
    let cleanup = child.finish(true, false).unwrap();
    let before = bytes(nonce);
    assert_eq!(before.len() as u64, records_length(2));
    reopen_codec(nonce, 2);
    assert_eq!(bytes(nonce), before);
    reopen_codec(nonce, 2);
    report(
        "codec_durable",
        json!({"nonce":nonce.component(),"ready":ready,"childBinding":child.binding(),"cleanup":cleanup_row(cleanup),"records":2,"byteLength":before.len(),"sha256":Sha256::digest(&before).iter().map(|b|format!("{b:02x}")).collect::<String>()}),
    );
}
#[test]
#[ignore = "selected native ordinary-user fixture gate only"]
fn native_private_journal_codec_torn_append_repairs_after_owned_child_kill() {
    native_journal_preflight("private_journal::native_fixtures::native_private_journal_codec_torn_append_repairs_after_owned_child_kill")
        .expect("actual own-process/thread ordinary medium context before journal effects");
    let nonce = seed();
    let mut child = child(nonce, ChildPhase::TornAppend);
    let ready = child.ready().unwrap();
    let cleanup = child.finish(true, false).unwrap();
    let before = bytes(nonce);
    assert_eq!(before.len() as u64, records_length(1) + 15);
    reopen_codec(nonce, 1);
    let repaired = bytes(nonce);
    assert_eq!(repaired.len() as u64, records_length(1));
    assert_eq!(repaired, before[..repaired.len()]);
    reopen_codec(nonce, 1);
    assert_eq!(bytes(nonce), repaired);
    report(
        "codec_torn",
        json!({"nonce":nonce.component(),"ready":ready,"childBinding":child.binding(),"cleanup":cleanup_row(cleanup),"beforeLength":before.len(),"repairedLength":repaired.len(),"secondOpenStable":true,"sha256":Sha256::digest(&repaired).iter().map(|b|format!("{b:02x}")).collect::<String>()}),
    );
}
#[test]
#[ignore = "selected native ordinary-user fixture gate only"]
fn native_private_journal_codec_complete_corruption_refuses_without_repair() {
    native_journal_preflight("private_journal::native_fixtures::native_private_journal_codec_complete_corruption_refuses_without_repair")
        .expect("actual own-process/thread ordinary medium context before journal effects");
    let nonce = seed();
    let mut child = child(nonce, ChildPhase::CorruptComplete);
    let ready = child.ready().unwrap();
    let cleanup = child.finish(true, false).unwrap();
    let before = bytes(nonce);
    assert_eq!(before.len() as u64, records_length(2));
    assert_eq!(before[16], b'!');
    let ctx = context(nonce, FixtureAdmission::Reopen, FixtureFault::None);
    let error = FileJournal::open_retained(open(&ctx).unwrap())
        .err()
        .expect("complete corruption refusal");
    assert_eq!(error, KernelFailure::CorruptJournal);
    assert_eq!(bytes(nonce), before);
    report(
        "codec_corrupt",
        json!({"nonce":nonce.component(),"ready":ready,"childBinding":child.binding(),"cleanup":cleanup_row(cleanup),"corruptJournal":true,"bytesUnchanged":true,"sha256":Sha256::digest(&before).iter().map(|b|format!("{b:02x}")).collect::<String>()}),
    );
}
#[test]
#[ignore = "selected native ordinary-user fixture gate only"]
fn native_private_journal_child_custody_failure_cleanup_is_observed() {
    native_journal_preflight("private_journal::native_fixtures::native_private_journal_child_custody_failure_cleanup_is_observed")
        .expect("actual own-process/thread ordinary medium context before journal effects");
    let nonce = seed();
    let mut early = OwnedFixtureChild::prepared(ChildStartup {
        nonce,
        phase: ChildPhase::HoldOwner,
    })
    .unwrap();
    early.spawn(false).unwrap();
    early.close_startup();
    let cleanup = early.finish(false, false).unwrap();
    assert_eq!(cleanup.exit_success, Some(false));
    assert!(!fixture_poisoned());
    let mut held = child(nonce, ChildPhase::HoldOwner);
    let ready = held.ready().unwrap();
    let observation = held.cleanup.clone();
    let held_binding = held.binding();
    // Closed controller error reaches exactly the same nonpanicking Drop cleanup.
    drop(held);
    assert_eq!(
        observation.get().state,
        CleanupState::ReapedAndReadersJoined
    );
    assert!(!observation.get().kill_error);
    assert!(!observation.get().reader_error);
    assert!(!observation.get().wait_error);
    assert!(!fixture_poisoned());
    let next = context(nonce, FixtureAdmission::Reopen, FixtureFault::None);
    let owner = open(&next).unwrap();
    assert_validated(&next.control);
    drop(owner);
    report(
        "child_failure_cleanup",
        json!({"nonce":nonce.component(),"childBindings":[early.binding(),held_binding],"earlyEof":cleanup_row(cleanup),"ready":ready,"dropCleanup":cleanup_row(observation.get()),"reopen":trace(&next.control)}),
    );
}
