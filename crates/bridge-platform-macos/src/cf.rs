//! Minimal owned CoreFoundation references. Create/Copy outputs are released
//! once; Get/static values are borrowed and never released by this module.
use crate::{format, native};
use bridge_domain::platform::{PlatformError, PlatformErrorCode};
use std::{
    ffi::{OsString, c_void},
    marker::PhantomData,
    os::unix::ffi::{OsStrExt, OsStringExt},
    path::{Path, PathBuf},
    ptr::NonNull,
    rc::Rc,
};

pub(crate) type Ref = *const c_void;
pub(crate) type Index = isize;

#[repr(C)]
struct ArrayCallbacks {
    version: Index,
    retain: Option<unsafe extern "C" fn(Ref, Ref) -> Ref>,
    release: Option<unsafe extern "C" fn(Ref, Ref)>,
    copy_description: Option<unsafe extern "C" fn(Ref) -> Ref>,
    equal: Option<unsafe extern "C" fn(Ref, Ref) -> u8>,
}

#[link(name = "CoreFoundation", kind = "framework")]
unsafe extern "C" {
    fn CFRelease(value: Ref);
    pub(crate) fn CFGetTypeID(value: Ref) -> usize;
    pub(crate) fn CFDataGetTypeID() -> usize;
    pub(crate) fn CFDataGetLength(value: Ref) -> Index;
    pub(crate) fn CFDataGetBytePtr(value: Ref) -> *const u8;
    fn CFDataCreate(allocator: Ref, bytes: *const u8, length: Index) -> Ref;
    fn CFStringCreateWithBytes(
        allocator: Ref,
        bytes: *const u8,
        length: Index,
        encoding: u32,
        external: u8,
    ) -> Ref;
    fn CFURLCreateFromFileSystemRepresentation(
        allocator: Ref,
        bytes: *const u8,
        length: Index,
        directory: u8,
    ) -> Ref;
    fn CFURLGetFileSystemRepresentation(
        url: Ref,
        resolve: u8,
        bytes: *mut u8,
        maximum: Index,
    ) -> u8;
    fn CFDictionaryCreateMutable(
        allocator: Ref,
        capacity: Index,
        key_callbacks: Ref,
        value_callbacks: Ref,
    ) -> Ref;
    fn CFDictionarySetValue(dictionary: Ref, key: Ref, value: Ref);
    pub(crate) fn CFDictionaryGetTypeID() -> usize;
    pub(crate) fn CFDictionaryGetValue(dictionary: Ref, key: Ref) -> Ref;
    pub(crate) fn CFArrayGetTypeID() -> usize;
    pub(crate) fn CFArrayGetCount(array: Ref) -> Index;
    pub(crate) fn CFArrayGetValueAtIndex(array: Ref, index: Index) -> Ref;
    fn CFArrayCreate(
        allocator: Ref,
        values: *const Ref,
        count: Index,
        callbacks: *const ArrayCallbacks,
    ) -> Ref;
    static kCFTypeArrayCallBacks: ArrayCallbacks;
    pub(crate) fn CFErrorGetTypeID() -> usize;
    pub(crate) fn CFErrorGetCode(error: Ref) -> Index;
    pub(crate) static kCFBooleanTrue: Ref;
    pub(crate) static kCFBooleanFalse: Ref;
}

pub(crate) struct Owned {
    pointer: NonNull<c_void>,
    _local: PhantomData<Rc<()>>,
}

impl Owned {
    /// The caller transfers a successful Create/Copy output, never a Get result.
    pub(crate) unsafe fn from_create(value: Ref) -> Result<Self, PlatformError> {
        Ok(Self {
            pointer: NonNull::new(value.cast_mut())
                .ok_or_else(|| PlatformError::new(PlatformErrorCode::NativeFailure))?,
            _local: PhantomData,
        })
    }

    pub(crate) fn as_ref(&self) -> Ref {
        self.pointer.as_ptr()
    }

    pub(crate) fn string(value: &str) -> Result<Self, PlatformError> {
        if value.contains('\0') || value.len() > format::MAX_PATH_BYTES {
            return Err(PlatformError::new(PlatformErrorCode::InvalidInput));
        }
        // SAFETY: valid UTF-8 bytes remain alive for the copying Create call.
        unsafe {
            Self::from_create(CFStringCreateWithBytes(
                std::ptr::null(),
                value.as_ptr(),
                value.len() as Index,
                0x0800_0100,
                0,
            ))
        }
    }

