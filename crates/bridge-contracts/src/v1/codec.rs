use super::{primitives::*, projections::DiagnosticContent, wire::*, workflow::*};
use schemars::{JsonSchema, Schema, generate::SchemaSettings};
use serde::{
    Deserialize, Serialize,
    de::{self, DeserializeSeed, MapAccess, SeqAccess, Visitor},
};
use serde_json::{Map, Value};
use sha2::{Digest, Sha256 as Sha256Hasher};
use std::{collections::HashSet, fmt, sync::OnceLock};

pub const MAX_MESSAGE_BYTES: usize = 256 * 1024;
pub const MAX_CONTAINER_DEPTH: usize = 32;
const MAX_SAFE_INTEGER: u64 = 9_007_199_254_740_991;

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DecodeFailure {
    pub request_id: Option<RequestId>,
    pub error: Box<BridgeError>,
}
impl fmt::Display for DecodeFailure {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("protocol message rejected")
    }
}
impl std::error::Error for DecodeFailure {}
fn failure(
    request_id: Option<RequestId>,
    code: ErrorCode,
    field: FieldPath,
    violation: ViolationCode,
) -> DecodeFailure {
    DecodeFailure {
        request_id,
        error: Box::new(BridgeError {
            code,
            retry_disposition: RetryDisposition::Never,
            supported_versions: if code == ErrorCode::UnsupportedProtocol {
                Some([ProtocolVersion])
            } else {
                None
            },
            violations: BoundedList::new(vec![FieldViolation {
                field,
                code: violation,
            }])
            .expect("one violation"),
            expected_revision: None,
            observed_revision: None,
            recovery: None,
        }),
    }
}

macro_rules! validated {
    ($name:ident,$dto:ident) => {
        #[derive(Clone, Debug, PartialEq, Eq)]
        pub struct $name($dto);
        impl $name {
            pub fn as_inner(&self) -> &$dto {
                &self.0
            }
            pub fn into_inner(self) -> $dto {
                self.0
            }
        }
    };
}
validated!(ValidatedRequest, Request);
validated!(ValidatedReply, Reply);
validated!(ValidatedEvent, Event);

pub fn schema_request() -> Schema {
    derived_schema::<Request>()
}
pub fn schema_reply() -> Schema {
    derived_schema::<Reply>()
}
pub fn schema_event() -> Schema {
    derived_schema::<Event>()
}
fn derived_schema<T: JsonSchema>() -> Schema {
    SchemaSettings::draft07()
        .for_deserialize()
        .into_generator()
        .into_root_schema_for::<T>()
}

type ValidatorResult = Result<jsonschema::Validator, ()>;
static REQUEST_VALIDATOR: OnceLock<ValidatorResult> = OnceLock::new();
static REPLY_VALIDATOR: OnceLock<ValidatorResult> = OnceLock::new();
static EVENT_VALIDATOR: OnceLock<ValidatorResult> = OnceLock::new();
fn validator(schema: Schema) -> ValidatorResult {
    jsonschema::options()
        .with_draft(jsonschema::Draft::Draft7)
        .offline()
        .with_pattern_options(jsonschema::PatternOptions::regex())
        .should_validate_formats(true)
        .should_ignore_unknown_formats(false)
        .build(schema.as_value())
        .map_err(|_| ())
}

