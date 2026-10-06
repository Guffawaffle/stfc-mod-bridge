use bridge_contracts::v1::*;
use serde_json::{Value, json};

const ID: &str = "11111111-1111-4111-8111-111111111111";
const HOST: &str = "22222222-2222-4222-8222-222222222222";

fn digest(character: char) -> String {
    format!("sha256:{}", character.to_string().repeat(64))
}
fn bytes(value: &Value) -> Vec<u8> {
    serde_json::to_vec(value).unwrap()
}
fn application(revision: &str, subject: char) -> Value {
    json!({
        "applicationId":"guffawaffle.stfc-mod-bridge",
        "installationRef":"bridge-installation-1", "revision":revision,
        "channelId":"stable", "platform":"windows", "architecture":"x86_64",
        "packageDigest":digest(subject), "pairingDigest":digest(subject),
        "payloads":[{"role":"application","digest":digest(subject),"size":"1024"}]
    })
}
fn selected_release() -> Value {
    json!({
        "selectionId":ID, "hostEpoch":HOST,
        "current":application("app-1", '1'), "offered":application("app-2", '2'),
        "releaseVersion":"2.0.0", "authority":"reviewed-pair-1", "revision":"selection-1"
    })
}
fn bridge_recovery(expected: Value) -> Value {
    json!({
        "application":application("app-1", '1'), "expectedApplication":expected,
        "bridgeJournalRef":"bridge-journal-1", "revision":"journal-1"
    })
}
fn recovery_ref(recovery: Value) -> Value {
    json!({
        "operationId":ID, "transaction":"bridge-journal-1",
        "target":{"kind":"bridge","recovery":recovery}
    })
}
fn semantics(action: &str, input: Value) -> Value {
    json!({
        "hashProfile":"bridge-plan-semantic-json-v1", "action":action,
        "capture":{"kind":action,"input":input}, "trustDomain":"bridge_application",
        "effects":[if action == "bridge_update" {"replace_bridge"} else {"recover_bridge"}]
    })
}
fn operation(captured: Value, recovery: Value) -> Value {
    json!({
        "operationId":ID, "operationRevision":"2", "semantics":captured,
        "state":{"status":"recovery_required","reason":"interrupted_transaction",
                 "recovery":recovery_ref(recovery)}
    })
}
fn command_result(name: &str, output: Value) -> Value {
    json!({"protocolVersion":1,"requestId":ID,"body":{
        "type":"result","result":{"type":"command","command":{"name":name,"output":output}}
    }})
}
fn query_result(name: &str, output: Value) -> Value {
    json!({"protocolVersion":1,"requestId":ID,"body":{
        "type":"result","result":{"type":"query","query":{"name":name,"output":output}}
    }})
}
fn error_result(recovery: Value) -> Value {
    json!({"protocolVersion":1,"requestId":ID,"body":{
        "type":"rejected","error":{"code":"recovery_required",
        "retryDisposition":"after_recovery","violations":[],"recovery":recovery_ref(recovery)}
    }})
}
fn close_result(recovery: Value) -> Value {
    command_result(
        "request_host_close",
        json!({
            "kind":"recovery_required","recoveries":[recovery_ref(recovery)]
        }),
    )
}
fn assert_semantic_refusal(value: &Value) {
    let failure = decode_reply(&bytes(value)).unwrap_err();
    assert_eq!(failure.error.code, ErrorCode::InvalidRequest);
    assert_eq!(
        failure.error.violations.as_slice()[0].code,
        ViolationCode::ConflictingBinding
    );
}

#[test]
fn bridge_update_recovery_accepts_exact_reviewed_rollback_and_forward_pairs() {
    let selected = selected_release();
    for expected in [selected["current"].clone(), selected["offered"].clone()] {
        let recovered = bridge_recovery(expected);
        let captured = semantics("bridge_update", json!({"selectedRelease":selected}));
        assert!(
            decode_reply(&bytes(&command_result(
                "commit",
                operation(captured, recovered.clone())
            )))
            .is_ok()
        );
        assert!(decode_reply(&bytes(&error_result(recovered.clone()))).is_ok());
        assert!(decode_reply(&bytes(&close_result(recovered))).is_ok());
    }
}

#[test]
fn bridge_recovery_nested_validity_is_enforced_in_operations_errors_and_host_close() {
    let valid = bridge_recovery(application("app-1", '1'));
    let mut alternatives = Vec::new();
    for (field, value) in [
        ("channelId", json!("preview")),
        ("applicationId", json!("other.bridge")),
        ("installationRef", json!("other-installation")),
        ("architecture", json!("arm64")),
        ("payloads", json!([])),
        (
            "payloads",
            json!([{"role":"update_helper","digest":digest('1'),"size":"1024"}]),
        ),
        (
            "payloads",
            json!([{"role":"application","digest":digest('1'),"size":"0"}]),
        ),
        (
            "payloads",
            json!([{"role":"application","digest":digest('1'),"size":"1"},{"role":"application","digest":digest('2'),"size":"1"}]),
        ),
    ] {
        let mut wrong = valid.clone();
        wrong["expectedApplication"][field] = value;
        alternatives.push(wrong);
    }
    let mut cross_platform = valid.clone();
    cross_platform["expectedApplication"]["platform"] = json!("macos");
    cross_platform["expectedApplication"]["architecture"] = json!("arm64");
    alternatives.push(cross_platform);
    let mut malformed_current = valid;
    malformed_current["application"]["payloads"] = json!([]);
    alternatives.push(malformed_current);

    for wrong in alternatives {
        let captured = semantics(
            "bridge_update",
            json!({"selectedRelease":selected_release()}),
        );
        assert_semantic_refusal(&command_result(
            "commit",
            operation(captured, wrong.clone()),
        ));
        assert_semantic_refusal(&error_result(wrong.clone()));
        assert_semantic_refusal(&close_result(wrong));
    }
}

