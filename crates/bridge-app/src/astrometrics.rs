//! Optional local sync destination. No outbox, remote forwarding or game control.
use crate::NativeIdentitySource;
use bridge_contracts::v1::*;
use rusqlite::{Connection, OptionalExtension, params};
use serde::Deserialize;
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::{
    io::{Read, Write},
    net::{TcpListener, TcpStream},
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU64, Ordering},
    },
    thread,
    time::{Duration, Instant},
};

const ADDRESS: &str = "127.0.0.1:58237";
const ENDPOINT: &str = "http://127.0.0.1:58237/sync";
const LIMIT: usize = 256 * 1024;
const APPLICATION_ID: i64 = 0x41535452;
const SCHEMA: &str = "CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE event(id INTEGER PRIMARY KEY,source_id TEXT NOT NULL,session TEXT NOT NULL,stream TEXT NOT NULL,
sequence TEXT NOT NULL,event_type TEXT NOT NULL,producer_version TEXT NOT NULL,utc_ms TEXT,payload TEXT NOT NULL,body_hash TEXT NOT NULL,
UNIQUE(source_id,session,stream,sequence));";

#[derive(Debug, Clone, Copy)]
struct Failure;
type Result<T> = std::result::Result<T, Failure>;
fn fail<T>(_: T) -> Failure {
    Failure
}
fn digest(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}
fn clip(text: &str, maximum: usize) -> String {
    let mut end = text.len().min(maximum);
    while !text.is_char_boundary(end) {
        end -= 1;
    }
    text[..end].to_owned()
}
fn label(s: &str, limit: usize) -> bool {
    !s.is_empty()
        && s.len() <= limit
        && s.bytes()
            .all(|b| b.is_ascii_alphanumeric() || b"._:-/".contains(&b))
}
fn decimal(s: &str) -> bool {
    s.bytes().all(|b| b.is_ascii_digit())
        && s.parse::<u64>().is_ok()
        && (s == "0" || !s.starts_with('0'))
}
fn depth(value: &Value, n: usize) -> bool {
    n <= 32
        && match value {
            Value::Array(a) => a.iter().all(|v| depth(v, n + 1)),
            Value::Object(o) => o.values().all(|v| depth(v, n + 1)),
            _ => true,
        }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Envelope {
    #[serde(rename = "type")]
    kind: String,
    schema: u32,
    source_id: String,
    producer_version: String,
    session: String,
    stream: String,
    sequence: String,
    utc_ms: Option<String>,
    event: String,
    payload: Value,
}

struct Store {
    db: Connection,
    token: HostEpoch,
}
impl Store {
    fn open(path: &std::path::Path) -> Result<Self> {
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(fail)?;
        }
        let mut db = Connection::open_with_flags(
            path,
            rusqlite::OpenFlags::SQLITE_OPEN_READ_WRITE
                | rusqlite::OpenFlags::SQLITE_OPEN_CREATE
                | rusqlite::OpenFlags::SQLITE_OPEN_NO_MUTEX,
        )
        .map_err(fail)?;
        db.busy_timeout(Duration::from_millis(250)).map_err(fail)?;
        let id: i64 = db
            .pragma_query_value(None, "application_id", |r| r.get(0))
            .map_err(fail)?;
        let version: i64 = db
            .pragma_query_value(None, "user_version", |r| r.get(0))
            .map_err(fail)?;
        if id == 0 && version == 0 {
            let count: i64 = db
                .query_row(
                    "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'",
                    [],
                    |r| r.get(0),
                )
                .map_err(fail)?;
            if count != 0 {
                return Err(Failure);
            }
            #[cfg(unix)]
            {
                use std::os::unix::fs::PermissionsExt;
                std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o600))
                    .map_err(fail)?;
            }
            let token = NativeIdentitySource::new().host_epoch().map_err(fail)?;
            let transaction = db.transaction().map_err(fail)?;
            transaction.execute_batch(SCHEMA).map_err(fail)?;
            transaction
                .execute(
                    "INSERT INTO metadata VALUES('schema_hash',?1)",
                    [digest(SCHEMA.as_bytes())],
                )
                .map_err(fail)?;
            transaction
                .execute(
                    "INSERT INTO metadata VALUES('sync_token',?1)",
                    [token.as_str()],
                )
                .map_err(fail)?;
            transaction
                .pragma_update(None, "application_id", APPLICATION_ID)
                .map_err(fail)?;
            transaction
                .pragma_update(None, "user_version", 1)
                .map_err(fail)?;
            transaction.commit().map_err(fail)?;
        } else if id != APPLICATION_ID || version != 1 {
            return Err(Failure);
        }
        let hash: String = db
            .query_row(
                "SELECT value FROM metadata WHERE key='schema_hash'",
                [],
                |r| r.get(0),
            )
            .map_err(fail)?;
        if hash != digest(SCHEMA.as_bytes()) {
            return Err(Failure);
        }
        // Keep append-only capture bounded. Reaching the cap rejects new writes;
        // it never deletes historical evidence. Size follows the database page size.
        let page_size: i64 = db
            .pragma_query_value(None, "page_size", |r| r.get(0))
            .map_err(fail)?;
        db.pragma_update(None, "max_page_count", (256_i64 * 1024 * 1024) / page_size)
            .map_err(fail)?;
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o600)).map_err(fail)?;
        }
        let token: String = db
            .query_row(
                "SELECT value FROM metadata WHERE key='sync_token'",
                [],
                |r| r.get(0),
            )
            .map_err(fail)?;
        Ok(Self {
            db,
            token: HostEpoch::new(token).map_err(fail)?,
        })
    }
    fn ingest(&mut self, bytes: &[u8]) -> std::result::Result<(), u16> {
        if bytes.len() > LIMIT {
            return Err(413);
        }
        let values: Vec<Value> = serde_json::from_slice(bytes).map_err(|_| 400u16)?;
        if values.is_empty() || values.len() > 16 || values.iter().any(|v| !depth(v, 0)) {
            return Err(400);
        }
        let mut records = Vec::new();
        for value in values {
            let hash = digest(value.to_string().as_bytes());
            let e: Envelope = serde_json::from_value(value).map_err(|_| 400u16)?;
            if e.kind != "diagnostic"
                || e.schema != 1
                || !label(&e.source_id, 128)
                || !label(&e.session, 128)
                || !label(&e.stream, 64)
                || !label(&e.event, 128)
                || !label(&e.producer_version, 64)
                || !decimal(&e.sequence)
                || e.utc_ms.as_ref().is_some_and(|s| !decimal(s))
                || !e.payload.is_object()
            {
                return Err(400);
            }
            records.push((e, hash));
        }
        let transaction = self.db.transaction().map_err(|_| 503u16)?;
        for (e, hash) in records {
            let old: Option<String> = transaction.query_row("SELECT body_hash FROM event WHERE source_id=?1 AND session=?2 AND stream=?3 AND sequence=?4",
                params![e.source_id,e.session,e.stream,e.sequence],|r|r.get(0)).optional().map_err(|_|503u16)?;
            if let Some(old) = old {
                if old != hash {
                    return Err(409);
                }
                continue;
            }
            transaction.execute("INSERT INTO event(source_id,session,stream,sequence,event_type,producer_version,utc_ms,payload,body_hash) VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9)",
                params![e.source_id,e.session,e.stream,e.sequence,e.event,e.producer_version,e.utc_ms,e.payload.to_string(),hash]).map_err(|_|503u16)?;
        }
        transaction.commit().map_err(|_| 503u16)?;
        Ok(())
    }
    fn timeline(&self) -> Result<(u64, Vec<AstrometricsEvent>)> {
        let count: i64 = self
            .db
            .query_row("SELECT count(*) FROM event", [], |r| r.get(0))
            .map_err(fail)?;
        let mut query = self.db.prepare("SELECT id,session,stream,sequence,event_type,utc_ms,payload FROM event ORDER BY id DESC LIMIT 30").map_err(fail)?;
        let rows = query
            .query_map([], |r| {
                let payload: String = r.get(6)?;
                let event: String = r.get(4)?;
                let value: Value = serde_json::from_str(&payload).unwrap_or(Value::Null);
                let summary = explain(&event, &value);
                Ok(AstrometricsEvent {
                    row_id: ProgressCount::new(r.get::<_, u32>(0)?.into()),
                    source_session: r.get(1)?,
                    stream: r.get(2)?,
                    sequence: r.get(3)?,
                    event_type: event,
                    utc_ms: r.get(5)?,
                    summary,
                    payload_json: clip(&payload, 512),
                    payload_truncated: payload.len() > 512,
                })
            })
            .map_err(fail)?;
        Ok((
            u64::try_from(count).map_err(fail)?,
            rows.collect::<std::result::Result<Vec<_>, _>>()
                .map_err(fail)?,
        ))
    }
}
fn explain(event: &str, payload: &Value) -> String {
    if event.contains("rejected") {
        return "A rejection was observed. Inspect the surrounding claim events; reward delivery is unconfirmed.".into();
    }
    if event.contains("stall") || event.contains("pending") {
        return "The producer reported pending/stalled work. Missing completion evidence does not establish a failed redemption.".into();
    }
    if payload
        .pointer("/data/claim/result_published")
        .and_then(Value::as_bool)
        == Some(true)
    {
        return "Claim result publication was observed. This is separate from reward presentation and inventory balance.".into();
    }
    "Recorded producer observation. Event order is local to its source/session/stream; cross-stream timing does not prove causation.".into()
}

