//! Retained backend-only private journal custody. No path or raw handle is
//! exposed, and construction never repairs an existing object's permissions.
//! Native qualification remains separate from the defensive pending protocol.
use bridge_journal_io::{JournalStorage, StorageFailure};
use std::cell::{Cell, RefCell, RefMut, UnsafeCell};
use std::ffi::c_void;
use std::fs::File;
use std::io::{self, Read, Seek, SeekFrom, Write};
use std::marker::PhantomData;
use std::mem::{ManuallyDrop, size_of, size_of_val};
use std::os::windows::io::{AsRawHandle, FromRawHandle};
use std::ptr;
use std::rc::Rc;
use std::sync::atomic::{AtomicU8, Ordering};
use windows::Wdk::Foundation::OBJECT_ATTRIBUTES;
use windows::Wdk::Storage::FileSystem::{
    FILE_BASIC_INFORMATION, FILE_DIRECTORY_FILE as NT_DIRECTORY, FILE_ID_INFORMATION,
    FILE_INFORMATION_CLASS, FILE_NON_DIRECTORY_FILE as NT_FILE, FILE_OPEN as NT_OPEN,
    FILE_OPEN_IF as NT_OPEN_IF, FILE_OPEN_REPARSE_POINT as NT_NOFOLLOW, FILE_STANDARD_INFORMATION,
    FILE_SYNCHRONOUS_IO_NONALERT as NT_SYNCHRONOUS, FileBasicInformation, FileFsDeviceInformation,
    FileIdInformation, FileStandardInformation, NtCreateFile, NtFlushBuffersFileEx,
    NtQueryInformationFile, NtQueryVolumeInformationFile,
};
use windows::Wdk::System::SystemServices::{
    FILE_FS_DEVICE_INFORMATION, FILE_REMOTE_DEVICE, FILE_REMOTE_DEVICE_VSMB,
};
use windows::Win32::Foundation::{
    CloseHandle, ERROR_IO_PENDING, ERROR_NO_TOKEN, HLOCAL, LocalFree, OBJ_CASE_INSENSITIVE,
    OBJ_DONT_REPARSE, UNICODE_STRING,
};
use windows::Win32::Foundation::{HANDLE, NTSTATUS};
use windows::Win32::Globalization::{CSTR_EQUAL, CompareStringOrdinal};
use windows::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, GetSecurityInfo,
    SDDL_REVISION_1, SE_FILE_OBJECT,
};
use windows::Win32::Security::*;
use windows::Win32::Storage::FileSystem::*;
use windows::Win32::System::Com::{
    COINIT_APARTMENTTHREADED, CoInitializeEx, CoTaskMemFree, CoUninitialize,
};
use windows::Win32::System::IO::{IO_STATUS_BLOCK, OVERLAPPED};
use windows::Win32::System::SystemServices::{
    ACCESS_ALLOWED_ACE_TYPE, FILE_PERSISTENT_ACLS, FILE_READ_ONLY_VOLUME,
};
use windows::Win32::System::Threading::{
    GetCurrentProcess, GetCurrentThread, OpenProcessToken, OpenThreadToken,
};
use windows::Win32::System::WindowsProgramming::DRIVE_FIXED;
use windows::Win32::UI::Shell::{FOLDERID_LocalAppData, KF_FLAG_NO_PACKAGE_REDIRECTION};
use windows::core::{BOOL, GUID, HRESULT, PCWSTR, PWSTR};

const FREE: u8 = 0;
const RESERVED: u8 = 1;
const QUARANTINED: u8 = 2;
const PENDING: NTSTATUS = NTSTATUS(0x103);
static JOURNAL_RESERVATION: AtomicU8 = AtomicU8::new(FREE);

