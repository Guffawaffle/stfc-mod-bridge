use super::*;
use serde_json::json;
use std::cell::RefCell;
use std::collections::BTreeMap;

struct Fixture {
    version: u32,
    status: c_int,
    response: Option<Vec<u8>>,
    reported_length: Option<usize>,
    requests: Vec<Vec<u8>>,
    live: BTreeMap<usize, Box<[u8]>>,
    frees: usize,
    unknown_frees: usize,
}
impl Default for Fixture {
    fn default() -> Self {
        Self {
            version: ABI_VERSION,
            status: 0,
            response: Some(br#"{"ok":true}"#.to_vec()),
            reported_length: None,
            requests: Vec::new(),
            live: BTreeMap::new(),
            frees: 0,
            unknown_frees: 0,
        }
    }
}
thread_local! { static FIXTURE: RefCell<Fixture> = RefCell::new(Fixture::default()); }

unsafe extern "C" fn fixture_version() -> u32 {
    FIXTURE.with(|value| value.borrow().version)
}
unsafe extern "C" fn fixture_execute(
    request: *const c_char,
    request_length: usize,
    output: *mut *mut c_char,
    length: *mut usize,
) -> c_int {
    // SAFETY: the consumer invokes the exact test ABI with live input bytes and
    // initialized writable out parameters; all returned storage is real memory.
    let request = unsafe { std::slice::from_raw_parts(request.cast(), request_length) }.to_vec();
    FIXTURE.with(|fixture| {
        let mut fixture = fixture.borrow_mut();
        fixture.requests.push(request);
        let response = fixture.response.clone();
        let count = response.as_ref().map_or(0, Vec::len);
        let pointer = if let Some(mut bytes) = response {
            // The producer's optional NUL is allocated, but excluded from length.
            bytes.push(0);
            let mut allocation = bytes.into_boxed_slice();
            let pointer = allocation.as_mut_ptr().cast::<c_char>();
            fixture.live.insert(pointer as usize, allocation);
            pointer
        } else {
            std::ptr::null_mut()
        };
        // SAFETY: real writable output parameters supplied by the consumer.
        unsafe {
            *output = pointer;
            *length = fixture.reported_length.unwrap_or(count);
        }
        fixture.status
    })
}
unsafe extern "C" fn fixture_free(pointer: *mut c_void) {
    FIXTURE.with(|fixture| {
        let mut fixture = fixture.borrow_mut();
        if fixture.live.remove(&(pointer as usize)).is_some() {
            fixture.frees += 1;
        } else {
            fixture.unknown_frees += 1;
        }
    });
}
fn fixture_client(fixture: Fixture) -> Result<TomlClient, TomlError> {
    FIXTURE.with(|current| *current.borrow_mut() = fixture);
    TomlClient::from_api(NativeApi {
        _module: None,
        _test_keepalive: None,
        version: fixture_version,
        execute: fixture_execute,
        free: fixture_free,
    })
}
fn response_client(bytes: &[u8]) -> TomlClient {
    fixture_client(Fixture {
        response: Some(bytes.to_vec()),
        ..Fixture::default()
    })
    .unwrap()
}
fn check_freed(count: usize) {
    FIXTURE.with(|fixture| {
        let fixture = fixture.borrow();
        assert_eq!(fixture.frees, count);
        assert_eq!(fixture.unknown_frees, 0);
        assert!(fixture.live.is_empty());
    });
}
fn validate_request() -> TomlRequest<'static> {
    TomlRequest::Validate { text: "x = 1\n" }
}

#[test]
fn version_is_checked_before_any_execute() {
    let result = fixture_client(Fixture {
        version: 2,
        ..Fixture::default()
    });
    assert!(matches!(result, Err(TomlError::UnsupportedAbiVersion)));
    FIXTURE.with(|fixture| assert!(fixture.borrow().requests.is_empty()));
    check_freed(0);
}

