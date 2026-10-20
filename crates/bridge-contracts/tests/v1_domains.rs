use bridge_contracts::v1::*;
use schemars::{JsonSchema, generate::SchemaSettings};
use serde_json::{Value, json};

const UUID: &str = "11111111-1111-4111-8111-111111111111";
const HOST: &str = "22222222-2222-4222-8222-222222222222";
fn digest(c: char) -> String {
    format!("sha256:{}", c.to_string().repeat(64))
}
fn bytes(v: &Value) -> Vec<u8> {
    serde_json::to_vec(v).unwrap()
}
fn installation() -> Value {
    json!({"kind":"registered","registrationId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","registrationRevision":"installation-1","physicalId":"physical-1","nativeTargetRef":"target-1"})
}
fn target() -> Value {
    json!({"installation":installation(),"profile":{"kind":"ordinary","ownerScope":"owner-1"}})
}
fn schema_binding() -> Value {
    json!({"providerId":"guffawaffle","schemaId":"stfc-community-mod.config-schema","schemaVersion":"1.0.0","digest":digest('a'),"runtimeArtifactDigest":digest('b')})
}
fn document(existing: bool) -> Value {
    json!({"documentId":UUID,"target":target(),"revision":"document-1","baseline":if existing{json!({"kind":"existing","fileIdentity":"file-1","contentDigest":digest('c')})}else{json!({"kind":"missing"})},"schema":schema_binding()})
}
fn draft_ref() -> Value {
    json!({"draftId":"33333333-3333-4333-8333-333333333333","hostEpoch":HOST,"revision":"1","document":document(false)})
}
fn evidence() -> Value {
    json!({"observationId":"44444444-4444-4444-8444-444444444444","observedAt":"2026-10-03T00:00:00Z","source":"catalog_metadata"})
}
fn observed(value: Value) -> Value {
    json!({"status":"observed","value":value,"evidence":evidence()})
}
fn unknown() -> Value {
    json!({"status":"unavailable","reason":"native_unavailable"})
}
fn inventory(items: Value) -> Value {
    json!({"items":items,"completeness":"complete","issues":[],"revision":"inventory-1"})
}
fn command(name: &str, input: Value) -> Value {
    json!({"protocolVersion":1,"requestId":UUID,"body":{"type":"command","command":{"name":name,"input":input}}})
}
fn prepare(kind: &str, input: Value) -> Value {
    command("prepare", json!({"intent":{"kind":kind,"input":input}}))
}
fn query(name: &str, input: Value) -> Value {
    json!({"protocolVersion":1,"requestId":UUID,"body":{"type":"query","query":{"name":name,"input":input}}})
}
fn query_result(name: &str, output: Value) -> Value {
    json!({"protocolVersion":1,"requestId":UUID,"body":{"type":"result","result":{"type":"query","query":{"name":name,"output":output}}}})
}
fn command_result(name: &str, mut output: Value) -> Value {
    if name == "set_draft_changes" {
        // Existing semantic vectors describe one draft. Explicitly construct
        // its old input, successor and opaque substitutions for the new wire.
        let previous = output["draft"].clone();
        let edits = output["edits"].clone();
        let revision = previous["revision"]
            .as_str()
            .unwrap()
            .parse::<u64>()
            .unwrap()
            + 1;
        output["draft"]["revision"] = json!(revision.to_string());
        let successor = output["draft"].clone();
        let mut transfers = Vec::new();
        for edit in output["edits"].as_array_mut().unwrap() {
            match edit["kind"].as_str().unwrap() {
                "set_private" => transfer_test_reference(
                    &mut edit["reference"],
                    false,
                    &previous,
                    &successor,
                    &mut transfers,
                ),
                "replace_secret" => transfer_test_reference(
                    &mut edit["reference"],
                    true,
                    &previous,
                    &successor,
                    &mut transfers,
                ),
                "set_sync_proxy" if edit["value"]["kind"] == "custom" => transfer_test_reference(
                    &mut edit["value"]["reference"],
                    false,
                    &previous,
                    &successor,
                    &mut transfers,
                ),
                "add_sync_destination" => {
                    transfer_test_reference(
                        &mut edit["destination"]["endpoint"],
                        false,
                        &previous,
                        &successor,
                        &mut transfers,
                    );
                    transfer_test_reference(
                        &mut edit["destination"]["secret"],
                        true,
                        &previous,
                        &successor,
                        &mut transfers,
                    );
                    if edit["destination"]["proxy"]["kind"] == "custom" {
                        transfer_test_reference(
                            &mut edit["destination"]["proxy"]["reference"],
                            false,
                            &previous,
                            &successor,
                            &mut transfers,
                        );
                    }
                }
                _ => {}
            }
        }
        output = json!({"accepted":{"draft":previous,"edits":edits},"snapshot":output,"protectedTransfers":transfers});
    }
    json!({"protocolVersion":1,"requestId":UUID,"body":{"type":"result","result":{"type":"command","command":{"name":name,"output":output}}}})
}
fn transfer_test_reference(
    reference: &mut Value,
    secret: bool,
    previous: &Value,
    successor: &Value,
    transfers: &mut Vec<Value>,
) {
    let binding = if secret { "draft" } else { "capturedFor" };
    if reference[binding] != *previous {
        return;
    }
    if let Some(transfer) = transfers
        .iter()
        .find(|transfer| transfer["from"] == *reference)
    {
        *reference = transfer["to"].clone();
        return;
    }
    let from = reference.clone();
    reference[binding] = successor.clone();
    reference[if secret { "secretId" } else { "valueId" }] = json!(format!(
        "{:08x}-1111-4111-8111-111111111111",
        0xeeee0000usize + transfers.len()
    ));
    transfers.push(
        json!({"kind":if secret {"secret"} else {"private"},"from":from,"to":reference.clone()}),
    );
}
fn field(id: &str, kind: Value, sensitivity: &str, default: Option<Value>, apply: &str) -> Value {
    let mut field = json!({"fieldId":id,"path":id.split('.').collect::<Vec<_>>(),"valueType":kind,"sensitivity":sensitivity,"category":"settings","searchTerms":[id],"apply":apply,"platforms":["windows","macos"],"aliases":[],"deprecated":false});
    if let Some(value) = default {
        field["defaultValue"] = value;
    }
    field
}
fn configuration_schema() -> Value {
    let mut schema = json!({"binding":schema_binding(),"fields":[
    field("setting.boolean",json!({"kind":"boolean"}),"public",Some(json!({"kind":"boolean","value":false})),"immediate"),
    field("setting.integer",json!({"kind":"integer","minimum":"-9223372036854775808","maximum":"9223372036854775807"}),"public",Some(json!({"kind":"integer","value":"1"})),"next_launch"),
    field("setting.number",json!({"kind":"number","minimum":"-2.5","maximum":"5.25"}),"public",Some(json!({"kind":"number","value":"1.5"})),"restart_required"),
    field("setting.string",json!({"kind":"string","maximumLength":"20"}),"public",Some(json!({"kind":"string","value":"synthetic"})),"immediate"),
    field("setting.enum",json!({"kind":"enum","values":["off","summary"]}),"public",Some(json!({"kind":"enum","value":"off"})),"next_launch"),
    field("setting.keys",json!({"kind":"keybinding","multiple":true,"keys":["SPACE","K"]}),"public",Some(json!({"kind":"keybinding","value":[{"key":"SPACE","modifiers":[]}]})),"next_launch"),
    field("setting.notification",json!({"kind":"notification_policy","sounds":["none","warning"]}),"public",Some(json!({"kind":"notification_policy","value":{"kind":"disabled"}})),"next_launch"),
    field("sync.endpoint",json!({"kind":"string","maximumLength":"4096"}),"private",None,"next_launch"),
    field("sync.token",json!({"kind":"string","maximumLength":"4096"}),"secret",None,"next_launch")
],"sync":[{"mode":"legacy","exposure":"creatable","feeds":["battlelog"],"fields":["sync.endpoint","sync.token"],"inheritsGlobalProxy":true},{"mode":"sidecar","exposure":"existing_configuration_only","feeds":[],"fields":["sync.endpoint","sync.token"],"inheritsGlobalProxy":false},{"mode":"majel","exposure":"hidden","feeds":[],"fields":["sync.endpoint","sync.token"],"inheritsGlobalProxy":true}]});
    for definition in schema["sync"].as_array_mut().unwrap() {
        definition["endpointFieldId"] = json!("sync.endpoint");
        definition["secretFieldId"] = json!("sync.token");
    }
    schema
}
fn document_snapshot() -> Value {
    let schema = configuration_schema();
    let fields:Vec<_>=schema["fields"].as_array().unwrap().iter().map(|f|json!({"fieldId":f["fieldId"],"overridden":false,"value":if f["sensitivity"]=="secret"{json!({"kind":"secret","configured":false})}else if f["sensitivity"]=="private"{json!({"kind":"absent"})}else{json!({"kind":"public","value":f["defaultValue"]})}})).collect();
    json!({"binding":document(false),"schema":schema,"fields":fields,"preservation":"supported","sync":[]})
}
fn clean_draft() -> Value {
    json!({"draft":draft_ref(),"schema":configuration_schema(),"edits":[],"apply":[],"state":"clean","validation":[]})
}
fn runtime_binding() -> Value {
    json!({"providerId":"guffawaffle","distributionId":"guffawaffle.stfc-community-mod","artifactDigest":digest('b'),"manifest":{"status":"unknown","reason":"unrecognized_identity"},"configurationSchemaDigest":digest('a'),"clientRevision":"client-1","platform":"windows","architecture":"x86_64"})
}
fn runtime_release() -> Value {
    json!({"selectionId":"55555555-5555-4555-8555-555555555555","hostEpoch":HOST,"revision":"release-1","target":target(),"providerId":"guffawaffle","distributionId":"guffawaffle.stfc-community-mod","channelId":"stable","releaseVersion":"2.1.0-guffa.9","clientRevision":"client-1","artifacts":[{"role":"runtime_module","platform":"windows","architecture":"x86_64","digest":digest('b'),"size":"512"}],"configurationSchema":schema_binding(),"authority":"reviewed-pair-1"})
}
fn managed_runtime() -> Value {
    json!({"receiptId":"receipt-1","revision":"receipt-revision-1","target":target(),"binding":runtime_binding()})
}
fn deploy(managed: bool) -> Value {
    json!({"target":target(),"selectedRelease":runtime_release(),"expectedOwnership":if managed{json!({"kind":"managed","reference":managed_runtime()})}else{json!({"kind":"absent"})},"configuration":{"kind":"unchanged","document":document(false)}})
}
fn client(version: &str) -> Value {
    json!({"version":version,"executableDigest":digest('e'),"architecture":"x86_64"})
}
fn game_check() -> Value {
    json!({"checkId":"66666666-6666-4666-8666-666666666666","hostEpoch":HOST,"revision":"check-1","installation":installation(),"currentClient":client("270"),"offeredClient":client("271"),"officialManifestDigest":digest('f'),"route":"canonical_native_direct"})
}
fn game_recovery() -> Value {
    json!({"installation":installation(),"nativeTransaction":"installation-path-key","revision":"journal-1","expectedClient":client("270")})
}
fn app() -> Value {
    json!({"applicationId":"guffawaffle.stfc-mod-bridge","installationRef":"bridge-installation-1","revision":"app-1","channelId":"stable","platform":"windows","architecture":"x86_64","packageDigest":digest('1'),"pairingDigest":digest('2'),"payloads":[{"role":"application","digest":digest('1'),"size":"1024"},{"role":"profiles_native","digest":digest('3'),"size":"512"},{"role":"toml_native","digest":digest('4'),"size":"512"},{"role":"update_helper","digest":digest('5'),"size":"512"}]})
}
fn bridge_release() -> Value {
    let mut offered = app();
    offered["revision"] = json!("app-2");
    offered["packageDigest"] = json!(digest('6'));
    json!({"selectionId":"77777777-7777-4777-8777-777777777777","hostEpoch":HOST,"current":app(),"offered":offered,"releaseVersion":"1.0.0","authority":"bridge-signing-policy-1","revision":"bridge-check-1"})
}
fn bridge_recovery() -> Value {
    json!({"application":app(),"expectedApplication":app(),"bridgeJournalRef":"bridge-journal-1","revision":"journal-1"})
}
fn profile(state: &str) -> Value {
    json!({"id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","revision":"profile-1","state":state})
}
fn prefs() -> Value {
    json!({"expectedRevision":"prefs-1","values":{"theme":"system","motion":"reduced","provider":{"providerId":"netniv","channelId":"stable"}}})
}
fn diagnostic_content() -> Value {
    json!({"target":target(),"disclosure":"redacted","facts":[{"kind":"target","value":target()},{"kind":"issue","value":{"code":"native_unavailable"}}]})
}
fn preview() -> Value {
    let content: DiagnosticContent = serde_json::from_value(diagnostic_content()).unwrap();
    json!({"previewId":"88888888-8888-4888-8888-888888888888","hostEpoch":HOST,"revision":"preview-1","target":target(),"disclosure":"redacted","digest":diagnostic_preview_digest(&content).unwrap()})
}
fn export() -> Value {
    json!({"preview":preview(),"destination":{"destinationId":"99999999-9999-4999-8999-999999999999","hostEpoch":HOST,"nativeTargetRef":"chosen-export-destination-1","revision":"destination-1"}})
}
fn prepared(kind: &str, capture_input: Value, domain: &str, effects: Value) -> Value {
    let mut capture = json!({"kind":kind,"input":capture_input});
    if matches!(
        kind,
        "runtime_install" | "runtime_update" | "runtime_repair" | "runtime_switch_source"
    ) {
        let participant = &capture["input"]["configuration"];
        capture["preparedConfiguration"] = if participant["kind"] == "unchanged" {
            json!({"kind":"unchanged","document":participant["document"]})
        } else {
            let baseline = if participant["kind"] == "save_reviewed_draft" {
                &participant["draft"]["document"]
            } else {
                &participant["document"]
            };
            json!({"kind":"write","baseline":baseline,"destinationSchema":capture["input"]["selectedRelease"]["configurationSchema"],"candidateDigest":digest('d')})
        };
    }
    let semantics = json!({"hashProfile":"bridge-plan-semantic-json-v1","action":kind,"capture":capture,"trustDomain":domain,"effects":effects});
    let typed: PlanSemantics = serde_json::from_value(semantics.clone()).unwrap();
    json!({"planRef":{"planId":UUID,"hostEpoch":HOST,"reviewDigest":semantic_plan_digest(&typed).unwrap()},"semantics":semantics,"expiresAt":"2026-10-04T00:00:00Z","grantsLock":false,"grantsPermission":false})
}
fn completed(plan: &Value, receipt: Value) -> Value {
    command_result(
        "commit",
        json!({"operationId":UUID,"operationRevision":"2","semantics":plan["semantics"],"state":{"status":"completed","outcome":{"kind":"changed","reason":"applied","receipt":receipt}}}),
    )
}
fn backup() -> Value {
    json!({"backupId":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb","document":document(true),"retainedDigest":digest('c'),"nativeBackupRef":"native-backup-1","createdAt":"2026-10-03T00:00:00Z"})
}

#[test]
fn all_management_intents_and_resolved_preparations_are_typed() {
    let create = json!({"name":"Synthetic profile","setup":{"kind":"new"},"preferredInstallation":{"kind":"registered","id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"expectedCatalogRevision":"catalog-1"});
    let create_capture = json!({"name":"Synthetic profile","setup":{"kind":"new"},"preferredInstallation":installation(),"catalogRevision":"catalog-1","nativePreparationRef":"prepared-profile-1","destinationOwner":"owner-1"});
    let ordinary = json!({"profile":{"kind":"ordinary","catalogId":"cccccccccccccccccccccccccccccccc","revision":"ordinary-1","ownerScope":"owner-1"},"preferredInstallation":{"kind":"clear"}});
    let isolated = json!({"profile":profile("active"),"name":"Renamed synthetic profile","preferredInstallation":{"kind":"keep","expected":{"kind":"none"}}});
    for (kind, input, capture, effects) in [
        (
            "create_profile",
            create,
            create_capture,
            json!(["publish_profile"]),
        ),
        (
            "edit_ordinary_profile",
            ordinary.clone(),
            ordinary,
            json!(["edit_profile_metadata"]),
        ),
        (
            "edit_isolated_profile",
            isolated.clone(),
            isolated,
            json!(["edit_profile_metadata"]),
        ),
        (
            "archive_profile",
            json!({"profile":profile("active")}),
            json!({"profile":profile("active")}),
            json!(["archive_profile"]),
        ),
        (
            "restore_profile",
            json!({"profile":profile("archived")}),
            json!({"profile":profile("archived")}),
            json!(["restore_profile"]),
        ),
        (
            "delete_profile",
            json!({"profile":profile("archived"),"confirmation":"delete_entire_owned_profile"}),
            json!({"profile":profile("archived"),"confirmation":"delete_entire_owned_profile"}),
            json!(["delete_owned_profile"]),
        ),
        (
            "register_installation",
            json!({"directory":{"platform":"windows","value":"C:\\Synthetic\\Game"},"name":"Synthetic installation","expectedCatalogRevision":"catalog-1"}),
            json!({"name":"Synthetic installation","physicalId":"physical-1","nativeTargetRef":"target-1","catalogRevision":"catalog-1"}),
            json!(["register_installation"]),
        ),
        (
            "edit_installation",
            json!({"installation":installation(),"name":"Renamed installation"}),
            json!({"installation":installation(),"name":"Renamed installation"}),
            json!(["edit_installation_metadata"]),
        ),
    ] {
        assert!(
            decode_request(&bytes(&prepare(kind, input))).is_ok(),
            "{kind}"
        );
        assert!(
            decode_reply(&bytes(&command_result(
                "prepare",
                prepared(kind, capture, "profile_state", effects)
            )))
            .is_ok(),
            "{kind}"
        );
    }
    assert!(
        decode_request(&bytes(&prepare(
            "archive_profile",
            json!({"profile":profile("archived")})
        )))
        .is_err()
    );
    assert!(decode_request(&bytes(&prepare("edit_ordinary_profile",json!({"profile":{"kind":"ordinary","catalogId":"cccccccccccccccccccccccccccccccc","revision":"ordinary-1","ownerScope":"owner-1"},"name":"Do not rename Default","preferredInstallation":{"kind":"keep"}})))).is_err());
    assert!(
        decode_request(&bytes(&prepare(
            "delete_profile",
            json!({"profile":profile("active")})
        )))
        .is_err()
    );
}
#[test]
fn import_capture_requires_exact_native_owner_and_declared_approval() {
    let source = json!({"sourceId":"native-source-user-1","sourceRevision":"source-1","destinationOwner":"owner-1","requiresNativeApproval":true});
    let input = json!({"name":"Imported synthetic profile","setup":{"kind":"windows_user_import","source":source,"approval":"request_native_approval"},"preferredInstallation":{"kind":"registered","id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"expectedCatalogRevision":"catalog-1"});
    assert!(decode_request(&bytes(&prepare("create_profile", input.clone()))).is_ok());
    let mut secret = input;
    secret["setup"]["password"] = json!("SYNTHETIC_PASSWORD_SENTINEL");
    let error = decode_request(&bytes(&prepare("create_profile", secret))).unwrap_err();
    assert!(
        !serde_json::to_string(&error)
            .unwrap()
            .contains("SYNTHETIC_PASSWORD")
    );
    let capture = json!({"name":"Imported synthetic profile","setup":{"kind":"windows_user_import","source":source,"approval":"request_native_approval"},"preferredInstallation":installation(),"catalogRevision":"catalog-1","nativePreparationRef":"prepared-import-1","destinationOwner":"owner-1"});
    assert!(
        decode_reply(&bytes(&command_result(
            "prepare",
            prepared(
                "create_profile",
                capture.clone(),
                "profile_state",
                json!(["publish_profile"])
            )
        )))
        .is_ok()
    );
    for (field, value) in [
        ("destinationOwner", json!("owner-2")),
        (
            "setup",
            json!({"kind":"windows_user_import","source":source,"approval":"decline"}),
        ),
    ] {
        let mut changed = capture.clone();
        changed[field] = value;
        let semantics = json!({"hashProfile":"bridge-plan-semantic-json-v1","action":"create_profile","capture":{"kind":"create_profile","input":changed},"trustDomain":"profile_state","effects":["publish_profile"]});
        assert!(semantic_plan_digest(&serde_json::from_value(semantics).unwrap()).is_err());
    }
}
#[test]
fn configuration_projects_every_semantic_type_and_no_plaintext_saved_secrets() {
    let valid = query_result("read_configuration", observed(document_snapshot()));
    assert!(decode_reply(&bytes(&valid)).is_ok());
    for wrong in [
        json!({"kind":"public","value":{"kind":"string","value":"SYNTHETIC_SECRET_SENTINEL"}}),
        json!({"kind":"secret","configured":true,"value":"SYNTHETIC_SECRET_SENTINEL"}),
    ] {
        let mut changed = valid.clone();
        changed["body"]["result"]["query"]["output"]["value"]["fields"][8]["value"] = wrong;
        let failure = decode_reply(&bytes(&changed)).unwrap_err();
        assert!(
            !serde_json::to_string(&failure)
                .unwrap()
                .contains("SYNTHETIC_SECRET")
        );
    }
    let mut changed = valid.clone();
    changed["body"]["result"]["query"]["output"]["value"]["fields"][0]["overridden"] = json!(true);
    assert!(decode_reply(&bytes(&changed)).is_err());
    let mut changed = valid;
    changed["body"]["result"]["query"]["output"]["value"]["fields"]
        .as_array_mut()
        .unwrap()
        .pop();
    assert!(decode_reply(&bytes(&changed)).is_err());
    assert_ne!(
        serde_json::from_value::<DocumentBinding>(document(false)).unwrap(),
        serde_json::from_value::<DocumentBinding>(document(true)).unwrap()
    );
}
#[test]
fn draft_edits_bind_secret_custody_schema_and_complete_apply_timing() {
    let secret = json!({"secretId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","draft":draft_ref(),"fieldId":"sync.token"});
    let edits = json!([{"kind":"set_public","fieldId":"setting.boolean","value":{"kind":"boolean","value":true}},{"kind":"set_public","fieldId":"setting.number","value":{"kind":"number","value":"2.5"}},{"kind":"replace_secret","fieldId":"sync.token","reference":secret}]);
    assert!(
        decode_request(&bytes(&command(
            "set_draft_changes",
            json!({"draft":draft_ref(),"edits":edits})
        )))
        .is_ok()
    );
    let mut draft = clean_draft();
    draft["state"] = json!("dirty");
    draft["edits"] = edits;
    draft["apply"] = json!(["immediate", "next_launch", "restart_required"]);
    assert!(decode_reply(&bytes(&command_result("set_draft_changes", draft.clone()))).is_ok());
    let plan = prepared(
        "save_configuration",
        json!({"draft":draft,"candidateDigest":digest('d')}),
        "configuration",
        json!(["write_configuration"]),
    );
    assert!(decode_reply(&bytes(&command_result("prepare", plan))).is_ok());
    let mut wrong = command(
        "set_draft_changes",
        json!({"draft":draft_ref(),"edits":[{"kind":"replace_secret","fieldId":"sync.token","reference":secret}]}),
    );
    wrong["body"]["command"]["input"]["edits"][0]["reference"]["draft"]["document"]["schema"]["digest"] =
        json!(digest('e'));
    assert!(decode_request(&bytes(&wrong)).is_err());
    let mut wrong = clean_draft();
    wrong["state"] = json!("dirty");
    wrong["edits"] = json!([{"kind":"set_public","fieldId":"setting.number","value":{"kind":"number","value":"9"}}]);
    wrong["apply"] = json!(["restart_required"]);
    assert!(decode_reply(&bytes(&command_result("set_draft_changes", wrong.clone()))).is_err());
    wrong["state"] = json!("invalid");
    wrong["validation"] = json!([{"fieldId":"setting.number","code":"constraint_violation"}]);
    assert!(decode_reply(&bytes(&command_result("set_draft_changes", wrong.clone()))).is_ok());
    let semantics = json!({"hashProfile":"bridge-plan-semantic-json-v1","action":"save_configuration","capture":{"kind":"save_configuration","input":{"draft":wrong,"candidateDigest":digest('d')}},"trustDomain":"configuration","effects":["write_configuration"]});
    assert!(semantic_plan_digest(&serde_json::from_value(semantics).unwrap()).is_err());
}
#[test]
fn decoded_toml_segments_remain_distinct_and_reset_is_first_class() {
    let mut schema = configuration_schema();
    schema["fields"][0]["path"] = json!(["literal.dot"]);
    schema["fields"][0]["aliases"] = json!([["literal", "dot"]]);
    let mut draft = clean_draft();
    draft["schema"] = schema;
    draft["state"] = json!("dirty");
    draft["edits"] = json!([{"kind":"remove_override","fieldId":"setting.boolean"}]);
    draft["apply"] = json!(["immediate"]);
    let decoded = decode_reply(&bytes(&command_result("set_draft_changes", draft))).unwrap();
    let value = serde_json::to_value(decoded.as_inner()).unwrap();
    assert_eq!(
        value["body"]["result"]["command"]["output"]["snapshot"]["schema"]["fields"][0]["path"],
        json!(["literal.dot"])
    );
    let mut duplicate = command(
        "set_draft_changes",
        json!({"draft":draft_ref(),"edits":[{"kind":"remove_override","fieldId":"setting.boolean"},{"kind":"set_public","fieldId":"setting.boolean","value":{"kind":"boolean","value":true}}]}),
    );
    assert!(decode_request(&bytes(&duplicate)).is_err());
    duplicate["body"]["command"]["input"]["edits"][1]["fieldId"] = json!("setting.enum");
    assert!(decode_request(&bytes(&duplicate)).is_ok());
}
#[test]
fn runtime_mutations_bind_release_provider_target_schema_and_managed_bytes() {
    for kind in ["runtime_install", "runtime_update", "runtime_repair"] {
        let input = deploy(kind != "runtime_install");
        assert!(decode_request(&bytes(&prepare(kind, input.clone()))).is_ok());
        assert!(
            decode_reply(&bytes(&command_result(
                "prepare",
                prepared(
                    kind,
                    input,
                    "runtime_distribution",
                    json!(["replace_runtime"])
                )
            )))
            .is_ok()
        );
    }
    assert!(decode_request(&bytes(&prepare("runtime_install", deploy(true)))).is_err());
    assert!(decode_request(&bytes(&prepare("runtime_update", deploy(false)))).is_err());
    let mut wrong = deploy(true);
    wrong["selectedRelease"]["providerId"] = json!("netniv");
    assert!(decode_request(&bytes(&prepare("runtime_update", wrong))).is_err());
    let mut wrong = deploy(true);
    wrong["configuration"]["document"]["target"]["installation"]["physicalId"] =
        json!("other-installation");
    assert!(decode_request(&bytes(&prepare("runtime_update", wrong))).is_err());
    let adopt = json!({"target":target(),"observedArtifactDigest":digest('b'),"recognizedBinding":runtime_binding(),"recognitionAuthority":"reviewed-pair-1","confirmation":"adopt_exact_recognized_artifact"});
    assert!(decode_request(&bytes(&prepare("runtime_adopt", adopt.clone()))).is_ok());
    let mut wrong = adopt;
    wrong["observedArtifactDigest"] = json!(digest('c'));
    assert!(decode_request(&bytes(&prepare("runtime_adopt", wrong))).is_err());
    for kind in ["runtime_remove", "runtime_stop_managing"] {
        assert!(
            decode_request(&bytes(&prepare(
                kind,
                json!({"reference":managed_runtime()})
            )))
            .is_ok()
        );
    }
}
#[test]
fn source_switch_is_explicit_and_does_not_relabel_an_ordinary_update() {
    let mut release = runtime_release();
    release["providerId"] = json!("netniv");
    release["distributionId"] = json!("netniv.stfc-community-mod");
    release["configurationSchema"]["providerId"] = json!("netniv");
    let switch = json!({"current":managed_runtime(),"selectedRelease":release,"configuration":{"kind":"compatible_migration","document":document(false),"destination":release["configurationSchema"]},"confirmation":"switch_runtime_and_configuration_source"});
    assert!(decode_request(&bytes(&prepare("runtime_switch_source", switch.clone()))).is_ok());
    assert!(
        decode_reply(&bytes(&command_result(
            "prepare",
            prepared(
                "runtime_switch_source",
                switch,
                "runtime_distribution",
                json!(["write_configuration", "replace_runtime"])
            )
        )))
        .is_ok()
    );
    let mut update = deploy(true);
    update["selectedRelease"] = release;
    assert!(decode_request(&bytes(&prepare("runtime_update", update))).is_err());
}
#[test]
fn game_and_bridge_references_cannot_cross_authority_domains() {
    for (kind, input, domain, effect) in [
        (
            "game_update",
            json!({"checkedUpdate":game_check()}),
            "game_client",
            "update_game",
        ),
        (
            "recover_game_update",
            json!({"recovery":game_recovery()}),
            "game_client",
            "recover_game",
        ),
        (
            "bridge_update",
            json!({"selectedRelease":bridge_release()}),
            "bridge_application",
            "replace_bridge",
        ),
        (
            "recover_bridge_update",
            json!({"recovery":bridge_recovery()}),
            "bridge_application",
            "recover_bridge",
        ),
    ] {
        assert!(decode_request(&bytes(&prepare(kind, input.clone()))).is_ok());
        assert!(
            decode_reply(&bytes(&command_result(
                "prepare",
                prepared(kind, input, domain, json!([effect]))
            )))
            .is_ok()
        );
    }
    assert!(
        decode_request(&bytes(&prepare(
            "bridge_update",
            json!({"selectedRelease":runtime_release()})
        )))
        .is_err()
    );
    assert!(
        decode_request(&bytes(&prepare(
            "game_update",
            json!({"checkedUpdate":bridge_release()})
        )))
        .is_err()
    );
    let mut release = bridge_release();
    release["offered"]["channelId"] = json!("preview");
    assert!(
        decode_request(&bytes(&prepare(
            "bridge_update",
            json!({"selectedRelease":release})
        )))
        .is_err()
    );
    let mut release = bridge_release();
    release["offered"]["payloads"][1] = release["offered"]["payloads"][0].clone();
    assert!(
        decode_request(&bytes(&prepare(
            "bridge_update",
            json!({"selectedRelease":release})
        )))
        .is_err()
    );
    let mut release = bridge_release();
    release["offered"]["architecture"] = json!("arm64");
    assert!(
        decode_request(&bytes(&prepare(
            "bridge_update",
            json!({"selectedRelease":release})
        )))
        .is_err()
    );
}
#[test]
fn new_changed_outcomes_require_matching_receipts_and_existing_file_backups() {
    let plan = prepared(
        "save_application_preferences",
        prefs(),
        "application_state",
        json!(["save_application_preferences"]),
    );
    let semantics = plan["semantics"].clone();
    let mut operation = json!({"operationId":UUID,"operationRevision":"2","semantics":semantics,"state":{"status":"completed","outcome":{"kind":"changed","reason":"applied"}}});
    assert!(decode_reply(&bytes(&command_result("commit", operation.clone()))).is_err());
    operation["state"]["outcome"]["receipt"] = json!({"kind":"application_preferences_saved","snapshot":{"revision":"prefs-2","values":prefs()["values"]}});
    assert!(decode_reply(&bytes(&command_result("commit", operation.clone()))).is_ok());
    operation["state"]["outcome"]["receipt"]["snapshot"]["values"]["theme"] = json!("dark");
    assert!(decode_reply(&bytes(&command_result("commit", operation))).is_err());
    let mut draft = clean_draft();
    draft["draft"]["document"] = document(true);
    let plan = prepared(
        "save_configuration",
        json!({"draft":draft,"candidateDigest":digest('d')}),
        "configuration",
        json!(["write_configuration"]),
    );
    let mut output = document(true);
    output["revision"] = json!("document-2");
    output["baseline"]["contentDigest"] = json!(digest('d'));
    let mut operation = json!({"operationId":UUID,"operationRevision":"2","semantics":plan["semantics"],"state":{"status":"completed","outcome":{"kind":"changed","reason":"applied","receipt":{"kind":"configuration_written","document":output}}}});
    assert!(decode_reply(&bytes(&command_result("commit", operation.clone()))).is_err());
    operation["state"]["outcome"]["receipt"]["backup"] = json!({"backupId":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb","document":document(true),"retainedDigest":digest('c'),"nativeBackupRef":"native-backup-1","createdAt":"2026-10-03T00:00:00Z"});
    assert!(decode_reply(&bytes(&command_result("commit", operation))).is_ok());
}
#[test]
fn inventory_unknowns_and_session_readiness_do_not_become_successful_empty_defaults() {
    let snapshot = json!({"cursor":{"hostEpoch":HOST,"streamId":UUID,"sequence":"0"},"preferences":unknown(),"profiles":unknown(),"installations":observed(inventory(json!([]))),"sessions":unknown(),"operations":inventory(json!([])),"capabilities":inventory(json!([]))});
    assert!(decode_reply(&bytes(&query_result("snapshot", snapshot.clone()))).is_ok());
    let mut wrong = snapshot;
    wrong["installations"]["value"]["completeness"] = json!("partial");
    assert!(decode_reply(&bytes(&query_result("snapshot", wrong))).is_err());
    let actions = json!([{"action":"game_update","availability":{"status":"unavailable","reason":{"code":"unavailable_native_route"}}},{"action":"launch_ordinary","availability":{"status":"unknown","reason":{"code":"unknown_identity"}}}]);
    assert!(decode_reply(&bytes(&query_result("get_actions", actions))).is_ok());
    assert!(
        decode_reply(&bytes(&query_result(
            "get_actions",
            json!([{"action":"launch_ordinary","availability":{"status":"blocked","reasons":[]}}])
        )))
        .is_err()
    );
}
#[test]
fn preview_digest_disclosure_and_export_host_are_bound() {
    let content = diagnostic_content();
    let reference = preview();
    assert!(
        decode_reply(&bytes(&query_result(
            "diagnostic_preview",
            json!({"reference":reference,"content":content})
        )))
        .is_ok()
    );
    let mut wrong = diagnostic_content();
    wrong["paths"] =
        json!({"installation":{"platform":"windows","value":"C:\\Synthetic\\Private"}});
    assert!(diagnostic_preview_digest(&serde_json::from_value(wrong.clone()).unwrap()).is_err());
    wrong["disclosure"] = json!("include_paths");
    assert!(diagnostic_preview_digest(&serde_json::from_value(wrong).unwrap()).is_ok());
    assert!(decode_request(&bytes(&prepare("export_diagnostics", export()))).is_ok());
    let mut wrong = export();
    wrong["destination"]["hostEpoch"] = json!(UUID);
    assert!(decode_request(&bytes(&prepare("export_diagnostics", wrong))).is_err());
    let plan = prepared(
        "export_diagnostics",
        export(),
        "diagnostics",
        json!(["export_diagnostics"]),
    );
    assert!(decode_reply(&bytes(&command_result("prepare", plan))).is_ok());
}
#[test]
fn every_extended_query_and_memory_command_has_a_closed_shape() {
    let queries = vec![
        query("snapshot", json!({})),
        query("list_profiles", json!({"state":"all"})),
        query("list_installations", json!({})),
        query("list_sessions", json!({})),
        query("list_import_sources", json!({})),
        query(
            "get_actions",
            json!({"scope":{"kind":"target","target":target()},"actions":["runtime_update","game_update"]}),
        ),
        query(
            "read_configuration",
            json!({"target":{"installation":{"kind":"registered","id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"profile":{"kind":"ordinary"}}}),
        ),
        query("configuration_history", json!({"document":document(true)})),
        query(
            "check_runtime_release",
            json!({"target":target(),"providerId":"guffawaffle","channelId":"stable"}),
        ),
        query(
            "check_game_update",
            json!({"installation":{"kind":"registered","id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}),
        ),
        query("check_bridge_update", json!({"application":app()})),
        query(
            "resume_events",
            json!({"after":{"hostEpoch":HOST,"streamId":UUID,"sequence":"0"},"maximumEvents":"128"}),
        ),
        query(
            "diagnostic_preview",
            json!({"target":{"installation":{"kind":"registered","id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"profile":{"kind":"ordinary"}},"disclosure":"redacted"}),
        ),
    ];
    for value in queries {
        assert!(decode_request(&bytes(&value)).is_ok());
    }
    for value in [
        command("open_draft", json!({"document":document(false)})),
        command("discard_draft", json!({"draft":draft_ref()})),
        command(
            "request_import_discovery",
            json!({"expectedDestinationOwner":"owner-1","approval":"request_native_approval"}),
        ),
    ] {
        assert!(decode_request(&bytes(&value)).is_ok());
    }
    assert!(decode_reply(&bytes(&command_result("open_draft", clean_draft()))).is_ok());
    assert!(decode_reply(&bytes(&query_result("check_game_update", unknown()))).is_ok());
    assert!(
        decode_reply(&bytes(&query_result(
            "check_runtime_release",
            observed(runtime_release())
        )))
        .is_ok()
    );
    assert!(
        decode_reply(&bytes(&query_result(
            "check_bridge_update",
            observed(bridge_release())
        )))
        .is_ok()
    );
}
#[test]
fn resumed_events_are_contiguous_in_exact_stream_and_epoch() {
    let after = json!({"hostEpoch":HOST,"streamId":UUID,"sequence":"0"});
    let event = json!({"protocolVersion":1,"cursor":{"hostEpoch":HOST,"streamId":UUID,"sequence":"1"},"body":{"type":"snapshot_invalidated","reason":"catalog_changed"}});
    let valid = json!({"after":after,"events":[event],"next":event["cursor"]});
    assert!(decode_reply(&bytes(&query_result("resume_events", valid.clone()))).is_ok());
    for (field, value) in [
        ("sequence", json!("2")),
        ("hostEpoch", json!(UUID)),
        ("streamId", json!(HOST)),
    ] {
        let mut wrong = valid.clone();
        wrong["events"][0]["cursor"][field] = value;
        assert!(decode_reply(&bytes(&query_result("resume_events", wrong))).is_err());
    }
}
#[test]
fn canonical_signed_and_fractional_strings_match_derived_schema() {
    fn accepts<T: JsonSchema>(value: Value) -> bool {
        let schema = SchemaSettings::draft07()
            .for_deserialize()
            .into_generator()
            .into_root_schema_for::<T>();
        jsonschema::options()
            .with_draft(jsonschema::Draft::Draft7)
            .offline()
            .with_pattern_options(jsonschema::PatternOptions::regex())
            .build(schema.as_value())
            .unwrap()
            .is_valid(&value)
    }
    for v in ["-9223372036854775808", "9223372036854775807", "0", "-1"] {
        assert!(serde_json::from_value::<SignedInteger>(json!(v)).is_ok());
        assert!(accepts::<SignedInteger>(json!(v)));
    }
    for v in [
        "9223372036854775808",
        "-9223372036854775809",
        "-0",
        "00",
        "1\n",
    ] {
        assert!(serde_json::from_value::<SignedInteger>(json!(v)).is_err());
        assert!(!accepts::<SignedInteger>(json!(v)));
    }
    for v in [
        "0",
        "-2.5",
        "0.00000000000000000000000000000001",
        "99999999999999999999999999999999",
    ] {
        assert!(DecimalValue::new(v).is_ok());
        assert!(accepts::<DecimalValue>(json!(v)));
    }
    for v in ["-0", "00", "1.0", "1e2", "-00.1", "1.2\n"] {
        assert!(DecimalValue::new(v).is_err());
        assert!(!accepts::<DecimalValue>(json!(v)));
    }
    assert_eq!(
        DecimalValue::new("-2.5")
            .unwrap()
            .compare(&DecimalValue::new("-2.05").unwrap()),
        std::cmp::Ordering::Less
    );
    assert!(accepts::<DistributionId>(json!(
        "guffawaffle.stfc-community-mod"
    )));
}
#[test]
fn new_objects_reject_array_forms_and_scoped_revision_errors_do_not_cross_resources() {
    let mut array = command("open_draft", json!({"document":document(false)}));
    array["body"]["command"]["input"]["document"] =
        json!([UUID,target(),"document-1",{"kind":"missing"},schema_binding()]);
    assert!(decode_request(&bytes(&array)).is_err());
    let error = json!({"code":"stale_revision","retryDisposition":"after_resnapshot","violations":[],"expectedRevision":{"scope":{"kind":"document","id":UUID,"target":target(),"schemaDigest":digest('a')},"revision":"document-1"},"observedRevision":{"scope":{"kind":"document","id":UUID,"target":target(),"schemaDigest":digest('a')},"revision":"document-2"}});
    let valid =
        json!({"protocolVersion":1,"requestId":UUID,"body":{"type":"rejected","error":error}});
    assert!(decode_reply(&bytes(&valid)).is_ok());
    let mut wrong = valid;
    wrong["body"]["error"]["observedRevision"]["scope"]["target"]["installation"]["physicalId"] =
        json!("other-target");
    assert!(decode_reply(&bytes(&wrong)).is_err());
}

#[test]
fn invalid_drafts_keep_public_type_errors_without_exposing_private_or_secret_values() {
    let mut invalid = clean_draft();
    invalid["state"] = json!("invalid");
    invalid["edits"] = json!([{"kind":"set_public","fieldId":"setting.boolean","value":{"kind":"string","value":"invalid public value"}}]);
    invalid["apply"] = json!(["immediate"]);
    invalid["validation"] = json!([{"fieldId":"setting.boolean","code":"invalid_type"}]);
    assert!(
        decode_reply(&bytes(&command_result(
            "set_draft_changes",
            invalid.clone()
        )))
        .is_ok()
    );
    for field in ["sync.token", "sync.endpoint", "unknown.field"] {
        let mut wrong = invalid.clone();
        wrong["edits"][0]["fieldId"] = json!(field);
        wrong["edits"][0]["value"]["value"] = json!("SYNTHETIC_PRIVATE_SENTINEL");
        wrong["apply"] = if field == "unknown.field" {
            json!([])
        } else {
            json!(["next_launch"])
        };
        wrong["validation"][0]["fieldId"] = json!(field);
        let error = decode_reply(&bytes(&command_result("set_draft_changes", wrong))).unwrap_err();
        assert!(
            !serde_json::to_string(&error)
                .unwrap()
                .contains("SYNTHETIC_PRIVATE")
        );
    }
}

#[test]
fn canonical_paths_and_aliases_have_one_schema_owner() {
    let valid = command_result("open_draft", clean_draft());
    for collision in [json!(["setting", "integer"]), json!(["setting", "boolean"])] {
        let mut wrong = valid.clone();
        wrong["body"]["result"]["command"]["output"]["schema"]["fields"][0]["aliases"] =
            json!([collision]);
        assert!(decode_reply(&bytes(&wrong)).is_err());
    }
    let mut wrong = valid;
    let schema = &mut wrong["body"]["result"]["command"]["output"]["schema"];
    schema["fields"][0]["aliases"] = json!([["legacy", "shared"]]);
    schema["fields"][1]["aliases"] = json!([["legacy", "shared"]]);
    assert!(decode_reply(&bytes(&wrong)).is_err());
}

#[test]
fn installation_edit_and_registration_receipts_preserve_immutable_identity() {
    let plan = prepared(
        "edit_installation",
        json!({"installation":installation(),"name":"Renamed"}),
        "profile_state",
        json!(["edit_installation_metadata"]),
    );
    let mut updated = installation();
    updated["registrationRevision"] = json!("installation-2");
    let valid = completed(
        &plan,
        json!({"kind":"installation_edited","installation":updated,"name":"Renamed"}),
    );
    assert!(decode_reply(&bytes(&valid)).is_ok());
    for (field, value) in [
        ("registrationId", json!("dddddddddddddddddddddddddddddddd")),
        ("physicalId", json!("other-folder")),
        ("nativeTargetRef", json!("other-native-target")),
    ] {
        let mut wrong = valid.clone();
        wrong["body"]["result"]["command"]["output"]["state"]["outcome"]["receipt"]["installation"]
            [field] = value;
        assert!(decode_reply(&bytes(&wrong)).is_err());
    }
    let mut wrong = valid;
    wrong["body"]["result"]["command"]["output"]["state"]["outcome"]["receipt"]["installation"] =
        json!({"kind":"directory","physicalId":"physical-1","nativeTargetRef":"target-1"});
    assert!(decode_reply(&bytes(&wrong)).is_err());
}

#[test]
fn bridge_recovery_requires_exact_expected_channel_platform_and_payload_pairing() {
    let plan = prepared(
        "recover_bridge_update",
        json!({"recovery":bridge_recovery()}),
        "bridge_application",
        json!(["recover_bridge"]),
    );
    let valid = completed(&plan, json!({"kind":"bridge_updated","application":app()}));
    assert!(decode_reply(&bytes(&valid)).is_ok());
    for (field, value) in [
        ("channelId", json!("preview")),
        ("pairingDigest", json!(digest('8'))),
        ("packageDigest", json!(digest('9'))),
        ("revision", json!("app-9")),
    ] {
        let mut wrong = valid.clone();
        wrong["body"]["result"]["command"]["output"]["state"]["outcome"]["receipt"]["application"]
            [field] = value;
        assert!(decode_reply(&bytes(&wrong)).is_err());
    }
    let mut wrong = valid;
    let application = &mut wrong["body"]["result"]["command"]["output"]["state"]["outcome"]["receipt"]
        ["application"];
    application["platform"] = json!("macos");
    application["architecture"] = json!("arm64");
    assert!(decode_reply(&bytes(&wrong)).is_err());
    let mut wrong = bridge_recovery();
    wrong["expectedApplication"]["channelId"] = json!("preview");
    assert!(
        decode_request(&bytes(&prepare(
            "recover_bridge_update",
            json!({"recovery":wrong})
        )))
        .is_err()
    );
}

#[test]
fn prepared_ephemeral_references_belong_to_the_preparing_host() {
    let cases = [
        prepared(
            "save_configuration",
            json!({"draft":clean_draft(),"candidateDigest":digest('d')}),
            "configuration",
            json!(["write_configuration"]),
        ),
        prepared(
            "runtime_update",
            deploy(true),
            "runtime_distribution",
            json!(["replace_runtime"]),
        ),
        prepared(
            "game_update",
            json!({"checkedUpdate":game_check()}),
            "game_client",
            json!(["update_game"]),
        ),
        prepared(
            "bridge_update",
            json!({"selectedRelease":bridge_release()}),
            "bridge_application",
            json!(["replace_bridge"]),
        ),
        prepared(
            "export_diagnostics",
            export(),
            "diagnostics",
            json!(["export_diagnostics"]),
        ),
    ];
    for mut plan in cases {
        assert!(decode_reply(&bytes(&command_result("prepare", plan.clone()))).is_ok());
        plan["planRef"]["hostEpoch"] = json!(UUID);
        assert!(decode_reply(&bytes(&command_result("prepare", plan))).is_err());
    }
    let valid = json!({"protocolVersion":1,"cursor":{"hostEpoch":HOST,"streamId":UUID,"sequence":"1"},"body":{"type":"draft_changed","draft":clean_draft()}});
    assert!(decode_event(&bytes(&valid)).is_ok());
    let mut wrong = valid;
    wrong["cursor"]["hostEpoch"] = json!(UUID);
    assert!(decode_event(&bytes(&wrong)).is_err());
    // Durable operations may replay admitted captures from a previous epoch.
    let plan = prepared(
        "save_application_preferences",
        prefs(),
        "application_state",
        json!(["save_application_preferences"]),
    );
    let operation = completed(
        &plan,
        json!({"kind":"application_preferences_saved","snapshot":{"revision":"prefs-2","values":prefs()["values"]}}),
    );
    let event = json!({"protocolVersion":1,"cursor":{"hostEpoch":UUID,"streamId":UUID,"sequence":"1"},"body":{"type":"operation_changed","operation":operation["body"]["result"]["command"]["output"]}});
    assert!(decode_event(&bytes(&event)).is_ok());
}

#[test]
fn runtime_receipts_bind_every_prepared_participant_and_selected_manifest() {
    let mut input = deploy(true);
    input["selectedRelease"]["artifacts"].as_array_mut().unwrap().push(json!({"role":"runtime_manifest","platform":"windows","architecture":"x86_64","digest":digest('e'),"size":"128"}));
    let plan = prepared(
        "runtime_update",
        input,
        "runtime_distribution",
        json!(["replace_runtime"]),
    );
    let mut reference = managed_runtime();
    reference["binding"]["manifest"] = json!({"status":"observed","digest":digest('e')});
    let valid = completed(
        &plan,
        json!({"kind":"runtime_managed","reference":reference,"configuration":{"kind":"unchanged","document":document(false)}}),
    );
    assert!(decode_reply(&bytes(&valid)).is_ok());
    for (field, value) in [
        ("clientRevision", json!("client-2")),
        ("architecture", json!("arm64")),
        ("manifest", json!({"status":"missing"})),
        (
            "manifest",
            json!({"status":"observed","digest":digest('f')}),
        ),
    ] {
        let mut wrong = valid.clone();
        wrong["body"]["result"]["command"]["output"]["state"]["outcome"]["receipt"]["reference"]
            ["binding"][field] = value;
        assert!(decode_reply(&bytes(&wrong)).is_err());
    }
    let mut wrong = valid.clone();
    wrong["body"]["result"]["command"]["output"]["state"]["outcome"]["receipt"]
        .as_object_mut()
        .unwrap()
        .remove("configuration");
    assert!(decode_reply(&bytes(&wrong)).is_err());
    let mut wrong = valid;
    wrong["body"]["result"]["command"]["output"]["state"]["outcome"]["receipt"]["configuration"]
        ["document"]["revision"] = json!("other-document");
    assert!(decode_reply(&bytes(&wrong)).is_err());

    let mut release = runtime_release();
    release["providerId"] = json!("netniv");
    release["distributionId"] = json!("netniv.stfc-community-mod");
    release["configurationSchema"]["providerId"] = json!("netniv");
    let input = json!({"current":managed_runtime(),"selectedRelease":release,"configuration":{"kind":"compatible_migration","document":document(true),"destination":release["configurationSchema"]},"confirmation":"switch_runtime_and_configuration_source"});
    let plan = prepared(
        "runtime_switch_source",
        input,
        "runtime_distribution",
        json!(["replace_runtime", "write_configuration"]),
    );
    let mut reference = managed_runtime();
    reference["binding"]["providerId"] = json!("netniv");
    reference["binding"]["distributionId"] = json!("netniv.stfc-community-mod");
    let mut written = document(true);
    written["schema"] = release["configurationSchema"].clone();
    written["revision"] = json!("document-2");
    written["baseline"]["contentDigest"] = json!(digest('d'));
    let valid = completed(
        &plan,
        json!({"kind":"runtime_managed","reference":reference,"configuration":{"kind":"written","document":written,"backup":backup()}}),
    );
    assert!(decode_reply(&bytes(&valid)).is_ok());
    for replacement in [
        json!({"kind":"unchanged","document":document(true)}),
        json!({"kind":"written","document":written}),
    ] {
        let mut wrong = valid.clone();
        wrong["body"]["result"]["command"]["output"]["state"]["outcome"]["receipt"]["configuration"] =
            replacement;
        assert!(decode_reply(&bytes(&wrong)).is_err());
    }
    let mut wrong = valid;
    wrong["body"]["result"]["command"]["output"]["state"]["outcome"]["receipt"]["configuration"]
        ["document"]["baseline"]["contentDigest"] = json!(digest('c'));
    assert!(decode_reply(&bytes(&wrong)).is_err());
}

#[test]
fn restore_binds_retained_bytes_but_excludes_descriptive_backup_timestamp_from_plan_hash() {
    let input = json!({"document":document(true),"backup":backup()});
    assert!(decode_request(&bytes(&prepare("restore_configuration", input.clone()))).is_ok());
    let plan = prepared(
        "restore_configuration",
        input.clone(),
        "configuration",
        json!(["write_configuration"]),
    );
    assert!(decode_reply(&bytes(&command_result("prepare", plan.clone()))).is_ok());
    let first = serde_json::from_value::<PlanSemantics>(plan["semantics"].clone()).unwrap();
    let mut changed = plan["semantics"].clone();
    changed["capture"]["input"]["backup"]["createdAt"] = json!("2026-10-02T23:59:59Z");
    let second = serde_json::from_value::<PlanSemantics>(changed.clone()).unwrap();
    assert_eq!(
        semantic_plan_digest(&first).unwrap(),
        semantic_plan_digest(&second).unwrap()
    );
    changed["capture"]["input"]["backup"]["retainedDigest"] = json!(digest('f'));
    assert!(semantic_plan_digest(&serde_json::from_value(changed).unwrap()).is_err());
    let mut wrong = input;
    wrong["backup"]["retainedDigest"] = json!(digest('d'));
    assert!(decode_request(&bytes(&prepare("restore_configuration", wrong))).is_err());
    let mut written = document(true);
    written["revision"] = json!("document-2");
    let valid = completed(
        &plan,
        json!({"kind":"configuration_written","document":written,"backup":backup()}),
    );
    assert!(decode_reply(&bytes(&valid)).is_ok());
    let mut wrong = valid;
    wrong["body"]["result"]["command"]["output"]["state"]["outcome"]["receipt"]["document"]["baseline"]
        ["contentDigest"] = json!(digest('d'));
    assert!(decode_reply(&bytes(&wrong)).is_err());
}

#[test]
fn new_sync_destinations_capture_desired_feed_choices_and_scoped_opaque_custody() {
    let reference = json!({"valueId":"dddddddd-dddd-4ddd-8ddd-dddddddddddd","document":document(false),"fieldId":"sync.endpoint","revision":"private-1"});
    let secret = json!({"secretId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","draft":draft_ref(),"fieldId":"sync.token"});
    let destination = json!({"id":"service-1","mode":"legacy","endpoint":reference,"secret":secret,"proxy":{"kind":"none"},"feeds":[{"feedId":"battlelog","desired":"off"}]});
    let valid = command(
        "set_draft_changes",
        json!({"draft":draft_ref(),"edits":[{"kind":"add_sync_destination","destination":destination}]}),
    );
    assert!(decode_request(&bytes(&valid)).is_ok());
    let mut draft = clean_draft();
    draft["state"] = json!("dirty");
    draft["edits"] = valid["body"]["command"]["input"]["edits"].clone();
    draft["apply"] = json!(["next_launch"]);
    assert!(decode_reply(&bytes(&command_result("set_draft_changes", draft))).is_ok());
    for (field, value) in [
        ("id", json!("local-sidecar")),
        (
            "secret",
            json!({"secretId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","draft":{"draftId":UUID,"hostEpoch":HOST,"revision":"1","document":document(false)},"fieldId":"sync.token"}),
        ),
    ] {
        let mut wrong = valid.clone();
        wrong["body"]["command"]["input"]["edits"][0]["destination"][field] = value;
        assert!(decode_request(&bytes(&wrong)).is_err());
    }
    let mut wrong = valid;
    wrong["body"]["command"]["input"]["edits"][0]["destination"]["feeds"][0]["resolved"] =
        observed(json!(true));
    assert!(decode_request(&bytes(&wrong)).is_err());
}

#[test]
fn saved_preferences_require_registered_targets_and_receipts_preserve_exact_choices() {
    let registered = json!({"kind":"registered","id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"});
    let create = json!({"name":"Synthetic profile","setup":{"kind":"new"},"preferredInstallation":registered,"expectedCatalogRevision":"catalog-1"});
    assert!(decode_request(&bytes(&prepare("create_profile", create.clone()))).is_ok());
    let mut wrong = create;
    wrong["preferredInstallation"] = json!({"kind":"directory","directory":{"platform":"windows","value":"C:\\Synthetic\\Game"}});
    assert!(decode_request(&bytes(&prepare("create_profile", wrong))).is_err());
    let capture = json!({"name":"Synthetic profile","setup":{"kind":"new"},"preferredInstallation":installation(),"catalogRevision":"catalog-1","nativePreparationRef":"create-1","destinationOwner":"owner-1"});
    let plan = prepared(
        "create_profile",
        capture,
        "profile_state",
        json!(["publish_profile"]),
    );
    let projection = json!({"kind":"isolated","reference":profile("active"),"name":"Synthetic profile","preferredInstallation":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","store":unknown()});
    let valid = completed(
        &plan,
        json!({"kind":"profile_published","profile":projection}),
    );
    assert!(decode_reply(&bytes(&valid)).is_ok());
    let mut wrong = valid;
    wrong["body"]["result"]["command"]["output"]["state"]["outcome"]["receipt"]["profile"]
        .as_object_mut()
        .unwrap()
        .remove("preferredInstallation");
    assert!(decode_reply(&bytes(&wrong)).is_err());

    let ordinary = json!({"kind":"ordinary","catalogId":"cccccccccccccccccccccccccccccccc","revision":"ordinary-1","ownerScope":"owner-1"});
    for (choice, preference) in [
        (json!({"kind":"clear"}), None),
        (
            json!({"kind":"set","installation":installation()}),
            Some("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        ),
        (
            json!({"kind":"keep","expected":{"kind":"registered","id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}),
            Some("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        ),
        (json!({"kind":"keep","expected":{"kind":"none"}}), None),
    ] {
        let plan = prepared(
            "edit_ordinary_profile",
            json!({"profile":ordinary,"preferredInstallation":choice}),
            "profile_state",
            json!(["edit_profile_metadata"]),
        );
        let mut projection = json!({"kind":"ordinary","reference":ordinary});
        if let Some(id) = preference {
            projection["preferredInstallation"] = json!(id);
        }
        let valid = completed(&plan, json!({"kind":"profile_edited","profile":projection}));
        assert!(decode_reply(&bytes(&valid)).is_ok());
        let mut wrong = valid;
        wrong["body"]["result"]["command"]["output"]["state"]["outcome"]["receipt"]["profile"]["preferredInstallation"] =
            json!("dddddddddddddddddddddddddddddddd");
        assert!(decode_reply(&bytes(&wrong)).is_err());
    }
    let wrong = json!({"profile":ordinary,"preferredInstallation":{"kind":"set","installation":{"kind":"directory","physicalId":"physical-1","nativeTargetRef":"target-1"}}});
    assert!(decode_request(&bytes(&prepare("edit_ordinary_profile", wrong))).is_err());
    let wrong = json!({"profile":profile("active"),"name":"Renamed","preferredInstallation":{"kind":"keep"}});
    assert!(decode_request(&bytes(&prepare("edit_isolated_profile", wrong))).is_err());
}

#[test]
fn installation_registration_receipts_bind_name_and_native_target() {
    let plan = prepared(
        "register_installation",
        json!({"name":"Synthetic installation","physicalId":"physical-1","nativeTargetRef":"target-1","catalogRevision":"catalog-1"}),
        "profile_state",
        json!(["register_installation"]),
    );
    let valid = completed(
        &plan,
        json!({"kind":"installation_registered","installation":installation(),"name":"Synthetic installation"}),
    );
    assert!(decode_reply(&bytes(&valid)).is_ok());
    for (field, value) in [
        ("name", json!("Other installation")),
        (
            "installation",
            json!({"kind":"registered","registrationId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","registrationRevision":"installation-1","physicalId":"physical-1","nativeTargetRef":"other-native-target"}),
        ),
    ] {
        let mut wrong = valid.clone();
        wrong["body"]["result"]["command"]["output"]["state"]["outcome"]["receipt"][field] = value;
        assert!(decode_reply(&bytes(&wrong)).is_err());
    }
    let edit = prepared(
        "edit_installation",
        json!({"installation":installation(),"name":"Renamed"}),
        "profile_state",
        json!(["edit_installation_metadata"]),
    );
    let wrong = completed(
        &edit,
        json!({"kind":"installation_edited","installation":installation(),"name":"Original"}),
    );
    assert!(decode_reply(&bytes(&wrong)).is_err());
}

#[test]
fn native_game_recovery_restores_exact_prior_client_in_exact_installation() {
    let plan = prepared(
        "game_update",
        json!({"checkedUpdate":game_check()}),
        "game_client",
        json!(["update_game"]),
    );
    let valid = command_result(
        "commit",
        json!({"operationId":UUID,"operationRevision":"2","semantics":plan["semantics"],"state":{"status":"recovery_required","reason":"interrupted_transaction","recovery":{"operationId":UUID,"transaction":"game-journal-1","target":{"kind":"game","recovery":game_recovery()}}}}),
    );
    assert!(decode_reply(&bytes(&valid)).is_ok());
    for (field, value) in [
        ("expectedClient", client("271")),
        (
            "installation",
            json!({"kind":"registered","registrationId":"dddddddddddddddddddddddddddddddd","registrationRevision":"installation-1","physicalId":"physical-1","nativeTargetRef":"target-1"}),
        ),
    ] {
        let mut wrong = valid.clone();
        wrong["body"]["result"]["command"]["output"]["state"]["recovery"]["target"]["recovery"]
            [field] = value;
        assert!(decode_reply(&bytes(&wrong)).is_err());
    }
    let mut wrong = valid;
    wrong["body"]["result"]["command"]["output"]["semantics"]["capture"]["input"]["checkedUpdate"]
        ["route"] = json!("canonical_managed_handoff");
    assert!(decode_reply(&bytes(&wrong)).is_err());
}

#[test]
fn protected_input_port_mints_only_opaque_references_for_exact_draft_and_field() {
    for sensitivity in ["private", "secret"] {
        let field = if sensitivity == "private" {
            "sync.endpoint"
        } else {
            "sync.token"
        };
        let binding = json!({"draft":draft_ref(),"fieldId":field,"sensitivity":sensitivity});
        let request = command("request_sensitive_input", binding.clone());
        assert!(decode_request(&bytes(&request)).is_ok());
        let reference = if sensitivity == "private" {
            json!({"valueId":"dddddddd-dddd-4ddd-8ddd-dddddddddddd","document":document(false),"fieldId":field,"revision":"private-1","capturedFor":draft_ref()})
        } else {
            json!({"secretId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","draft":draft_ref(),"fieldId":field})
        };
        let valid = command_result(
            "request_sensitive_input",
            json!({"binding":binding,"outcome":{"status":format!("captured_{sensitivity}"),"reference":reference}}),
        );
        assert!(decode_reply(&bytes(&valid)).is_ok());
        for outcome in [
            json!({"status":"cancelled"}),
            json!({"status":"unavailable","reason":"protected_entry_unavailable"}),
        ] {
            assert!(
                decode_reply(&bytes(&command_result(
                    "request_sensitive_input",
                    json!({"binding":binding,"outcome":outcome})
                )))
                .is_ok()
            );
        }
        for (field, value) in [
            ("fieldId", json!("other.field")),
            (
                "draft",
                json!({"draftId":UUID,"hostEpoch":HOST,"revision":"1","document":document(false)}),
            ),
            (
                "sensitivity",
                json!(if sensitivity == "private" {
                    "secret"
                } else {
                    "private"
                }),
            ),
        ] {
            let mut wrong = valid.clone();
            wrong["body"]["result"]["command"]["output"]["binding"][field] = value;
            assert!(decode_reply(&bytes(&wrong)).is_err());
        }
        let mut wrong = valid;
        wrong["body"]["result"]["command"]["output"]["binding"]["draft"]["hostEpoch"] = json!(UUID);
        assert!(decode_reply(&bytes(&wrong)).is_err());
        let mut wrong = request;
        wrong["body"]["command"]["input"]["value"] = json!("SYNTHETIC_SECRET_SENTINEL");
        let failure = decode_request(&bytes(&wrong)).unwrap_err();
        assert!(
            !serde_json::to_string(&failure)
                .unwrap()
                .contains("SYNTHETIC_SECRET")
        );
    }
}

#[test]
fn sync_roles_bind_declared_schema_fields_with_unique_sensitive_purposes() {
    let mut baseline = clean_draft();
    baseline["schema"]["fields"]
        .as_array_mut()
        .unwrap()
        .push(field(
            "sync.proxy",
            json!({"kind":"string","maximumLength":"4096"}),
            "private",
            None,
            "next_launch",
        ));
    baseline["schema"]["sync"][0]["fields"]
        .as_array_mut()
        .unwrap()
        .push(json!("sync.proxy"));
    baseline["schema"]["sync"][0]["proxyFieldId"] = json!("sync.proxy");
    assert!(decode_reply(&bytes(&command_result("open_draft", baseline.clone()))).is_ok());
    for (role, value) in [
        ("endpointFieldId", "sync.token"),
        ("secretFieldId", "sync.endpoint"),
        ("proxyFieldId", "sync.endpoint"),
        ("endpointFieldId", "foreign.field"),
    ] {
        let mut wrong = baseline.clone();
        wrong["schema"]["sync"][0][role] = json!(value);
        assert!(
            decode_reply(&bytes(&command_result("open_draft", wrong))).is_err(),
            "{role}:{value}"
        );
    }
    let mut wrong = baseline.clone();
    wrong["schema"]["sync"][0]["fields"] = json!(["sync.endpoint", "sync.proxy"]);
    assert!(decode_reply(&bytes(&command_result("open_draft", wrong))).is_err());
    let mut wrong = baseline.clone();
    wrong["schema"]["sync"][0]["fields"]
        .as_array_mut()
        .unwrap()
        .push(json!("foreign.field"));
    assert!(decode_reply(&bytes(&command_result("open_draft", wrong))).is_err());
    let mut wrong = baseline;
    wrong["schema"]["fields"][7]["sensitivity"] = json!("public");
    assert!(decode_reply(&bytes(&command_result("open_draft", wrong))).is_err());
}

#[test]
fn authoritative_sync_drafts_require_producer_creation_feed_and_sensitive_field_contracts() {
    let reference = json!({"valueId":"dddddddd-dddd-4ddd-8ddd-dddddddddddd","document":document(false),"fieldId":"sync.endpoint","revision":"private-1"});
    let secret = json!({"secretId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","draft":draft_ref(),"fieldId":"sync.token"});
    let mut draft = clean_draft();
    draft["state"] = json!("dirty");
    draft["apply"] = json!(["next_launch"]);
    draft["edits"] = json!([{"kind":"add_sync_destination","destination":{"id":"service-1","mode":"legacy","endpoint":reference,"secret":secret,"proxy":{"kind":"none"},"feeds":[{"feedId":"battlelog","desired":"on"}]}}]);
    assert!(decode_reply(&bytes(&command_result("set_draft_changes", draft.clone()))).is_ok());
    for mode in [0, 1, 2, 3, 4] {
        let mut wrong = draft.clone();
        match mode {
            0 => wrong["schema"]["sync"] = json!([]),
            1 => wrong["schema"]["sync"][0]["exposure"] = json!("existing_configuration_only"),
            2 => wrong["edits"][0]["destination"]["feeds"][0]["feedId"] = json!("unsupported-feed"),
            3 => wrong["edits"][0]["destination"]["mode"] = json!("sidecar"),
            _ => wrong["edits"][0]["destination"]["mode"] = json!("majel"),
        }
        assert!(decode_reply(&bytes(&command_result("set_draft_changes", wrong.clone()))).is_err());
        wrong["state"] = json!("invalid");
        wrong["validation"] = json!([{"code":"unsupported_sync_field"}]);
        assert!(decode_reply(&bytes(&command_result("set_draft_changes", wrong))).is_ok());
    }
    for kind in ["endpoint", "secret"] {
        let mut wrong = draft.clone();
        wrong["edits"][0]["destination"][kind]["fieldId"] = json!("setting.boolean");
        wrong["state"] = json!("invalid");
        wrong["validation"] = json!([{"code":"private_value_required"}]);
        assert!(decode_reply(&bytes(&command_result("set_draft_changes", wrong))).is_err());
    }
}

#[test]
fn export_destination_port_returns_scoped_custody_without_path_payloads() {
    let binding = json!({"preview":preview()});
    let request = command("request_export_destination", binding.clone());
    assert!(decode_request(&bytes(&request)).is_ok());
    let valid = command_result(
        "request_export_destination",
        json!({"binding":binding,"outcome":{"status":"captured","destination":export()["destination"]}}),
    );
    assert!(decode_reply(&bytes(&valid)).is_ok());
    for outcome in [
        json!({"status":"cancelled"}),
        json!({"status":"unavailable","reason":"selection_unavailable"}),
        json!({"status":"unavailable","reason":"unsupported_platform"}),
    ] {
        assert!(
            decode_reply(&bytes(&command_result(
                "request_export_destination",
                json!({"binding":binding,"outcome":outcome})
            )))
            .is_ok()
        );
    }
    let mut wrong = valid.clone();
    wrong["body"]["result"]["command"]["output"]["outcome"]["destination"]["hostEpoch"] =
        json!(UUID);
    assert!(decode_reply(&bytes(&wrong)).is_err());
    let mut wrong = valid;
    wrong["body"]["result"]["command"]["output"]["outcome"]["destination"]["path"] =
        json!("C:\\Synthetic\\Private-export.json");
    let failure = decode_reply(&bytes(&wrong)).unwrap_err();
    assert!(
        !serde_json::to_string(&failure)
            .unwrap()
            .contains("Private-export")
    );
    let mut wrong = request;
    wrong["body"]["command"]["input"]["path"] = json!("C:\\Synthetic\\Private-export.json");
    assert!(decode_request(&bytes(&wrong)).is_err());
}

#[test]
fn first_use_store_modes_do_not_create_new_profile_identity_from_client_setup_refs() {
    let create = json!({"name":"Synthetic profile","setup":{"kind":"new"},"preferredInstallation":{"kind":"registered","id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"expectedCatalogRevision":"catalog-1"});
    assert!(decode_request(&bytes(&prepare("create_profile", create.clone()))).is_ok());
    for kind in ["resume", "existing"] {
        let mut wrong = create.clone();
        wrong["setup"] = json!({"kind":kind,"nativeSetupRef":"client-invented-setup-ref"});
        assert!(decode_request(&bytes(&prepare("create_profile", wrong))).is_err());
    }
    let target = json!({"installation":{"kind":"registered","id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"profile":{"kind":"isolated","id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}});
    for mode in ["new", "resume", "existing"] {
        assert!(
            decode_request(&bytes(&prepare(
                "launch_isolated",
                json!({"target":target,"storeMode":mode})
            )))
            .is_ok()
        );
    }
}
