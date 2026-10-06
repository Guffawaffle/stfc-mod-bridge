#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    // This foundation exposes no filesystem, process or mutation IPC commands.
    tauri::Builder::default()
        .run(tauri::generate_context!())
        .expect("failed to start Bridge desktop shell");
}
