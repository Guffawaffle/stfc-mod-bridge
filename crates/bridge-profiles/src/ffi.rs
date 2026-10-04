use crate::{
    AbiStatus, CatalogReply, CatalogRequest, ConsumerError, DiagnosticDisposition,
    MAX_LEASE_ERROR_BYTES, MAX_PATH_BYTES, MAX_REQUEST_BYTES, MAX_RESPONSE_BYTES, NativeId,
    NativePath, response,
};
use bridge_native::{LoadedModule, NativeComponent};
use std::{
    ffi::{CString, c_char, c_int, c_void},
    marker::PhantomData,
    ptr::NonNull,
    rc::Rc,
    sync::Arc,
};

type RequestFn = unsafe extern "C" fn(*const c_char, *mut *mut c_char) -> c_int;
type FreeFn = unsafe extern "C" fn(*mut c_void);
type AcquireFn =
    unsafe extern "C" fn(*const c_char, *const c_char, *mut *mut c_void, *mut *mut c_char) -> c_int;
type ReleaseFn = unsafe extern "C" fn(*mut c_void);

enum ModuleOwner {
    Native {
        _module: Arc<LoadedModule>,
    },
    #[cfg(test)]
    Fixture {
        _module: Rc<tests::ModuleProbe>,
    },
}
struct Functions {
    owner: ModuleOwner,
    request: RequestFn,
    free: FreeFn,
    acquire_data: AcquireFn,
    release_data: ReleaseFn,
    acquire_installation: AcquireFn,
    release_installation: ReleaseFn,
}

/// A client must be created, invoked and dropped on one owning thread.
///
/// ```compile_fail
/// fn send<T: Send>() {}
/// send::<bridge_profiles::ProfilesClient>();
/// ```
/// ```compile_fail
/// fn sync<T: Sync>() {}
/// sync::<bridge_profiles::ProfilesClient>();
/// ```
pub struct ProfilesClient {
    functions: Arc<Functions>,
    _thread: PhantomData<Rc<()>>,
}
impl ProfilesClient {
    /// Bind exactly the adopted producer's six v1 allocation/lease exports.
    ///
    /// # Safety
    /// The adopted module must implement the documented signatures and allocator
    /// pairing. Every returned nonnull pointer must denote its own allocation or
    /// valid opaque lease. Returned strings must be readable through a NUL; the
    /// ABI supplies neither allocation lengths nor a producer-side response cap.
    /// Bounded scanning limits work, but cannot make an invalid pointer safe.
    /// Native initializers and calls execute trusted foreign code. The module
    /// hash/origin observations do not establish native runtime qualification.
    pub unsafe fn bind(module: Arc<LoadedModule>) -> Result<Self, ConsumerError> {
        if module.identity().component != NativeComponent::Profiles {
            return Err(ConsumerError::WrongComponent);
        }
        let functions = Functions {
            // SAFETY: the caller adopts the exact producer ABI; the loader checks
            // each symbol's actual originating module before it is retained.
            request: unsafe { module.resolve(c"stfc_profiles_catalog_request_v1") }
                .map_err(|_| ConsumerError::BindingFailure)?,
            free: unsafe { module.resolve(c"stfc_profiles_free_v1") }
                .map_err(|_| ConsumerError::BindingFailure)?,
            acquire_data: unsafe { module.resolve(c"stfc_profiles_acquire_data_lease_v1") }
                .map_err(|_| ConsumerError::BindingFailure)?,
            release_data: unsafe { module.resolve(c"stfc_profiles_release_data_lease_v1") }
                .map_err(|_| ConsumerError::BindingFailure)?,
            acquire_installation: unsafe {
                module.resolve(c"stfc_profiles_acquire_installation_lease_v1")
            }
            .map_err(|_| ConsumerError::BindingFailure)?,
            release_installation: unsafe {
                module.resolve(c"stfc_profiles_release_installation_lease_v1")
            }
            .map_err(|_| ConsumerError::BindingFailure)?,
            owner: ModuleOwner::Native { _module: module },
        };
        // Arc is a lifetime container mandated by the module custody contract;
        // this intentionally thread-confined table is never shared across threads.
        #[allow(clippy::arc_with_non_send_sync)]
        let functions = Arc::new(functions);
        let client = Self {
            functions,
            _thread: PhantomData,
        };
        client.verify_api2()?;
        Ok(client)
    }