struct Reservation(&'static AtomicU8);
impl Reservation {
    fn acquire(state: &'static AtomicU8) -> Result<Self, StorageFailure> {
        state
            .compare_exchange(FREE, RESERVED, Ordering::AcqRel, Ordering::Acquire)
            .map(|_| Self(state))
            .map_err(|state| {
                if state == QUARANTINED {
                    StorageFailure::Unavailable
                } else {
                    StorageFailure::Busy
                }
            })
    }
    fn quarantine(&self) {
        self.0.store(QUARANTINED, Ordering::Release);
    }
}
impl Drop for Reservation {
    fn drop(&mut self) {
        // Quarantine is permanent. A completed guard releases only after its
        // entire custody allocation has been destroyed.
        let _ = self
            .0
            .compare_exchange(RESERVED, FREE, Ordering::AcqRel, Ordering::Acquire);
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Completion {
    Idle,
    InFlight,
    Quarantined,
}

/// The one capsule owns all native request allocations and all ancestry. No
/// destructor can drop an outstanding output independently from that custody.
/// RefCell permits custody checks through JournalStorage::validate_custody.
struct CustodyGuard<C> {
    capsule: Option<Box<RefCell<C>>>,
    completion: Cell<Completion>,
    reservation: ManuallyDrop<Reservation>,
    _local: PhantomData<Rc<()>>,
}
impl<C> CustodyGuard<C> {
    fn new(reservation: Reservation, capsule: C) -> Self {
        Self {
            capsule: Some(Box::new(RefCell::new(capsule))),
            completion: Cell::new(Completion::Idle),
            reservation: ManuallyDrop::new(reservation),
            _local: PhantomData,
        }
    }
    fn borrow(&self) -> Result<RefMut<'_, C>, StorageFailure> {
        if self.completion.get() != Completion::Idle {
            return Err(StorageFailure::Unavailable);
        }
        self.capsule
            .as_ref()
            .ok_or(StorageFailure::Unavailable)?
            .try_borrow_mut()
            .map_err(|_| StorageFailure::Busy)
    }
    fn arm(&self) -> Result<(), StorageFailure> {
        if self.completion.get() != Completion::Idle {
            return Err(StorageFailure::Unavailable);
        }
        self.completion.set(Completion::InFlight);
        Ok(())
    }
    fn finish(&self, primary: NTSTATUS) -> Result<(), StorageFailure> {
        // Classify immediately after FFI, before error conversion or reading
        // any outputs. STATUS_PENDING is positive and must precede NT_SUCCESS.
        if self.completion.get() != Completion::InFlight {
            return Err(StorageFailure::Unavailable);
        }
        if primary == PENDING {
            self.completion.set(Completion::Quarantined);
            self.reservation.quarantine();
            return Err(StorageFailure::Unavailable);
        }
        self.completion.set(Completion::Idle);
        if primary.0 >= 0 {
            Ok(())
        } else {
            Err(native_failure(primary))
        }
    }
}
impl<C> Drop for CustodyGuard<C> {
    fn drop(&mut self) {
        if self.completion.get() != Completion::Idle {
            self.reservation.quarantine();
            // A Box move preserves pointee addresses. Intentional retention
            // includes RefCell, request graph, handles and the reservation.
            if let Some(capsule) = self.capsule.take() {
                std::mem::forget(capsule);
            }
            return;
        }
        drop(self.capsule.take());
        // SAFETY: completed capsule destruction above precedes the sole
        // reservation destruction; quarantine never takes this branch.
        unsafe { ManuallyDrop::drop(&mut self.reservation) };
    }
}

fn native_failure(status: NTSTATUS) -> StorageFailure {
    match status.0 as u32 {
        0xc000_0043 | 0xc000_0054 | 0xc000_0055 => StorageFailure::Busy,
        0xc000_0022 | 0xc000_0279 | 0xc000_050b => StorageFailure::Unsafe,
        _ => StorageFailure::Unavailable,
    }
}

const MAX_PATH_UNITS: usize = 32_767;
const MAX_COMPONENTS: usize = 128;
const MAX_COMPONENT_UNITS: usize = 255;
const MAX_JOURNAL_BYTES: u64 = 128 * 1024 * 1024;
const STATE_NAMES: [&str; 2] = ["STFCModBridgeNext", "v1"];
const JOURNAL_NAME: &str = "operations.wal";

#[link(name = "shell32")]
unsafe extern "system" {
    #[link_name = "SHGetKnownFolderPath"]
    fn known_folder_path_raw(
        folder: *const GUID,
        flags: u32,
        token: HANDLE,
        path: *mut PWSTR,
    ) -> HRESULT;
}

struct OwnedHandle(HANDLE);
impl Drop for OwnedHandle {
    fn drop(&mut self) {
        // SAFETY: token ownership is unique and acquired by a successful open.
        let _ = unsafe { CloseHandle(self.0) };
    }
}
struct LocalAllocation(*mut c_void);
impl Drop for LocalAllocation {
    fn drop(&mut self) {
        if !self.0.is_null() {
            // SAFETY: the conversion/security APIs return LocalAlloc storage.
            let _ = unsafe { LocalFree(Some(HLOCAL(self.0))) };
        }
    }
}
struct TaskAllocation(PWSTR);
impl Drop for TaskAllocation {
    fn drop(&mut self) {
        if !self.0.is_null() {
            // SAFETY: SHGetKnownFolderPath's allocation has this allocator.
            unsafe { CoTaskMemFree(Some(self.0.0.cast())) };
        }
    }
}
struct Apartment;
impl Apartment {
    fn enter() -> Result<Self, StorageFailure> {
        // SAFETY: initialization is local to this nontransferable actor.
        let status = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
        if status.is_ok() {
            Ok(Self)
        } else {
            Err(StorageFailure::Unavailable)
        }
    }
}
impl Drop for Apartment {
    fn drop(&mut self) {
        // SAFETY: each successful S_OK/S_FALSE is balanced exactly once.
        unsafe { CoUninitialize() };
    }
}

#[derive(Clone)]
struct Route {
    root: [u16; 4],
    components: Vec<Box<[u16]>>,
    full: Vec<u16>,
}
fn validate_component(name: &[u16]) -> Result<(), StorageFailure> {
    if name.is_empty()
        || name.len() > MAX_COMPONENT_UNITS
        || name.ends_with(&[b'.' as u16])
        || name.ends_with(&[b' ' as u16])
        || name == [b'.' as u16]
        || name == [b'.' as u16, b'.' as u16]
        || name.iter().any(|unit| {
            *unit < 32
                || *unit == 127
                || b"\\/:*?\"<>|".iter().any(|byte| *unit == u16::from(*byte))
        })
    {
        return Err(StorageFailure::Unsafe);
    }
    let stem: Vec<u16> = name
        .iter()
        .copied()
        .take_while(|unit| *unit != b'.' as u16)
        .map(|unit| {
            if (b'a' as u16..=b'z' as u16).contains(&unit) {
                unit - 32
            } else {
                unit
            }
        })
        .collect();
    let reserved = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$"]
        .iter()
        .any(|word| stem.iter().copied().eq(word.encode_utf16()));
    let numbered = stem.len() == 4
        && (stem[..3] == [67, 79, 77] || stem[..3] == [76, 80, 84])
        && ((49..=57).contains(&stem[3]) || [0xb9, 0xb2, 0xb3].contains(&stem[3]));
    if reserved || numbered {
        Err(StorageFailure::Unsafe)
    } else {
        Ok(())
    }
}
fn route(path: Vec<u16>) -> Result<Route, StorageFailure> {
    if path.len() < 4
        || path.len() > MAX_PATH_UNITS
        || !((65..=90).contains(&path[0]) || (97..=122).contains(&path[0]))
        || path[1] != b':' as u16
        || path[2] != b'\\' as u16
        || path.contains(&0)
    {
        return Err(StorageFailure::Unsafe);
    }
    let components: Vec<Box<[u16]>> = path[3..]
        .split(|unit| *unit == b'\\' as u16)
        .map(Box::from)
        .collect();
    if components.is_empty() || components.len() > MAX_COMPONENTS {
        return Err(StorageFailure::Unsafe);
    }
    for component in &components {
        validate_component(component)?;
    }
    // Include fixed private names before any namespace effect.
    let extra: usize = STATE_NAMES
        .iter()
        .chain([JOURNAL_NAME].iter())
        .map(|name| name.len() + 1)
        .sum();
    if path
        .len()
        .checked_add(extra)
        .is_none_or(|length| length > MAX_PATH_UNITS)
    {
        return Err(StorageFailure::Unsafe);
    }
    Ok(Route {
        root: [path[0], path[1], path[2], 0],
        components,
        full: path,
    })
}
fn known_folder() -> Result<Route, StorageFailure> {
    let mut output = PWSTR::null();
    // SAFETY: correct system ABI and initialized out slot. The raw declaration
    // preserves allocations even on HRESULT failure, unlike the convenience wrapper.
    let status = unsafe {
        known_folder_path_raw(
            &FOLDERID_LocalAppData,
            KF_FLAG_NO_PACKAGE_REDIRECTION.0 as u32,
            HANDLE::default(),
            &mut output,
        )
    };
    let allocation = TaskAllocation(output);
    if status.is_err() || allocation.0.is_null() {
        return Err(StorageFailure::Unavailable);
    }
    let mut path = Vec::new();
    for index in 0..MAX_PATH_UNITS {
        // SAFETY: successful Known Folder output is allocated and terminated;
        // a bounded scan refuses unsupported length without a fallback.
        let unit = unsafe { *allocation.0.0.add(index) };
        if unit == 0 {
            return route(path);
        }
        path.push(unit);
    }
    Err(StorageFailure::Unsafe)
}
fn win_failure(error: windows::core::Error) -> StorageFailure {
    match error.code().0 as u32 & 0xffff {
        32 | 33 => StorageFailure::Busy,
        5 => StorageFailure::Unsafe,
        _ => StorageFailure::Unavailable,
    }
}
fn refuse_impersonation() -> Result<(), StorageFailure> {
    let mut output = HANDLE::default();
    // SAFETY: current-thread pseudohandle and valid initialized output.
    match unsafe { OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, true, &mut output) } {
        Ok(()) => {
            drop(OwnedHandle(output));
            Err(StorageFailure::Unsafe)
        }
        Err(error) if error.code().0 as u32 & 0xffff == ERROR_NO_TOKEN.0 => Ok(()),
        Err(_) => Err(StorageFailure::Unsafe),
    }
}
struct OwnedSid([u32; 17]);
impl OwnedSid {
    fn pointer(&self) -> PSID {
        PSID(self.0.as_ptr().cast_mut().cast())
    }
}
fn within(pointer: *const c_void, bytes: usize, base: *const c_void, length: usize) -> bool {
    let pointer = pointer as usize;
    let base = base as usize;
    pointer >= base
        && pointer
            .checked_add(bytes)
            .zip(base.checked_add(length))
            .is_some_and(|(end, limit)| end <= limit)
}
fn sid_extent(pointer: PSID, base: *const c_void, length: usize) -> Result<usize, StorageFailure> {
    if pointer.0.is_null()
        || !(pointer.0 as usize).is_multiple_of(4)
        || !within(pointer.0, 8, base, length)
    {
        return Err(StorageFailure::Unsafe);
    }
    // SAFETY: checked SID header extent precedes reading its subauthority count.
    let count = unsafe { *pointer.0.cast::<u8>().add(1) } as usize;
    let bytes = 8 + count * 4;
    if bytes > SECURITY_MAX_SID_SIZE as usize || !within(pointer.0, bytes, base, length) {
        return Err(StorageFailure::Unsafe);
    }
    // SAFETY: the complete aligned SID extent is contained in live allocation.
    if !unsafe { IsValidSid(pointer) }.as_bool() {
        return Err(StorageFailure::Unsafe);
    }
    if unsafe { GetLengthSid(pointer) } as usize != bytes {
        return Err(StorageFailure::Unsafe);
    }
    Ok(bytes)
}
fn process_sid() -> Result<OwnedSid, StorageFailure> {
    refuse_impersonation()?;
    let mut output = HANDLE::default();
    // SAFETY: current process pseudohandle and initialized token output.
    unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut output) }
        .map_err(win_failure)?;
    let token = OwnedHandle(output);
    let mut buffer = [0u64; 512];
    let mut written = 0;
    // SAFETY: aligned bounded TokenUser allocation with an independent size output.
    unsafe {
        GetTokenInformation(
            token.0,
            TokenUser,
            Some(buffer.as_mut_ptr().cast()),
            size_of_val(&buffer) as u32,
            &mut written,
        )
    }
    .map_err(win_failure)?;
    let length = written as usize;
    if length < size_of::<TOKEN_USER>() || length > size_of_val(&buffer) {
        return Err(StorageFailure::Unsafe);
    }
    // SAFETY: token header is aligned and entirely present; interior pointer is
    // checked against this allocation before any native SID operation.
    let user = unsafe { ptr::read(buffer.as_ptr().cast::<TOKEN_USER>()) };
    let bytes = sid_extent(user.User.Sid, buffer.as_ptr().cast(), length)?;
    let mut sid = OwnedSid([0; 17]);
    // SAFETY: destination has SID alignment/capacity; source extent was checked.
    unsafe { CopySid(bytes as u32, PSID(sid.0.as_mut_ptr().cast()), user.User.Sid) }
        .map_err(win_failure)?;
    Ok(sid)
}
fn sid_string(sid: &OwnedSid) -> Result<String, StorageFailure> {
    let mut text = PWSTR::null();
    // SAFETY: copied valid SID and preserved LocalAlloc output on every return.
    let result = unsafe { ConvertSidToStringSidW(sid.pointer(), &mut text) };
    let allocation = LocalAllocation(text.0.cast());
    result.map_err(win_failure)?;
    if allocation.0.is_null() {
        return Err(StorageFailure::Unsafe);
    }
    let mut units = Vec::new();
    for index in 0..184 {
        // SAFETY: conversion returns a terminated SID string; bounded policy.
        let unit = unsafe { *text.0.add(index) };
        if unit == 0 {
            break;
        }
        units.push(unit);
    }
    if units.is_empty() || units.len() == 184 {
        return Err(StorageFailure::Unsafe);
    }
    String::from_utf16(&units).map_err(|_| StorageFailure::Unsafe)
}
fn descriptor_from_sddl(text: &str) -> Result<LocalAllocation, StorageFailure> {
    if text.len() > 2048 || text.contains('\0') {
        return Err(StorageFailure::Unsafe);
    }
    let sddl: Vec<u16> = text.encode_utf16().chain([0]).collect();
    let mut descriptor = PSECURITY_DESCRIPTOR::default();
    let mut bytes = 0;
    // SAFETY: terminated bounded SDDL and preserved native allocation outputs.
    let result = unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            PCWSTR(sddl.as_ptr()),
            SDDL_REVISION_1,
            &mut descriptor,
            Some(&mut bytes),
        )
    };
    let allocation = LocalAllocation(descriptor.0);
    result.map_err(win_failure)?;
    if allocation.0.is_null() || bytes == 0 || bytes > 4096 {
        return Err(StorageFailure::Unsafe);
    }
    Ok(allocation)
}
fn security_descriptor(sid: &OwnedSid, directory: bool) -> Result<LocalAllocation, StorageFailure> {
    let sid_text = sid_string(sid)?;
    let flags = if directory { "OICI" } else { "" };
    descriptor_from_sddl(&format!("O:{sid_text}D:P(A;{flags};FA;;;{sid_text})"))
}

