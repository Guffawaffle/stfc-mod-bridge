//! Opt-in acceptance of the production receiver, dispatcher, owner and SQLite store.
//! The fixed production port must be free; this test never stops another listener.
use bridge_app::{
    NativeIdentitySource,
    observations::{ObservationSource, ReadOnlyApplication, Views, unavailable},
};
use bridge_contracts::v1::*;
use bridge_engine::host::{HostExit, HostFailure, HostHandle, OwnedFrame, spawn_local_host};
use rusqlite::{Connection, OpenFlags};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::{
    cell::Cell,
    fs,
    io::{Read, Write},
    net::{SocketAddr, TcpListener, TcpStream},
    path::{Path, PathBuf},
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

const ADDRESS: &str = "127.0.0.1:58237";
const MAXIMUM: &str = "18446744073709551615";
type Check<T> = Result<T, &'static str>;

struct MissingCatalog;
impl ObservationSource for MissingCatalog {
    fn read(&mut self, _: &Evidence) -> Views {
        Views {
            profiles: unavailable(ObservationReason::NativeUnavailable),
            installations: unavailable(ObservationReason::NativeUnavailable),
            sessions: unavailable(ObservationReason::NativeUnavailable),
        }
    }
}

struct Fixture {
    directory: PathBuf,
    database: PathBuf,
    retain_observation: bool,
}
impl Fixture {
    fn new() -> Check<Self> {
        let supplied = std::env::var_os("BRIDGE_ASTROMETRICS_ACCEPTANCE_DIR");
        let retain_observation = supplied.is_some();
        let directory = if let Some(value) = supplied {
            let path = PathBuf::from(value);
            if !path.is_absolute() || !path.is_dir() {
                return Err("Acceptance output must be an absolute existing directory");
            }
            path.canonicalize()
                .map_err(|_| "Cannot resolve acceptance output")?
        } else {
            let epoch = NativeIdentitySource::new()
                .host_epoch()
                .map_err(|_| "Cannot allocate private fixture identity")?;
            let path = std::env::temp_dir()
                .join(format!("bridge-astrometrics-acceptance-{}", epoch.as_str()));
            fs::create_dir(&path).map_err(|_| "Cannot create private fixture directory")?;
            path
        };
        let database = directory.join("fixture-events.sqlite");
        if database.exists()
            || directory.join("fixture-events.sqlite-journal").exists()
            || directory.join("fixture-events.sqlite-wal").exists()
            || directory.join("fixture-events.sqlite-shm").exists()
            || directory.join("astrometrics-observation.json").exists()
        {
            return Err("Acceptance output already contains fixture artifacts; refusing reuse");
        }
        Ok(Self {
            directory,
            database,
            retain_observation,
        })
    }
    fn remove_database(&self) -> Check<()> {
        for name in [
            "fixture-events.sqlite",
            "fixture-events.sqlite-journal",
            "fixture-events.sqlite-wal",
            "fixture-events.sqlite-shm",
        ] {
            match fs::remove_file(self.directory.join(name)) {
                Ok(()) => {}
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
                Err(_) => return Err("Cannot remove private acceptance database artifact"),
            }
        }
        Ok(())
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        // Exact files belonging to this fresh fixture only; never recursively delete.
        for name in [
            "fixture-events.sqlite",
            "fixture-events.sqlite-journal",
            "fixture-events.sqlite-wal",
            "fixture-events.sqlite-shm",
        ] {
            let _ = fs::remove_file(self.directory.join(name));
        }
        if !self.retain_observation {
            let _ = fs::remove_file(self.directory.join("astrometrics-observation.json"));
            let _ = fs::remove_dir(&self.directory);
        }
    }
}

struct Application {
    handle: HostHandle,
    owner: Option<JoinHandle<Result<HostExit, HostFailure>>>,
    next_request: Cell<u64>,
}
impl Application {
    fn new(database: &Path) -> Check<Self> {
        let database = database.to_owned();
        let (handle, owner) = spawn_local_host(move || {
            ReadOnlyApplication::new(MissingCatalog).map(|host| host.with_astrometrics(database))
        })
        .map_err(|_| "Cannot spawn production application owner")?;
        Ok(Self {
            handle,
            owner: Some(owner),
            next_request: Cell::new(1),
        })
    }
    fn view(&self, enabled: Option<bool>) -> Check<AstrometricsView> {
        let id = self.next_request.get();
        self.next_request.set(id + 1);
        let body = enabled.map_or_else(
            || json!({"type":"query","query":{"name":"astrometrics","input":{}}}),
            |enabled| json!({"type":"command","command":{"name":"set_astrometrics","input":{"enabled":enabled}}}),
        );
        let encoded = serde_json::to_vec(&json!({"protocolVersion":1,
            "requestId":format!("10000000-0000-4000-8000-{id:012x}"),"body":body}))
        .map_err(|_| "Cannot encode acceptance request")?;
        decode_request(&encoded).map_err(|_| "Acceptance request failed protocol validation")?;
        let pending = self
            .handle
            .exchange(OwnedFrame::new(encoded).map_err(|_| "Cannot frame acceptance request")?)
            .map_err(|_| "Application refused acceptance exchange")?;
        let response = pending
            .recv_timeout(Duration::from_secs(15))
            .map_err(|_| "Application exchange timed out")?
            .map_err(|_| "Application exchange failed")?;
        let reply = decode_reply(response.as_bytes())
            .map_err(|_| "Application reply failed protocol validation")?;
        // Never format a reply or view: an enabled view contains the private token.
        match reply.into_inner().body {
            ReplyBody::Result {
                result: ResultPayload::Query { query },
            } if enabled.is_none() => {
                if let QueryResult::Astrometrics(view) = *query {
                    Ok(view)
                } else {
                    Err("Unexpected query reply kind")
                }
            }
            ReplyBody::Result {
                result: ResultPayload::Command { command },
            } if enabled.is_some() => {
                if let CommandResult::SetAstrometrics(view) = *command {
                    Ok(view)
                } else {
                    Err("Unexpected command reply kind")
                }
            }
            _ => Err("Application rejected acceptance request"),
        }
    }
    fn close(&mut self) -> Check<()> {
        let Some(owner) = self.owner.as_ref() else {
            return Ok(());
        };
        let pending = self
            .handle
            .request_close()
            .map_err(|_| "Cannot request normal owner close")?;
        let disposition = pending
            .recv_timeout(Duration::from_secs(5))
            .map_err(|_| "Normal owner close timed out")?
            .map_err(|_| "Normal owner close failed")?;
        if !matches!(disposition, CloseDisposition::Ready) {
            return Err("Normal owner close was not ready");
        }
        let deadline = Instant::now() + Duration::from_secs(5);
        while !owner.is_finished() && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(5));
        }
        if !owner.is_finished() {
            return Err("Application owner did not finish after ready close");
        }
        let exit = self
            .owner
            .take()
            .ok_or("Application owner disappeared")?
            .join()
            .map_err(|_| "Application owner panicked")?
            .map_err(|_| "Application owner exited unsuccessfully")?;
        if !matches!(exit.disposition, CloseDisposition::Ready) || exit.failure.is_some() {
            return Err("Application owner did not exit cleanly");
        }
        Ok(())
    }
}
impl Drop for Application {
    fn drop(&mut self) {
        let _ = self.close();
    }
}

