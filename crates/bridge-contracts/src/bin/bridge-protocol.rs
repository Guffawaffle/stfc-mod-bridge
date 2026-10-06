//! Development schema/fixture utility. This is not the application dispatcher
//! and is not a production package entry point.
use bridge_contracts::v1::{self, Event, Reply, Request};
use schemars::{JsonSchema, generate::SchemaSettings};
use serde_json::{Value, json};
use std::io::{self, Read};

#[derive(JsonSchema)]
#[allow(dead_code)]
struct ProtocolContract {
    request: Request,
    reply: Reply,
    event: Event,
}

fn main() {
    let arguments: Vec<String> = std::env::args().skip(1).collect();
    let result = match arguments.as_slice() {
        [mode] if mode == "schema" => Ok(json!({
            "schemaVersion": "bridge-protocol-schema/v1", "protocolVersion": 1,
            "draft": "draft-07", "schemas": {
                "request": v1::schema_request(), "reply": v1::schema_reply(),
                "event": v1::schema_event(),
                "aggregate": SchemaSettings::draft07().for_deserialize().into_generator().into_root_schema_for::<ProtocolContract>()
            }
        })),
        [mode, kind]
            if mode == "decode" && ["request", "reply", "event"].contains(&kind.as_str()) =>
        {
            decode(kind)
        }
        _ => Err("invalid_arguments"),
    };
    match result {
        Ok(value) => println!("{value}"),
        Err(code) => {
            println!("{}", json!({"accepted": false, "utilityError": code}));
            std::process::exit(2);
        }
    }
}

fn decode(kind: &str) -> Result<Value, &'static str> {
    let mut bytes = Vec::new();
    io::stdin()
        .take(v1::MAX_MESSAGE_BYTES as u64 + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| "input_unavailable")?;
    Ok(match kind {
        "request" => match v1::decode_request(&bytes) {
            Ok(value) => json!({"accepted": true, "normalized": value.as_inner()}),
            Err(error) => json!({"accepted": false, "error": error}),
        },
        "reply" => match v1::decode_reply(&bytes) {
            Ok(value) => json!({"accepted": true, "normalized": value.as_inner()}),
            Err(error) => json!({"accepted": false, "error": error}),
        },
        "event" => match v1::decode_event(&bytes) {
            Ok(value) => json!({"accepted": true, "normalized": value.as_inner()}),
            Err(error) => json!({"accepted": false, "error": error}),
        },
        _ => return Err("invalid_arguments"),
    })
}
