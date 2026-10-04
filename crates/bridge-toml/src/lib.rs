//! Typed consumer of the producer-owned, offline STFC TOML C ABI.
//!
//! This crate performs no file, process or network operations. Native adoption
//! is explicitly unsafe; pointer checks cannot prove allocation validity.

use bridge_native::{LoadError, LoadedModule, NativeComponent};
use serde::de::Error as _;
use serde::ser::SerializeMap;
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use std::ffi::{c_char, c_int, c_void};
use std::io::Write;
use std::marker::PhantomData;
use std::num::NonZeroU32;
use std::ptr::NonNull;
use std::rc::Rc;
use std::sync::Arc;

pub const ABI_VERSION: u32 = 1;
/// Producer maximum, measured after JSON encoding and escaping.
pub const MAX_REQUEST_BYTES: usize = 64 * 1024 * 1024;
/// Consumer processing bound; the producer has no response size cap.
pub const MAX_RESPONSE_BYTES: usize = 64 * 1024 * 1024;

/// Decoded key segments: literal dots do not become separators. Empty quoted
/// key segments are valid; an empty path vector is not.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(transparent)]
pub struct TomlPath(Vec<String>);
impl TomlPath {
    pub fn new(segments: Vec<String>) -> Result<Self, TomlError> {
        if segments.is_empty() {
            Err(TomlError::InvalidPath)
        } else {
            Ok(Self(segments))
        }
    }
    pub fn segments(&self) -> &[String] {
        &self.0
    }
}
impl<'de> Deserialize<'de> for TomlPath {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        Self::new(Vec::<String>::deserialize(deserializer)?)
            .map_err(|_| D::Error::custom("empty TOML path"))
    }
}

