//! Native composition for the read-only observation host. Renderer arguments
//! cannot choose a module, catalog root, filesystem path or game process.
use bridge_app::{
    NativeIdentitySource,
    observations::{ProfilesObservations, ReadOnlyApplication},
};
use bridge_engine::host::{HostHandle, spawn_local_host};
use bridge_host_adapter::{
    controller::{CloseStart, ResponseFailure, SubscribeStart, TransportController},
    registration::{SystemClock, TrustedEpoch},
};
use serde_json::{Value, json};
use std::{
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::Duration,
};
use tauri::{State, WebviewWindow, ipc::Response};

type Failure = Value;
fn refused() -> Failure {
    json!({"schemaVersion":1,"code":"unavailable_binding","delivery":"not_sent"})
}
fn uncertain() -> Failure {
    json!({"schemaVersion":1,"code":"delivery_failed","delivery":"may_have_reached_backend"})
}
fn native_fault(value: impl serde::Serialize) -> Failure {
    serde_json::to_value(value).unwrap_or_else(|_| uncertain())
}

pub fn allowed_url(url: &tauri::Url) -> bool {
    if !url.username().is_empty()
        || url.password().is_some()
        || url.query().is_some()
        || !matches!(url.path(), "/" | "/index.html")
    {
        return false;
    }
    let packaged = url.port().is_none()
        && ((url.scheme() == "tauri" && url.host_str() == Some("localhost"))
            || (matches!(url.scheme(), "http" | "https")
                && url.host_str() == Some("tauri.localhost")));
    let development = !cfg!(feature = "custom-protocol")
        && url.scheme() == "http"
        && url.host_str() == Some("127.0.0.1")
        && url.port() == Some(1420);
    packaged || development
}
struct Document {
    epoch: String,
    controller: Arc<TransportController>,
}
struct Documents {
    current: Option<Document>,
    retired: Vec<Arc<TransportController>>,
}
pub struct Desktop {
    host: HostHandle,
    documents: Mutex<Documents>,
    shutting_down: AtomicBool,
    closed: AtomicBool,
    owner: Mutex<
        Option<
            thread::JoinHandle<
                Result<bridge_engine::host::HostExit, bridge_engine::host::HostFailure>,
            >,
        >,
    >,
    service: Mutex<Option<thread::JoinHandle<()>>>,
}
impl Desktop {
    pub fn new(
        root: PathBuf,
        astrometrics_db: PathBuf,
    ) -> Result<Arc<Self>, Box<dyn std::error::Error>> {
        let (host, owner) = spawn_local_host(move || {
            ReadOnlyApplication::new(ProfilesObservations::open(&root))
                .map(|host| host.with_astrometrics(astrometrics_db))
        })?;
        let desktop = Arc::new(Self {
            host,
            documents: Mutex::new(Documents {
                current: None,
                retired: vec![],
            }),
            shutting_down: AtomicBool::new(false),
            closed: AtomicBool::new(false),
            owner: Mutex::new(Some(owner)),
            service: Mutex::new(None),
        });
        desktop.new_document();
        let weak = Arc::downgrade(&desktop);
        let service = thread::Builder::new()
            .name("bridge-observation-delivery".into())
            .spawn(move || {
                loop {
                    {
                        let Some(desktop) = weak.upgrade() else {
                            break;
                        };
                        if desktop.shutting_down.load(Ordering::Acquire) {
                            break;
                        }
                        desktop.service();
                    }
                    thread::sleep(Duration::from_millis(5));
                }
            })?;
        *desktop
            .service
            .lock()
            .map_err(|_| "service state unavailable")? = Some(service);
        Ok(desktop)
    }
    fn service(&self) {
        let controllers = if let Ok(documents) = self.documents.lock() {
            documents
                .current
                .iter()
                .map(|document| document.controller.clone())
                .chain(documents.retired.iter().cloned())
                .collect::<Vec<_>>()
        } else {
            return;
        };
        for controller in controllers {
            let _ = controller.service_exchanges();
            let _ = controller.expire();
        }
        if let Ok(mut documents) = self.documents.lock() {
            documents.retired.retain(|controller| {
                let Ok(delivery) = controller.snapshot() else {
                    return true;
                };
                let Ok(registry) = controller.observation_snapshot() else {
                    return true;
                };
                delivery.pending + delivery.settled + delivery.building > 0
                    || registry.preparing + registry.ready + registry.closing > 0
                    || registry.poll_reserved
            });
        }
    }
    pub fn new_document(&self) {
        let Ok(mut documents) = self.documents.lock() else {
            return;
        };
        if let Some(old) = documents.current.take() {
            let _ = old.controller.revoke_document();
            documents.retired.push(old.controller);
        }
        if self.shutting_down.load(Ordering::Acquire) || documents.retired.len() >= 8 {
            return;
        }
        let Ok(epoch) = NativeIdentitySource::new().host_epoch() else {
            return;
        };
        let Ok(trusted) = TrustedEpoch::from_root(epoch.as_str()) else {
            return;
        };
        documents.current = Some(Document {
            epoch: epoch.as_str().to_owned(),
            controller: TransportController::new(self.host.clone(), trusted, Arc::new(SystemClock)),
        });
    }
    fn with_current<T>(
        &self,
        window: &WebviewWindow,
        epoch: Option<&str>,
        operation: impl FnOnce(&Document) -> Result<T, Failure>,
    ) -> Result<T, Failure> {
        if window.label() != "main"
            || !window.url().is_ok_and(|url| allowed_url(&url))
            || self.shutting_down.load(Ordering::Acquire)
        {
            return Err(refused());
        }
        let documents = self.documents.lock().map_err(|_| refused())?;
        let document = documents.current.as_ref().ok_or_else(refused)?;
        if epoch.is_some_and(|epoch| epoch != document.epoch) {
            return Err(refused());
        }
        operation(document)
    }
    pub fn is_closed(&self) -> bool {
        self.closed.load(Ordering::Acquire)
    }
    pub fn shutdown(self: &Arc<Self>, app: tauri::AppHandle) {
        if self.shutting_down.swap(true, Ordering::AcqRel) {
            return;
        }
        if let Ok(documents) = self.documents.lock() {
            for controller in documents
                .current
                .iter()
                .map(|document| &document.controller)
                .chain(documents.retired.iter())
            {
                let _ = controller.stop_submission();
                let _ = controller.revoke_document();
            }
        }
        let desktop = self.clone();
        thread::spawn(move || {
            // The owner is read-only, but a native read must return and release
            // its module/handles before normal process exit. Never stop the game.
            loop {
                if desktop
                    .owner
                    .lock()
                    .ok()
                    .and_then(|owner| owner.as_ref().map(|owner| owner.is_finished()))
                    .unwrap_or(false)
                {
                    break;
                }
                if let Ok(pending) = desktop.host.request_close() {
                    loop {
                        match pending.recv_timeout(Duration::from_millis(50)) {
                            Ok(_) | Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => break,
                            Err(std::sync::mpsc::RecvTimeoutError::Timeout) => continue,
                        }
                    }
                    break;
                }
                thread::sleep(Duration::from_millis(10));
            }
            if let Ok(mut service) = desktop.service.lock()
                && let Some(service) = service.take()
            {
                let _ = service.join();
            }
            if let Ok(mut owner) = desktop.owner.lock()
                && let Some(owner) = owner.take()
            {
                let _ = owner.join();
            }
            desktop.closed.store(true, Ordering::Release);
            app.exit(0);
        });
    }
}