#[derive(Clone, PartialEq, Eq)]
struct Identity {
    volume: u64,
    file: [u8; 16],
}
struct Observation {
    identity: Identity,
    path: Vec<u16>,
    length: u64,
}
struct Directory {
    file: File,
    observation: Option<Observation>,
    private: bool,
}

struct CreateRequest {
    handle: UnsafeCell<HANDLE>,
    iosb: UnsafeCell<IO_STATUS_BLOCK>,
    name: Box<[u16]>,
    counted: UNICODE_STRING,
    attributes: OBJECT_ATTRIBUTES,
    owns_handle: Cell<bool>,
}
impl CreateRequest {
    fn new(
        parent: HANDLE,
        name: &[u16],
        security: *const SECURITY_DESCRIPTOR,
    ) -> Result<Box<Self>, StorageFailure> {
        validate_component(name)?;
        let mut request = Box::new(Self {
            handle: UnsafeCell::new(HANDLE::default()),
            iosb: UnsafeCell::new(iosb()),
            name: name.into(),
            counted: UNICODE_STRING::default(),
            attributes: OBJECT_ATTRIBUTES::default(),
            owns_handle: Cell::new(false),
        });
        let bytes = u16::try_from(name.len() * 2).map_err(|_| StorageFailure::Unsafe)?;
        request.counted = UNICODE_STRING {
            Length: bytes,
            MaximumLength: bytes,
            Buffer: PWSTR(request.name.as_mut_ptr()),
        };
        request.attributes = OBJECT_ATTRIBUTES {
            Length: size_of::<OBJECT_ATTRIBUTES>() as u32,
            RootDirectory: parent,
            ObjectName: &request.counted,
            Attributes: OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE,
            SecurityDescriptor: security,
            SecurityQualityOfService: ptr::null(),
        };
        Ok(request)
    }
}
impl Drop for CreateRequest {
    fn drop(&mut self) {
        if self.owns_handle.get() {
            let handle = *self.handle.get_mut();
            if !handle.is_invalid() {
                // SAFETY: successful completed creation not transferred to File.
                let _ = unsafe { CloseHandle(handle) };
            }
        }
    }
}
struct QueryRequest {
    iosb: UnsafeCell<IO_STATUS_BLOCK>,
    output: UnsafeCell<[u64; 16]>,
}
struct FlushRequest {
    iosb: UnsafeCell<IO_STATUS_BLOCK>,
}
struct LockRequest {
    overlapped: UnsafeCell<OVERLAPPED>,
}
enum Request {
    Create(Box<CreateRequest>),
    Query(Box<QueryRequest>),
    Flush(Box<FlushRequest>),
    Lock(Box<LockRequest>),
}
struct Capsule {
    request: Option<Request>,
    directories: Vec<Directory>,
    leaf: Option<File>,
    leaf_observation: Option<Observation>,
    temporary: Vec<File>,
    selected: Option<Route>,
    sid: Option<OwnedSid>,
    directory_sd: Option<LocalAllocation>,
    leaf_sd: Option<LocalAllocation>,
    apartment: Option<Apartment>,
}
impl Capsule {
    fn empty() -> Self {
        Self {
            request: None,
            directories: Vec::with_capacity(MAX_COMPONENTS + 3),
            leaf: None,
            leaf_observation: None,
            temporary: Vec::with_capacity(MAX_COMPONENTS + 1),
            selected: None,
            sid: None,
            directory_sd: None,
            leaf_sd: None,
            apartment: None,
        }
    }
}
fn file_handle(file: &File) -> HANDLE {
    HANDLE(file.as_raw_handle())
}
fn iosb() -> IO_STATUS_BLOCK {
    let mut value = IO_STATUS_BLOCK::default();
    value.Anonymous.Status = PENDING;
    value
}
fn completed_output(
    value: &UnsafeCell<IO_STATUS_BLOCK>,
    primary: NTSTATUS,
    expected: Option<usize>,
) -> Result<(), StorageFailure> {
    // SAFETY: callers first classified a nonpending primary return. Reading an
    // IOSB after pending is never reachable. Primary status remains authoritative.
    let value = unsafe { *value.get() };
    if unsafe { value.Anonymous.Status } != primary
        || expected.is_some_and(|bytes| value.Information != bytes)
    {
        return Err(StorageFailure::Unsafe);
    }
    Ok(())
}
enum OpenPolicy {
    Ancestor,
    CreationParent,
    PrivateDirectory,
    PrivateLeaf,
}
fn relative_open(
    guard: &CustodyGuard<Capsule>,
    capsule: &mut Capsule,
    parent: HANDLE,
    name: &[u16],
    policy: OpenPolicy,
) -> Result<File, StorageFailure> {
    let (directory, private, create, writable_parent) = match policy {
        OpenPolicy::Ancestor => (true, false, false, false),
        OpenPolicy::CreationParent => (true, false, false, true),
        OpenPolicy::PrivateDirectory => (true, true, true, true),
        OpenPolicy::PrivateLeaf => (false, true, true, false),
    };
    let security = if private {
        if directory {
            capsule.directory_sd.as_ref()
        } else {
            capsule.leaf_sd.as_ref()
        }
        .ok_or(StorageFailure::Unsafe)?
        .0
        .cast()
    } else {
        ptr::null()
    };
    let request = CreateRequest::new(parent, name, security)?;
    capsule.request = Some(Request::Create(request));
    let Request::Create(request) = capsule
        .request
        .as_ref()
        .ok_or(StorageFailure::Unavailable)?
    else {
        return Err(StorageFailure::Unavailable);
    };
    let access = if !directory {
        FILE_GENERIC_READ | FILE_GENERIC_WRITE
    } else {
        let base =
            FILE_LIST_DIRECTORY | FILE_TRAVERSE | FILE_READ_ATTRIBUTES | READ_CONTROL | SYNCHRONIZE;
        if private || writable_parent {
            base | FILE_ADD_FILE | FILE_ADD_SUBDIRECTORY
        } else {
            base
        }
    };
    let share = if !directory {
        FILE_SHARE_MODE(0)
    } else if private {
        FILE_SHARE_READ
    } else {
        FILE_SHARE_READ | FILE_SHARE_WRITE
    };
    guard.arm()?;
    // SAFETY: complete heap request graph and parent/descriptor custody are
    // already retained. Synchronous, single-component, nonfollowing open.
    let primary = unsafe {
        NtCreateFile(
            request.handle.get(),
            access,
            &request.attributes,
            request.iosb.get(),
            None,
            FILE_ATTRIBUTE_NORMAL,
            share,
            if create { NT_OPEN_IF } else { NT_OPEN },
            (if directory { NT_DIRECTORY } else { NT_FILE }) | NT_SYNCHRONOUS | NT_NOFOLLOW,
            None,
            0,
        )
    };
    if primary != PENDING && primary.0 >= 0 {
        request.owns_handle.set(true);
    }
    guard.finish(primary)?;
    completed_output(&request.iosb, primary, None)?;
    // SAFETY: completed successful handle output, retained until transfer.
    let handle = unsafe { *request.handle.get() };
    if handle.is_invalid() {
        return Err(StorageFailure::Unsafe);
    }
    request.owns_handle.set(false);
    // SAFETY: successful owned handle transfers exactly once; no outstanding call.
    let file = unsafe { File::from_raw_handle(handle.0) };
    capsule.request = None;
    Ok(file)
}
fn query(
    guard: &CustodyGuard<Capsule>,
    capsule: &mut Capsule,
    handle: HANDLE,
    class: FILE_INFORMATION_CLASS,
    length: usize,
    volume: bool,
) -> Result<[u64; 16], StorageFailure> {
    if length == 0 || length > 128 {
        return Err(StorageFailure::Unsafe);
    }
    capsule.request = Some(Request::Query(Box::new(QueryRequest {
        iosb: UnsafeCell::new(iosb()),
        output: UnsafeCell::new([0; 16]),
    })));
    let Request::Query(request) = capsule
        .request
        .as_ref()
        .ok_or(StorageFailure::Unavailable)?
    else {
        return Err(StorageFailure::Unavailable);
    };
    guard.arm()?;
    // SAFETY: exact fixed-class buffer length/alignment, retained heap outputs
    // and retained queried handle. The bool selects only the device-info class.
    let primary = unsafe {
        if volume {
            NtQueryVolumeInformationFile(
                handle,
                request.iosb.get(),
                request.output.get().cast(),
                length as u32,
                FileFsDeviceInformation,
            )
        } else {
            NtQueryInformationFile(
                handle,
                request.iosb.get(),
                request.output.get().cast(),
                length as u32,
                class,
            )
        }
    };
    guard.finish(primary)?;
    completed_output(&request.iosb, primary, Some(length))?;
    // SAFETY: fully completed and extent checked numeric output copy.
    let output = unsafe { *request.output.get() };
    capsule.request = None;
    Ok(output)
}
fn ordinal_equal(left: &[u16], right: &[u16]) -> bool {
    // SAFETY: bounded UTF-16 slices; API applies Windows ordinal name semantics.
    (unsafe { CompareStringOrdinal(left, right, true) }) == CSTR_EQUAL
}
fn physical_name(handle: HANDLE) -> Result<Vec<u16>, StorageFailure> {
    let mut path = vec![0u16; MAX_PATH_UNITS + 1];
    // SAFETY: retained file and bounded writable synchronous output.
    let length = unsafe {
        GetFinalPathNameByHandleW(
            handle,
            &mut path,
            GETFINALPATHNAMEBYHANDLE_FLAGS(FILE_NAME_NORMALIZED.0 | VOLUME_NAME_DOS.0),
        )
    } as usize;
    if length < 7 || length >= path.len() || path[..4] != [92, 92, 63, 92] {
        return Err(StorageFailure::Unsafe);
    }
    path.truncate(length);
    path.drain(..4);
    Ok(path)
}
fn observe(
    guard: &CustodyGuard<Capsule>,
    capsule: &mut Capsule,
    handle: HANDLE,
    directory: bool,
) -> Result<Observation, StorageFailure> {
    // SAFETY: GetFileType is synchronous and operates on the retained handle.
    if unsafe { GetFileType(handle) } != FILE_TYPE_DISK {
        return Err(StorageFailure::Unsafe);
    }
    let raw = query(
        guard,
        capsule,
        handle,
        FileBasicInformation,
        size_of::<FILE_BASIC_INFORMATION>(),
        false,
    )?;
    // SAFETY: exact numeric structure/class, complete extent and u64 alignment.
    let basic = unsafe { ptr::read(raw.as_ptr().cast::<FILE_BASIC_INFORMATION>()) };
    if basic.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT.0 != 0
        || !directory && basic.FileAttributes & FILE_ATTRIBUTE_READONLY.0 != 0
    {
        return Err(StorageFailure::Unsafe);
    }
    let raw = query(
        guard,
        capsule,
        handle,
        FileStandardInformation,
        size_of::<FILE_STANDARD_INFORMATION>(),
        false,
    )?;
    // Check native boolean bytes before materializing Rust bool fields.
    let bytes = unsafe { std::slice::from_raw_parts(raw.as_ptr().cast::<u8>(), 128) };
    if bytes[std::mem::offset_of!(FILE_STANDARD_INFORMATION, DeletePending)] > 1
        || bytes[std::mem::offset_of!(FILE_STANDARD_INFORMATION, Directory)] > 1
    {
        return Err(StorageFailure::Unsafe);
    }
    // SAFETY: checked bool representation plus complete exact class/extent.
    let standard = unsafe { ptr::read(raw.as_ptr().cast::<FILE_STANDARD_INFORMATION>()) };
    if standard.DeletePending
        || standard.Directory != directory
        || standard.EndOfFile < 0
        || (!directory
            && (standard.NumberOfLinks != 1 || standard.EndOfFile as u64 > MAX_JOURNAL_BYTES))
    {
        return Err(StorageFailure::Unsafe);
    }
    let raw = query(
        guard,
        capsule,
        handle,
        FileIdInformation,
        size_of::<FILE_ID_INFORMATION>(),
        false,
    )?;
    // SAFETY: exact numeric file identity structure, extent and alignment.
    let id = unsafe { ptr::read(raw.as_ptr().cast::<FILE_ID_INFORMATION>()) };
    if id.FileId.Identifier == [0; 16] {
        return Err(StorageFailure::Unsafe);
    }
    qualify_volume(guard, capsule, handle)?;
    Ok(Observation {
        identity: Identity {
            volume: id.VolumeSerialNumber,
            file: id.FileId.Identifier,
        },
        path: physical_name(handle)?,
        length: standard.EndOfFile as u64,
    })
}
fn qualify_volume(
    guard: &CustodyGuard<Capsule>,
    capsule: &mut Capsule,
    handle: HANDLE,
) -> Result<(), StorageFailure> {
    let mut flags = 0;
    let mut name = [0u16; 32];
    // SAFETY: synchronous retained-handle query with fixed writable buffers.
    unsafe {
        GetVolumeInformationByHandleW(handle, None, None, None, Some(&mut flags), Some(&mut name))
    }
    .map_err(win_failure)?;
    let end = name
        .iter()
        .position(|unit| *unit == 0)
        .ok_or(StorageFailure::Unsafe)?;
    if flags & FILE_PERSISTENT_ACLS == 0
        || flags & FILE_READ_ONLY_VOLUME != 0
        || !ordinal_equal(&name[..end], &[78, 84, 70, 83])
    {
        return Err(StorageFailure::Unsafe);
    }
    let raw = query(
        guard,
        capsule,
        handle,
        FILE_INFORMATION_CLASS(0),
        size_of::<FILE_FS_DEVICE_INFORMATION>(),
        true,
    )?;
    // SAFETY: complete exact device information structure/extent and alignment.
    let device = unsafe { ptr::read(raw.as_ptr().cast::<FILE_FS_DEVICE_INFORMATION>()) };
    if device.DeviceType != FILE_DEVICE_DISK.0
        || device.Characteristics & (FILE_REMOTE_DEVICE | FILE_REMOTE_DEVICE_VSMB) != 0
    {
        return Err(StorageFailure::Unsafe);
    }
    Ok(())
}