#[derive(Clone, Copy, Debug)]
pub enum PathInput<'a> {
    Expression(&'a str),
    Segments(&'a TomlPath),
}

/// Values remain TOML source, preserving int64, floats and datetime syntax.
#[derive(Clone, Copy, Debug)]
pub enum TomlRequest<'a> {
    Validate {
        text: &'a str,
    },
    Read {
        text: &'a str,
    },
    Set {
        text: &'a str,
        path: &'a TomlPath,
        value: &'a str,
    },
    Remove {
        text: &'a str,
        path: &'a TomlPath,
    },
    RemoveTable {
        text: &'a str,
        path: &'a TomlPath,
    },
    RenameTable {
        text: &'a str,
        path: &'a TomlPath,
        destination: &'a TomlPath,
    },
    NormalizeValue {
        value: &'a str,
    },
    DecodeString {
        value: &'a str,
    },
    ParsePath {
        input: PathInput<'a>,
    },
}
impl Serialize for TomlRequest<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut map = serializer.serialize_map(None)?;
        match *self {
            Self::Validate { text } | Self::Read { text } => {
                map.serialize_entry(
                    "operation",
                    if matches!(self, Self::Validate { .. }) {
                        "validate"
                    } else {
                        "read"
                    },
                )?;
                map.serialize_entry("text", text)?;
            }
            Self::Set { text, path, value } => {
                map.serialize_entry("operation", "set")?;
                map.serialize_entry("text", text)?;
                map.serialize_entry("path", path)?;
                map.serialize_entry("value", value)?;
            }
            Self::Remove { text, path } | Self::RemoveTable { text, path } => {
                map.serialize_entry(
                    "operation",
                    if matches!(self, Self::Remove { .. }) {
                        "remove"
                    } else {
                        "remove_table"
                    },
                )?;
                map.serialize_entry("text", text)?;
                map.serialize_entry("path", path)?;
            }
            Self::RenameTable {
                text,
                path,
                destination,
            } => {
                map.serialize_entry("operation", "rename_table")?;
                map.serialize_entry("text", text)?;
                map.serialize_entry("path", path)?;
                map.serialize_entry("destination", destination)?;
            }
            Self::NormalizeValue { value } | Self::DecodeString { value } => {
                map.serialize_entry(
                    "operation",
                    if matches!(self, Self::NormalizeValue { .. }) {
                        "normalize_value"
                    } else {
                        "decode_string"
                    },
                )?;
                map.serialize_entry("value", value)?;
            }
            Self::ParsePath { input } => {
                map.serialize_entry("operation", "parse_path")?;
                match input {
                    PathInput::Expression(value) => map.serialize_entry("value", value)?,
                    PathInput::Segments(path) => map.serialize_entry("path", path)?,
                }
            }
        }
        map.end()
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct TomlOverride {
    pub path: TomlPath,
    pub canonical_path: String,
    /// Original TOML source, never a JSON numeric conversion.
    pub value: String,
    /// Producer-normalized TOML source.
    pub semantic_value: String,
    pub line: NonZeroU32,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct TomlTable {
    pub path: TomlPath,
    pub canonical_path: String,
    pub line: NonZeroU32,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TomlSnapshot {
    pub overrides: Vec<TomlOverride>,
    pub tables: Vec<TomlTable>,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ParsedPath {
    pub path: TomlPath,
    pub canonical_path: String,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum TomlReply {
    Validated,
    Snapshot(TomlSnapshot),
    EditedText(String),
    Value(String),
    ParsedPath(ParsedPath),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeErrorCode {
    InvalidUtf8,
    InvalidPath,
    InvalidValue,
    InvalidDocument,
    DuplicateTarget,
    UnsupportedTarget,
    InternalError,
}
impl<'de> Deserialize<'de> for NativeErrorCode {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        match String::deserialize(deserializer)?.as_str() {
            "InvalidUtf8" => Ok(Self::InvalidUtf8),
            "InvalidPath" => Ok(Self::InvalidPath),
            "InvalidValue" => Ok(Self::InvalidValue),
            "InvalidDocument" => Ok(Self::InvalidDocument),
            "DuplicateTarget" => Ok(Self::DuplicateTarget),
            "UnsupportedTarget" => Ok(Self::UnsupportedTarget),
            "InternalError" => Ok(Self::InternalError),
            _ => Err(D::Error::custom("unknown native error code")),
        }
    }
}
impl NativeErrorCode {
    pub fn code(self) -> &'static str {
        match self {
            Self::InvalidUtf8 => "InvalidUtf8",
            Self::InvalidPath => "InvalidPath",
            Self::InvalidValue => "InvalidValue",
            Self::InvalidDocument => "InvalidDocument",
            Self::DuplicateTarget => "DuplicateTarget",
            Self::UnsupportedTarget => "UnsupportedTarget",
            Self::InternalError => "InternalError",
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AbiStatus {
    InvalidInput,
    InternalFailure,
    Unexpected,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ResponseFault {
    ContradictoryOutput,
    MissingAllocation,
    Empty,
    TooLarge,
    InvalidUtf8,
    InvalidShape,
}
/// Closed, path-free errors. Native diagnostics and inputs are not retained.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TomlError {
    Load(LoadError),
    WrongComponent,
    UnsupportedAbiVersion,
    InvalidPath,
    RequestTooLarge,
    RequestEncodingFailed,
    AbiFailure(AbiStatus),
    MalformedResponse(ResponseFault),
    NativeRefusal {
        code: NativeErrorCode,
        line: Option<NonZeroU32>,
    },
}
impl std::fmt::Display for TomlError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            Self::Load(error) => error.code,
            Self::WrongComponent => "wrong_native_component",
            Self::UnsupportedAbiVersion => "unsupported_toml_abi",
            Self::InvalidPath => "invalid_toml_path",
            Self::RequestTooLarge => "toml_request_too_large",
            Self::RequestEncodingFailed => "toml_request_encoding_failed",
            Self::AbiFailure(AbiStatus::InvalidInput) => "toml_abi_invalid_input",
            Self::AbiFailure(AbiStatus::InternalFailure) => "toml_abi_internal_failure",
            Self::AbiFailure(AbiStatus::Unexpected) => "toml_abi_unexpected_status",
            Self::MalformedResponse(_) => "malformed_toml_response",
            Self::NativeRefusal { code, .. } => code.code(),
        })
    }
}
impl std::error::Error for TomlError {}
impl From<LoadError> for TomlError {
    fn from(value: LoadError) -> Self {
        Self::Load(value)
    }
}

type AbiVersionFn = unsafe extern "C" fn() -> u32;
type ExecuteFn = unsafe extern "C" fn(*const c_char, usize, *mut *mut c_char, *mut usize) -> c_int;
type FreeFn = unsafe extern "C" fn(*mut c_void);
struct NativeApi {
    // Production always holds Some; only private allocated unit fixtures omit a
    // dynamically loaded module. Every buffer retains this originating table.
    _module: Option<Arc<LoadedModule>>,
    #[cfg(test)]
    _test_keepalive: Option<Arc<()>>,
    version: AbiVersionFn,
    execute: ExecuteFn,
    free: FreeFn,
}

/// Calls and allocator destruction stay on the creating thread. No reentrancy
/// promise or cancellation of synchronous native work is implied.
///
/// ```compile_fail
/// fn requires_send<T: Send>() {}
/// requires_send::<bridge_toml::TomlClient>();
/// ```
/// ```compile_fail
/// fn requires_sync<T: Sync>() {}
/// requires_sync::<bridge_toml::TomlClient>();
/// ```
pub struct TomlClient {
    api: Arc<NativeApi>,
    _thread: PhantomData<Rc<()>>,
}
impl TomlClient {
    /// Bind the three exact exports of the reviewed TOML ABI.
    ///
    /// # Safety
    /// Adopt a trusted producer implementing these exact C signatures and its
    /// allocation contract; exclusively coordinate component calls in this
    /// process. Identity checks do not prove arbitrary code or pointer validity.
    pub unsafe fn bind(module: Arc<LoadedModule>) -> Result<Self, TomlError> {
        if module.identity().component != NativeComponent::Toml {
            return Err(TomlError::WrongComponent);
        }
        // SAFETY: exact signatures from stfc_toml/c_api.h; trust is established
        // by the caller and the completed table retains originating code.
        let version = unsafe { module.resolve::<AbiVersionFn>(c"stfc_toml_abi_version")? };
        // SAFETY: same reviewed producer and exact function signature.
        let execute = unsafe { module.resolve::<ExecuteFn>(c"stfc_toml_execute")? };
        // SAFETY: same reviewed producer and exact allocator signature.
        let free = unsafe { module.resolve::<FreeFn>(c"stfc_toml_free")? };
        Self::from_api(NativeApi {
            _module: Some(module),
            #[cfg(test)]
            _test_keepalive: None,
            version,
            execute,
            free,
        })
    }
    #[expect(
        clippy::arc_with_non_send_sync,
        reason = "Arc retains code through deliberately thread-bound allocation guards"
    )]
    fn from_api(api: NativeApi) -> Result<Self, TomlError> {
        // SAFETY: only trusted binding or valid private fixtures construct this
        // table, and its originating code is retained throughout invocation.
        if unsafe { (api.version)() } != ABI_VERSION {
            return Err(TomlError::UnsupportedAbiVersion);
        }
        Ok(Self {
            api: Arc::new(api),
            _thread: PhantomData,
        })
    }
    pub fn execute(&mut self, request: TomlRequest<'_>) -> Result<TomlReply, TomlError> {
        self.execute_bounded(request, MAX_REQUEST_BYTES)
    }
    fn execute_bounded(
        &mut self,
        request: TomlRequest<'_>,
        request_limit: usize,
    ) -> Result<TomlReply, TomlError> {
        let encoded = encode_request(request, request_limit)?;
        let mut pointer = std::ptr::null_mut();
        let mut length = 0usize;
        // SAFETY: live length-bounded UTF-8 JSON and initialized writable output
        // parameters. Trusted allocation/code obligations come from binding.
        let status = unsafe {
            (self.api.execute)(
                encoded.as_ptr().cast(),
                encoded.len(),
                &mut pointer,
                &mut length,
            )
        };
        // Guard allocated outputs before checking status or reading any bytes.
        // Matching free still assumes the producer returned its own allocation.
        let output = NonNull::new(pointer).map(|pointer| NativeBuffer {
            pointer,
            length,
            api: Arc::clone(&self.api),
            _thread: PhantomData,
        });
        if status != 0 {
            if output.is_some() || length != 0 {
                return Err(TomlError::MalformedResponse(
                    ResponseFault::ContradictoryOutput,
                ));
            }
            return Err(TomlError::AbiFailure(match status {
                1 => AbiStatus::InvalidInput,
                2 => AbiStatus::InternalFailure,
                _ => AbiStatus::Unexpected,
            }));
        }
        let output = output.ok_or(TomlError::MalformedResponse(if length == 0 {
            ResponseFault::MissingAllocation
        } else {
            ResponseFault::ContradictoryOutput
        }))?;
        let bytes = output.bytes()?;
        std::str::from_utf8(bytes)
            .map_err(|_| TomlError::MalformedResponse(ResponseFault::InvalidUtf8))?;
        decode_response(request, bytes)
    }
}
struct NativeBuffer {
    pointer: NonNull<c_char>,
    length: usize,
    api: Arc<NativeApi>,
    _thread: PhantomData<Rc<()>>,
}
impl NativeBuffer {
    fn bytes(&self) -> Result<&[u8], TomlError> {
        if self.length == 0 {
            return Err(TomlError::MalformedResponse(ResponseFault::Empty));
        }
        if self.length > MAX_RESPONSE_BYTES || self.length > isize::MAX as usize {
            return Err(TomlError::MalformedResponse(ResponseFault::TooLarge));
        }
        // SAFETY: adopted producer guarantees readable initialized bytes of the
        // reported length in one allocation. Bounds limit processing; they do
        // not prove foreign pointer validity. Table/allocation outlive the borrow.
        Ok(unsafe { std::slice::from_raw_parts(self.pointer.as_ptr().cast(), self.length) })
    }
}
impl Drop for NativeBuffer {
    fn drop(&mut self) {
        // SAFETY: the originating allocator owns this pointer, and code remains
        // retained until its one matching free has completed on this thread.
        unsafe { (self.api.free)(self.pointer.as_ptr().cast()) };
    }
}
struct BoundedRequest {
    bytes: Vec<u8>,
    limit: usize,
    exceeded: bool,
}
impl Write for BoundedRequest {
    fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
        let length = self.bytes.len().checked_add(bytes.len());
        if length.is_none_or(|length| length > self.limit) {
            self.exceeded = true;
            return Err(std::io::Error::other("request byte bound"));
        }
        let length = length.unwrap_or(0);
        if length > self.bytes.capacity() {
            let capacity = length
                .max(self.bytes.capacity().saturating_mul(2))
                .min(self.limit);
            self.bytes
                .try_reserve_exact(capacity - self.bytes.len())
                .map_err(|_| std::io::Error::other("request allocation"))?;
        }
        self.bytes.extend_from_slice(bytes);
        Ok(bytes.len())
    }
    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}