struct Receiver {
    stop: Arc<AtomicBool>,
    owner: Option<thread::JoinHandle<()>>,
}
impl Drop for Receiver {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(owner) = self.owner.take() {
            let _ = owner.join();
        }
    }
}
fn read_http(
    stream: &mut TcpStream,
    token: &str,
    stop: &AtomicBool,
) -> std::result::Result<Vec<u8>, u16> {
    stream
        .set_read_timeout(Some(Duration::from_millis(100)))
        .map_err(|_| 400u16)?;
    stream
        .set_write_timeout(Some(Duration::from_millis(100)))
        .map_err(|_| 400u16)?;
    let deadline = Instant::now() + Duration::from_secs(2);
    let mut bytes = Vec::new();
    let header_end = loop {
        if stop.load(Ordering::Acquire) || Instant::now() >= deadline {
            return Err(408);
        }
        let mut chunk = [0; 1024];
        match stream.read(&mut chunk) {
            Ok(0) => return Err(400),
            Ok(n) => bytes.extend_from_slice(&chunk[..n]),
            Err(e)
                if matches!(
                    e.kind(),
                    std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut
                ) =>
            {
                continue;
            }
            Err(_) => return Err(400),
        }
        if let Some(n) = bytes.windows(4).position(|b| b == b"\r\n\r\n") {
            if n > 8192 {
                return Err(431);
            }
            break n + 4;
        }
        if bytes.len() > 8192 {
            return Err(431);
        }
    };
    let header = std::str::from_utf8(&bytes[..header_end]).map_err(|_| 400u16)?;
    let mut lines = header.split("\r\n");
    if lines.next() != Some("POST /sync HTTP/1.1") {
        return Err(404);
    }
    let mut length = None;
    let mut authenticated = false;
    let mut auth_seen = false;
    let mut content_type = false;
    for line in lines.filter(|s| !s.is_empty()) {
        let (name, value) = line.split_once(':').ok_or(400u16)?;
        let value = value.trim();
        match name.to_ascii_lowercase().as_str() {
            "content-length" => {
                if length.is_some() {
                    return Err(400);
                }
                length = Some(value.parse::<usize>().map_err(|_| 400u16)?);
            }
            "stfc-sync-token" => {
                if auth_seen {
                    return Err(400);
                }
                auth_seen = true;
                authenticated = value == token;
            }
            "content-type" => {
                content_type = value.eq_ignore_ascii_case("application/json");
            }
            "transfer-encoding" | "origin" | "expect" => return Err(400),
            _ => {}
        }
    }
    if !authenticated {
        return Err(401);
    }
    if !content_type {
        return Err(415);
    }
    let length = length.ok_or(411u16)?;
    if length > LIMIT {
        return Err(413);
    }
    while bytes.len() - header_end < length {
        if stop.load(Ordering::Acquire) || Instant::now() >= deadline {
            return Err(408);
        }
        let mut chunk = [0; 4096];
        let remaining = (length - (bytes.len() - header_end)).min(chunk.len());
        match stream.read(&mut chunk[..remaining]) {
            Ok(0) => return Err(400),
            Ok(n) => bytes.extend_from_slice(&chunk[..n]),
            Err(e)
                if matches!(
                    e.kind(),
                    std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut
                ) =>
            {
                continue;
            }
            Err(_) => return Err(400),
        }
    }
    if bytes.len() - header_end != length {
        return Err(400);
    }
    Ok(bytes.split_off(header_end))
}

