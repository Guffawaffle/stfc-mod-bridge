use bridge_contracts::v1::*;
use schemars::{JsonSchema, generate::SchemaSettings};
use serde_json::{Value, json};

const REQUEST_ID: &str = "11111111-1111-4111-8111-111111111111";
const HOST_ID: &str = "22222222-2222-4222-8222-222222222222";
const OPERATION_ID: &str = "33333333-3333-4333-8333-333333333333";
const PLAN_ID: &str = "44444444-4444-4444-8444-444444444444";
const SESSION_ID: &str = "55555555-5555-4555-8555-555555555555";
fn bytes(value: &Value) -> Vec<u8> {
    serde_json::to_vec(value).unwrap()
}
fn installation() -> Value {
    json!({"kind":"registered","registrationId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","registrationRevision":"registration-1","physicalId":"physical-1","nativeTargetRef":"native-1"})
}
fn target(isolated: bool) -> Value {
    json!({"installation":installation(),"profile":if isolated{json!({"kind":"isolated","id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","revision":"profile-1"})}else{json!({"kind":"ordinary","ownerScope":"synthetic-owner"})}})
}
fn session() -> Value {
    json!({"sessionId":SESSION_ID,"revision":"session-1","process":{"pid":1234,"startIdentity":{"platform":"windows","value":"filetime-133000000000000001"},"executableIdentity":"executable-1","installationPhysicalId":"physical-1","architecture":"x86_64"}})
}
fn selector(isolated: bool) -> Value {
    json!({"installation":{"kind":"registered","id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"profile":if isolated{json!({"kind":"isolated","id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"})}else{json!({"kind":"ordinary"})}})
}
fn query(name: &str, input: Value) -> Value {
    json!({"protocolVersion":1,"requestId":REQUEST_ID,"body":{"type":"query","query":{"name":name,"input":input}}})
}
fn command(name: &str, input: Value) -> Value {
    json!({"protocolVersion":1,"requestId":REQUEST_ID,"body":{"type":"command","command":{"name":name,"input":input}}})
}
fn semantics(isolated: bool) -> Value {
    json!({"hashProfile":"bridge-plan-semantic-json-v1","action":if isolated{"launch_isolated"}else{"launch_ordinary"},"capture":if isolated{json!({"kind":"launch_isolated","target":target(true),"catalogRevision":"catalog-1","runtime":{"kind":"absent"},"storeMode":"new"})}else{json!({"kind":"launch_ordinary","target":target(false),"catalogRevision":"catalog-1","runtime":{"kind":"absent"}})},"trustDomain":"session","effects":if isolated{json!(["launch_session","create_isolated_store"])}else{json!(["launch_session"])}})
}
fn plan() -> Value {
    let semantics = semantics(false);
    let typed: PlanSemantics = serde_json::from_value(semantics.clone()).unwrap();
    json!({"planRef":{"planId":PLAN_ID,"hostEpoch":HOST_ID,"reviewDigest":semantic_plan_digest(&typed).unwrap()},"semantics":semantics,"expiresAt":"2026-10-03T18:00:00Z","grantsLock":false,"grantsPermission":false})
}
fn operation() -> Value {
    json!({"operationId":OPERATION_ID,"operationRevision":"1","semantics":semantics(false),"state":{"status":"completed","outcome":{"kind":"changed","reason":"applied"}}})
}
fn result(name: &str, output: Value) -> Value {
    json!({"protocolVersion":1,"requestId":REQUEST_ID,"body":{"type":"result","result":{"type":"command","command":{"name":name,"output":output}}}})
}
fn event(body: Value) -> Value {
    json!({"protocolVersion":1,"cursor":{"hostEpoch":HOST_ID,"streamId":"66666666-6666-4666-8666-666666666666","sequence":"1"},"body":body})
}
fn schema_valid<T: JsonSchema>(value: Value) -> bool {
    let schema = SchemaSettings::draft07()
        .for_deserialize()
        .into_generator()
        .into_root_schema_for::<T>();
    jsonschema::options()
        .with_draft(jsonschema::Draft::Draft7)
        .offline()
        .with_pattern_options(jsonschema::PatternOptions::regex())
        .should_validate_formats(true)
        .should_ignore_unknown_formats(false)
        .build(schema.as_value())
        .unwrap()
        .is_valid(&value)
}

#[test]
fn explicit_queries_and_typed_intents_round_trip() {
    let requests = vec![
        query("hello", json!({})),
        query("resolve_target", json!({"target":selector(false)})),
        query("get_operation", json!({"operationId":OPERATION_ID})),
        command(
            "prepare",
            json!({"intent":{"kind":"launch_ordinary","input":{"target":selector(false)}}}),
        ),
        command(
            "prepare",
            json!({"intent":{"kind":"launch_isolated","input":{"target":selector(true),"storeMode":"new"}}}),
        ),
        command(
            "prepare",
            json!({"intent":{"kind":"focus_session","input":{"session":session()}}}),
        ),
        command(
            "commit",
            json!({"planRef":plan()["planRef"],"idempotencyKey":"77777777-7777-4777-8777-777777777777"}),
        ),
        command(
            "cancel_operation",
            json!({"operationId":OPERATION_ID,"expectedOperationRevision":"18446744073709551615"}),
        ),
        command(
            "request_host_close",
            json!({"expectedCursor":event(json!({"type":"snapshot_invalidated","reason":"session_changed"}))["cursor"]}),
        ),
    ];
    for request in requests {
        let decoded = decode_request(&bytes(&request)).unwrap();
        assert_eq!(serde_json::to_value(decoded.as_inner()).unwrap(), request);
    }
}
#[test]
fn schema_enforces_objects_through_tagged_content() {
    let cases = vec![
        json!([1,REQUEST_ID,{"type":"query","query":{"name":"hello","input":{}}}]),
        query("hello", json!([])),
        json!({"protocolVersion":1,"requestId":REQUEST_ID,"body":["query",{"name":"hello","input":{}}]}),
        command(
            "prepare",
            json!({"intent":{"kind":"launch_ordinary","input":{"target":[selector(false)["installation"],selector(false)["profile"]]}}}),
        ),
        command(
            "prepare",
            json!({"intent":{"kind":"focus_session","input":{"session":[SESSION_ID,"session-1",session()["process"]]}}}),
        ),
    ];
    for value in cases {
        assert!(decode_request(&bytes(&value)).is_err());
    }
    let mut reply = result("prepare", plan());
    reply["body"]["result"]["command"]["output"]["planRef"] =
        json!([PLAN_ID, HOST_ID, plan()["planRef"]["reviewDigest"]]);
    assert!(decode_reply(&bytes(&reply)).is_err());
    let mut value = event(json!({"type":"operation_changed","operation":operation()}));
    value["cursor"] = json!([HOST_ID, "66666666-6666-4666-8666-666666666666", "1"]);
    assert!(decode_event(&bytes(&value)).is_err());
}
#[test]
fn strict_unknown_fields_tags_and_ordinary_isolated_boundary() {
    let mut value = query("hello", json!({}));
    value["unexpected"] = json!(true);
    assert!(decode_request(&bytes(&value)).is_err());
    assert!(decode_request(&bytes(&query("future_query", json!({})))).is_err());
    for extra in [
        json!({"target":selector(false),"storeMode":"new"}),
        json!({"target":selector(true)}),
        json!({"target":selector(false),"session":session()}),
    ] {
        assert!(
            decode_request(&bytes(&command(
                "prepare",
                json!({"intent":{"kind":"launch_ordinary","input":extra}})
            )))
            .is_err()
        );
    }
    let mut value = selector(false);
    value["profile"]["id"] = json!("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    assert!(decode_request(&bytes(&query("resolve_target", json!({"target":value})))).is_err());
}
#[test]
fn decoded_duplicate_keys_and_trailing_input_are_rejected() {
    for raw in [
        format!(
            r#"{{"protocolVersion":1,"protocol\u0056ersion":1,"requestId":"{REQUEST_ID}","body":{{}}}}"#
        ),
        format!(
            r#"{{"protocolVersion":1,"requestId":"{REQUEST_ID}","body":{{"type":"query","query":{{"name":"hello","input":{{}},"in\u0070ut":{{}}}}}}}}"#
        ),
    ] {
        assert!(decode_request(raw.as_bytes()).is_err());
    }
    let mut raw = bytes(&query("hello", json!({})));
    raw.extend_from_slice(b" {}");
    assert!(decode_request(&raw).is_err());
    let mut raw = bytes(&query("hello", json!({})));
    raw.extend_from_slice(b" trailing");
    assert!(decode_request(&raw).is_err());
}
#[test]
fn unsupported_version_routes_before_unfamiliar_body_and_echoes_only_valid_id() {
    let value =
        json!({"protocolVersion":2,"requestId":REQUEST_ID,"brandNewBody":{"unfamiliar":true}});
    let failure = decode_request(&bytes(&value)).unwrap_err();
    assert_eq!(failure.error.code, ErrorCode::UnsupportedProtocol);
    assert_eq!(failure.request_id.unwrap().as_str(), REQUEST_ID);
    assert!(failure.error.supported_versions.is_some());
    let value = json!({"protocolVersion":99,"requestId":"SYNTHETIC_SECRET_SENTINEL","body":null});
    let failure = decode_request(&bytes(&value)).unwrap_err();
    assert!(failure.request_id.is_none());
    assert!(
        !serde_json::to_string(&failure)
            .unwrap()
            .contains("SYNTHETIC_SECRET")
    );
}
#[test]
fn framing_enforces_utf8_surrogates_and_lexical_safe_integers() {
    assert!(decode_request(&[0xff, 0xfe]).is_err());
    for token in [
        "1.0",
        "1e0",
        "1e-9999",
        "3.00000000000000001",
        "9007199254740992",
        "-9007199254740992",
        "-0",
    ] {
        let raw = format!(
            r#"{{"protocolVersion":{token},"requestId":"{REQUEST_ID}","body":{{"type":"query","query":{{"name":"hello","input":{{}}}}}}}}"#
        );
        assert_eq!(
            decode_request(raw.as_bytes())
                .unwrap_err()
                .error
                .violations
                .as_slice()[0]
                .code,
            ViolationCode::InvalidFraming,
            "{token}"
        );
    }
    let raw = format!(r#"{{"protocolVersion":2,"requestId":"{REQUEST_ID}","body":"\ud800"}}"#);
    assert!(decode_request(raw.as_bytes()).is_err());
    let raw = format!(r#"{{"protocolVersion":2,"requestId":"{REQUEST_ID}","body":"\udc00"}}"#);
    assert!(decode_request(raw.as_bytes()).is_err());
}
#[test]
fn exact_message_size_and_container_depth_boundaries() {
    let prefix = r#"{"protocolVersion":2,"body":""#;
    let suffix = r#""}"#;
    let raw = format!(
        "{prefix}{}{suffix}",
        "x".repeat(MAX_MESSAGE_BYTES - prefix.len() - suffix.len())
    );
    assert_eq!(raw.len(), MAX_MESSAGE_BYTES);
    assert_eq!(
        decode_request(raw.as_bytes()).unwrap_err().error.code,
        ErrorCode::UnsupportedProtocol
    );
    let raw = format!(
        "{prefix}{}{suffix}",
        "x".repeat(MAX_MESSAGE_BYTES - prefix.len() - suffix.len() + 1)
    );
    assert_eq!(
        decode_request(raw.as_bytes()).unwrap_err().error.code,
        ErrorCode::InvalidRequest
    );
    for depth in [32, 33] {
        let raw = format!(
            "{{\"protocolVersion\":2,\"body\":{}0{}}}",
            "[".repeat(depth - 1),
            "]".repeat(depth - 1)
        );
        let failure = decode_request(raw.as_bytes()).unwrap_err();
        assert_eq!(
            failure.error.code,
            if depth == 32 {
                ErrorCode::UnsupportedProtocol
            } else {
                ErrorCode::InvalidRequest
            }
        );
    }
}
#[test]
fn distinct_identity_validation_and_schema_agree() {
    for value in [
        "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA".into(),
        REQUEST_ID.replace('-', ""),
        "00000000-0000-0000-0000-000000000000".into(),
        "11111111-1111-0111-8111-111111111111".into(),
    ] {
        assert!(RequestId::new(&value).is_err());
        assert!(!schema_valid::<RequestId>(json!(value)));
    }
    assert!(RequestId::new(REQUEST_ID).is_ok());
    assert!(schema_valid::<RequestId>(json!(REQUEST_ID)));
    for value in [
        "a".repeat(31),
        "A".repeat(32),
        format!("{}\n", "a".repeat(32)),
    ] {
        assert!(ProfileId::new(&value).is_err());
        assert!(!schema_valid::<ProfileId>(json!(value)));
    }
    for ending in ["\n", "\r", "\u{2028}", "\u{2029}"] {
        let value = format!("revision-1{ending}");
        assert!(OpaqueRevision::new(&value).is_err());
        assert!(!schema_valid::<OpaqueRevision>(json!(value)));
        let value = format!("provider{ending}");
        assert!(!schema_valid::<ProviderId>(json!(value)));
    }
    assert!(Sha256::new(format!("sha256:{}", "a".repeat(64))).is_ok());
    assert!(Sha256::new("sha256:INVALID").is_err());
}
#[test]
fn decimal_counter_range_is_exact_in_rust_and_schema() {
    for value in ["0", "1", "9007199254740992", "18446744073709551615"] {
        let json = json!(value);
        assert!(serde_json::from_value::<Sequence>(json.clone()).is_ok());
        assert!(schema_valid::<Sequence>(json));
    }
    for value in [
        "",
        "00",
        "01",
        "-1",
        "+1",
        " 1",
        "1e1",
        "18446744073709551616",
        "99999999999999999999",
        "1\n",
        "1\u{2028}",
    ] {
        let json = json!(value);
        assert!(
            serde_json::from_value::<Sequence>(json.clone()).is_err(),
            "{value}"
        );
        assert!(!schema_valid::<Sequence>(json), "{value}");
    }
    assert!(serde_json::from_value::<Sequence>(json!(1)).is_err());
    assert!(!schema_valid::<Sequence>(json!(1)));
}
#[test]
fn pid_path_and_timestamp_constraints_match_schema() {
    for pid in [0_u64, u32::MAX as u64 + 1] {
        assert!(!schema_valid::<Pid>(json!(pid)));
    }
    for path in [
        "C:relative",
        "relative",
        "\\rooted",
        "C:\\bad\nfile",
        "\\\\server",
        "\\\\server\\",
        "C:\\foo:bar",
        "C:/folder",
    ] {
        assert!(WindowsAbsolutePath::new(path).is_err(), "{path}");
        assert!(!schema_valid::<WindowsAbsolutePath>(json!(path)), "{path}");
    }
    for path in ["C:\\Games\\Synthetic Game", "\\\\server\\share\\game"] {
        assert!(WindowsAbsolutePath::new(path).is_ok());
        assert!(schema_valid::<WindowsAbsolutePath>(json!(path)));
    }
    assert!(MacAbsolutePath::new("/Applications/Synthetic.app").is_ok());
    assert!(MacAbsolutePath::new("relative.app").is_err());
    for timestamp in [
        "2026-02-29T00:00:00Z",
        "2026-10-03T24:00:00Z",
        "2026-10-03T00:00:60Z",
        "2016-12-31T23:59:60Z",
        "2026-10-03T00:00:00+00:00",
    ] {
        assert!(UtcTimestamp::new(timestamp).is_err());
        assert!(!schema_valid::<UtcTimestamp>(json!(timestamp)));
    }
    assert!(UtcTimestamp::new("2024-02-29T01:02:03.123456789Z").is_ok());
}
#[test]
fn null_optional_assertions_normalize_but_required_fields_reject() {
    let mut value = query("resolve_target", json!({"target":selector(false)}));
    value["body"]["query"]["input"]["target"]["installation"]["revisionAssertion"] = Value::Null;
    let normalized =
        serde_json::to_value(decode_request(&bytes(&value)).unwrap().as_inner()).unwrap();
    assert!(
        normalized["body"]["query"]["input"]["target"]["installation"]
            .get("revisionAssertion")
            .is_none()
    );
    let mut value = query("hello", json!({}));
    value["requestId"] = Value::Null;
    assert!(decode_request(&bytes(&value)).is_err());
    let mut reply = result("prepare", plan());
    reply.as_object_mut().unwrap().remove("requestId");
    assert!(decode_reply(&bytes(&reply)).is_err());
    let mut reply = result("prepare", plan());
    reply["requestId"] = Value::Null;
    assert!(decode_reply(&bytes(&reply)).is_err());
}
#[test]
fn prepared_capture_digest_and_grants_are_semantically_checked() {
    let valid = result("prepare", plan());
    assert!(decode_reply(&bytes(&valid)).is_ok());
    for key in ["grantsLock", "grantsPermission"] {
        let mut value = valid.clone();
        value["body"]["result"]["command"]["output"][key] = json!(true);
        assert!(decode_reply(&bytes(&value)).is_err());
    }
    let mut value = valid.clone();
    value["body"]["result"]["command"]["output"]["semantics"]["capture"]["catalogRevision"] =
        json!("catalog-2");
    assert!(decode_reply(&bytes(&value)).is_err());
    for key in ["action", "trustDomain"] {
        let mut value = semantics(false);
        value[key] = json!(if key == "action" {
            "focus_session"
        } else {
            "game_client"
        });
        let typed: PlanSemantics = serde_json::from_value(value).unwrap();
        assert!(semantic_plan_digest(&typed).is_err());
    }
    let mut value = semantics(false);
    value["capture"]["target"] = target(true);
    let typed: PlanSemantics = serde_json::from_value(value).unwrap();
    assert!(semantic_plan_digest(&typed).is_err());
}
#[test]
fn semantic_hash_sorts_only_declared_set_and_binds_preconditions() {
    // Independently calculated from sorted UTF-8 JSON plus the versioned NUL
    // prefix using Node's SHA-256, so a Rust serializer change cannot silently
    // update the transport's review identity.
    let ordinary: PlanSemantics = serde_json::from_value(semantics(false)).unwrap();
    assert_eq!(
        semantic_plan_digest(&ordinary).unwrap().as_str(),
        "sha256:3168f376007b9b990330fe1a1f20019fa3ea4b54fecef2f15b69383c8dbb759d"
    );
    let value = semantics(true);
    let typed: PlanSemantics = serde_json::from_value(value.clone()).unwrap();
    let original = semantic_plan_digest(&typed).unwrap();
    let mut reversed = value.clone();
    reversed["effects"].as_array_mut().unwrap().reverse();
    let typed: PlanSemantics = serde_json::from_value(reversed).unwrap();
    assert_eq!(semantic_plan_digest(&typed).unwrap(), original);
    let mut duplicate = value.clone();
    duplicate["effects"] = json!(["launch_session", "launch_session", "create_isolated_store"]);
    let typed: PlanSemantics = serde_json::from_value(duplicate).unwrap();
    assert!(semantic_plan_digest(&typed).is_err());
    let mut changed = value;
    changed["capture"]["target"]["profile"]["revision"] = json!("profile-2");
    let typed: PlanSemantics = serde_json::from_value(changed).unwrap();
    assert_ne!(semantic_plan_digest(&typed).unwrap(), original);
    let mut value = semantics(false);
    value["capture"]["runtime"] = json!({"kind":"unrecognized","artifactDigest":format!("sha256:{}","a".repeat(64)),"choice":"reject"});
    let before: PlanSemantics = serde_json::from_value(value.clone()).unwrap();
    value["capture"]["runtime"]["choice"] = json!("allow_once");
    value["capture"]["unrecognizedRuntimeChoice"] = json!("allow_once");
    let after: PlanSemantics = serde_json::from_value(value).unwrap();
    assert_ne!(
        semantic_plan_digest(&before).unwrap(),
        semantic_plan_digest(&after).unwrap()
    );
}
#[test]
fn exact_focus_capture_binds_physical_session_identity() {
    let value = json!({"hashProfile":"bridge-plan-semantic-json-v1","action":"focus_session","capture":{"kind":"focus_session","session":session(),"target":target(false)},"trustDomain":"session","effects":["focus_session"]});
    let original: PlanSemantics = serde_json::from_value(value.clone()).unwrap();
    assert!(semantic_plan_digest(&original).is_ok());
    let mut changed = value.clone();
    changed["capture"]["session"]["process"]["startIdentity"]["value"] = json!("filetime-recycled");
    let typed: PlanSemantics = serde_json::from_value(changed).unwrap();
    assert_ne!(
        semantic_plan_digest(&original).unwrap(),
        semantic_plan_digest(&typed).unwrap()
    );
    let mut changed = value;
    changed["capture"]["session"]["process"]["installationPhysicalId"] = json!("physical-other");
    let typed: PlanSemantics = serde_json::from_value(changed).unwrap();
    assert!(semantic_plan_digest(&typed).is_err());
}
#[test]
fn terminal_recovery_and_progress_outcomes_cannot_conflict() {
    assert!(decode_reply(&bytes(&result("commit", operation()))).is_ok());
    let mut op = operation();
    op["state"]["outcome"]["reason"] = json!("already_satisfied");
    assert!(decode_reply(&bytes(&result("commit", op))).is_err());
    let mut op = operation();
    op["state"] = json!({"status":"running","progress":{"phase":"download","measurement":{"unit":"bytes","completed":"5","total":"4"}}});
    assert!(decode_reply(&bytes(&result("commit", op.clone()))).is_err());
    op["state"]["progress"]["measurement"]
        .as_object_mut()
        .unwrap()
        .remove("total");
    assert!(decode_reply(&bytes(&result("commit", op))).is_ok());
    let mut op = operation();
    op["state"] = json!({"status":"recovery_required","reason":"interrupted_transaction","recovery":{"operationId":OPERATION_ID,"transaction":"native-transaction-1","target":{"kind":"launch","target":target(false)}}});
    assert!(decode_reply(&bytes(&result("commit", op.clone()))).is_ok());
    op["state"]["recovery"]["operationId"] = json!(REQUEST_ID);
    assert!(decode_reply(&bytes(&result("commit", op))).is_err());
    let mut op = operation();
    op["state"]["recovery"] = json!({});
    assert!(decode_reply(&bytes(&result("commit", op))).is_err());
}
#[test]
fn cancellation_close_and_events_preserve_explicit_obligations() {
    let mut op = operation();
    op["state"] = json!({"status":"cancellation_requested","progress":{"phase":"settle","measurement":{"unit":"unknown"}}});
    assert!(
        decode_reply(&bytes(&result(
            "cancel_operation",
            json!({"kind":"requested","operation":op})
        )))
        .is_ok()
    );
    assert!(
        decode_reply(&bytes(&result(
            "cancel_operation",
            json!({"kind":"requested","operation":operation()})
        )))
        .is_err()
    );
    assert!(
        decode_reply(&bytes(&result(
            "request_host_close",
            json!({"kind":"deferred","obligations":[]})
        )))
        .is_err()
    );
    let obligations =
        json!([{"kind":"operation","operationId":OPERATION_ID,"operationRevision":"1"}]);
    assert!(
        decode_reply(&bytes(&result(
            "request_host_close",
            json!({"kind":"deferred","obligations":obligations})
        )))
        .is_ok()
    );
    assert!(
        decode_event(&bytes(&event(
            json!({"type":"operation_changed","operation":operation()})
        )))
        .is_ok()
    );
    assert!(
        decode_event(&bytes(&event(
            json!({"type":"host_close_deferred","obligations":[]})
        )))
        .is_err()
    );
    let mut value = event(json!({"type":"snapshot_invalidated","reason":"retention_gap"}));
    value["cursor"]["sequence"] = json!("18446744073709551616");
    assert!(decode_event(&bytes(&value)).is_err());
}
#[test]
fn stable_rejections_and_observations_do_not_echo_private_payloads() {
    let error = json!({"code":"invalid_request","retryDisposition":"never","violations":[{"field":"body","code":"invalid_shape"}]});
    let reply =
        json!({"protocolVersion":1,"requestId":null,"body":{"type":"rejected","error":error}});
    assert!(decode_reply(&bytes(&reply)).is_ok());
    let mut reply = reply;
    reply["body"]["error"]["rawMessage"] = json!("SYNTHETIC_SECRET_SENTINEL C:\\Users\\Private");
    let failure = decode_reply(&bytes(&reply)).unwrap_err();
    let serialized = serde_json::to_string(&failure).unwrap();
    assert!(!serialized.contains("SYNTHETIC_SECRET"));
    assert!(!serialized.contains("Private"));
    assert_eq!(failure.to_string(), "protocol message rejected");
    let reply = json!({"protocolVersion":1,"requestId":REQUEST_ID,"body":{"type":"result","result":{"type":"query","query":{"name":"resolve_target","output":{"target":{"status":"unavailable","reason":"native_unavailable"}}}}}});
    assert!(decode_reply(&bytes(&reply)).is_ok());
    let serialized = serde_json::to_string(&plan()).unwrap();
    assert!(!serialized.contains("directory"));
    assert!(!serialized.contains("C:\\"));
}
#[test]
fn generated_schemas_are_draft7_and_do_not_require_external_resolution() {
    for schema in [schema_request(), schema_reply(), schema_event()] {
        let value = schema.as_value();
        assert_eq!(value["$schema"], "http://json-schema.org/draft-07/schema#");
        fn refs(value: &Value) {
            match value {
                Value::Object(map) => {
                    if let Some(reference) = map.get("$ref") {
                        assert!(reference.as_str().unwrap().starts_with("#/"));
                    }
                    for value in map.values() {
                        refs(value);
                    }
                }
                Value::Array(list) => {
                    for value in list {
                        refs(value);
                    }
                }
                _ => {}
            }
        }
        refs(value);
    }
    for reference in [
        "https://example.invalid/schema.json",
        "file:///synthetic/private/schema.json",
    ] {
        assert!(
            jsonschema::options()
                .with_draft(jsonschema::Draft::Draft7)
                .offline()
                .build(&json!({"$ref":reference}))
                .is_err()
        );
    }
}

#[test]
fn collections_are_bounded_without_truncating_obligations() {
    let obligations: Vec<_> = (0..64)
        .flat_map(|index| {
            let mut custody = session();
            custody["sessionId"] = json!(format!("{:08x}-1111-4111-8111-111111111111", index + 256));
            custody["process"]["pid"] = json!(index + 1000);
            [
                json!({"kind":"operation","operationId":format!("{index:08x}-1111-4111-8111-111111111111"),"operationRevision":"1"}),
                json!({"kind":"session_custody","session":custody}),
            ]
        })
        .collect();
    assert_eq!(obligations.len(), 128);
    let valid = result(
        "request_host_close",
        json!({"kind":"deferred","obligations":obligations}),
    );
    let decoded = decode_reply(&bytes(&valid)).unwrap();
    assert_eq!(serde_json::to_value(decoded.into_inner()).unwrap(), valid);
    let valid_event = event(json!({"type":"host_close_deferred","obligations":obligations}));
    let decoded = decode_event(&bytes(&valid_event)).unwrap();
    assert_eq!(
        serde_json::to_value(decoded.into_inner()).unwrap(),
        valid_event
    );
    let mut excess = valid;
    excess["body"]["result"]["command"]["output"]["obligations"]
        .as_array_mut()
        .unwrap()
        .push(json!({"kind":"session_custody","session":session()}));
    assert!(decode_reply(&bytes(&excess)).is_err());
    let mut excess_event = valid_event;
    excess_event["body"]["obligations"]
        .as_array_mut()
        .unwrap()
        .push(json!({"kind":"session_custody","session":session()}));
    assert!(decode_event(&bytes(&excess_event)).is_err());
    assert!(BoundedList::<u8, 2>::new(vec![1, 2, 3]).is_err());
}