fn encode_request(request: TomlRequest<'_>, limit: usize) -> Result<Vec<u8>, TomlError> {
    let mut output = BoundedRequest {
        bytes: Vec::new(),
        limit,
        exceeded: false,
    };
    serde_json::to_writer(&mut output, &request).map_err(|_| {
        if output.exceeded {
            TomlError::RequestTooLarge
        } else {
            TomlError::RequestEncodingFailed
        }
    })?;
    Ok(output.bytes)
}

struct True;
struct False;
impl<'de> Deserialize<'de> for True {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        if bool::deserialize(deserializer)? {
            Ok(Self)
        } else {
            Err(D::Error::custom("expected true"))
        }
    }
}
impl<'de> Deserialize<'de> for False {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        if bool::deserialize(deserializer)? {
            Err(D::Error::custom("expected false"))
        } else {
            Ok(Self)
        }
    }
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FailureReply {
    ok: False,
    error: FailureDetail,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FailureDetail {
    code: NativeErrorCode,
    #[serde(default, deserialize_with = "present_line")]
    line: Option<NonZeroU32>,
}
fn present_line<'de, D: Deserializer<'de>>(
    deserializer: D,
) -> Result<Option<NonZeroU32>, D::Error> {
    NonZeroU32::deserialize(deserializer).map(Some)
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ValidateReply {
    ok: True,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ReadReply {
    ok: True,
    overrides: Vec<TomlOverride>,
    tables: Vec<TomlTable>,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct TextReply {
    ok: True,
    text: String,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ValueReply {
    ok: True,
    value: String,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct PathReply {
    ok: True,
    path: TomlPath,
    value: String,
}
#[derive(Deserialize)]
#[serde(untagged)]
enum Outcome<T> {
    Success(T),
    Refused(FailureReply),
}
fn decode_outcome<'de, T: Deserialize<'de>>(bytes: &'de [u8]) -> Result<T, TomlError> {
    match serde_json::from_slice::<Outcome<T>>(bytes)
        .map_err(|_| TomlError::MalformedResponse(ResponseFault::InvalidShape))?
    {
        Outcome::Success(result) => Ok(result),
        Outcome::Refused(result) => {
            let _ = result.ok;
            Err(TomlError::NativeRefusal {
                code: result.error.code,
                line: result.error.line,
            })
        }
    }
}
fn decode_response(request: TomlRequest<'_>, bytes: &[u8]) -> Result<TomlReply, TomlError> {
    let malformed = || TomlError::MalformedResponse(ResponseFault::InvalidShape);
    match request {
        TomlRequest::Validate { .. } => {
            let response = decode_outcome::<ValidateReply>(bytes)?;
            let _ = response.ok;
            Ok(TomlReply::Validated)
        }
        TomlRequest::Read { .. } => {
            let response = decode_outcome::<ReadReply>(bytes)?;
            let _ = response.ok;
            if response.overrides.iter().any(|row| {
                row.canonical_path.is_empty()
                    || row.value.is_empty()
                    || row.semantic_value.is_empty()
            }) || response
                .tables
                .iter()
                .any(|row| row.canonical_path.is_empty())
            {
                return Err(malformed());
            }
            Ok(TomlReply::Snapshot(TomlSnapshot {
                overrides: response.overrides,
                tables: response.tables,
            }))
        }
        TomlRequest::Set { .. }
        | TomlRequest::Remove { .. }
        | TomlRequest::RemoveTable { .. }
        | TomlRequest::RenameTable { .. } => {
            let response = decode_outcome::<TextReply>(bytes)?;
            let _ = response.ok;
            Ok(TomlReply::EditedText(response.text))
        }
        TomlRequest::NormalizeValue { .. } | TomlRequest::DecodeString { .. } => {
            let response = decode_outcome::<ValueReply>(bytes)?;
            let _ = response.ok;
            if matches!(request, TomlRequest::NormalizeValue { .. }) && response.value.is_empty() {
                return Err(malformed());
            }
            Ok(TomlReply::Value(response.value))
        }
        TomlRequest::ParsePath { .. } => {
            let response = decode_outcome::<PathReply>(bytes)?;
            let _ = response.ok;
            if response.value.is_empty() {
                return Err(malformed());
            }
            if let TomlRequest::ParsePath {
                input: PathInput::Segments(expected),
            } = request
                && response.path != *expected
            {
                return Err(malformed());
            }
            Ok(TomlReply::ParsedPath(ParsedPath {
                path: response.path,
                canonical_path: response.value,
            }))
        }
    }
}

#[cfg(test)]
mod tests;
