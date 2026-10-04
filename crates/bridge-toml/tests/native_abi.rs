//! Explicit native producer proof. Default unit tests do not qualify this lane.
//! Root runs this ignored test by exact name with its adopted tracked manifest.

use bridge_native::{
    LoadedModule, ModuleProvenance, NativeComponent, NativeHost, PinnedModuleSpec,
};
use bridge_toml::{
    NativeErrorCode, PathInput, TomlClient, TomlError, TomlPath, TomlReply, TomlRequest,
};
use serde_json::{Value, json};
use std::path::PathBuf;
use std::sync::Arc;

fn adopted_spec() -> PinnedModuleSpec {
    let owner =
        std::fs::canonicalize(PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..")).unwrap();
    let supplied = PathBuf::from(
        std::env::var_os("BRIDGE_TEST_NATIVE_MANIFEST")
            .expect("explicit native adoption manifest is required"),
    );
    assert!(supplied.is_absolute(), "native manifest must be absolute");
    let physical = std::fs::canonicalize(supplied).expect("native manifest must exist");
    let expected =
        std::fs::canonicalize(owner.join("dependencies/next-native-inputs.json")).unwrap();
    assert_eq!(
        physical, expected,
        "only the owning tracked adoption manifest is accepted"
    );
    assert!(
        physical.starts_with(&owner),
        "manifest must remain in the owning checkout"
    );
    let manifest: Value = serde_json::from_slice(&std::fs::read(physical).unwrap()).unwrap();
    assert_eq!(manifest["schemaVersion"], "bridge-native-probe-inputs/v1");
    assert_eq!(manifest["owningRepository"], "Guffawaffle/stfc-mod-bridge");
    let host = bridge_native::current_host().expect("an actual supported native host is required");
    let candidates: Vec<_> = manifest["modules"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|row| {
            row["component"] == "toml"
                && serde_json::from_value::<NativeHost>(row["host"].clone()).ok() == Some(host)
        })
        .collect();
    assert_eq!(
        candidates.len(),
        1,
        "one explicit adopted TOML module for the actual host is required"
    );
    let row = candidates[0];
    assert_eq!(row["symbolAbi"], 1);
    assert_eq!(row["producer"], "Guffawaffle/stfc-mod");
    assert_eq!(row["componentPath"], "shared/toml");
    let provenance: ModuleProvenance = serde_json::from_value(row["provenance"].clone()).unwrap();
    PinnedModuleSpec::new(
        NativeComponent::Toml,
        host,
        owner,
        PathBuf::from(row["relativePath"].as_str().unwrap()),
        row["sha256"].as_str().unwrap().to_owned(),
        provenance,
    )
    .unwrap()
}

fn read(client: &mut TomlClient, text: &str) -> bridge_toml::TomlSnapshot {
    let TomlReply::Snapshot(snapshot) = client.execute(TomlRequest::Read { text }).unwrap() else {
        panic!("native read must return a typed snapshot")
    };
    snapshot
}
fn edited(reply: TomlReply) -> String {
    let TomlReply::EditedText(text) = reply else {
        panic!("native mutation preparation must return text")
    };
    text
}

#[test]
#[ignore = "root-selected actual native ABI gate; fails without explicit adopted manifest"]
fn producer_abi_all_nine_operations_and_refusals() {
    let spec = adopted_spec();
    // SAFETY: root explicitly adopts reviewed historical producer bytes through
    // the tracked manifest. This test owns the component's synchronous call lane.
    let module = unsafe { LoadedModule::open(&spec) }.unwrap();
    let identity = module.identity().clone();
    let weak = Arc::downgrade(&module);
    // SAFETY: exact reviewed three-export TOML ABI and allocator; retained module
    // custody and serialized calls remain on this test's one creating thread.
    let mut client = unsafe { TomlClient::bind(Arc::clone(&module)) }.unwrap();
    drop(module);
    assert!(
        weak.upgrade().is_some(),
        "consumer must retain originating code"
    );

    let text = "# retained comment\r\n\"literal.dot\" = 9223372036854775807\r\ndate = 1979-05-27T07:32:00-08:00\r\n[section]\r\nflag = true\r\n";
    assert_eq!(
        client.execute(TomlRequest::Validate { text }).unwrap(),
        TomlReply::Validated
    );
    let snapshot = read(&mut client, text);
    let literal = TomlPath::new(vec!["literal.dot".into()]).unwrap();
    assert_eq!(
        snapshot
            .overrides
            .iter()
            .find(|row| row.path == literal)
            .unwrap()
            .value,
        "9223372036854775807"
    );
    assert_eq!(
        snapshot
            .overrides
            .iter()
            .find(|row| row.path.segments() == ["date"])
            .unwrap()
            .value,
        "1979-05-27T07:32:00-08:00"
    );

    let changed = edited(
        client
            .execute(TomlRequest::Set {
                text,
                path: &literal,
                value: "-9223372036854775808",
            })
            .unwrap(),
    );
    assert!(changed.contains("# retained comment"));
    assert_eq!(
        read(&mut client, &changed)
            .overrides
            .iter()
            .find(|row| row.path == literal)
            .unwrap()
            .value,
        "-9223372036854775808"
    );
    let removed = edited(
        client
            .execute(TomlRequest::Remove {
                text,
                path: &literal,
            })
            .unwrap(),
    );
    assert!(
        !read(&mut client, &removed)
            .overrides
            .iter()
            .any(|row| row.path == literal)
    );

    let section = TomlPath::new(vec!["section".into()]).unwrap();
    let destination = TomlPath::new(vec!["renamed".into()]).unwrap();
    let removed_table = edited(
        client
            .execute(TomlRequest::RemoveTable {
                text,
                path: &section,
            })
            .unwrap(),
    );
    assert!(
        !read(&mut client, &removed_table)
            .overrides
            .iter()
            .any(|row| row
                .path
                .segments()
                .first()
                .is_some_and(|segment| segment == "section"))
    );
    let renamed = edited(
        client
            .execute(TomlRequest::RenameTable {
                text,
                path: &section,
                destination: &destination,
            })
            .unwrap(),
    );
    assert!(
        read(&mut client, &renamed)
            .overrides
            .iter()
            .any(|row| row.path.segments() == ["renamed", "flag"])
    );

    assert_eq!(
        client
            .execute(TomlRequest::NormalizeValue {
                value: "9223372036854775807"
            })
            .unwrap(),
        TomlReply::Value("9223372036854775807".into())
    );
    assert_eq!(
        client
            .execute(TomlRequest::DecodeString {
                value: r#""snowman \u2603""#
            })
            .unwrap(),
        TomlReply::Value("snowman ☃".into())
    );
    let TomlReply::ParsedPath(parsed) = client
        .execute(TomlRequest::ParsePath {
            input: PathInput::Expression("\"literal.dot\""),
        })
        .unwrap()
    else {
        panic!("parsed path expected")
    };
    assert_eq!(parsed.path, literal);
    assert_eq!(parsed.canonical_path, "\"literal.dot\"");
    assert_eq!(
        client
            .execute(TomlRequest::ParsePath {
                input: PathInput::Segments(&literal)
            })
            .unwrap(),
        TomlReply::ParsedPath(parsed)
    );

    for (request, expected) in [
        (
            TomlRequest::Validate { text: "x = [\n" },
            NativeErrorCode::InvalidDocument,
        ),
        (
            TomlRequest::Validate {
                text: "x = 1\nx = 2\n",
            },
            NativeErrorCode::DuplicateTarget,
        ),
        (
            TomlRequest::NormalizeValue {
                value: "1\nsecond = 2",
            },
            NativeErrorCode::InvalidValue,
        ),
        (
            TomlRequest::DecodeString { value: "12" },
            NativeErrorCode::InvalidValue,
        ),
        (
            TomlRequest::RemoveTable {
                text: "[[section]]\nx = 1\n",
                path: &section,
            },
            NativeErrorCode::UnsupportedTarget,
        ),
    ] {
        let error = client.execute(request).unwrap_err();
        assert!(
            matches!(error, TomlError::NativeRefusal { code, .. } if code == expected),
            "typed native refusal expected: {error}"
        );
    }
    drop(client);
    assert!(
        weak.upgrade().is_none(),
        "code ownership ends after final consumer/allocator custody"
    );
    println!(
        "BRIDGE_NATIVE_TOML_OBSERVATION {}",
        json!({
            "schemaVersion":"bridge-toml-native-observation/v1", "module":identity,
            "operations":["validate","read","set","remove","remove_table","rename_table","normalize_value","decode_string","parse_path"],
            "structuredRefusalsObserved":true, "moduleRetainedAfterCallerDrop":true,
            "gameRuntimeQualified":false, "releaseQualified":false
        })
    );
}
