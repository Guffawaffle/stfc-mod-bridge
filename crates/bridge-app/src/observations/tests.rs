use super::*;
use serde_json::json;
use std::sync::{
    Arc,
    atomic::{AtomicUsize, Ordering},
};

struct Missing;
impl ObservationSource for Missing {
    fn read(&mut self, _: &Evidence) -> Views {
        Views {
            profiles: unavailable(ObservationReason::AccessDenied),
            installations: unavailable(ObservationReason::NativeUnavailable),
            sessions: unavailable(ObservationReason::IncompleteInventory),
        }
    }
}
fn request(body: Value) -> ValidatedRequest {
    decode_request(
        json!({"protocolVersion":1,"requestId":"10000000-0000-4000-8000-000000000001","body":body})
            .to_string()
            .as_bytes(),
    )
    .unwrap()
}
fn query(host: &mut impl LocalHost, name: &str) -> Value {
    let reply = host
        .dispatch(request(
            json!({"type":"query","query":{"name":name,"input":{}}}),
        ))
        .unwrap();
    serde_json::to_value(reply.as_inner()).unwrap()
}
#[test]
fn absent_native_reads_never_become_empty_complete_inventories() {
    let mut host = ReadOnlyApplication::new(Missing).unwrap();
    let reply = query(&mut host, "snapshot");
    let view = &reply["body"]["result"]["query"]["output"];
    assert_eq!(view["installations"]["status"], "unavailable");
    assert_eq!(view["sessions"]["reason"], "incomplete_inventory");
    assert_eq!(view["profiles"]["reason"], "access_denied");
    assert_eq!(view["operations"]["items"], json!([]));
}
#[test]
fn read_only_host_advertises_no_mutation_and_rejects_commands() {
    let mut host = ReadOnlyApplication::new(Missing).unwrap();
    assert_eq!(
        query(&mut host, "hello")["body"]["result"]["query"]["output"]["implementedCommands"],
        json!(["request_host_close"])
    );
    let reply = host
        .dispatch(request(
            json!({"type":"command","command":{"name":"cancel_operation","input":{
        "operationId":"10000000-0000-4000-8000-000000000002","expectedOperationRevision":"1"}}}),
        ))
        .unwrap();
    assert!(matches!(reply.as_inner().body, ReplyBody::Rejected { .. }));
}
#[test]
fn freshness_does_not_emit_a_new_domain_revision() {
    let mut host = ReadOnlyApplication::new(Missing).unwrap();
    query(&mut host, "snapshot");
    let before = host.cursor();
    query(&mut host, "snapshot");
    assert_eq!(host.cursor(), before);
}
struct Changing {
    counter: Arc<AtomicUsize>,
}
impl ObservationSource for Changing {
    fn read(&mut self, evidence: &Evidence) -> Views {
        let n = self.counter.load(Ordering::Relaxed);
        let views = |items: Value| json!({"status":"observed","evidence":evidence,"value":{"items":items,"completeness":"complete","issues":[],"revision":format!("revision-{n}")}});
        Views {
            profiles: wire_value(views(json!([]))).unwrap(),
            sessions: wire_value(views(json!([]))).unwrap(),
            installations: wire_value(views(json!([]))).unwrap(),
        }
    }
}
#[test]
fn changed_observations_invalidate_and_retention_gaps_require_resnapshot() {
    let counter = Arc::new(AtomicUsize::new(0));
    let mut host = ReadOnlyApplication::new(Changing {
        counter: counter.clone(),
    })
    .unwrap();
    let start = host.cursor();
    query(&mut host, "snapshot");
    assert_eq!(
        host.events_after(&start).unwrap().events.as_slice().len(),
        1
    );
    for value in 1..20 {
        counter.store(value, Ordering::Relaxed);
        query(&mut host, "snapshot");
    }
    assert_eq!(
        host.events_after(&start).unwrap_err(),
        HostFailure::ResnapshotRequired
    );
    let current = host.cursor();
    assert!(
        host.events_after(&current)
            .unwrap()
            .events
            .as_slice()
            .is_empty()
    );
}
#[test]
fn foreign_or_future_cursor_never_reads_another_stream() {
    let mut host = ReadOnlyApplication::new(Missing).unwrap();
    let mut foreign = host.cursor();
    foreign.host_epoch = HostEpoch::new("20000000-0000-4000-8000-000000000001").unwrap();
    assert_eq!(
        host.events_after(&foreign).unwrap_err(),
        HostFailure::ResnapshotRequired
    );
    let mut future = host.cursor();
    future.sequence = Sequence::new(100);
    assert_eq!(
        host.events_after(&future).unwrap_err(),
        HostFailure::ResnapshotRequired
    );
}
#[test]
fn close_refuses_further_queries_without_native_work() {
    let mut host = ReadOnlyApplication::new(Missing).unwrap();
    assert_eq!(host.request_close().unwrap(), CloseDisposition::Ready);
    let reply = query(&mut host, "snapshot");
    assert_eq!(reply["body"]["type"], "rejected");
}

#[test]
fn event_replay_honors_the_requested_page_and_next_cursor() {
    let counter = Arc::new(AtomicUsize::new(0));
    let mut host = ReadOnlyApplication::new(Changing {
        counter: counter.clone(),
    })
    .unwrap();
    let start = host.cursor();
    for value in 0..3 {
        counter.store(value, Ordering::Relaxed);
        query(&mut host, "snapshot");
    }
    let reply = host
        .dispatch(request(json!({"type":"query","query":{
        "name":"resume_events","input":{"after":start,"maximumEvents":"1"}}})))
        .unwrap();
    let value = serde_json::to_value(reply.as_inner()).unwrap();
    let output = &value["body"]["result"]["query"]["output"];
    assert_eq!(output["events"].as_array().unwrap().len(), 1);
    assert_eq!(
        output["next"]["sequence"],
        output["events"][0]["cursor"]["sequence"]
    );
    assert_ne!(output["next"], serde_json::to_value(host.cursor()).unwrap());
    let after: Cursor = serde_json::from_value(output["next"].clone()).unwrap();
    assert_eq!(
        host.events_after(&after).unwrap().events.as_slice().len(),
        2
    );
}