fn inspect_descriptor(
    allocation: &LocalAllocation,
    sid: &OwnedSid,
    directory: bool,
) -> Result<(), StorageFailure> {
    let descriptor = PSECURITY_DESCRIPTOR(allocation.0);
    // SAFETY: native conversion/GetSecurityInfo returns a live descriptor.
    if allocation.0.is_null() || !(unsafe { IsValidSecurityDescriptor(descriptor) }).as_bool() {
        return Err(StorageFailure::Unsafe);
    }
    let length = unsafe { GetSecurityDescriptorLength(descriptor) } as usize;
    if !(20..=4096).contains(&length) {
        return Err(StorageFailure::Unsafe);
    }
    let mut control = 0;
    let mut revision = 0;
    unsafe { GetSecurityDescriptorControl(descriptor, &mut control, &mut revision) }
        .map_err(win_failure)?;
    if revision != 1
        || control & (SE_DACL_PROTECTED.0 | SE_SELF_RELATIVE.0)
            != (SE_DACL_PROTECTED.0 | SE_SELF_RELATIVE.0)
    {
        return Err(StorageFailure::Unsafe);
    }
    let mut owner = PSID::default();
    let mut defaulted = BOOL::default();
    unsafe { GetSecurityDescriptorOwner(descriptor, &mut owner, &mut defaulted) }
        .map_err(win_failure)?;
    if defaulted.as_bool() {
        return Err(StorageFailure::Unsafe);
    }
    sid_extent(owner, allocation.0, length)?;
    // SAFETY: both owner and copied current SID are valid, bounded allocations.
    if unsafe { EqualSid(owner, sid.pointer()) }.is_err() {
        return Err(StorageFailure::Unsafe);
    }
    let mut present = BOOL::default();
    let mut dacl = ptr::null_mut();
    let mut defaulted = BOOL::default();
    unsafe { GetSecurityDescriptorDacl(descriptor, &mut present, &mut dacl, &mut defaulted) }
        .map_err(win_failure)?;
    if !present.as_bool()
        || defaulted.as_bool()
        || dacl.is_null()
        || !within(dacl.cast(), size_of::<ACL>(), allocation.0, length)
    {
        return Err(StorageFailure::Unsafe);
    }
    // SAFETY: ACL header lies inside the descriptor allocation.
    let header = unsafe { ptr::read(dacl) };
    let acl_length = header.AclSize as usize;
    if header.AclRevision != 2
        || header.AceCount != 1
        || acl_length < size_of::<ACL>()
        || !within(dacl.cast(), acl_length, allocation.0, length)
    {
        return Err(StorageFailure::Unsafe);
    }
    if !(unsafe { IsValidAcl(dacl) }).as_bool() {
        return Err(StorageFailure::Unsafe);
    }
    let mut information = ACL_SIZE_INFORMATION::default();
    unsafe {
        GetAclInformation(
            dacl,
            (&mut information as *mut ACL_SIZE_INFORMATION).cast(),
            size_of::<ACL_SIZE_INFORMATION>() as u32,
            AclSizeInformation,
        )
    }
    .map_err(win_failure)?;
    if information.AceCount != 1 || information.AclBytesInUse as usize > acl_length {
        return Err(StorageFailure::Unsafe);
    }
    let mut ace = ptr::null_mut();
    unsafe { GetAce(dacl, 0, &mut ace) }.map_err(win_failure)?;
    if !within(ace, size_of::<ACE_HEADER>(), dacl.cast(), acl_length) {
        return Err(StorageFailure::Unsafe);
    }
    // SAFETY: checked ACE header extent; subsequent SID extent is separately checked.
    let header = unsafe { ptr::read_unaligned(ace.cast::<ACE_HEADER>()) };
    let ace_length = header.AceSize as usize;
    let sid_offset = std::mem::offset_of!(ACCESS_ALLOWED_ACE, SidStart);
    if header.AceType as u32 != ACCESS_ALLOWED_ACE_TYPE
        || header.AceFlags != if directory { 3 } else { 0 }
        || ace_length < sid_offset + 8
        || !within(ace, ace_length, dacl.cast(), acl_length)
    {
        return Err(StorageFailure::Unsafe);
    }
    let mask =
        unsafe { ptr::read_unaligned(ace.cast::<u8>().add(size_of::<ACE_HEADER>()).cast::<u32>()) };
    if mask != FILE_ALL_ACCESS.0 {
        return Err(StorageFailure::Unsafe);
    }
    let ace_sid = PSID(unsafe { ace.cast::<u8>().add(sid_offset) }.cast());
    let sid_bytes = sid_extent(ace_sid, ace, ace_length)?;
    if sid_offset + sid_bytes != ace_length || unsafe { EqualSid(ace_sid, sid.pointer()) }.is_err()
    {
        return Err(StorageFailure::Unsafe);
    }
    Ok(())
}
fn private_security(handle: HANDLE, sid: &OwnedSid, directory: bool) -> Result<(), StorageFailure> {
    let mut output = PSECURITY_DESCRIPTOR::default();
    // SAFETY: synchronous handle security query; ownership is the whole returned
    // descriptor, never its interior owner/DACL pointers.
    let status = unsafe {
        GetSecurityInfo(
            handle,
            SE_FILE_OBJECT,
            OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
            None,
            None,
            None,
            None,
            Some(&mut output),
        )
    };
    let allocation = LocalAllocation(output.0);
    if status.0 != 0 {
        return Err(if status.0 == 5 {
            StorageFailure::Unsafe
        } else {
            StorageFailure::Unavailable
        });
    }
    inspect_descriptor(&allocation, sid, directory)
}
fn root_file(selected: &Route) -> Result<File, StorageFailure> {
    // Drive type is an additional restriction, not local-volume proof.
    if unsafe { GetDriveTypeW(PCWSTR(selected.root.as_ptr())) } != DRIVE_FIXED {
        return Err(StorageFailure::Unsafe);
    }
    // SAFETY: validated terminated local drive root; no inherited handle,
    // reparse following, deletion sharing or overwrite disposition.
    let handle = unsafe {
        CreateFileW(
            PCWSTR(selected.root.as_ptr()),
            (FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | READ_CONTROL | SYNCHRONIZE).0,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            None,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            None,
        )
    }
    .map_err(win_failure)?;
    // SAFETY: successful unique handle transfer; standard-library I/O remains
    // synchronous. No claim covers arbitrary Win32 contract violations.
    Ok(unsafe { File::from_raw_handle(handle.0) })
}
fn expected_path(parent: &[u16], name: &[u16]) -> Vec<u16> {
    let mut path = parent.to_vec();
    if path.last() != Some(&(b'\\' as u16)) {
        path.push(b'\\' as u16);
    }
    path.extend_from_slice(name);
    path
}
fn record_directory(
    guard: &CustodyGuard<Capsule>,
    capsule: &mut Capsule,
    file: File,
    private: bool,
    expected: &[u16],
) -> Result<(), StorageFailure> {
    // Place the handle in complete custody before the first query submission.
    capsule.directories.push(Directory {
        file,
        observation: None,
        private,
    });
    let index = capsule.directories.len() - 1;
    let handle = file_handle(&capsule.directories[index].file);
    let observation = observe(guard, capsule, handle, true)?;
    if !ordinal_equal(&observation.path, expected)
        || capsule
            .directories
            .first()
            .and_then(|row| row.observation.as_ref())
            .is_some_and(|root| root.identity.volume != observation.identity.volume)
    {
        return Err(StorageFailure::Unsafe);
    }
    if private {
        private_security(
            handle,
            capsule.sid.as_ref().ok_or(StorageFailure::Unsafe)?,
            true,
        )?;
    }
    capsule.directories[index].observation = Some(observation);
    Ok(())
}
fn refresh_known_folder(
    guard: &CustodyGuard<Capsule>,
    capsule: &mut Capsule,
) -> Result<(), StorageFailure> {
    let selected = known_folder()?;
    let expected = capsule.selected.as_ref().ok_or(StorageFailure::Unsafe)?;
    if !ordinal_equal(&selected.full, &expected.full) {
        return Err(StorageFailure::Unsafe);
    }
    let expected_root = capsule
        .directories
        .first()
        .and_then(|row| row.observation.as_ref())
        .ok_or(StorageFailure::Unsafe)?
        .identity
        .clone();
    let expected_known = capsule
        .directories
        .get(selected.components.len())
        .and_then(|row| row.observation.as_ref())
        .ok_or(StorageFailure::Unsafe)?
        .identity
        .clone();
    capsule.temporary.push(root_file(&selected)?);
    let handle = file_handle(capsule.temporary.last().ok_or(StorageFailure::Unsafe)?);
    let root = observe(guard, capsule, handle, true)?;
    if root.identity != expected_root || !ordinal_equal(&root.path, &selected.root[..3]) {
        return Err(StorageFailure::Unsafe);
    }
    let mut expected_path = selected.root[..3].to_vec();
    for component in &selected.components {
        let parent = file_handle(capsule.temporary.last().ok_or(StorageFailure::Unsafe)?);
        let file = relative_open(guard, capsule, parent, component, OpenPolicy::Ancestor)?;
        capsule.temporary.push(file);
        let handle = file_handle(capsule.temporary.last().ok_or(StorageFailure::Unsafe)?);
        let observation = observe(guard, capsule, handle, true)?;
        expected_path = super_path(&expected_path, component);
        if observation.identity.volume != expected_root.volume
            || !ordinal_equal(&observation.path, &expected_path)
        {
            return Err(StorageFailure::Unsafe);
        }
        if capsule.temporary.len() == selected.components.len() + 1
            && observation.identity != expected_known
        {
            return Err(StorageFailure::Unsafe);
        }
    }
    capsule.temporary.clear();
    Ok(())
}
fn super_path(parent: &[u16], child: &[u16]) -> Vec<u16> {
    expected_path(parent, child)
}
fn validate(guard: &CustodyGuard<Capsule>, capsule: &mut Capsule) -> Result<u64, StorageFailure> {
    let current = process_sid()?;
    let sid = capsule.sid.as_ref().ok_or(StorageFailure::Unsafe)?;
    if unsafe { EqualSid(current.pointer(), sid.pointer()) }.is_err() {
        return Err(StorageFailure::Unsafe);
    }
    refresh_known_folder(guard, capsule)?;
    for index in 0..capsule.directories.len() {
        let handle = file_handle(&capsule.directories[index].file);
        let current = observe(guard, capsule, handle, true)?;
        let directory = &capsule.directories[index];
        let expected = directory
            .observation
            .as_ref()
            .ok_or(StorageFailure::Unsafe)?;
        if current.identity != expected.identity || !ordinal_equal(&current.path, &expected.path) {
            return Err(StorageFailure::Unsafe);
        }
        if directory.private {
            private_security(
                handle,
                capsule.sid.as_ref().ok_or(StorageFailure::Unsafe)?,
                true,
            )?;
        }
    }
    let handle = file_handle(capsule.leaf.as_ref().ok_or(StorageFailure::Unsafe)?);
    let current = observe(guard, capsule, handle, false)?;
    let expected = capsule
        .leaf_observation
        .as_ref()
        .ok_or(StorageFailure::Unsafe)?;
    if current.identity != expected.identity || !ordinal_equal(&current.path, &expected.path) {
        return Err(StorageFailure::Unsafe);
    }
    private_security(
        handle,
        capsule.sid.as_ref().ok_or(StorageFailure::Unsafe)?,
        false,
    )?;
    Ok(current.length)
}
fn native_flush(
    guard: &CustodyGuard<Capsule>,
    capsule: &mut Capsule,
    handle: HANDLE,
) -> Result<(), StorageFailure> {
    capsule.request = Some(Request::Flush(Box::new(FlushRequest {
        iosb: UnsafeCell::new(iosb()),
    })));
    let Request::Flush(request) = capsule
        .request
        .as_ref()
        .ok_or(StorageFailure::Unavailable)?
    else {
        return Err(StorageFailure::Unavailable);
    };
    guard.arm()?;
    // SAFETY: synchronous flags0/null0 protocol, retained handle and heap IOSB.
    let primary = unsafe { NtFlushBuffersFileEx(handle, 0, ptr::null(), 0, request.iosb.get()) };
    guard.finish(primary)?;
    completed_output(&request.iosb, primary, None)?;
    capsule.request = None;
    Ok(())
}
fn constructor_flush(
    known_index: usize,
    directory_count: usize,
    mut flush: impl FnMut(Option<usize>) -> Result<(), StorageFailure>,
) -> Result<(), StorageFailure> {
    if known_index >= directory_count || directory_count > MAX_COMPONENTS + 3 {
        return Err(StorageFailure::Unsafe);
    }
    flush(None)?;
    for index in (known_index..directory_count).rev() {
        flush(Some(index))?;
    }
    flush(None)
}
fn lock_leaf(guard: &CustodyGuard<Capsule>, capsule: &mut Capsule) -> Result<(), StorageFailure> {
    let handle = file_handle(capsule.leaf.as_ref().ok_or(StorageFailure::Unsafe)?);
    capsule.request = Some(Request::Lock(Box::new(LockRequest {
        overlapped: UnsafeCell::new(OVERLAPPED::default()),
    })));
    let Request::Lock(request) = capsule
        .request
        .as_ref()
        .ok_or(StorageFailure::Unavailable)?
    else {
        return Err(StorageFailure::Unavailable);
    };
    guard.arm()?;
    // SAFETY: entire zero-offset lock request is heap retained. Synchronous
    // handle plus FAIL_IMMEDIATELY excludes the documented async wait route.
    let result = unsafe {
        LockFileEx(
            handle,
            LOCKFILE_EXCLUSIVE_LOCK | LOCKFILE_FAIL_IMMEDIATELY,
            Some(0),
            u32::MAX,
            u32::MAX,
            request.overlapped.get(),
        )
    };
    if result
        .as_ref()
        .is_err_and(|error| error.code().0 as u32 & 0xffff == ERROR_IO_PENDING.0)
    {
        guard.finish(PENDING)?;
    }
    guard.finish(NTSTATUS(0))?;
    result.map_err(win_failure)?;
    capsule.request = None;
    Ok(())
}

