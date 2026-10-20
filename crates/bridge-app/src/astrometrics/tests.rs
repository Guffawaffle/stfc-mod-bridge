use super::*;
use serde_json::json;

struct Fixture {
    path: PathBuf,
}
impl Fixture {
    fn new() -> Self {
        Self {
            path: std::env::temp_dir()
                .join(format!(
                    "bridge-astrometrics-{}",
                    NativeIdentitySource::new().host_epoch().unwrap().as_str()
                ))
                .join("events.sqlite"),
        }
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        // Exact fixture files only; never recurse over a computed directory.
        let _ = std::fs::remove_file(&self.path);
        let _ = std::fs::remove_file(self.path.with_extension("sqlite-journal"));
        if let Some(parent) = self.path.parent() {
            let _ = std::fs::remove_dir(parent);
        }
    }
}
fn event(sequence: &str) -> Value {
    json!({"type":"diagnostic","schema":1,"source_id":"fixture-source","producer_version":"1.1.9.4","session":"fixture-session",
        "stream":"claims/main","sequence":sequence,"utc_ms":"1792470620804","event":"summary_navigation_rejected",
        "payload":{"claim":"fixture-claim","quantity":"18446744073709551615"}})
}
#[test]
fn disabled_collection_creates_no_files_or_socket_and_shutdown_keeps_records() {
    let fixture = Fixture::new();
    let mut collector = Astrometrics::new(fixture.path.clone());
    assert!(!collector.view().enabled);
    assert!(!fixture.path.exists());
    collector.set_enabled(false);
    assert!(!fixture.path.exists());
}
#[test]
fn committed_events_survive_reopen_and_identical_redelivery_is_idempotent() {
    let fixture = Fixture::new();
    let token;
    {
        let mut store = Store::open(&fixture.path).unwrap();
        token = store.token.clone();
        let bytes = serde_json::to_vec(&vec![event("18446744073709551615")]).unwrap();
        store.ingest(&bytes).unwrap();
        store.ingest(&bytes).unwrap();
        assert_eq!(store.timeline().unwrap().0, 1);
    }
    let store = Store::open(&fixture.path).unwrap();
    assert_eq!(store.token, token);
    let (count, events) = store.timeline().unwrap();
    assert_eq!(count, 1);
    assert_eq!(events[0].sequence, "18446744073709551615");
    assert!(events[0].payload_json.contains("18446744073709551615"));
    assert!(events[0].summary.contains("unconfirmed"));
}
#[test]
fn conflicting_replay_rolls_back_the_entire_batch_and_preserves_prior_evidence() {
    let fixture = Fixture::new();
    let mut store = Store::open(&fixture.path).unwrap();
    store
        .ingest(&serde_json::to_vec(&vec![event("1")]).unwrap())
        .unwrap();
    let mut conflict = event("1");
    conflict["payload"]["quantity"] = json!("different");
    assert_eq!(
        store.ingest(&serde_json::to_vec(&vec![event("2"), conflict]).unwrap()),
        Err(409)
    );
    assert_eq!(store.timeline().unwrap().0, 1);
    let mut future = event("3");
    future["schema"] = json!(2);
    assert_eq!(
        store.ingest(&serde_json::to_vec(&vec![future]).unwrap()),
        Err(400)
    );
    assert_eq!(store.timeline().unwrap().0, 1);
}
#[test]
fn refuses_unrelated_database_without_modifying_its_schema() {
    let fixture = Fixture::new();
    std::fs::create_dir_all(fixture.path.parent().unwrap()).unwrap();
    {
        let db = Connection::open(&fixture.path).unwrap();
        db.execute_batch(
            "CREATE TABLE personal(value TEXT);INSERT INTO personal VALUES('retained');",
        )
        .unwrap();
    }
    assert!(Store::open(&fixture.path).is_err());
    let db = Connection::open(&fixture.path).unwrap();
    assert_eq!(
        db.query_row("SELECT value FROM personal", [], |r| r.get::<_, String>(0))
            .unwrap(),
        "retained"
    );
    assert_eq!(
        db.query_row("PRAGMA application_id", [], |r| r.get::<_, i64>(0))
            .unwrap(),
        0
    );
}
fn http_request(header: &str, body: &[u8], authorized: bool) -> (u16, PathBuf) {
    let fixture = Fixture::new();
    let mut store = Store::open(&fixture.path).unwrap();
    let token = store.token.as_str().to_owned();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    let thread = thread::spawn(move || {
        let (mut stream, _) = listener.accept().unwrap();
        let bytes = read_http(&mut stream, &token, &AtomicBool::new(false));
        let result = bytes
            .and_then(|bytes| store.ingest(&bytes))
            .map_or_else(|code| code, |_| 200);
        assert_eq!(
            store.timeline().unwrap().0,
            if result == 200 { 1 } else { 0 }
        );
        result
    });
    let mut stream = TcpStream::connect(address).unwrap();
    let value = if authorized {
        Store::open(&fixture.path)
            .unwrap()
            .token
            .as_str()
            .to_owned()
    } else {
        "wrong".into()
    };
    write!(stream,"POST /sync HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nstfc-sync-token: {value}\r\nContent-Length: {}\r\n{header}\r\n",body.len()).unwrap();
    stream.write_all(body).unwrap();
    let result = thread.join().unwrap();
    (result, fixture.path.clone())
}
#[test]
fn real_loopback_http_accepts_current_sync_shape_and_refuses_browser_or_bad_auth() {
    let body = serde_json::to_vec(&vec![event("1")]).unwrap();
    assert_eq!(http_request("", &body, true).0, 200);
    assert_eq!(http_request("", &body, false).0, 401);
    assert_eq!(
        http_request("Origin: https://example.com\r\n", &body, true).0,
        400
    );
    assert_eq!(
        http_request("Transfer-Encoding: chunked\r\n", &body, true).0,
        400
    );
    assert_eq!(http_request("Content-Length: 1\r\n", &body, true).0, 400);
}
#[test]
fn bounded_previews_are_utf8_and_never_claim_rewards_from_missing_data() {
    let text = "界".repeat(600);
    assert!(clip(&text, 512).len() <= 512);
    assert!(!explain("claim_enter", &json!({})).contains("succeeded"));
    let fixture = Fixture::new();
    let mut store = Store::open(&fixture.path).unwrap();
    let mut value = event("1");
    value["payload"]["text"] = json!(text);
    store
        .ingest(&serde_json::to_vec(&vec![value]).unwrap())
        .unwrap();
    assert!(store.timeline().unwrap().1[0].payload_truncated);
}

#[test]
fn stopping_a_partial_http_read_joins_before_receiver_release() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    let stop = Arc::new(AtomicBool::new(false));
    let ended = Arc::new(AtomicBool::new(false));
    let worker_stop = stop.clone();
    let worker_ended = ended.clone();
    let (ready_tx, ready_rx) = std::sync::mpsc::channel();
    let owner = thread::spawn(move || {
        let (mut stream, _) = listener.accept().unwrap();
        ready_tx.send(()).unwrap();
        assert_eq!(
            read_http(&mut stream, "fixture-token", &worker_stop),
            Err(408)
        );
        worker_ended.store(true, Ordering::Release);
    });
    let mut stream = TcpStream::connect(address).unwrap();
    stream.write_all(b"POST /sync HTTP/1.1\r\n").unwrap();
    ready_rx.recv_timeout(Duration::from_secs(2)).unwrap();
    drop(Receiver {
        stop,
        owner: Some(owner),
    });
    assert!(ended.load(Ordering::Acquire));
    assert!(TcpStream::connect(address).is_err());
}