    pub(crate) fn data(value: &[u8]) -> Result<Self, PlatformError> {
        if value.len() > format::MAX_SECRET_BYTES + 64 {
            return Err(PlatformError::new(PlatformErrorCode::TooLarge));
        }
        // SAFETY: bounded live bytes are copied by CFDataCreate.
        unsafe {
            Self::from_create(CFDataCreate(
                std::ptr::null(),
                value.as_ptr(),
                value.len() as Index,
            ))
        }
    }

    pub(crate) fn url(path: &Path, directory: bool) -> Result<Self, PlatformError> {
        let bytes = path.as_os_str().as_bytes();
        format::absolute_components(bytes)?;
        // SAFETY: bounded filesystem bytes are copied by the URL Create call.
        unsafe {
            Self::from_create(CFURLCreateFromFileSystemRepresentation(
                std::ptr::null(),
                bytes.as_ptr(),
                bytes.len() as Index,
                u8::from(directory),
            ))
        }
    }

    pub(crate) fn array(values: &[&Owned]) -> Result<Self, PlatformError> {
        if values.len() > 16 {
            return Err(PlatformError::new(PlatformErrorCode::TooLarge));
        }
        let pointers: Vec<_> = values.iter().map(|value| value.as_ref()).collect();
        // SAFETY: CFType callbacks retain each live input; array owns its references.
        unsafe {
            Self::from_create(CFArrayCreate(
                std::ptr::null(),
                pointers.as_ptr(),
                pointers.len() as Index,
                &kCFTypeArrayCallBacks,
            ))
        }
    }
}

impl Drop for Owned {
    fn drop(&mut self) {
        // SAFETY: exactly one owning Create/Copy reference is retained.
        unsafe { CFRelease(self.as_ref()) };
    }
}

pub(crate) fn url_path(url: Ref) -> Result<PathBuf, PlatformError> {
    let mut bytes = [0; format::MAX_PATH_BYTES + 1];
    // SAFETY: caller supplies a borrowed live URL, with bounded writable output.
    if unsafe { CFURLGetFileSystemRepresentation(url, 1, bytes.as_mut_ptr(), bytes.len() as Index) }
        == 0
    {
        return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
    }
    let end = bytes
        .iter()
        .position(|byte| *byte == 0)
        .ok_or_else(|| PlatformError::new(PlatformErrorCode::TooLarge))?;
    format::absolute_components(&bytes[..end])?;
    Ok(PathBuf::from(OsString::from_vec(bytes[..end].to_vec())))
}

/// Callbacks are deliberately NULL: the dictionary borrows fixed static keys
/// and retained owned values. All values live until after its final native call.
pub(crate) struct Dictionary {
    dictionary: Owned,
    values: Vec<Owned>,
}

impl Dictionary {
    pub(crate) fn new() -> Result<Self, PlatformError> {
        // SAFETY: CF creates a mutable pointer dictionary with no callbacks.
        let dictionary = unsafe {
            Owned::from_create(CFDictionaryCreateMutable(
                std::ptr::null(),
                0,
                std::ptr::null(),
                std::ptr::null(),
            ))?
        };
        Ok(Self {
            dictionary,
            values: Vec::new(),
        })
    }

    pub(crate) fn put_owned(&mut self, key: Ref, value: Owned) {
        self.put_static(key, value.as_ref());
        self.values.push(value);
    }

    pub(crate) fn put_static(&mut self, key: Ref, value: Ref) {
        // SAFETY: dictionary is mutable/local; keys and values stay live.
        unsafe { CFDictionarySetValue(self.dictionary.as_ref(), key, value) };
    }

    pub(crate) fn as_ref(&self) -> Ref {
        self.dictionary.as_ref()
    }
}

pub(crate) fn checked_data(value: Ref, maximum: usize) -> Result<Vec<u8>, PlatformError> {
    // SAFETY: borrowed value is checked for null/type before Data APIs.
    if value.is_null() || unsafe { CFGetTypeID(value) != CFDataGetTypeID() } {
        return Err(PlatformError::new(PlatformErrorCode::UnknownObservation));
    }
    // SAFETY: CFData type validated above, reference retained by caller.
    let length = unsafe { CFDataGetLength(value) };
    if length < 0 || length as usize > maximum {
        return Err(PlatformError::new(PlatformErrorCode::TooLarge));
    }
    if length == 0 {
        return Ok(Vec::new());
    }
    // SAFETY: CFData type/length checked; borrowed storage remains alive.
    let pointer = unsafe { CFDataGetBytePtr(value) };
    if pointer.is_null() {
        return Err(native::status_error(-50));
    }
    // SAFETY: CFData guarantees byte storage of its reported length.
    Ok(unsafe { std::slice::from_raw_parts(pointer, length as usize) }.to_vec())
}