// This visitor detects decoded key aliases before insertion. Value is only an
// internal framing/schema representation, never an extensible wire payload.
struct StrictSeed {
    containers: usize,
}
impl<'de> DeserializeSeed<'de> for StrictSeed {
    type Value = Value;
    fn deserialize<D: serde::Deserializer<'de>>(self, d: D) -> Result<Value, D::Error> {
        d.deserialize_any(self)
    }
}
impl<'de> Visitor<'de> for StrictSeed {
    type Value = Value;
    fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("bounded strict JSON")
    }
    fn visit_bool<E: de::Error>(self, v: bool) -> Result<Value, E> {
        Ok(Value::Bool(v))
    }
    fn visit_unit<E: de::Error>(self) -> Result<Value, E> {
        Ok(Value::Null)
    }
    fn visit_str<E: de::Error>(self, v: &str) -> Result<Value, E> {
        Ok(Value::String(v.into()))
    }
    fn visit_string<E: de::Error>(self, v: String) -> Result<Value, E> {
        Ok(Value::String(v))
    }
    fn visit_i64<E: de::Error>(self, v: i64) -> Result<Value, E> {
        if v.unsigned_abs() <= MAX_SAFE_INTEGER {
            Ok(Value::Number(v.into()))
        } else {
            Err(E::custom("invalid JSON number"))
        }
    }
    fn visit_u64<E: de::Error>(self, v: u64) -> Result<Value, E> {
        if v <= MAX_SAFE_INTEGER {
            Ok(Value::Number(v.into()))
        } else {
            Err(E::custom("invalid JSON number"))
        }
    }
    fn visit_f64<E: de::Error>(self, _: f64) -> Result<Value, E> {
        Err(E::custom("invalid JSON number"))
    }
    fn visit_seq<A: SeqAccess<'de>>(self, mut sequence: A) -> Result<Value, A::Error> {
        if self.containers >= MAX_CONTAINER_DEPTH {
            return Err(de::Error::custom("JSON depth exceeded"));
        }
        let mut values = Vec::new();
        while let Some(value) = sequence.next_element_seed(StrictSeed {
            containers: self.containers + 1,
        })? {
            values.push(value);
        }
        Ok(Value::Array(values))
    }
    fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<Value, A::Error> {
        if self.containers >= MAX_CONTAINER_DEPTH {
            return Err(de::Error::custom("JSON depth exceeded"));
        }
        let mut keys = HashSet::new();
        let mut values = Map::new();
        while let Some(key) = map.next_key::<String>()? {
            if !keys.insert(key.clone()) {
                return Err(de::Error::custom("duplicate JSON key"));
            }
            let value = map.next_value_seed(StrictSeed {
                containers: self.containers + 1,
            })?;
            values.insert(key, value);
        }
        Ok(Value::Object(values))
    }
}
fn framed(input: &[u8]) -> Result<Value, DecodeFailure> {
    if input.len() > MAX_MESSAGE_BYTES || std::str::from_utf8(input).is_err() {
        return Err(failure(
            None,
            ErrorCode::InvalidRequest,
            FieldPath::Envelope,
            ViolationCode::InvalidFraming,
        ));
    }
    let mut d = serde_json::Deserializer::from_slice(input);
    let value = StrictSeed { containers: 0 }
        .deserialize(&mut d)
        .map_err(|_| {
            failure(
                None,
                ErrorCode::InvalidRequest,
                FieldPath::Envelope,
                ViolationCode::InvalidFraming,
            )
        })?;
    d.end().map_err(|_| {
        failure(
            None,
            ErrorCode::InvalidRequest,
            FieldPath::Envelope,
            ViolationCode::InvalidFraming,
        )
    })?;
    Ok(value)
}
fn decode<T: for<'de> Deserialize<'de>>(
    input: &[u8],
    slot: &OnceLock<ValidatorResult>,
    schema: fn() -> Schema,
    valid: fn(&T) -> bool,
) -> Result<T, DecodeFailure> {
    let value = framed(input)?;
    let request_id = value
        .get("requestId")
        .and_then(Value::as_str)
        .and_then(|s| RequestId::new(s).ok());
    let object = value.as_object().ok_or_else(|| {
        failure(
            request_id.clone(),
            ErrorCode::InvalidRequest,
            FieldPath::Envelope,
            ViolationCode::InvalidShape,
        )
    })?;
    match object.get("protocolVersion") {
        Some(version) if version.as_u64() == Some(1) => {}
        Some(Value::Number(_)) => {
            return Err(failure(
                request_id,
                ErrorCode::UnsupportedProtocol,
                FieldPath::ProtocolVersion,
                ViolationCode::InvalidValue,
            ));
        }
        _ => {
            return Err(failure(
                request_id,
                ErrorCode::InvalidRequest,
                FieldPath::ProtocolVersion,
                ViolationCode::InvalidValue,
            ));
        }
    }
    let validator = slot
        .get_or_init(|| validator(schema()))
        .as_ref()
        .map_err(|_| {
            failure(
                request_id.clone(),
                ErrorCode::InternalFailure,
                FieldPath::Envelope,
                ViolationCode::InvalidShape,
            )
        })?;
    if !validator.is_valid(&value) {
        return Err(failure(
            request_id,
            ErrorCode::InvalidRequest,
            FieldPath::Body,
            ViolationCode::InvalidShape,
        ));
    }
    let dto: T = serde_json::from_value(value).map_err(|_| {
        failure(
            request_id.clone(),
            ErrorCode::InvalidRequest,
            FieldPath::Body,
            ViolationCode::InvalidValue,
        )
    })?;
    if !valid(&dto) {
        return Err(failure(
            request_id,
            ErrorCode::InvalidRequest,
            FieldPath::Body,
            ViolationCode::ConflictingBinding,
        ));
    }
    Ok(dto)
}
pub fn decode_request(input: &[u8]) -> Result<ValidatedRequest, DecodeFailure> {
    decode(input, &REQUEST_VALIDATOR, schema_request, Request::valid).map(ValidatedRequest)
}
pub fn decode_reply(input: &[u8]) -> Result<ValidatedReply, DecodeFailure> {
    decode(input, &REPLY_VALIDATOR, schema_reply, Reply::valid).map(ValidatedReply)
}
pub fn decode_event(input: &[u8]) -> Result<ValidatedEvent, DecodeFailure> {
    decode(input, &EVENT_VALIDATOR, schema_event, Event::valid).map(ValidatedEvent)
}

