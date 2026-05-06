//! AIMemory Desktop — Rust shell for the Tauri control panel.
//!
//! Surfaces a small command set to the React frontend:
//!
//! * [`runtime_config`] — reads the API URL + key the API service writes to
//!   `%ProgramData%\AIMemory\Api\runtime.json` on startup, so the frontend
//!   can talk to the local API without prompting the user.
//! * [`service_status`], [`service_start`], [`service_stop`], [`service_restart`]
//!   — control the `aimemory-api` and `aimemory-ingestor` Windows services
//!   via the SCM (no UAC prompts during normal use; install/uninstall is
//!   the installer's job).
//! * [`pick_folder`] — folder picker for "Add watch path".

mod runtime;
mod services;

use serde::Serialize;

#[derive(Debug, thiserror::Error, Serialize)]
pub enum DesktopError {
    #[error("io error: {0}")]
    Io(String),
    #[error("service error: {0}")]
    Service(String),
    #[error("not supported on this platform")]
    Unsupported,
    #[error("not found")]
    NotFound,
}

impl From<std::io::Error> for DesktopError {
    fn from(value: std::io::Error) -> Self {
        DesktopError::Io(value.to_string())
    }
}

#[tauri::command]
fn runtime_config() -> Result<runtime::RuntimeConfig, DesktopError> {
    runtime::read_runtime_config()
}

#[tauri::command]
fn service_status(name: services::ServiceName) -> Result<services::ServiceStatus, DesktopError> {
    services::status(name)
}

#[tauri::command]
async fn service_start(name: services::ServiceName) -> Result<(), DesktopError> {
    services::start(name)
}

#[tauri::command]
async fn service_stop(name: services::ServiceName) -> Result<(), DesktopError> {
    services::stop(name)
}

#[tauri::command]
async fn service_restart(name: services::ServiceName) -> Result<(), DesktopError> {
    services::stop(name)?;
    // Brief pause to let SCM transition through STOP_PENDING before we re-issue Start.
    std::thread::sleep(std::time::Duration::from_millis(500));
    services::start(name)
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_fs::init())
        .invoke_handler(tauri::generate_handler![
            runtime_config,
            service_status,
            service_start,
            service_stop,
            service_restart,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
