use schemars::{JsonSchema, Schema, SchemaGenerator};
use serde::{Deserialize, Deserializer, Serialize, Serializer, de};
use serde_json::json;
use std::borrow::Cow;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct InvalidPrimitive;
impl std::fmt::Display for InvalidPrimitive {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("invalid protocol primitive")
    }
}
impl std::error::Error for InvalidPrimitive {}

fn schema(value: serde_json::Value) -> Schema {
    value.try_into().expect("static schema object")
}
fn string_schema(pattern: &str, max: usize, format: Option<&str>) -> Schema {
    let mut value = json!({"type":"string","pattern":pattern,"maxLength":max,"not":{"pattern":"[\\r\\n\\u2028\\u2029]"}});
    if let Some(format) = format {
        value["format"] = json!(format);
    }
    schema(value)
}
fn lowercase_hex(value: &str) -> bool {
    value
        .bytes()
        .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}
fn uuid(value: &str) -> bool {
    let bytes = value.as_bytes();
    value.len() == 36
        && [8, 13, 18, 23].into_iter().all(|i| bytes[i] == b'-')
        && bytes.iter().enumerate().all(|(i, b)| {
            [8, 13, 18, 23].contains(&i) || b.is_ascii_digit() || (b'a'..=b'f').contains(b)
        })
        && (b'1'..=b'8').contains(&bytes[14])
        && matches!(bytes[19], b'8' | b'9' | b'a' | b'b')
}
fn opaque(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b"._:-".contains(&b))
        && value.as_bytes()[0].is_ascii_alphanumeric()
}
fn stable(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 64
        && value.as_bytes()[0].is_ascii_lowercase()
        && value
            .bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b"_-".contains(&b))
}
macro_rules! string_type {
    ($name:ident, $validate:expr, $pattern:expr, $max:expr $(, $format:expr)?) => {
        #[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize)]
        #[serde(transparent)]
        pub struct $name(String);
        impl $name {
            pub fn new(value: impl Into<String>) -> Result<Self, InvalidPrimitive> {
                let value = value.into();
                if ($validate)(&value) { Ok(Self(value)) } else { Err(InvalidPrimitive) }
            }
            pub fn as_str(&self) -> &str { &self.0 }
        }
        impl<'de> Deserialize<'de> for $name {
            fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
                Self::new(String::deserialize(d)?).map_err(de::Error::custom)
            }
        }
        impl JsonSchema for $name {
            fn schema_name() -> Cow<'static, str> { stringify!($name).into() }
            fn json_schema(_: &mut SchemaGenerator) -> Schema {
                string_schema($pattern,$max,None $(.or(Some($format)))?)
            }
        }
    };
}
const UUID_PATTERN: &str =
    "^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$";