    fn verify_api2(&self) -> Result<(), ConsumerError> {
        // The producer has no version export. This operation reports only the
        // OS-user root path and creates neither catalog state nor native leases.
        match self.request(&CatalogRequest {
            root: None,
            operation: crate::CatalogOperation::CatalogLocation,
        })? {
            CatalogReply::Success(output)
                if matches!(*output, crate::CatalogSuccess::CatalogLocation(_)) =>
            {
                Ok(())
            }
            _ => Err(ConsumerError::InvalidResponse),
        }
    }

    /// Execute a synchronous, typed JSON API 2 request on the owning thread.
    /// A structured native refusal is `Ok(CatalogReply::Refused(...))`, distinct
    /// from an allocation ABI failure. No timeout or observer can cancel this ABI.
    pub fn request(&self, request: &CatalogRequest) -> Result<CatalogReply, ConsumerError> {
        let bytes = request.encoded()?;
        if bytes.len() > MAX_REQUEST_BYTES {
            return Err(ConsumerError::RequestTooLarge);
        }
        let input = CString::new(bytes).map_err(|_| ConsumerError::InvalidInput)?;
        let mut raw = std::ptr::null_mut();
        // SAFETY: the bound ABI is adopted, the input is bounded/NUL terminated,
        // and output storage is valid until this synchronous invocation returns.
        let status = unsafe { (self.functions.request)(input.as_ptr(), &mut raw) };
        // Guard every returned allocation before examining status or content.
        let response = NativeBuffer::new(raw, self.functions.clone());
        if status != 0 {
            return Err(ConsumerError::Abi {
                status: abi_status(status),
                diagnostic: if response.is_some() {
                    DiagnosticDisposition::Suppressed
                } else {
                    DiagnosticDisposition::Absent
                },
            });
        }
        let response = response.ok_or(ConsumerError::MissingOutput)?;
        response::decode(request, response.bytes(MAX_RESPONSE_BYTES)?)
    }

    /// Shared profile-data access. This is neither a game session reservation nor
    /// an exclusive writer lock. Hold through the owned configuration operation.
    pub fn acquire_data_lease(
        &self,
        root: Option<&NativePath>,
        id: &NativeId,
    ) -> Result<DataLease, ConsumerError> {
        self.acquire(
            root,
            id.as_str(),
            self.functions.acquire_data,
            self.functions.release_data,
        )
        .map(|guard| DataLease { _guard: guard })
    }

    /// Shared installation access. The producer may write a coordination lock to
    /// its OS-user catalog even when `root` is an explicit private fixture root.
    /// Retain this lease for the exact child's required launch/session lifetime.
    pub fn acquire_installation_lease(
        &self,
        root: Option<&NativePath>,
        game: &NativePath,
    ) -> Result<InstallationLease, ConsumerError> {
        self.acquire(
            root,
            game.as_str(),
            self.functions.acquire_installation,
            self.functions.release_installation,
        )
        .map(|guard| InstallationLease { _guard: guard })
    }