fn canonical(value: &Value, output: &mut Vec<u8>) -> Result<(), ()> {
    match value {
        Value::Object(map) => {
            output.push(b'{');
            let mut keys: Vec<_> = map.keys().collect();
            keys.sort();
            for (i, key) in keys.into_iter().enumerate() {
                if i > 0 {
                    output.push(b',');
                }
                serde_json::to_writer(&mut *output, key).map_err(|_| ())?;
                output.push(b':');
                canonical(&map[key], output)?;
            }
            output.push(b'}');
        }
        Value::Array(values) => {
            output.push(b'[');
            for (i, value) in values.iter().enumerate() {
                if i > 0 {
                    output.push(b',');
                }
                canonical(value, output)?;
            }
            output.push(b']');
        }
        _ => serde_json::to_writer(output, value).map_err(|_| ())?,
    }
    Ok(())
}
/// Hash validated normalized plan semantics. Only `effects` is an unordered set;
/// all other arrays retain their semantic order. IDs/expiry outside this DTO do
/// not enter the digest. Backup creation time is descriptive metadata and is
/// excluded at its exact typed location; backup identity and retained bytes
/// remain bound. This digest is review identity, not execution authority.
pub fn semantic_plan_digest(semantics: &PlanSemantics) -> Result<Sha256, DecodeFailure> {
    let invalid = || {
        failure(
            None,
            ErrorCode::InvalidRequest,
            FieldPath::Plan,
            ViolationCode::ConflictingBinding,
        )
    };
    if !semantics.valid() {
        return Err(invalid());
    }
    let mut normalized = semantics.clone();
    let mut effects = normalized.effects.as_slice().to_vec();
    effects.sort_by_cached_key(|effect| serde_json::to_string(effect).expect("closed effect enum"));
    normalized.effects = BoundedList::new(effects).map_err(|_| invalid())?;
    let mut value = serde_json::to_value(normalized).map_err(|_| invalid())?;
    if matches!(
        semantics.capture,
        PreparedCapture::RestoreConfiguration { .. }
    ) {
        value["capture"]["input"]["backup"]
            .as_object_mut()
            .ok_or_else(invalid)?
            .remove("createdAt");
    }
    let mut bytes = b"bridge-plan-semantic-json-v1\0".to_vec();
    canonical(&value, &mut bytes).map_err(|_| invalid())?;
    let hash = Sha256Hasher::digest(bytes);
    let hex: String = hash.iter().map(|b| format!("{b:02x}")).collect();
    Sha256::new(format!("sha256:{hex}")).map_err(|_| invalid())
}

pub fn diagnostic_preview_digest(content: &DiagnosticContent) -> Result<Sha256, DecodeFailure> {
    let invalid = || {
        failure(
            None,
            ErrorCode::InvalidRequest,
            FieldPath::Body,
            ViolationCode::ConflictingBinding,
        )
    };
    if !content.valid() {
        return Err(invalid());
    }
    let value = serde_json::to_value(content).map_err(|_| invalid())?;
    let mut bytes = b"bridge-diagnostic-preview-json-v1\0".to_vec();
    canonical(&value, &mut bytes).map_err(|_| invalid())?;
    let hash = Sha256Hasher::digest(bytes);
    let hex: String = hash.iter().map(|b| format!("{b:02x}")).collect();
    Sha256::new(format!("sha256:{hex}")).map_err(|_| invalid())
}