#[test]
fn bridge_update_recovery_refuses_third_bindings_and_changed_current_pairs() {
    let selected = selected_release();
    let captured = semantics("bridge_update", json!({"selectedRelease":selected}));
    let third = bridge_recovery(application("app-3", '3'));
    assert_semantic_refusal(&command_result(
        "commit",
        operation(captured.clone(), third.clone()),
    ));
    // These standalone records have no reviewed release selection. Their
    // self-consistency is validated; admission and journal authority are engine work.
    assert!(decode_reply(&bytes(&error_result(third.clone()))).is_ok());
    assert!(decode_reply(&bytes(&close_result(third))).is_ok());
    for field in ["revision", "packageDigest", "pairingDigest", "payloads"] {
        let mut wrong = bridge_recovery(selected["offered"].clone());
        wrong["application"][field] = application("app-3", '3')[field].clone();
        assert_semantic_refusal(&command_result(
            "commit",
            operation(captured.clone(), wrong),
        ));
        let mut wrong = bridge_recovery(selected["current"].clone());
        wrong["expectedApplication"][field] = application("app-3", '3')[field].clone();
        assert_semantic_refusal(&command_result(
            "commit",
            operation(captured.clone(), wrong),
        ));
    }
}

#[test]
fn explicit_bridge_recovery_preserves_its_exact_expected_pair_and_nested_validity() {
    let explicit = bridge_recovery(selected_release()["offered"].clone());
    let captured = semantics("recover_bridge_update", json!({"recovery":explicit}));
    assert!(
        decode_reply(&bytes(&command_result(
            "commit",
            operation(captured.clone(), explicit.clone())
        )))
        .is_ok()
    );
    let mut wrong = explicit;
    wrong["expectedApplication"] = selected_release()["current"].clone();
    assert_semantic_refusal(&command_result("commit", operation(captured, wrong)));

    let mut malformed = bridge_recovery(application("app-1", '1'));
    malformed["expectedApplication"]["payloads"] = json!([]);
    let captured = semantics("recover_bridge_update", json!({"recovery":malformed}));
    assert_semantic_refusal(&command_result("commit", operation(captured, malformed)));
}

fn installation() -> Value {
    json!({"kind":"registered","registrationId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "registrationRevision":"catalog-2","physicalId":"physical-1","nativeTargetRef":"target-1"})
}
fn observed(value: Value) -> Value {
    json!({"status":"observed","value":value,"evidence":{
        "observationId":ID,"observedAt":"2026-10-03T00:00:00Z","source":"catalog_metadata"
    }})
}
fn inventory(items: Value) -> Value {
    json!({"items":items,"completeness":"complete","issues":[],"revision":"inventory-1"})
}
fn unavailable() -> Value {
    json!({"status":"unavailable","reason":"native_unavailable"})
}
fn projection(binding: Value, recovered_installation: Value) -> Value {
    json!({"binding":binding,"name":"Synthetic installation","client":unavailable(),
        "update":observed(json!({"installation":recovered_installation,
            "nativeTransaction":"installation-path-key","revision":"journal-1",
            "expectedClient":{"version":"270","executableDigest":digest('a'),"architecture":"x86_64"}}))
    })
}
fn snapshot(installations: Value) -> Value {
    json!({"cursor":{"hostEpoch":HOST,"streamId":ID,"sequence":"0"},
        "preferences":unavailable(),"profiles":unavailable(),"installations":installations,
        "sessions":unavailable(),"operations":inventory(json!([])),"capabilities":inventory(json!([]))})
}
fn installation_replies(value: Value) -> [Value; 2] {
    let output = observed(inventory(json!([value])));
    [
        query_result("list_installations", output.clone()),
        query_result("snapshot", snapshot(output)),
    ]
}

#[test]
fn installation_recovery_projection_preserves_identity_across_historical_metadata_revisions() {
    let current = installation();
    let mut historical = current.clone();
    historical["registrationRevision"] = json!("catalog-1");
    for recovered in [current.clone(), historical] {
        for reply in installation_replies(projection(current.clone(), recovered)) {
            assert!(decode_reply(&bytes(&reply)).is_ok());
        }
    }
    for (field, value) in [
        ("registrationId", json!("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
        ("physicalId", json!("physical-2")),
        ("nativeTargetRef", json!("target-2")),
    ] {
        let mut foreign = current.clone();
        foreign[field] = value;
        for reply in installation_replies(projection(current.clone(), foreign)) {
            assert_semantic_refusal(&reply);
        }
    }
}

#[test]
fn installation_recovery_projection_does_not_infer_registered_directory_equivalence() {
    let registered = installation();
    let directory =
        json!({"kind":"directory","physicalId":"physical-1","nativeTargetRef":"target-1"});
    for reply in installation_replies(projection(directory.clone(), directory.clone())) {
        assert!(decode_reply(&bytes(&reply)).is_ok());
    }
    for (binding, recovered) in [
        (registered.clone(), directory.clone()),
        (directory.clone(), registered),
    ] {
        for reply in installation_replies(projection(binding, recovered)) {
            assert_semantic_refusal(&reply);
        }
    }
    for (field, value) in [
        ("physicalId", json!("physical-2")),
        ("nativeTargetRef", json!("target-2")),
    ] {
        let mut foreign = directory.clone();
        foreign[field] = value;
        for reply in installation_replies(projection(directory.clone(), foreign)) {
            assert_semantic_refusal(&reply);
        }
    }
}