fn event(sequence: &str) -> Value {
    json!({"type":"diagnostic","schema":1,"source_id":"acceptance-source","producer_version":"1.1.9.4",
        "session":"acceptance-session","stream":"claims/main","sequence":sequence,"utc_ms":"1792470620804",
        "event":"summary_navigation_rejected","payload":{"claim":"acceptance-claim","quantity":MAXIMUM}})
}
fn post(token: &str, events: &[Value]) -> Check<u16> {
    let address: SocketAddr = ADDRESS
        .parse()
        .map_err(|_| "Invalid fixed acceptance address")?;
    let mut stream = TcpStream::connect_timeout(&address, Duration::from_secs(2))
        .map_err(|_| "Cannot connect to production receiver")?;
    stream
        .set_read_timeout(Some(Duration::from_secs(5)))
        .map_err(|_| "Cannot bound HTTP read")?;
    stream
        .set_write_timeout(Some(Duration::from_secs(2)))
        .map_err(|_| "Cannot bound HTTP write")?;
    let body = serde_json::to_vec(events).map_err(|_| "Cannot encode diagnostic envelope")?;
    write!(stream, "POST /sync HTTP/1.1\r\nHost: 127.0.0.1:58237\r\nContent-Type: application/json\r\nstfc-sync-token: {token}\r\nContent-Length: {}\r\n\r\n", body.len())
        .map_err(|_| "Cannot write diagnostic HTTP headers")?;
    stream
        .write_all(&body)
        .map_err(|_| "Cannot write diagnostic HTTP body")?;
    let mut response = Vec::new();
    stream
        .take(4097)
        .read_to_end(&mut response)
        .map_err(|_| "Cannot read diagnostic HTTP response")?;
    if response.len() > 4096 {
        return Err("Diagnostic HTTP response exceeded acceptance bound");
    }
    let text =
        std::str::from_utf8(&response).map_err(|_| "Diagnostic HTTP response is not UTF-8")?;
    let first = text
        .lines()
        .next()
        .ok_or("Diagnostic HTTP response has no status")?;
    let mut parts = first.split_whitespace();
    if parts.next() != Some("HTTP/1.1") {
        return Err("Diagnostic HTTP response has wrong version");
    }
    parts
        .next()
        .ok_or("Diagnostic HTTP response has no status code")?
        .parse()
        .map_err(|_| "Diagnostic HTTP response has invalid status code")
}
fn socket_closed() -> Check<bool> {
    let address: SocketAddr = ADDRESS
        .parse()
        .map_err(|_| "Invalid fixed acceptance address")?;
    match TcpStream::connect_timeout(&address, Duration::from_millis(250)) {
        Err(error) if error.kind() == std::io::ErrorKind::ConnectionRefused => Ok(true),
        _ => Ok(false),
    }
}
fn digest(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}

