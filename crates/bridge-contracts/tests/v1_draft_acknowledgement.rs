//! Synthetic wire relationship vectors only. They grant no native custody,
//! payload equality, staging retry ledger or physical Save authority.
use bridge_contracts::v1::*;
use serde_json::{Value, json};

const REQUEST: &str = "11111111-1111-4111-8111-111111111111";
const HOST: &str = "22222222-2222-4222-8222-222222222222";
const DRAFT: &str = "33333333-3333-4333-8333-333333333333";
fn id(label: u32) -> String {
    format!("{label:08x}-1111-4111-8111-111111111111")
}
fn digest(label: char) -> String {
    format!("sha256:{}", label.to_string().repeat(64))
}
fn document() -> Value {
    json!({
        "documentId": id(1), "revision":"synthetic-document-1",
        "target": {
            "installation": {"kind":"registered", "registrationId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "registrationRevision":"synthetic-registration-1", "physicalId":"synthetic-installation-1",
                "nativeTargetRef":"synthetic-target-1"},
            "profile":{"kind":"ordinary", "ownerScope":"synthetic-owner"}
        },
        "baseline":{"kind":"existing", "fileIdentity":"synthetic-file-1", "contentDigest":digest('a')},
        "schema":{"providerId":"guffawaffle", "schemaId":"stfc-community-mod.config-schema",
            "schemaVersion":"1.0.0", "digest":digest('b'), "runtimeArtifactDigest":digest('c')}
    })
}
fn draft(revision: &str) -> Value {
    json!({"draftId":DRAFT,"hostEpoch":HOST,"revision":revision,"document":document()})
}
fn field(name: &str, sensitivity: &str) -> Value {
    json!({"fieldId":name,"path":name.split('.').collect::<Vec<_>>(),
        "valueType":if sensitivity=="public" {json!({"kind":"boolean"})}
            else {json!({"kind":"string","maximumLength":"4096"})},
        "sensitivity":sensitivity,"category":"synthetic-settings","searchTerms":[],
        "apply":"next_launch","platforms":["windows","macos"],"aliases":[],"deprecated":false})
}
fn schema() -> Value {
    json!({"binding":document()["schema"],"fields":[field("public.flag","public"),
        field("private.endpoint","private"),field("private.proxy","private"),field("secret.token","secret")],
        "sync":[{"mode":"legacy","exposure":"creatable","feeds":["battlelog"],
            "fields":["private.endpoint","private.proxy","secret.token"],"endpointFieldId":"private.endpoint",
            "secretFieldId":"secret.token","proxyFieldId":"private.proxy","inheritsGlobalProxy":true}]})
}
fn private(label: u32, revision: &str, field_id: &str) -> Value {
    json!({"valueId":id(label),"document":document(),"fieldId":field_id,
        "revision":"synthetic-entry-value-1","capturedFor":draft(revision)})
}
fn secret(label: u32, revision: &str) -> Value {
    json!({"secretId":id(label),"draft":draft(revision),"fieldId":"secret.token"})
}
fn envelope(output: Value) -> Value {
    json!({"protocolVersion":1,"requestId":REQUEST,"body":{"type":"result",
        "result":{"type":"command","command":{"name":"set_draft_changes","output":output}}}})
}
fn bytes(value: &Value) -> Vec<u8> {
    serde_json::to_vec(value).unwrap()
}
fn accepts(output: &Value) -> bool {
    decode_reply(&bytes(&envelope(output.clone()))).is_ok()
}
fn accepted_round_trip(output: &Value) {
    let wire = envelope(output.clone());
    let decoded = decode_reply(&bytes(&wire)).unwrap();
    assert_eq!(serde_json::to_value(decoded.as_inner()).unwrap(), wire);
    assert!(decode_request(&bytes(&json!({"protocolVersion":1,"requestId":REQUEST,
        "body":{"type":"command","command":{"name":"set_draft_changes","input":output["accepted"]}}}))).is_ok());
}
fn public_edit() -> Value {
    json!({"kind":"set_public","fieldId":"public.flag","value":{"kind":"boolean","value":true}})
}
fn result(old_edits: Value, new_edits: Value, transfers: Value) -> Value {
    let empty = new_edits.as_array().unwrap().is_empty();
    json!({"accepted":{"draft":draft("7"),"edits":old_edits},
        "snapshot":{"draft":draft("8"),"schema":schema(),"edits":new_edits,
            "apply":if empty {json!([])} else {json!(["next_launch"])},
            "state":if empty {"clean"} else {"dirty"},"validation":[]},"protectedTransfers":transfers})
}
fn mixed() -> Value {
    let old_private = private(10, "7", "private.endpoint");
    let new_private = private(11, "8", "private.endpoint");
    let old_secret = secret(12, "7");
    let new_secret = secret(13, "8");
    let old_proxy = private(14, "7", "private.proxy");
    let new_proxy = private(15, "8", "private.proxy");
    result(
        json!([public_edit(),{"kind":"set_private","fieldId":"private.endpoint","reference":old_private},
            {"kind":"replace_secret","fieldId":"secret.token","reference":old_secret},
            {"kind":"set_sync_proxy","destinationId":"synthetic-destination",
                "value":{"kind":"custom","reference":old_proxy}}]),
        json!([public_edit(),{"kind":"set_private","fieldId":"private.endpoint","reference":new_private},
            {"kind":"replace_secret","fieldId":"secret.token","reference":new_secret},
            {"kind":"set_sync_proxy","destinationId":"synthetic-destination",
                "value":{"kind":"custom","reference":new_proxy}}]),
        json!([{"kind":"private","from":old_private,"to":new_private},
            {"kind":"secret","from":old_secret,"to":new_secret},
            {"kind":"private","from":old_proxy,"to":new_proxy}]),
    )
}