string_type!(RequestId, uuid, UUID_PATTERN, 36);
string_type!(OperationId, uuid, UUID_PATTERN, 36);
string_type!(IdempotencyKey, uuid, UUID_PATTERN, 36);
string_type!(HostEpoch, uuid, UUID_PATTERN, 36);
string_type!(PlanId, uuid, UUID_PATTERN, 36);
string_type!(SessionId, uuid, UUID_PATTERN, 36);
string_type!(StreamId, uuid, UUID_PATTERN, 36);
string_type!(ObservationId, uuid, UUID_PATTERN, 36);
string_type!(DocumentId, uuid, UUID_PATTERN, 36);
string_type!(DraftId, uuid, UUID_PATTERN, 36);
string_type!(BackupId, uuid, UUID_PATTERN, 36);
string_type!(PreviewId, uuid, UUID_PATTERN, 36);
string_type!(RuntimeReleaseId, uuid, UUID_PATTERN, 36);
string_type!(GameUpdateId, uuid, UUID_PATTERN, 36);
string_type!(BridgeReleaseId, uuid, UUID_PATTERN, 36);
string_type!(SecretRefId, uuid, UUID_PATTERN, 36);
string_type!(PrivateValueId, uuid, UUID_PATTERN, 36);
string_type!(ExportDestinationId, uuid, UUID_PATTERN, 36);
string_type!(
    ProfileId,
    |s: &str| s.len() == 32 && lowercase_hex(s),
    "^[0-9a-f]{32}$",
    32
);
string_type!(
    InstallationId,
    |s: &str| s.len() == 32 && lowercase_hex(s),
    "^[0-9a-f]{32}$",
    32
);
string_type!(
    Sha256,
    |s: &str| s
        .strip_prefix("sha256:")
        .is_some_and(|v| v.len() == 64 && lowercase_hex(v)),
    "^sha256:[0-9a-f]{64}$",
    71
);
const OPAQUE_PATTERN: &str = "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$";
string_type!(OpaqueRevision, opaque, OPAQUE_PATTERN, 128);
string_type!(PhysicalInstallationId, opaque, OPAQUE_PATTERN, 128);
string_type!(NativeTargetRef, opaque, OPAQUE_PATTERN, 128);
string_type!(OwnerScope, opaque, OPAQUE_PATTERN, 128);
string_type!(ExecutableIdentity, opaque, OPAQUE_PATTERN, 128);
string_type!(ProcessGeneration, opaque, OPAQUE_PATTERN, 128);
string_type!(NativeTransactionRef, opaque, OPAQUE_PATTERN, 128);
string_type!(NativePreparationRef, opaque, OPAQUE_PATTERN, 128);
string_type!(NativeFileIdentity, opaque, OPAQUE_PATTERN, 128);
string_type!(ImportSourceId, opaque, OPAQUE_PATTERN, 128);
string_type!(RuntimeReceiptId, opaque, OPAQUE_PATTERN, 128);
string_type!(ArtifactAuthorityRef, opaque, OPAQUE_PATTERN, 128);
string_type!(BridgeAuthorityRef, opaque, OPAQUE_PATTERN, 128);
string_type!(SchemaVersion, opaque, OPAQUE_PATTERN, 128);
string_type!(ReleaseVersion, opaque, OPAQUE_PATTERN, 128);
string_type!(ApplicationIdentity, opaque, OPAQUE_PATTERN, 128);
const STABLE_PATTERN: &str = "^[a-z][a-z0-9_-]{0,63}$";
string_type!(ProviderId, stable, STABLE_PATTERN, 64);
fn dotted_stable(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value.as_bytes()[0].is_ascii_lowercase()
        && value
            .bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b"._-".contains(&b))
}
const DOTTED_STABLE_PATTERN: &str = "^[a-z][a-z0-9._-]{0,127}$";
string_type!(DistributionId, dotted_stable, DOTTED_STABLE_PATTERN, 128);
string_type!(SchemaId, dotted_stable, DOTTED_STABLE_PATTERN, 128);
string_type!(FieldId, dotted_stable, DOTTED_STABLE_PATTERN, 128);
string_type!(CategoryId, dotted_stable, DOTTED_STABLE_PATTERN, 128);
string_type!(CapabilityId, dotted_stable, DOTTED_STABLE_PATTERN, 128);
string_type!(DestinationId, dotted_stable, DOTTED_STABLE_PATTERN, 128);
string_type!(FeedId, dotted_stable, DOTTED_STABLE_PATTERN, 128);
string_type!(ChannelId, stable, STABLE_PATTERN, 64);
string_type!(EnumValue, opaque, OPAQUE_PATTERN, 128);
string_type!(KeyToken, opaque, OPAQUE_PATTERN, 128);
string_type!(PhaseId, stable, STABLE_PATTERN, 64);

// No lookaround: the same exact range is accepted by Rust regex and browser Ajv.
fn decimal_pattern() -> String {
    let max = "18446744073709551615";
    let mut alternatives = vec!["0".to_owned(), "[1-9][0-9]{0,18}".to_owned()];
    for (i, digit) in max.bytes().enumerate() {
        let minimum = if i == 0 { b'1' } else { b'0' };
        if digit > minimum {
            alternatives.push(format!(
                "{}[{}-{}][0-9]{{{}}}",
                &max[..i],
                minimum as char,
                (digit - 1) as char,
                max.len() - i - 1
            ));
        }
    }
    alternatives.push(max.to_owned());
    format!("^({})$", alternatives.join("|"))
}
macro_rules! decimal_type {
    ($name:ident) => {
        #[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
        pub struct $name(u64);
        impl $name { pub const fn new(value: u64) -> Self { Self(value) } pub const fn get(self) -> u64 { self.0 } }
        impl Serialize for $name { fn serialize<S: Serializer>(&self,s:S)->Result<S::Ok,S::Error> { s.serialize_str(&self.0.to_string()) } }
        impl<'de> Deserialize<'de> for $name {
            fn deserialize<D:Deserializer<'de>>(d:D)->Result<Self,D::Error> {
                let s=String::deserialize(d)?;
                if s.is_empty() || (s.len()>1 && s.starts_with('0')) || !s.bytes().all(|b| b.is_ascii_digit()) { return Err(de::Error::custom(InvalidPrimitive)); }
                s.parse::<u64>().map(Self).map_err(|_| de::Error::custom(InvalidPrimitive))
            }
        }
        impl JsonSchema for $name {
            fn schema_name()->Cow<'static,str>{stringify!($name).into()}
            fn json_schema(_: &mut SchemaGenerator)->Schema { schema(json!({"type":"string","pattern":decimal_pattern(),"maxLength":20,"not":{"pattern":"[\\r\\n\\u2028\\u2029]"}})) }
        }
    };
}
decimal_type!(Sequence);
decimal_type!(RevisionCounter);
decimal_type!(ProgressCount);