/// One backend-owned journal in the OS-selected user state namespace. It has
/// no public injection constructor, clone, transfer or path/handle accessor.
/// Sharing, ACL and namespace-flush behavior require native qualification.
///
/// ```compile_fail
/// fn require_send<T: Send>() {}
/// require_send::<bridge_platform_windows::NativePrivateJournalStorage>();
/// ```
/// ```compile_fail
/// fn require_sync<T: Sync>() {}
/// require_sync::<bridge_platform_windows::NativePrivateJournalStorage>();
/// ```
pub struct NativePrivateJournalStorage {
    guard: CustodyGuard<Capsule>,
    refused: Cell<bool>,
}
impl NativePrivateJournalStorage {
    /// Provision only the fixed new backend namespace; foreign objects refuse.
    /// Failure may leave declared entries; it never removes or repairs them.
    pub fn open() -> Result<Self, StorageFailure> {
        let reservation = Reservation::acquire(&JOURNAL_RESERVATION)?;
        let guard = CustodyGuard::new(reservation, Capsule::empty());
        {
            let mut capsule = guard.borrow()?;
            refuse_impersonation()?;
            capsule.apartment = Some(Apartment::enter()?);
            capsule.sid = Some(process_sid()?);
            let sid = capsule.sid.as_ref().ok_or(StorageFailure::Unsafe)?;
            let directory_sd = security_descriptor(sid, true)?;
            let leaf_sd = security_descriptor(sid, false)?;
            inspect_descriptor(&directory_sd, sid, true)?;
            inspect_descriptor(&leaf_sd, sid, false)?;
            capsule.directory_sd = Some(directory_sd);
            capsule.leaf_sd = Some(leaf_sd);
            let selected = known_folder()?;
            let file = root_file(&selected)?;
            record_directory(&guard, &mut capsule, file, false, &selected.root[..3])?;
            let mut path = selected.root[..3].to_vec();
            for (index, component) in selected.components.iter().enumerate() {
                let parent = file_handle(
                    &capsule
                        .directories
                        .last()
                        .ok_or(StorageFailure::Unsafe)?
                        .file,
                );
                let file = relative_open(
                    &guard,
                    &mut capsule,
                    parent,
                    component,
                    if index + 1 == selected.components.len() {
                        OpenPolicy::CreationParent
                    } else {
                        OpenPolicy::Ancestor
                    },
                )?;
                path = expected_path(&path, component);
                record_directory(&guard, &mut capsule, file, false, &path)?;
            }
            capsule.selected = Some(selected);
            for name in STATE_NAMES {
                let name: Vec<u16> = name.encode_utf16().collect();
                let parent = file_handle(
                    &capsule
                        .directories
                        .last()
                        .ok_or(StorageFailure::Unsafe)?
                        .file,
                );
                let file = relative_open(
                    &guard,
                    &mut capsule,
                    parent,
                    &name,
                    OpenPolicy::PrivateDirectory,
                )?;
                path = expected_path(&path, &name);
                record_directory(&guard, &mut capsule, file, true, &path)?;
            }
            let name: Vec<u16> = JOURNAL_NAME.encode_utf16().collect();
            let parent = file_handle(
                &capsule
                    .directories
                    .last()
                    .ok_or(StorageFailure::Unsafe)?
                    .file,
            );
            let file = relative_open(&guard, &mut capsule, parent, &name, OpenPolicy::PrivateLeaf)?;
            capsule.leaf = Some(file);
            let handle = file_handle(capsule.leaf.as_ref().ok_or(StorageFailure::Unsafe)?);
            let observation = observe(&guard, &mut capsule, handle, false)?;
            let root = capsule
                .directories
                .first()
                .and_then(|row| row.observation.as_ref())
                .ok_or(StorageFailure::Unsafe)?;
            if root.identity.volume != observation.identity.volume
                || !ordinal_equal(&observation.path, &expected_path(&path, &name))
            {
                return Err(StorageFailure::Unsafe);
            }
            private_security(
                handle,
                capsule.sid.as_ref().ok_or(StorageFailure::Unsafe)?,
                false,
            )?;
            capsule.leaf_observation = Some(observation);
            lock_leaf(&guard, &mut capsule)?;
            validate(&guard, &mut capsule)?;
            // Every constructor flushes the entire private namespace chain,
            // including surviving entries from a previous failed attempt.
            let known_index = capsule
                .selected
                .as_ref()
                .ok_or(StorageFailure::Unsafe)?
                .components
                .len();
            let directory_count = capsule.directories.len();
            constructor_flush(known_index, directory_count, |index| {
                let selected = match index {
                    Some(index) => file_handle(&capsule.directories[index].file),
                    None => handle,
                };
                native_flush(&guard, &mut capsule, selected)
            })?;
            validate(&guard, &mut capsule)?;
        }
        Ok(Self {
            guard,
            refused: Cell::new(false),
        })
    }
    fn with<T>(
        &self,
        operation: impl FnOnce(&CustodyGuard<Capsule>, &mut Capsule) -> Result<T, StorageFailure>,
    ) -> Result<T, StorageFailure> {
        if self.refused.get() {
            return Err(StorageFailure::Unsafe);
        }
        let result = (|| {
            let mut capsule = self.guard.borrow()?;
            operation(&self.guard, &mut capsule)
        })();
        if result.is_err() {
            self.refused.set(true);
        }
        result
    }
}
fn io_failure(_: StorageFailure) -> io::Error {
    io::ErrorKind::Other.into()
}
impl JournalStorage for NativePrivateJournalStorage {
    fn validate_custody(&self) -> Result<u64, StorageFailure> {
        self.with(validate)
    }
    fn truncate(&mut self, length: u64) -> Result<(), StorageFailure> {
        self.with(|guard, capsule| {
            if length > MAX_JOURNAL_BYTES {
                return Err(StorageFailure::Unsafe);
            }
            validate(guard, capsule)?;
            capsule
                .leaf
                .as_ref()
                .ok_or(StorageFailure::Unsafe)?
                .set_len(length)
                .map_err(|_| StorageFailure::Unavailable)?;
            if validate(guard, capsule)? != length {
                return Err(StorageFailure::Unsafe);
            }
            Ok(())
        })
    }
    fn sync_durable(&mut self) -> Result<(), StorageFailure> {
        self.with(|guard, capsule| {
            validate(guard, capsule)?;
            let handle = file_handle(capsule.leaf.as_ref().ok_or(StorageFailure::Unsafe)?);
            // SAFETY: documented synchronous writable-file flush; no output
            // lifetime is claimed for arbitrary standard-library/API violations.
            unsafe { FlushFileBuffers(handle) }.map_err(win_failure)?;
            validate(guard, capsule)?;
            Ok(())
        })
    }
}
impl Read for NativePrivateJournalStorage {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        self.with(|guard, capsule| {
            validate(guard, capsule)?;
            let count = capsule
                .leaf
                .as_mut()
                .ok_or(StorageFailure::Unsafe)?
                .read(buffer)
                .map_err(|_| StorageFailure::Unavailable)?;
            validate(guard, capsule)?;
            Ok(count)
        })
        .map_err(io_failure)
    }
}
impl Write for NativePrivateJournalStorage {
    fn write(&mut self, buffer: &[u8]) -> io::Result<usize> {
        self.with(|guard, capsule| {
            validate(guard, capsule)?;
            let file = capsule.leaf.as_mut().ok_or(StorageFailure::Unsafe)?;
            let position = file
                .stream_position()
                .map_err(|_| StorageFailure::Unavailable)?;
            if position
                .checked_add(buffer.len() as u64)
                .is_none_or(|end| end > MAX_JOURNAL_BYTES)
            {
                return Err(StorageFailure::Unsafe);
            }
            let count = file
                .write(buffer)
                .map_err(|_| StorageFailure::Unavailable)?;
            validate(guard, capsule)?;
            Ok(count)
        })
        .map_err(io_failure)
    }
    fn flush(&mut self) -> io::Result<()> {
        self.sync_durable().map_err(io_failure)
    }
}
impl Seek for NativePrivateJournalStorage {
    fn seek(&mut self, position: SeekFrom) -> io::Result<u64> {
        self.with(|guard, capsule| {
            let length = validate(guard, capsule)?;
            let file = capsule.leaf.as_mut().ok_or(StorageFailure::Unsafe)?;
            let target = match position {
                SeekFrom::Start(value) => Some(value),
                SeekFrom::End(delta) => length.checked_add_signed(delta),
                SeekFrom::Current(delta) => file
                    .stream_position()
                    .map_err(|_| StorageFailure::Unavailable)?
                    .checked_add_signed(delta),
            }
            .filter(|value| *value <= MAX_JOURNAL_BYTES)
            .ok_or(StorageFailure::Unsafe)?;
            let observed = file
                .seek(SeekFrom::Start(target))
                .map_err(|_| StorageFailure::Unavailable)?;
            if observed != target {
                return Err(StorageFailure::Unsafe);
            }
            validate(guard, capsule)?;
            Ok(observed)
        })
        .map_err(io_failure)
    }
}