#[test]
fn public_empty_and_saved_private_stage_need_no_transfers() {
    accepted_round_trip(&result(json!([]), json!([]), json!([])));
    accepted_round_trip(&result(
        json!([public_edit()]),
        json!([public_edit()]),
        json!([]),
    ));
    let mut saved = private(20, "7", "private.endpoint");
    saved.as_object_mut().unwrap().remove("capturedFor");
    let edits = json!([{"kind":"set_private","fieldId":"private.endpoint","reference":saved}]);
    accepted_round_trip(&result(edits.clone(), edits, json!([])));
}

#[test]
fn mixed_protected_stage_round_trips_exact_accepted_input_and_successor() {
    accepted_round_trip(&mixed());
}

#[test]
fn nested_sync_capture_transfers_once_for_every_exact_repeated_use() {
    let mut output = mixed();
    let old_destination = json!({"id":"new-destination","mode":"legacy","endpoint":private(10,"7","private.endpoint"),
        "secret":secret(12,"7"),"proxy":{"kind":"custom","reference":private(14,"7","private.proxy")},
        "feeds":[{"feedId":"battlelog","desired":"inherit"}]});
    let mut new_destination = old_destination.clone();
    new_destination["endpoint"] = private(11, "8", "private.endpoint");
    new_destination["secret"] = secret(13, "8");
    new_destination["proxy"]["reference"] = private(15, "8", "private.proxy");
    output["accepted"]["edits"]
        .as_array_mut()
        .unwrap()
        .push(json!({"kind":"add_sync_destination","destination":old_destination}));
    output["snapshot"]["edits"]
        .as_array_mut()
        .unwrap()
        .push(json!({"kind":"add_sync_destination","destination":new_destination}));
    accepted_round_trip(&output);
    assert_eq!(
        output["accepted"]["edits"][4]["destination"]["endpoint"]["capturedFor"]["revision"],
        "7"
    );
}

#[test]
fn transfer_order_is_unordered_but_edit_order_and_values_remain_exact() {
    let mut output = mixed();
    output["protectedTransfers"]
        .as_array_mut()
        .unwrap()
        .reverse();
    accepted_round_trip(&output);
    let mut wrong = output.clone();
    wrong["snapshot"]["edits"]
        .as_array_mut()
        .unwrap()
        .swap(0, 1);
    assert!(!accepts(&wrong));
    wrong = output;
    wrong["snapshot"]["edits"][0]["value"]["value"] = json!(false);
    assert!(!accepts(&wrong));
}

#[test]
fn successor_revision_is_exact_checked_increment_with_no_same_revision_fast_path() {
    // No protected refs can independently reject a changed successor revision.
    let output = result(json!([public_edit()]), json!([public_edit()]), json!([]));
    assert!(accepts(&output));
    for revision in ["6", "7", "9", "18446744073709551615"] {
        let mut wrong = output.clone();
        wrong["snapshot"]["draft"]["revision"] = json!(revision);
        assert!(!accepts(&wrong), "{revision}");
    }
    let mut exhausted = result(json!([public_edit()]), json!([public_edit()]), json!([]));
    exhausted["accepted"]["draft"]["revision"] = json!("18446744073709551615");
    exhausted["snapshot"]["draft"]["revision"] = json!("0");
    assert!(!accepts(&exhausted));
}