pub struct Astrometrics {
    path: PathBuf,
    store: Option<Arc<Mutex<Store>>>,
    receiver: Option<Receiver>,
    rejected: Arc<AtomicU64>,
    unavailable: bool,
    token: Option<HostEpoch>,
}
impl Astrometrics {
    pub fn new(path: PathBuf) -> Self {
        Self {
            path,
            store: None,
            receiver: None,
            rejected: Arc::new(AtomicU64::new(0)),
            unavailable: false,
            token: None,
        }
    }
    pub fn set_enabled(&mut self, enabled: bool) {
        if !enabled {
            self.receiver.take();
            self.unavailable = false;
            return;
        }
        if self.receiver.is_some() {
            return;
        }
        self.unavailable = self.start().is_err();
    }
    fn start(&mut self) -> Result<()> {
        let listener = TcpListener::bind(ADDRESS).map_err(fail)?;
        listener.set_nonblocking(true).map_err(fail)?;
        let store = match &self.store {
            Some(s) => s.clone(),
            None => Arc::new(Mutex::new(Store::open(&self.path)?)),
        };
        let receiver_token = store.lock().map_err(fail)?.token.clone();
        let token = receiver_token.as_str().to_owned();
        let stop = Arc::new(AtomicBool::new(false));
        let worker_stop = stop.clone();
        let worker_store = store.clone();
        let rejected = self.rejected.clone();
        let owner=thread::Builder::new().name("bridge-astrometrics".into()).spawn(move|| {
            while !worker_stop.load(Ordering::Acquire) {
                match listener.accept() {
                    Ok((mut stream,address))=>{
                        let code=if !address.ip().is_loopback(){403}else {
                            match read_http(&mut stream,&token,&worker_stop) {
                                Ok(bytes)=>worker_store.lock().map_err(|_|503u16).and_then(|mut s|s.ingest(&bytes)).map_or_else(|code|code,|_|200),
                                Err(code)=>code
                            }
                        };
                        if code!=200 {rejected.fetch_add(1,Ordering::Relaxed);}
                        let _=write!(stream,"HTTP/1.1 {code} Result\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    },
                    Err(e) if e.kind()==std::io::ErrorKind::WouldBlock=>thread::sleep(Duration::from_millis(10)),
                    Err(_)=>break,
                }
            }
        }).map_err(fail)?;
        self.token = Some(receiver_token);
        self.store = Some(store);
        self.receiver = Some(Receiver {
            stop,
            owner: Some(owner),
        });
        Ok(())
    }
    pub fn view(&self) -> AstrometricsView {
        let enabled = self.receiver.is_some();
        let facts = self
            .store
            .as_ref()
            .and_then(|s| s.lock().ok())
            .and_then(|s| s.timeline().ok().map(|t| (s.token.clone(), t)));
        let failed = self.unavailable
            || (self.store.is_some() && facts.is_none())
            || self
                .receiver
                .as_ref()
                .is_some_and(|r| r.owner.as_ref().is_none_or(|o| o.is_finished()));
        let (count, events) = facts.map(|(_, t)| t).unwrap_or((0, vec![]));
        AstrometricsView {
            available: !failed,
            enabled,
            notice: if failed {
                AstrometricsNotice::Unavailable
            } else if enabled {
                AstrometricsNotice::Listening
            } else {
                AstrometricsNotice::Disabled
            },
            stored_events: ProgressCount::new(count),
            rejected_requests: ProgressCount::new(self.rejected.load(Ordering::Relaxed)),
            endpoint: enabled.then(|| ENDPOINT.into()),
            sync_token: if enabled { self.token.clone() } else { None },
            events: BoundedList::new(events).unwrap(),
        }
    }
}

#[cfg(test)]
mod tests;