#[test]
fn all_nine_operations_encode_exact_producer_shapes() {
    let path = TomlPath::new(vec!["a.b".into(), "".into()]).unwrap();
    let destination = TomlPath::new(vec!["renamed".into()]).unwrap();
    let text = "\"a.b\".\"\" = 9223372036854775807\n";
    let value = "1979-05-27T07:32:00-08:00";
    let cases = [
        (
            TomlRequest::Validate { text },
            json!({"operation":"validate","text":text}),
        ),
        (
            TomlRequest::Read { text },
            json!({"operation":"read","text":text}),
        ),
        (
            TomlRequest::Set {
                text,
                path: &path,
                value,
            },
            json!({"operation":"set","text":text,"path":["a.b", ""],"value":value}),
        ),
        (
            TomlRequest::Remove { text, path: &path },
            json!({"operation":"remove","text":text,"path":["a.b", ""]}),
        ),
        (
            TomlRequest::RemoveTable { text, path: &path },
            json!({"operation":"remove_table","text":text,"path":["a.b", ""]}),
        ),
        (
            TomlRequest::RenameTable {
                text,
                path: &path,
                destination: &destination,
            },
            json!({"operation":"rename_table","text":text,"path":["a.b", ""],"destination":["renamed"]}),
        ),
        (
            TomlRequest::NormalizeValue { value },
            json!({"operation":"normalize_value","value":value}),
        ),
        (
            TomlRequest::DecodeString {
                value: "\"snowman ☃\"",
            },
            json!({"operation":"decode_string","value":"\"snowman ☃\""}),
        ),
        (
            TomlRequest::ParsePath {
                input: PathInput::Segments(&path),
            },
            json!({"operation":"parse_path","path":["a.b", ""]}),
        ),
        (
            TomlRequest::ParsePath {
                input: PathInput::Expression("\"a.b\".\"\""),
            },
            json!({"operation":"parse_path","value":"\"a.b\".\"\""}),
        ),
    ];
    for (request, expected) in cases {
        let actual: serde_json::Value =
            serde_json::from_slice(&encode_request(request, MAX_REQUEST_BYTES).unwrap()).unwrap();
        assert_eq!(actual, expected);
    }
    assert_eq!(TomlPath::new(Vec::new()), Err(TomlError::InvalidPath));
}