#[cfg(test)]
mod custody_tests {
    use super::*;
    use std::sync::{
        Arc, Barrier,
        atomic::{AtomicUsize, Ordering},
    };
    struct Probe {
        drops: Arc<AtomicUsize>,
        output: Box<UnsafeCell<[usize; 3]>>,
        request: Box<UnsafeCell<[usize; 4]>>,
    }
    impl Drop for Probe {
        fn drop(&mut self) {
            self.drops.fetch_add(1, Ordering::SeqCst);
        }
    }
    fn state() -> &'static AtomicU8 {
        Box::leak(Box::new(AtomicU8::new(FREE)))
    }
    fn guard(state: &'static AtomicU8, drops: Arc<AtomicUsize>) -> CustodyGuard<Probe> {
        CustodyGuard::new(
            Reservation::acquire(state).unwrap(),
            Probe {
                drops,
                output: Box::new(UnsafeCell::new([0; 3])),
                request: Box::new(UnsafeCell::new([0; 4])),
            },
        )
    }
    #[test]
    fn pending_preserves_all_output_addresses_and_refuses_second_submission() {
        let state = state();
        let drops = Arc::new(AtomicUsize::new(0));
        let owner = guard(state, drops.clone());
        let (outputs, inputs) = {
            let custody = owner.borrow().unwrap();
            (custody.output.get(), custody.request.get())
        };
        owner.arm().unwrap();
        assert_eq!(owner.finish(PENDING), Err(StorageFailure::Unavailable));
        assert!(owner.borrow().is_err());
        assert!(owner.arm().is_err());
        assert_eq!(owner.finish(NTSTATUS(0)), Err(StorageFailure::Unavailable));
        drop(owner);
        assert_eq!(drops.load(Ordering::SeqCst), 0);
        assert!(matches!(
            Reservation::acquire(state),
            Err(StorageFailure::Unavailable)
        ));
        // SAFETY: synthetic late native writes target intentionally retained
        // allocations, without concurrent readers or deallocation.
        unsafe {
            (*outputs)[0] = 123;
            (*inputs)[3] = 456;
            assert_eq!((*outputs)[0], 123);
            assert_eq!((*inputs)[3], 456);
        }
    }
    #[test]
    fn unwind_before_classification_retains_constructor_custody() {
        let state = state();
        let drops = Arc::new(AtomicUsize::new(0));
        let outcome = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let owner = guard(state, drops.clone());
            owner.arm().unwrap();
            panic!("injected unclassified return");
        }));
        assert!(outcome.is_err());
        assert_eq!(drops.load(Ordering::SeqCst), 0);
        assert_eq!(state.load(Ordering::Acquire), QUARANTINED);
    }
    #[test]
    fn completed_error_destroys_custody_before_reservation_release() {
        struct OrderProbe(&'static AtomicU8, Arc<AtomicUsize>);
        impl Drop for OrderProbe {
            fn drop(&mut self) {
                assert_eq!(self.0.load(Ordering::Acquire), RESERVED);
                self.1.fetch_add(1, Ordering::SeqCst);
            }
        }
        let state = state();
        let drops = Arc::new(AtomicUsize::new(0));
        let owner = CustodyGuard::new(
            Reservation::acquire(state).unwrap(),
            OrderProbe(state, drops.clone()),
        );
        owner.arm().unwrap();
        assert_eq!(
            owner.finish(NTSTATUS(0xc000_0001u32 as i32)),
            Err(StorageFailure::Unavailable)
        );
        drop(owner);
        assert_eq!(drops.load(Ordering::SeqCst), 1);
        assert!(Reservation::acquire(state).is_ok());
    }
    #[test]
    fn competing_constructor_cannot_submit_before_leaf_acquisition() {
        let state = state();
        let barrier = Arc::new(Barrier::new(2));
        let submitted = Arc::new(AtomicUsize::new(0));
        let owner = guard(state, Arc::new(AtomicUsize::new(0)));
        let other_barrier = barrier.clone();
        let other_submitted = submitted.clone();
        let competitor = std::thread::spawn(move || {
            other_barrier.wait();
            if let Ok(_reservation) = Reservation::acquire(state) {
                other_submitted.fetch_add(1, Ordering::SeqCst);
            }
        });
        owner.arm().unwrap();
        barrier.wait();
        competitor.join().unwrap();
        assert_eq!(submitted.load(Ordering::SeqCst), 0);
        assert_eq!(owner.finish(PENDING), Err(StorageFailure::Unavailable));
        drop(owner);
    }
    #[test]
    fn boxed_storage_early_error_runs_the_same_quarantine_drop() {
        let state = state();
        let drops = Arc::new(AtomicUsize::new(0));
        let owner = Box::new(guard(state, drops.clone()));
        owner.arm().unwrap();
        let error = owner.finish(PENDING);
        drop(owner);
        assert_eq!(error, Err(StorageFailure::Unavailable));
        assert_eq!(drops.load(Ordering::SeqCst), 0);
        assert_eq!(state.load(Ordering::Acquire), QUARANTINED);
    }
    #[test]
    fn real_create_graph_remains_stable_after_quarantine_and_drop() {
        struct NativeProbe {
            request: Box<CreateRequest>,
            drops: Arc<AtomicUsize>,
        }
        impl Drop for NativeProbe {
            fn drop(&mut self) {
                self.drops.fetch_add(1, Ordering::SeqCst);
            }
        }
        let state = state();
        let drops = Arc::new(AtomicUsize::new(0));
        let name: Vec<u16> = "operations.wal".encode_utf16().collect();
        let owner = CustodyGuard::new(
            Reservation::acquire(state).unwrap(),
            NativeProbe {
                request: CreateRequest::new(HANDLE::default(), &name, ptr::null()).unwrap(),
                drops: drops.clone(),
            },
        );
        let (handle, iosb, attributes, counted, name_pointer) = {
            let custody = owner.borrow().unwrap();
            let request = &custody.request;
            (
                request.handle.get(),
                request.iosb.get(),
                &request.attributes as *const OBJECT_ATTRIBUTES,
                &request.counted as *const UNICODE_STRING,
                request.name.as_ptr(),
            )
        };
        owner.arm().unwrap();
        assert_eq!(owner.finish(PENDING), Err(StorageFailure::Unavailable));
        drop(owner);
        assert_eq!(drops.load(Ordering::SeqCst), 0);
        // SAFETY: synthetic completion targets the complete intentionally retained
        // native graph. No ownership transfer/close or concurrent reads occur.
        unsafe {
            ptr::write(handle, HANDLE(123usize as *mut c_void));
            (*iosb).Anonymous.Status = NTSTATUS(0);
            (*iosb).Information = 1;
            assert_eq!((*attributes).ObjectName, counted);
            assert_eq!((*counted).Buffer.0, name_pointer.cast_mut());
            assert_eq!((*counted).Length as usize, name.len() * 2);
            assert_eq!(*name_pointer, name[0]);
        }
        assert!(matches!(
            Reservation::acquire(state),
            Err(StorageFailure::Unavailable)
        ));
    }
    #[test]
    fn real_query_outputs_accept_late_mock_completion_after_unwind() {
        struct NativeProbe {
            request: Box<QueryRequest>,
            drops: Arc<AtomicUsize>,
        }
        impl Drop for NativeProbe {
            fn drop(&mut self) {
                self.drops.fetch_add(1, Ordering::SeqCst);
            }
        }
        let state = state();
        let drops = Arc::new(AtomicUsize::new(0));
        let owner = CustodyGuard::new(
            Reservation::acquire(state).unwrap(),
            NativeProbe {
                request: Box::new(QueryRequest {
                    iosb: UnsafeCell::new(iosb()),
                    output: UnsafeCell::new([0; 16]),
                }),
                drops: drops.clone(),
            },
        );
        let (iosb, output) = {
            let custody = owner.borrow().unwrap();
            (custody.request.iosb.get(), custody.request.output.get())
        };
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(move || {
            owner.arm().unwrap();
            panic!("unclassified query");
        }));
        assert!(result.is_err());
        assert_eq!(drops.load(Ordering::SeqCst), 0);
        // SAFETY: both late outputs remain live in the same quarantined capsule.
        unsafe {
            (*iosb).Anonymous.Status = NTSTATUS(0);
            (*iosb).Information = 8;
            (*output)[0] = 777;
            assert_eq!((*output)[0], 777);
        }
        assert_eq!(state.load(Ordering::Acquire), QUARANTINED);
    }
    #[test]
    fn trait_object_early_shared_validation_failure_retains_custody() {
        struct Storage {
            guard: CustodyGuard<Probe>,
        }
        impl Read for Storage {
            fn read(&mut self, _: &mut [u8]) -> io::Result<usize> {
                Ok(0)
            }
        }
        impl Write for Storage {
            fn write(&mut self, _: &[u8]) -> io::Result<usize> {
                Ok(0)
            }
            fn flush(&mut self) -> io::Result<()> {
                Ok(())
            }
        }
        impl Seek for Storage {
            fn seek(&mut self, _: SeekFrom) -> io::Result<u64> {
                Ok(0)
            }
        }
        impl JournalStorage for Storage {
            fn validate_custody(&self) -> Result<u64, StorageFailure> {
                self.guard.arm()?;
                self.guard.finish(PENDING)?;
                Ok(0)
            }
            fn truncate(&mut self, _: u64) -> Result<(), StorageFailure> {
                Ok(())
            }
            fn sync_durable(&mut self) -> Result<(), StorageFailure> {
                self.guard.arm()?;
                self.guard.finish(PENDING)
            }
        }
        let state = state();
        let drops = Arc::new(AtomicUsize::new(0));
        let storage: Box<dyn JournalStorage> = Box::new(Storage {
            guard: guard(state, drops.clone()),
        });
        assert_eq!(storage.validate_custody(), Err(StorageFailure::Unavailable));
        drop(storage);
        assert_eq!(drops.load(Ordering::SeqCst), 0);
        assert_eq!(state.load(Ordering::Acquire), QUARANTINED);
    }
    #[test]
    fn completed_conflicting_or_unwritten_iosb_is_refusal_not_quarantine() {
        let state = state();
        let owner = guard(state, Arc::new(AtomicUsize::new(0)));
        let output = UnsafeCell::new(iosb());
        owner.arm().unwrap();
        owner.finish(NTSTATUS(0)).unwrap();
        assert_eq!(
            completed_output(&output, NTSTATUS(0), Some(8)),
            Err(StorageFailure::Unsafe)
        );
        drop(owner);
        assert_eq!(state.load(Ordering::Acquire), FREE);
    }
    #[test]
    fn full_namespace_protocol_repeats_after_every_completed_failure() {
        let expected = [None, Some(4), Some(3), Some(2), None];
        for failure in 0..expected.len() {
            let mut first = Vec::new();
            let outcome = constructor_flush(2, 5, |stage| {
                let index = first.len();
                first.push(stage);
                if index == failure {
                    Err(StorageFailure::Unavailable)
                } else {
                    Ok(())
                }
            });
            assert_eq!(outcome, Err(StorageFailure::Unavailable));
            assert_eq!(first.len(), failure + 1);
            // Surviving declared objects are all existing on the next attempt;
            // no FILE_CREATED disposition is needed to schedule the full chain.
            let mut second = Vec::new();
            constructor_flush(2, 5, |stage| {
                second.push(stage);
                Ok(())
            })
            .unwrap();
            assert_eq!(second, expected);
        }
    }
    #[test]
    fn path_policy_refuses_redirected_alias_and_unbounded_routes() {
        for invalid in [
            "C:relative",
            "\\\\server\\share\\state",
            "\\\\?\\C:\\state",
            "C:\\bad:stream",
            "C:\\a\\..\\b",
            "C:\\CON.txt",
            "C:\\COM¹.txt",
            "C:\\tail.",
            "C:\\tail ",
            "C:\\a\\\\b",
        ] {
            assert!(
                route(invalid.encode_utf16().collect()).is_err(),
                "{invalid}"
            );
        }
        assert!(
            route(
                "C:\\Users\\Commander\\AppData\\Local"
                    .encode_utf16()
                    .collect()
            )
            .is_ok()
        );
        let excessive = format!("C:\\{}", vec!["a"; MAX_COMPONENTS + 1].join("\\"));
        assert!(route(excessive.encode_utf16().collect()).is_err());
        assert!(validate_component(&vec![65; MAX_COMPONENT_UNITS + 1]).is_err());
    }
    #[test]
    fn sid_extent_checks_interior_pointer_and_count_before_native_helpers() {
        let mut allocation = [0u32; 20];
        let base = allocation.as_mut_ptr().cast::<u8>();
        // SAFETY: valid initialized synthetic storage, no token/filesystem effect.
        unsafe {
            *base = 1;
            *base.add(1) = 16;
        }
        assert!(sid_extent(PSID(base.cast()), base.cast(), size_of_val(&allocation)).is_err());
        assert!(
            sid_extent(
                PSID(unsafe { base.add(1) }.cast()),
                base.cast(),
                size_of_val(&allocation)
            )
            .is_err()
        );
        assert!(
            sid_extent(
                PSID(unsafe { base.add(76) }.cast()),
                base.cast(),
                size_of_val(&allocation)
            )
            .is_err()
        );
    }
    #[test]
    fn synthetic_private_security_rejects_grants_owner_mask_and_inheritance_drift() {
        // Native token/SD conversions only: no file or namespace is opened.
        let sid = process_sid().unwrap();
        let text = sid_string(&sid).unwrap();
        for directory in [false, true] {
            let descriptor = security_descriptor(&sid, directory).unwrap();
            assert!(inspect_descriptor(&descriptor, &sid, directory).is_ok());
        }
        for sddl in [
            format!("O:{text}D:(A;;FA;;;{text})"),
            format!("O:{text}D:P(A;;FA;;;{text})(A;;FA;;;WD)"),
            format!("O:WDD:P(A;;FA;;;{text})"),
            format!("O:{text}D:P(A;;FR;;;{text})"),
            format!("O:{text}D:P(A;OICI;FA;;;{text})"),
            format!("O:{text}D:P"),
            format!("O:{text}D:NO_ACCESS_CONTROL"),
        ] {
            let descriptor = descriptor_from_sddl(&sddl).unwrap();
            assert!(inspect_descriptor(&descriptor, &sid, false).is_err());
        }
    }
}