fn unsigned_bound_pattern(max: &str) -> String {
    let mut alternatives = vec!["0".to_owned()];
    if max.len() > 1 {
        alternatives.push(format!("[1-9][0-9]{{0,{}}}", max.len() - 2));
    }
    for (i, digit) in max.bytes().enumerate() {
        let minimum = if i == 0 { b'1' } else { b'0' };
        if digit > minimum {
            alternatives.push(format!(
                "{}[{}-{}][0-9]{{{}}}",
                &max[..i],
                minimum as char,
                (digit - 1) as char,
                max.len() - i - 1
            ));
        }
    }
    alternatives.push(max.into());
    format!("({})", alternatives.join("|"))
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct SignedInteger(i64);
impl SignedInteger {
    pub const fn new(value: i64) -> Self {
        Self(value)
    }
    pub const fn get(self) -> i64 {
        self.0
    }
}
impl Serialize for SignedInteger {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(&self.0.to_string())
    }
}
impl<'de> Deserialize<'de> for SignedInteger {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        let text = String::deserialize(d)?;
        let digits = text.strip_prefix('-').unwrap_or(&text);
        if digits.is_empty()
            || (digits.len() > 1 && digits.starts_with('0'))
            || !digits.bytes().all(|b| b.is_ascii_digit())
            || text == "-0"
        {
            return Err(de::Error::custom(InvalidPrimitive));
        }
        text.parse::<i64>()
            .map(Self)
            .map_err(|_| de::Error::custom(InvalidPrimitive))
    }
}
impl JsonSchema for SignedInteger {
    fn schema_name() -> Cow<'static, str> {
        "SignedInteger".into()
    }
    fn json_schema(_: &mut SchemaGenerator) -> Schema {
        let pattern = format!(
            "^({}|-{})$",
            unsigned_bound_pattern("9223372036854775807"),
            unsigned_bound_pattern("9223372036854775808")
        );
        let mut value = string_schema(&pattern, 20, None).to_value();
        value["not"] = json!({"anyOf":[{"const":"-0"},{"pattern":"[\\r\\n\\u2028\\u2029]"}]});
        schema(value)
    }
}
fn decimal_value(value: &str) -> bool {
    let unsigned = value.strip_prefix('-').unwrap_or(value);
    let mut parts = unsigned.split('.');
    let whole = parts.next().unwrap_or("");
    let fraction = parts.next();
    !whole.is_empty()
        && whole.len() <= 32
        && (whole.len() == 1 || !whole.starts_with('0'))
        && whole.bytes().all(|b| b.is_ascii_digit())
        && parts.next().is_none()
        && fraction.is_none_or(|f| {
            !f.is_empty()
                && f.len() <= 32
                && !f.ends_with('0')
                && f.bytes().all(|b| b.is_ascii_digit())
        })
        && value != "-0"
}
string_type!(
    DecimalValue,
    decimal_value,
    "^(0|[1-9][0-9]{0,31}|-[1-9][0-9]{0,31}|-?(0|[1-9][0-9]{0,31})\\.[0-9]{0,31}[1-9])$",
    66
);
impl DecimalValue {
    pub fn compare(&self, other: &Self) -> std::cmp::Ordering {
        fn magnitude(value: &str) -> (String, String) {
            let value = value.strip_prefix('-').unwrap_or(value);
            let (whole, fraction) = value.split_once('.').unwrap_or((value, ""));
            (whole.to_owned(), format!("{fraction:0<32}"))
        }
        let negative = self.0.starts_with('-');
        let other_negative = other.0.starts_with('-');
        if negative != other_negative {
            return if negative {
                std::cmp::Ordering::Less
            } else {
                std::cmp::Ordering::Greater
            };
        }
        let (whole, fraction) = magnitude(&self.0);
        let (other_whole, other_fraction) = magnitude(&other.0);
        let order = whole
            .len()
            .cmp(&other_whole.len())
            .then_with(|| whole.cmp(&other_whole))
            .then_with(|| fraction.cmp(&other_fraction));
        if negative { order.reverse() } else { order }
    }
}