#[test]
fn encoded_byte_limit_counts_escaping_and_prevents_native_call() {
    let request = TomlRequest::Validate { text: "\0\0\0" };
    let full = encode_request(request, MAX_REQUEST_BYTES).unwrap();
    assert!(full.len() > 3);
    assert_eq!(encode_request(request, full.len()).unwrap(), full);
    assert_eq!(
        encode_request(request, full.len() - 1),
        Err(TomlError::RequestTooLarge)
    );
    let mut client = response_client(br#"{"ok":true}"#);
    assert_eq!(
        client.execute_bounded(request, full.len() - 1),
        Err(TomlError::RequestTooLarge)
    );
    FIXTURE.with(|fixture| assert!(fixture.borrow().requests.is_empty()));
    check_freed(0);
}

#[test]
fn valid_allocation_freed_on_success_and_exact_request_reaches_abi() {
    let mut client = response_client(br#"{"ok":true}"#);
    assert_eq!(client.execute(validate_request()), Ok(TomlReply::Validated));
    FIXTURE.with(|fixture| {
        let fixture = fixture.borrow();
        assert_eq!(fixture.requests.len(), 1);
        assert_eq!(
            serde_json::from_slice::<serde_json::Value>(&fixture.requests[0]).unwrap(),
            json!({"operation":"validate","text":"x = 1\n"})
        );
    });
    check_freed(1);
}

#[test]
fn snapshot_preserves_int64_datetime_and_literal_dot_paths_as_strings() {
    let response = json!({"ok":true,"overrides":[
        {"path":["a.b"],"canonicalPath":"\"a.b\"","value":"9223372036854775807","semanticValue":"9223372036854775807","line":1},
        {"path":["date"],"canonicalPath":"date","value":"1979-05-27T07:32:00-08:00","semanticValue":"1979-05-27T07:32:00-08:00","line":2}
    ],"tables":[{"path":[""],"canonicalPath":"\"\"","line":3}]});
    let mut client = response_client(&serde_json::to_vec(&response).unwrap());
    let TomlReply::Snapshot(snapshot) = client
        .execute(TomlRequest::Read { text: "fixture" })
        .unwrap()
    else {
        panic!("snapshot expected")
    };
    assert_eq!(snapshot.overrides[0].value, "9223372036854775807");
    assert_eq!(snapshot.overrides[0].semantic_value, "9223372036854775807");
    assert_eq!(snapshot.overrides[0].path.segments(), &["a.b"]);
    assert_eq!(snapshot.overrides[1].value, "1979-05-27T07:32:00-08:00");
    assert_eq!(snapshot.tables[0].path.segments(), &[""]);
    check_freed(1);
}

#[test]
fn edited_normalized_decoded_and_parsed_reply_families_are_distinct() {
    let path = TomlPath::new(vec!["x".into()]).unwrap();
    for request in [
        TomlRequest::Set {
            text: "x=1",
            path: &path,
            value: "2",
        },
        TomlRequest::Remove {
            text: "x=1",
            path: &path,
        },
        TomlRequest::RemoveTable {
            text: "[x]",
            path: &path,
        },
        TomlRequest::RenameTable {
            text: "[x]",
            path: &path,
            destination: &path,
        },
    ] {
        let mut client = response_client(br#"{"ok":true,"text":""}"#);
        assert_eq!(
            client.execute(request),
            Ok(TomlReply::EditedText(String::new()))
        );
        check_freed(1);
    }
    let mut client = response_client(br#"{"ok":true,"value":""}"#);
    assert_eq!(
        client.execute(TomlRequest::DecodeString { value: "\"\"" }),
        Ok(TomlReply::Value(String::new()))
    );
    check_freed(1);
    let mut client = response_client(br#"{"ok":true,"value":"9223372036854775807"}"#);
    assert_eq!(
        client.execute(TomlRequest::NormalizeValue {
            value: "9223372036854775807"
        }),
        Ok(TomlReply::Value("9223372036854775807".into()))
    );
    check_freed(1);
    let mut client = response_client(br#"{"ok":true,"path":["x"],"value":"x"}"#);
    assert_eq!(
        client.execute(TomlRequest::ParsePath {
            input: PathInput::Segments(&path)
        }),
        Ok(TomlReply::ParsedPath(ParsedPath {
            path: path.clone(),
            canonical_path: "x".into()
        }))
    );
    check_freed(1);
}

#[test]
fn all_native_refusal_codes_and_positive_line_are_typed_and_freed() {
    for code in [
        NativeErrorCode::InvalidUtf8,
        NativeErrorCode::InvalidPath,
        NativeErrorCode::InvalidValue,
        NativeErrorCode::InvalidDocument,
        NativeErrorCode::DuplicateTarget,
        NativeErrorCode::UnsupportedTarget,
        NativeErrorCode::InternalError,
    ] {
        let bytes =
            serde_json::to_vec(&json!({"ok":false,"error":{"code":code.code(),"line":3}})).unwrap();
        let mut client = response_client(&bytes);
        assert_eq!(
            client.execute(validate_request()),
            Err(TomlError::NativeRefusal {
                code,
                line: NonZeroU32::new(3)
            })
        );
        check_freed(1);
    }
    let mut client = response_client(br#"{"ok":false,"error":{"code":"InvalidDocument"}}"#);
    assert_eq!(
        client.execute(validate_request()),
        Err(TomlError::NativeRefusal {
            code: NativeErrorCode::InvalidDocument,
            line: None
        })
    );
    check_freed(1);
}

#[test]
fn abi_failures_are_distinct_from_structured_refusals() {
    for (status, expected) in [
        (1, AbiStatus::InvalidInput),
        (2, AbiStatus::InternalFailure),
        (-7, AbiStatus::Unexpected),
    ] {
        let mut client = fixture_client(Fixture {
            status,
            response: None,
            ..Fixture::default()
        })
        .unwrap();
        assert_eq!(
            client.execute(validate_request()),
            Err(TomlError::AbiFailure(expected))
        );
        check_freed(0);
    }
}

#[test]
fn contradictory_outputs_release_valid_allocation_without_reading_length() {
    for (status, length) in [(1, 0), (2, usize::MAX), (99, 5)] {
        let mut client = fixture_client(Fixture {
            status,
            response: Some(vec![b'x']),
            reported_length: Some(length),
            ..Fixture::default()
        })
        .unwrap();
        assert_eq!(
            client.execute(validate_request()),
            Err(TomlError::MalformedResponse(
                ResponseFault::ContradictoryOutput
            ))
        );
        check_freed(1);
    }
    for status in [0, 1, 2] {
        let mut client = fixture_client(Fixture {
            status,
            response: None,
            reported_length: Some(1),
            ..Fixture::default()
        })
        .unwrap();
        assert_eq!(
            client.execute(validate_request()),
            Err(TomlError::MalformedResponse(
                ResponseFault::ContradictoryOutput
            ))
        );
        check_freed(0);
    }
}

#[test]
fn response_bounds_and_nulls_are_checked_before_slice_construction() {
    for length in [MAX_RESPONSE_BYTES + 1, isize::MAX as usize + 1, usize::MAX] {
        // One real readable byte is enough: oversize reported lengths must never
        // be sliced, while the originating allocator still receives its pointer.
        let mut client = fixture_client(Fixture {
            response: Some(vec![b'x']),
            reported_length: Some(length),
            ..Fixture::default()
        })
        .unwrap();
        assert_eq!(
            client.execute(validate_request()),
            Err(TomlError::MalformedResponse(ResponseFault::TooLarge))
        );
        check_freed(1);
    }
    let mut client = fixture_client(Fixture {
        response: None,
        ..Fixture::default()
    })
    .unwrap();
    assert_eq!(
        client.execute(validate_request()),
        Err(TomlError::MalformedResponse(
            ResponseFault::MissingAllocation
        ))
    );
    check_freed(0);
    let mut client = response_client(b"");
    assert_eq!(
        client.execute(validate_request()),
        Err(TomlError::MalformedResponse(ResponseFault::Empty))
    );
    check_freed(1);
}

#[test]
fn malformed_json_utf8_duplicates_versions_and_private_fields_are_refused() {
    let invalid = [
        &b"\xff"[..],
        &b"{"[..],
        &br#"{"ok":true} {"ok":true}"#[..],
        &br#"{"ok":true,"ok":true}"#[..],
        &br#"{"ok":true,"apiVersion":1}"#[..],
        &br#"{"ok":true,"text":"wrong family"}"#[..],
        &br#"{"ok":false,"error":{"code":"Unknown"}}"#[..],
        &br#"{"ok":false,"error":{"code":"InvalidDocument","line":0}}"#[..],
        &br#"{"ok":false,"error":{"code":"InvalidDocument","line":1.0}}"#[..],
        &br#"{"ok":false,"error":{"code":"InvalidDocument","line":null}}"#[..],
        &br#"{"ok":false,"error":{"code":{"InvalidDocument":null}}}"#[..],
        &br#"{"ok":false,"error":{"code":"InvalidDocument","message":"C:/private/account"}}"#[..],
        &br#"{"ok":true,"error":{"code":"InvalidDocument"}}"#[..],
        &br#"{"ok":null}"#[..],
        &br#"{"ok":1}"#[..],
    ];
    for bytes in invalid {
        let mut client = response_client(bytes);
        let error = client.execute(validate_request()).unwrap_err();
        assert!(
            matches!(error, TomlError::MalformedResponse(_)),
            "{bytes:?}"
        );
        assert!(!format!("{error:?} {error}").contains("private/account"));
        check_freed(1);
    }
}

#[test]
fn malformed_snapshot_rows_and_changed_decoded_paths_are_refused() {
    for row in [
        json!({"path":[],"canonicalPath":"x","value":"1","semanticValue":"1","line":1}),
        json!({"path":["x"],"canonicalPath":"x","value":1,"semanticValue":"1","line":1}),
        json!({"path":["x"],"canonicalPath":"x","value":"1","semanticValue":"1","line":0}),
        json!({"path":["x"],"canonicalPath":"","value":"1","semanticValue":"1","line":1}),
        json!({"path":["x"],"canonicalPath":"x","value":"1","semanticValue":"","line":1}),
    ] {
        let mut client = response_client(
            &serde_json::to_vec(&json!({"ok":true,"overrides":[row],"tables":[]})).unwrap(),
        );
        assert_eq!(
            client.execute(TomlRequest::Read { text: "fixture" }),
            Err(TomlError::MalformedResponse(ResponseFault::InvalidShape))
        );
        check_freed(1);
    }
    let path = TomlPath::new(vec!["a.b".into()]).unwrap();
    let mut client = response_client(br#"{"ok":true,"path":["a","b"],"value":"a.b"}"#);
    assert_eq!(
        client.execute(TomlRequest::ParsePath {
            input: PathInput::Segments(&path)
        }),
        Err(TomlError::MalformedResponse(ResponseFault::InvalidShape))
    );
    check_freed(1);
}

#[test]
fn allocation_guard_retains_origin_after_client_drop_and_frees_exactly_once() {
    let client = response_client(br#"{"ok":true}"#);
    let keepalive = Arc::new(());
    let weak = Arc::downgrade(&keepalive);
    let api = NativeApi {
        _module: None,
        _test_keepalive: Some(keepalive),
        version: fixture_version,
        execute: fixture_execute,
        free: fixture_free,
    };
    // Construct through the same private checked table path, then retain its
    // allocation independently of the client. No invalid addresses are used.
    drop(client);
    let client = TomlClient::from_api(api).unwrap();
    let input = encode_request(validate_request(), MAX_REQUEST_BYTES).unwrap();
    let mut pointer = std::ptr::null_mut();
    let mut length = 0;
    // SAFETY: real private fixture and live bounded request/out parameters.
    assert_eq!(
        unsafe {
            (client.api.execute)(
                input.as_ptr().cast(),
                input.len(),
                &mut pointer,
                &mut length,
            )
        },
        0
    );
    let buffer = NativeBuffer {
        pointer: NonNull::new(pointer).unwrap(),
        length,
        api: Arc::clone(&client.api),
        _thread: PhantomData,
    };
    drop(client);
    assert!(weak.upgrade().is_some());
    assert_eq!(buffer.bytes().unwrap(), br#"{"ok":true}"#);
    drop(buffer);
    assert!(weak.upgrade().is_none());
    check_freed(1);
}