#[test]
fn successor_cannot_change_host_draft_document_or_schema_scope() {
    let output = result(json!([public_edit()]), json!([public_edit()]), json!([]));
    assert!(accepts(&output));
    for (pointer, value) in [
        ("/snapshot/draft/hostEpoch", json!(id(30))),
        ("/snapshot/draft/draftId", json!(id(31))),
        (
            "/snapshot/draft/document/revision",
            json!("synthetic-document-2"),
        ),
        (
            "/snapshot/draft/document/target/installation/physicalId",
            json!("synthetic-other-installation"),
        ),
        (
            "/snapshot/draft/document/baseline/fileIdentity",
            json!("synthetic-other-file"),
        ),
        ("/snapshot/draft/document/schema/digest", json!(digest('d'))),
        ("/snapshot/schema/binding/digest", json!(digest('d'))),
    ] {
        let mut wrong = output.clone();
        *wrong.pointer_mut(pointer).unwrap() = value;
        assert!(!accepts(&wrong), "{pointer}");
    }
}

#[test]
fn missing_unused_duplicate_source_and_duplicate_destination_transfers_are_refused() {
    let output = mixed();
    assert!(accepts(&output));
    let mut wrong = output.clone();
    wrong["protectedTransfers"].as_array_mut().unwrap().pop();
    assert!(!accepts(&wrong));
    wrong = output.clone();
    wrong["protectedTransfers"]
        .as_array_mut()
        .unwrap()
        .push(json!({"kind":"private",
        "from":private(40,"7","private.endpoint"),"to":private(41,"8","private.endpoint")}));
    assert!(!accepts(&wrong));
    wrong = output.clone();
    let duplicate = wrong["protectedTransfers"][0].clone();
    wrong["protectedTransfers"]
        .as_array_mut()
        .unwrap()
        .push(duplicate);
    assert!(!accepts(&wrong));
    wrong = output;
    wrong["protectedTransfers"][2]["to"]["valueId"] = json!(id(11));
    wrong["snapshot"]["edits"][3]["value"]["reference"]["valueId"] = json!(id(11));
    assert!(!accepts(&wrong));
}

#[test]
fn source_and_destination_references_require_the_exact_old_and_new_draft() {
    let output = mixed();
    assert!(accepts(&output));
    for pointer in [
        "/protectedTransfers/0/from/capturedFor/revision",
        "/protectedTransfers/0/to/capturedFor/revision",
        "/protectedTransfers/1/from/draft/revision",
        "/protectedTransfers/1/to/draft/revision",
    ] {
        let mut wrong = output.clone();
        *wrong.pointer_mut(pointer).unwrap() = json!("6");
        assert!(!accepts(&wrong), "{pointer}");
    }
    let mut wrong = output;
    wrong["accepted"]["edits"][1]["reference"]["capturedFor"]["hostEpoch"] = json!(id(50));
    assert!(!accepts(&wrong));
}

#[test]
fn transfer_cannot_change_field_purpose_or_private_value_revision() {
    let output = mixed();
    assert!(accepts(&output));
    for (pointer, value) in [
        ("/protectedTransfers/0/to/fieldId", json!("private.proxy")),
        (
            "/protectedTransfers/1/to/fieldId",
            json!("private.endpoint"),
        ),
        (
            "/protectedTransfers/0/to/revision",
            json!("synthetic-other-entry-value"),
        ),
        ("/protectedTransfers/0/kind", json!("secret")),
        ("/protectedTransfers/1/kind", json!("private")),
        (
            "/protectedTransfers/0/to/document/revision",
            json!("synthetic-document-2"),
        ),
    ] {
        let mut wrong = output.clone();
        *wrong.pointer_mut(pointer).unwrap() = value;
        assert!(!accepts(&wrong), "{pointer}");
    }
}