macro_rules! unicode_type {
    ($name:ident,$max:expr,$pattern:expr,$forbidden:expr,$validator:expr)=>{
        #[derive(Clone,Debug,PartialEq,Eq,Serialize)]
        #[serde(transparent)]
        pub struct $name(String);
        impl $name {pub fn new(value:impl Into<String>)->Result<Self,InvalidPrimitive>{let value=value.into();if value.chars().count()<=$max && ($validator)(&value){Ok(Self(value))}else{Err(InvalidPrimitive)}} pub fn as_str(&self)->&str{&self.0}}
        impl<'de> Deserialize<'de> for $name {fn deserialize<D:Deserializer<'de>>(d:D)->Result<Self,D::Error>{Self::new(String::deserialize(d)?).map_err(de::Error::custom)}}
        impl JsonSchema for $name {fn schema_name()->Cow<'static,str>{stringify!($name).into()} fn json_schema(_: &mut SchemaGenerator)->Schema{schema(json!({"type":"string","maxLength":$max,"pattern":$pattern,"not":{"pattern":$forbidden}}))}}
    };
}
unicode_type!(
    DisplayName,
    128,
    "^[^\\x00-\\x1f\\x7f\\u2028\\u2029]+$",
    "[\\x00-\\x1f\\x7f\\u2028\\u2029]",
    |s: &str| !s.is_empty()
        && !s
            .chars()
            .any(|c| c < ' ' || matches!(c, '\u{7f}' | '\u{2028}' | '\u{2029}'))
);
unicode_type!(
    SchemaText,
    512,
    "^[^\\x00-\\x1f\\x7f\\u2028\\u2029]*$",
    "[\\x00-\\x1f\\x7f\\u2028\\u2029]",
    |s: &str| !s
        .chars()
        .any(|c| c < ' ' || matches!(c, '\u{7f}' | '\u{2028}' | '\u{2029}'))
);
unicode_type!(
    ConfigString,
    4096,
    "^[^\\x00-\\x08\\x0b\\x0c\\x0e-\\x1f\\x7f]*$",
    "[\\x00-\\x08\\x0b\\x0c\\x0e-\\x1f\\x7f]",
    |s: &str| !s
        .chars()
        .any(|c| (c < ' ' && !matches!(c, '\t' | '\n' | '\r')) || c == '\u{7f}')
);
unicode_type!(
    TomlPathSegment,
    256,
    "^[^\\x00-\\x1f\\x7f\\u2028\\u2029]+$",
    "[\\x00-\\x1f\\x7f\\u2028\\u2029]",
    |s: &str| !s.is_empty()
        && !s
            .chars()
            .any(|c| c < ' ' || matches!(c, '\u{7f}' | '\u{2028}' | '\u{2029}'))
);

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(transparent)]
pub struct Pid(u32);
impl Pid {
    pub fn new(value: u32) -> Result<Self, InvalidPrimitive> {
        if value > 0 {
            Ok(Self(value))
        } else {
            Err(InvalidPrimitive)
        }
    }
    pub const fn get(self) -> u32 {
        self.0
    }
}
impl<'de> Deserialize<'de> for Pid {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        Self::new(u32::deserialize(d)?).map_err(de::Error::custom)
    }
}
impl JsonSchema for Pid {
    fn schema_name() -> Cow<'static, str> {
        "Pid".into()
    }
    fn json_schema(_: &mut SchemaGenerator) -> Schema {
        schema(json!({"type":"integer","minimum":1,"maximum":u32::MAX}))
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ProtocolVersion;
impl Serialize for ProtocolVersion {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_u8(1)
    }
}
impl<'de> Deserialize<'de> for ProtocolVersion {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        if u8::deserialize(d)? == 1 {
            Ok(Self)
        } else {
            Err(de::Error::custom(InvalidPrimitive))
        }
    }
}
impl JsonSchema for ProtocolVersion {
    fn schema_name() -> Cow<'static, str> {
        "ProtocolVersion".into()
    }
    fn json_schema(_: &mut SchemaGenerator) -> Schema {
        schema(json!({"type":"integer","const":1}))
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct FalseFlag;
impl Serialize for FalseFlag {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_bool(false)
    }
}
impl<'de> Deserialize<'de> for FalseFlag {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        if !bool::deserialize(d)? {
            Ok(Self)
        } else {
            Err(de::Error::custom(InvalidPrimitive))
        }
    }
}
impl JsonSchema for FalseFlag {
    fn schema_name() -> Cow<'static, str> {
        "FalseFlag".into()
    }
    fn json_schema(_: &mut SchemaGenerator) -> Schema {
        schema(json!({"type":"boolean","const":false}))
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(transparent)]
pub struct BoundedList<T, const N: usize>(Vec<T>);
impl<T, const N: usize> BoundedList<T, N> {
    pub fn new(values: Vec<T>) -> Result<Self, InvalidPrimitive> {
        if values.len() <= N {
            Ok(Self(values))
        } else {
            Err(InvalidPrimitive)
        }
    }
    pub fn as_slice(&self) -> &[T] {
        &self.0
    }
}
impl<'de, T: Deserialize<'de>, const N: usize> Deserialize<'de> for BoundedList<T, N> {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        Self::new(Vec::deserialize(d)?).map_err(de::Error::custom)
    }
}
impl<T: JsonSchema, const N: usize> JsonSchema for BoundedList<T, N> {
    fn schema_name() -> Cow<'static, str> {
        format!("BoundedList_{}_{}", T::schema_name(), N).into()
    }
    fn json_schema(g: &mut SchemaGenerator) -> Schema {
        schema(json!({"type":"array","items":g.subschema_for::<T>(),"maxItems":N}))
    }
}