    fn acquire(
        &self,
        root: Option<&NativePath>,
        selector: &str,
        acquire: AcquireFn,
        release: ReleaseFn,
    ) -> Result<LeaseGuard, ConsumerError> {
        let root = root
            .map(|p| CString::new(p.as_str()).map_err(|_| ConsumerError::InvalidInput))
            .transpose()?;
        if root
            .as_ref()
            .is_some_and(|r| r.as_bytes().len() > MAX_PATH_BYTES)
        {
            return Err(ConsumerError::InvalidInput);
        }
        let selector = CString::new(selector).map_err(|_| ConsumerError::InvalidInput)?;
        let mut raw_lease = std::ptr::null_mut();
        let mut raw_error = std::ptr::null_mut();
        let status = unsafe {
            acquire(
                root.as_ref().map_or(std::ptr::null(), |r| r.as_ptr()),
                selector.as_ptr(),
                &mut raw_lease,
                &mut raw_error,
            )
        };
        // Both pointers acquire their originating guards before any refusal.
        let error = NativeBuffer::new(raw_error, self.functions.clone());
        let lease = NonNull::new(raw_lease).map(|pointer| LeaseGuard {
            pointer,
            release,
            functions: self.functions.clone(),
            _thread: PhantomData,
        });
        if status == 0 {
            if error.is_some() {
                return Err(ConsumerError::ContradictoryOutput);
            }
            return lease.ok_or(ConsumerError::MissingOutput);
        }
        if lease.is_some() {
            return Err(ConsumerError::ContradictoryOutput);
        }
        let diagnostic = match error {
            None => DiagnosticDisposition::Absent,
            Some(error) => match error.bytes(MAX_LEASE_ERROR_BYTES) {
                Ok(_) => DiagnosticDisposition::Suppressed,
                Err(ConsumerError::InvalidUtf8) => DiagnosticDisposition::InvalidUtf8,
                Err(_) => DiagnosticDisposition::TooLarge,
            },
        };
        Err(ConsumerError::Abi {
            status: abi_status(status),
            diagnostic,
        })
    }
}
fn abi_status(status: c_int) -> AbiStatus {
    match status {
        1 => AbiStatus::InvalidInput,
        2 => AbiStatus::NativeFailure,
        _ => AbiStatus::Unexpected,
    }
}

struct NativeBuffer {
    pointer: NonNull<c_char>,
    functions: Arc<Functions>,
    _thread: PhantomData<Rc<()>>,
}
impl NativeBuffer {
    fn new(pointer: *mut c_char, functions: Arc<Functions>) -> Option<Self> {
        NonNull::new(pointer).map(|pointer| Self {
            pointer,
            functions,
            _thread: PhantomData,
        })
    }
    fn bytes(&self, maximum: usize) -> Result<&[u8], ConsumerError> {
        let pointer = self.pointer.as_ptr().cast::<u8>();
        for length in 0..=maximum {
            // SAFETY: unsafe bind adopts the producer's readable NUL-terminated
            // allocation contract. We stop at NUL, and never scan beyond the cap
            // plus its one terminator byte. This does not validate allocation size.
            if unsafe { pointer.add(length).read() } == 0 {
                let bytes = unsafe { std::slice::from_raw_parts(pointer, length) };
                std::str::from_utf8(bytes).map_err(|_| ConsumerError::InvalidUtf8)?;
                return Ok(bytes);
            }
        }
        Err(ConsumerError::ResponseTooLarge)
    }
}
impl Drop for NativeBuffer {
    fn drop(&mut self) {
        // The matching allocator runs while its function table/module is alive.
        unsafe { (self.functions.free)(self.pointer.as_ptr().cast()) };
    }
}
struct LeaseGuard {
    pointer: NonNull<c_void>,
    release: ReleaseFn,
    functions: Arc<Functions>,
    _thread: PhantomData<Rc<()>>,
}
impl Drop for LeaseGuard {
    fn drop(&mut self) {
        let _module_custody = &self.functions.owner;
        unsafe { (self.release)(self.pointer.as_ptr()) };
    }
}

/// Opaque shared profile-data lease, with its originating module retained.
/// ```compile_fail
/// fn send<T: Send>() {} send::<bridge_profiles::DataLease>();
/// ```
/// ```compile_fail
/// fn sync<T: Sync>() {} sync::<bridge_profiles::DataLease>();
/// ```
pub struct DataLease {
    _guard: LeaseGuard,
}

/// Opaque shared installation lease; exposes no physical/session identity getter.
/// ```compile_fail
/// fn send<T: Send>() {} send::<bridge_profiles::InstallationLease>();
/// ```
/// ```compile_fail
/// fn sync<T: Sync>() {} sync::<bridge_profiles::InstallationLease>();
/// ```
pub struct InstallationLease {
    _guard: LeaseGuard,
}

#[cfg(test)]
mod tests;