#[tauri::command]
pub fn bridge_bootstrap(
    window: WebviewWindow,
    state: State<'_, Arc<Desktop>>,
) -> Result<Value, Failure> {
    state.with_current(&window, None, |document| {
        Ok(json!({"documentEpoch":document.epoch}))
    })
}
#[tauri::command]
pub async fn bridge_exchange(
    window: WebviewWindow,
    state: State<'_, Arc<Desktop>>,
    document_epoch: String,
    frame: String,
) -> Result<Response, Failure> {
    let desktop = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let controller = desktop.with_current(&window, Some(&document_epoch), |document| {
            Ok(document.controller.clone())
        })?;
        let mut ticket = controller
            .submit_exchange(frame.into_bytes())
            .map_err(native_fault)?;
        loop {
            desktop.with_current(&window, Some(&document_epoch), |_| Ok(()))?;
            if let Some(work) = ticket.take_response().map_err(native_fault)? {
                let built = work
                    .build(|bytes| {
                        let frame = std::str::from_utf8(bytes).map_err(|_| ResponseFailure)?;
                        Ok(Response::new(
                            serde_json::to_string(frame).map_err(|_| ResponseFailure)?,
                        ))
                    })
                    .map_err(native_fault)?;
                return desktop.with_current(&window, Some(&document_epoch), |_| {
                    built.commit(Ok).map_err(native_fault)
                });
            }
            thread::sleep(Duration::from_millis(5));
        }
    })
    .await
    .map_err(|_| uncertain())?
}
#[tauri::command]
pub async fn bridge_subscribe(
    window: WebviewWindow,
    state: State<'_, Arc<Desktop>>,
    document_epoch: String,
    registration_key: String,
) -> Result<Response, Failure> {
    let desktop = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let controller = desktop.with_current(&window, Some(&document_epoch), |document| {
            Ok(document.controller.clone())
        })?;
        let ready = match controller
            .begin_subscribe(&registration_key)
            .map_err(native_fault)?
        {
            SubscribeStart::Ready(ready) => ready,
            SubscribeStart::Prepare(work) => work
                .attach()
                .map_err(native_fault)?
                .wait_to_ready()
                .map_err(native_fault)?,
        };
        desktop.with_current(&window, Some(&document_epoch), |_| {
            ready.recheck().map_err(native_fault)?;
            Ok(Response::new(
                serde_json::to_string(ready.ack()).map_err(|_| uncertain())?,
            ))
        })
    })
    .await
    .map_err(|_| uncertain())?
}
#[tauri::command]
pub fn bridge_poll(
    window: WebviewWindow,
    state: State<'_, Arc<Desktop>>,
    document_epoch: String,
    registration_key: String,
) -> Result<Response, Failure> {
    state.with_current(&window, Some(&document_epoch), |document| {
        let built = document
            .controller
            .begin_poll(&registration_key)
            .map_err(native_fault)?
            .build(|encoded| Ok(Response::new(encoded.to_owned())))
            .map_err(native_fault)?;
        built.commit(Ok).map_err(native_fault)
    })
}
#[tauri::command]
pub async fn bridge_unsubscribe(
    window: WebviewWindow,
    state: State<'_, Arc<Desktop>>,
    document_epoch: String,
    registration_key: String,
) -> Result<Response, Failure> {
    let desktop = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let controller = desktop.with_current(&window, Some(&document_epoch), |document| {
            Ok(document.controller.clone())
        })?;
        let ack = match controller
            .unsubscribe(&registration_key)
            .map_err(native_fault)?
        {
            CloseStart::Closed(ack) => ack,
            CloseStart::Waiting(work) => work
                .wait_timeout(Duration::from_secs(5))
                .map_err(native_fault)?,
        };
        desktop.with_current(&window, Some(&document_epoch), |_| {
            Ok(Response::new(
                serde_json::to_string(&ack).map_err(|_| uncertain())?,
            ))
        })
    })
    .await
    .map_err(|_| uncertain())?
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn native_binding_refuses_remote_credentials_queries_and_other_documents() {
        for value in [
            "https://example.com/",
            "http://tauri.localhost/other.html",
            "http://user@tauri.localhost/",
            "http://tauri.localhost/?target=other",
            "http://127.0.0.1:1421/",
            "file:///C:/unexpected.html",
        ] {
            assert!(!allowed_url(&tauri::Url::parse(value).unwrap()), "{value}");
        }
        assert!(allowed_url(
            &tauri::Url::parse("http://tauri.localhost/").unwrap()
        ));
        assert!(allowed_url(
            &tauri::Url::parse("tauri://localhost/index.html").unwrap()
        ));
        assert_eq!(
            allowed_url(&tauri::Url::parse("http://127.0.0.1:1420/").unwrap()),
            !cfg!(feature = "custom-protocol")
        );
    }
}
