#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod ipc;
use std::{path::PathBuf, sync::Arc};
use tauri::{Manager, RunEvent, webview::PageLoadEvent};

fn main() {
    // The native host chooses this root. Renderer requests cannot supply paths
    // or alter the compiled native pins. Packaging the qualified native inputs
    // remains separate from this development observation flow.
    let mut args = std::env::args_os().skip(1);
    let mut native_root = std::env::current_exe()
        .ok()
        .and_then(|path| path.parent().map(PathBuf::from))
        .unwrap_or_default();
    while let Some(argument) = args.next() {
        if argument == "--native-root" {
            let Some(root) = args.next() else {
                return;
            };
            native_root = PathBuf::from(root);
        } else {
            return;
        }
    }
    let app = tauri::Builder::default()
        .plugin(
            tauri::plugin::Builder::<tauri::Wry, ()>::new("bridge-local-document")
                .on_navigation(|webview, url| webview.label() == "main" && ipc::allowed_url(url))
                .build(),
        )
        .setup(move |app| {
            app.manage(ipc::Desktop::new(native_root)?);
            Ok(())
        })
        .on_page_load(|webview, payload| {
            if webview.label() == "main"
                && payload.event() == PageLoadEvent::Started
                && let Some(state) = webview.try_state::<Arc<ipc::Desktop>>()
            {
                state.new_document();
            }
        })
        .invoke_handler(tauri::generate_handler![
            ipc::bridge_bootstrap,
            ipc::bridge_exchange,
            ipc::bridge_subscribe,
            ipc::bridge_poll,
            ipc::bridge_unsubscribe
        ])
        .build(tauri::generate_context!())
        .expect("failed to start Bridge desktop shell");
    app.run(|app, event| {
        if let RunEvent::ExitRequested { api, .. } = event {
            let state = app.state::<Arc<ipc::Desktop>>();
            if !state.is_closed() {
                api.prevent_exit();
                state.inner().shutdown(app.clone());
            }
        }
    });
}