#[test]
#[ignore = "Requires exclusive access to the production loopback port; run through Astrometrics acceptance"]
fn astrometrics_real_receiver_acceptance() -> Check<()> {
    let reservation = TcpListener::bind(ADDRESS).map_err(
        |_| "Production Astrometrics port is occupied; refusing to disturb its listener",
    )?;
    let fixture = Fixture::new()?;
    let mut application = Application::new(&fixture.database)?;
    let initial = application.view(None)?;
    assert!(
        !initial.enabled && initial.history_available && initial.events.as_slice().is_empty(),
        "Fresh collector is not disabled/empty"
    );
    assert!(
        initial.sync_token.is_none(),
        "Disabled collector exposed a token"
    );
    assert!(
        !fixture.database.exists(),
        "Disabled collector created a database"
    );
    drop(reservation);
    assert!(socket_closed()?, "Disabled application opened a receiver");

    let listening = application.view(Some(true))?;
    assert!(
        listening.enabled
            && listening.available
            && listening.notice == AstrometricsNotice::Listening,
        "Production receiver did not start; port may have become occupied"
    );
    assert!(
        listening.endpoint.as_deref() == Some("http://127.0.0.1:58237/sync"),
        "Production endpoint differs from contract"
    );
    let token = listening
        .sync_token
        .as_ref()
        .ok_or("Listening collector has no token")?;
    let first = event("1");
    assert!(
        post("wrong-acceptance-token", std::slice::from_ref(&first))? == 401,
        "Wrong-token request was not refused"
    );
    assert!(
        application.view(None)?.stored_events.get() == 0,
        "Unauthorized request changed SQLite evidence"
    );
    assert!(
        post(token.as_str(), std::slice::from_ref(&first))? == 200,
        "Authenticated request was not committed"
    );
    assert!(
        application.view(None)?.stored_events.get() == 1,
        "Authenticated request did not produce one event"
    );
    assert!(
        post(token.as_str(), std::slice::from_ref(&first))? == 200,
        "Identical replay was not accepted"
    );
    assert!(
        application.view(None)?.stored_events.get() == 1,
        "Identical replay duplicated stored evidence"
    );
    let mut conflict = first.clone();
    conflict["payload"]["claim"] = json!("conflicting-claim");
    assert!(
        post(token.as_str(), &[event("2"), conflict])? == 409,
        "Conflicting replay was not refused atomically"
    );
    assert!(
        application.view(None)?.stored_events.get() == 1,
        "Conflicting batch changed stored evidence"
    );

    let disabled = application.view(Some(false))?;
    assert!(
        !disabled.enabled && disabled.history_available && disabled.stored_events.get() == 1,
        "Stopping collection hid retained history"
    );
    assert!(
        disabled.sync_token.is_none(),
        "Stopped collector exposed a token"
    );
    assert!(
        socket_closed()?,
        "Stopped collector retained a listening socket"
    );
    let restarted = application.view(Some(true))?;
    assert!(
        restarted.enabled && restarted.available,
        "Production receiver could not restart"
    );
    let restarted_token = restarted
        .sync_token
        .as_ref()
        .ok_or("Restarted collector has no token")?;
    assert!(
        restarted_token == token,
        "Receiver restart changed its retained token"
    );
    assert!(
        post(restarted_token.as_str(), &[event(MAXIMUM)])? == 200,
        "Maximum-sequence event was not committed"
    );
    assert!(
        application.view(None)?.stored_events.get() == 2,
        "Second event did not reach real SQLite"
    );
    application.close()?;
    let released =
        TcpListener::bind(ADDRESS).map_err(|_| "Normal close did not release production port")?;

    let bytes_before =
        fs::read(&fixture.database).map_err(|_| "Cannot read committed fixture database")?;
    {
        let db = Connection::open_with_flags(&fixture.database, OpenFlags::SQLITE_OPEN_READ_ONLY)
            .map_err(|_| "Cannot independently read committed SQLite database")?;
        let count: i64 = db
            .query_row("SELECT count(*) FROM event", [], |row| row.get(0))
            .map_err(|_| "Cannot independently count SQLite evidence")?;
        let original: String = db
            .query_row("SELECT payload FROM event WHERE sequence='1'", [], |row| {
                row.get(0)
            })
            .map_err(|_| "Cannot independently read original SQLite evidence")?;
        let original: Value =
            serde_json::from_str(&original).map_err(|_| "Original stored payload is not JSON")?;
        assert!(
            original["claim"].as_str() == Some("acceptance-claim")
                && original["quantity"].as_str() == Some(MAXIMUM),
            "Conflicting replay changed original SQLite evidence"
        );
        let payload: String = db
            .query_row(
                "SELECT payload FROM event WHERE sequence=?1",
                [MAXIMUM],
                |row| row.get(0),
            )
            .map_err(|_| "Maximum sequence was not preserved in SQLite")?;
        let payload: Value =
            serde_json::from_str(&payload).map_err(|_| "Stored payload is not JSON")?;
        assert!(
            count == 2 && payload["quantity"].as_str() == Some(MAXIMUM),
            "SQLite lost exact event count or unsigned integer text"
        );
    }
    let mut reopened = Application::new(&fixture.database)?;
    let history = reopened.view(None)?;
    assert!(
        !history.enabled && history.history_available && history.stored_events.get() == 2,
        "Recreated disabled application cannot inspect retained history"
    );
    assert!(
        history.sync_token.is_none(),
        "History-only application exposed a token"
    );
    assert!(
        history
            .events
            .as_slice()
            .iter()
            .any(|row| row.sequence == MAXIMUM),
        "History projection lost maximum sequence text"
    );
    reopened.close()?;
    let bytes_after =
        fs::read(&fixture.database).map_err(|_| "Cannot read database after history inspection")?;
    assert!(
        bytes_before == bytes_after,
        "Read-only history inspection modified database bytes"
    );
    drop(released);
    assert!(
        socket_closed()?,
        "History-only application opened a receiver"
    );

    // The database contains a real receiver token. Retain only its digest and
    // sanitized assertions in the wrapper's evidence directory.
    fixture.remove_database()?;

    if fixture.retain_observation {
        let observation = json!({"schemaVersion":"bridge-astrometrics-native-observation/v1", "testName":"astrometrics_real_receiver_acceptance",
            "success":true,"authenticatedStatus":200,"unauthorizedStatus":401,"duplicateStatus":200,"conflictingStatus":409,
            "storedEvents":2,"disabledCreatesNoStore":true,"disabledSocketClosed":true,"idempotentReplay":true,
            "conflictingReplayPreservesEvidence":true,"maximumSequencePreserved":true,"historyReadableWhileDisabled":true,
            "historyReadLeavesDatabaseUnchanged":true,"normalCloseJoinsOwner":true,"normalCloseReleasesPort":true,
            "databaseSha256":digest(&bytes_after)});
        let mut output = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(fixture.directory.join("astrometrics-observation.json"))
            .map_err(|_| "Cannot create sanitized acceptance observation")?;
        serde_json::to_writer_pretty(&mut output, &observation)
            .map_err(|_| "Cannot write sanitized acceptance observation")?;
        output
            .write_all(b"\n")
            .map_err(|_| "Cannot finish sanitized acceptance observation")?;
    }
    println!("ASTROMETRICS_ACCEPTANCE_OK");
    Ok(())
}