#[test]
fn destination_ids_are_fresh_and_cannot_alias_accepted_saved_or_protected_refs() {
    let output = mixed();
    assert!(accepts(&output));
    for label in [10, 14] {
        let mut wrong = output.clone();
        wrong["protectedTransfers"][0]["to"]["valueId"] = json!(id(label));
        wrong["snapshot"]["edits"][1]["reference"]["valueId"] = json!(id(label));
        assert!(!accepts(&wrong));
    }
    let mut wrong = output;
    let mut saved = private(11, "7", "private.endpoint");
    saved.as_object_mut().unwrap().remove("capturedFor");
    let edit = json!({"kind":"set_sync_proxy","destinationId":"second-synthetic-destination",
        "value":{"kind":"custom","reference":saved}});
    wrong["accepted"]["edits"]
        .as_array_mut()
        .unwrap()
        .push(edit.clone());
    wrong["snapshot"]["edits"]
        .as_array_mut()
        .unwrap()
        .push(edit);
    assert!(!accepts(&wrong));
}

#[test]
fn saved_document_private_refs_cannot_be_promoted_by_a_transfer() {
    let mut saved = private(60, "7", "private.endpoint");
    saved.as_object_mut().unwrap().remove("capturedFor");
    let to = private(61, "8", "private.endpoint");
    let output = result(
        json!([{"kind":"set_private","fieldId":"private.endpoint","reference":saved}]),
        json!([{"kind":"set_private","fieldId":"private.endpoint","reference":to}]),
        json!([{"kind":"private","from":saved,"to":to}]),
    );
    assert!(!accepts(&output));
}

#[test]
fn acknowledged_intent_cannot_add_remove_or_change_destination_feed_or_proxy() {
    let mut output = mixed();
    assert!(accepts(&output));
    output["snapshot"]["edits"].as_array_mut().unwrap().pop();
    assert!(!accepts(&output));
    output = mixed();
    output["snapshot"]["edits"][3]["destinationId"] = json!("foreign-destination");
    assert!(!accepts(&output));
    output = mixed();
    output["snapshot"]["edits"][3]["value"] = json!({"kind":"global"});
    assert!(!accepts(&output));
    output = mixed();
    output["accepted"]["edits"][0]["value"]["value"] = json!(false);
    assert!(!accepts(&output));
}

#[test]
fn private_plaintext_and_bare_legacy_reply_shapes_remain_refused() {
    let output = mixed();
    assert!(accepts(&output));
    let mut wrong = output.clone();
    wrong["snapshot"]["edits"][1] = json!({"kind":"set_public","fieldId":"private.endpoint",
        "value":{"kind":"string","value":"synthetic-private-value"}});
    wrong["snapshot"]["state"] = json!("invalid");
    wrong["snapshot"]["validation"] =
        json!([{"fieldId":"private.endpoint","code":"private_value_required"}]);
    assert!(!accepts(&wrong));
    assert!(!accepts(&output["snapshot"]));
    wrong = output;
    wrong["protectedTransfers"][0]["nativePayload"] = json!("synthetic-value");
    assert!(!accepts(&wrong));
}

#[test]
fn transfer_list_bound_is_isolated_at_direct_deserialization() {
    // These duplicate entries intentionally avoid semantic acknowledgment
    // validation. Direct DTO/list decoding isolates only the cardinality bound.
    let output = mixed();
    let transfer = output["protectedTransfers"][0].clone();
    let at_limit = Value::Array(vec![transfer.clone(); 256]);
    let beyond_limit = Value::Array(vec![transfer; 257]);
    assert!(
        serde_json::from_value::<BoundedList<ProtectedReferenceTransfer, 256>>(at_limit.clone())
            .is_ok()
    );
    assert!(
        serde_json::from_value::<BoundedList<ProtectedReferenceTransfer, 256>>(
            beyond_limit.clone()
        )
        .is_err()
    );
    let mut wrong = output.clone();
    wrong["protectedTransfers"] = at_limit;
    assert!(serde_json::from_value::<SetDraftChangesResult>(wrong.clone()).is_ok());
    wrong["protectedTransfers"] = beyond_limit;
    assert!(serde_json::from_value::<SetDraftChangesResult>(wrong).is_err());
}

#[test]
fn stage_reply_frame_cap_is_enforced_by_the_real_codec() {
    let output = mixed();
    assert!(accepts(&output));
    let mut oversized = bytes(&envelope(output));
    oversized.resize(MAX_MESSAGE_BYTES + 1, b' ');
    assert!(decode_reply(&oversized).is_err());
}