fn timestamp(value: &str) -> bool {
    let bytes = value.as_bytes();
    if bytes.len() < 20
        || !value.is_ascii()
        || bytes[4] != b'-'
        || bytes[7] != b'-'
        || bytes[10] != b'T'
        || bytes[13] != b':'
        || bytes[16] != b':'
        || *bytes.last().unwrap() != b'Z'
    {
        return false;
    }
    if ![0..4, 5..7, 8..10, 11..13, 14..16, 17..19]
        .into_iter()
        .all(|r| bytes[r].iter().all(u8::is_ascii_digit))
    {
        return false;
    }
    if bytes.len() > 20
        && (bytes[19] != b'.'
            || bytes.len() > 30
            || bytes.len() < 22
            || !bytes[20..bytes.len() - 1].iter().all(u8::is_ascii_digit))
    {
        return false;
    }
    let parse = |range: std::ops::Range<usize>| value[range].parse::<u32>().unwrap();
    let year = parse(0..4);
    let month = parse(5..7);
    let day = parse(8..10);
    let leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
    let days = match month {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        2 => {
            if leap {
                29
            } else {
                28
            }
        }
        _ => 0,
    };
    day > 0 && day <= days && parse(11..13) < 24 && parse(14..16) < 60 && parse(17..19) < 60
}
string_type!(
    UtcTimestamp,
    timestamp,
    "^[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\\.[0-9]{1,9})?Z$",
    30,
    "date-time"
);

fn mac_path(s: &str) -> bool {
    s.starts_with('/')
        && s.chars().count() <= 4096
        && !s
            .chars()
            .any(|c| c < ' ' || matches!(c, '\u{7f}' | '\u{2028}' | '\u{2029}'))
}
fn windows_path(s: &str) -> bool {
    if s.chars().count() > 4096
        || s.chars().any(|c| {
            c < ' '
                || matches!(c, '\u{7f}' | '\u{2028}' | '\u{2029}')
                || "/:*?\"<>|".contains(c) && c != ':'
        })
    {
        return false;
    }
    let b = s.as_bytes();
    if b.len() >= 3 && b[0].is_ascii_alphabetic() && b[1] == b':' && b[2] == b'\\' {
        return !s[2..].contains(':');
    }
    if let Some(rest) = s.strip_prefix("\\\\") {
        let mut parts = rest.split('\\');
        return !parts.next().unwrap_or("").is_empty()
            && !parts.next().unwrap_or("").is_empty()
            && !rest.contains(':');
    }
    false
}
string_type!(MacAbsolutePath, mac_path, "^/[^\\x00-\\x1f\\x7f]*$", 4096);
string_type!(
    WindowsAbsolutePath,
    windows_path,
    "^([A-Za-z]:\\\\[^/\\x00-\\x1f\\x7f:*?\"<>|]*|\\\\\\\\[^\\\\/\\x00-\\x1f\\x7f:*?\"<>|]+\\\\[^\\\\/\\x00-\\x1f\\x7f:*?\"<>|]+(\\\\[^/\\x00-\\x1f\\x7f:*?\"<>|]*)?)$",
    4096
);
