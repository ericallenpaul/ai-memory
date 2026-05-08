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
//! * [`distributed_status`], [`distributed_enable`], [`distributed_disable`],
//!   [`pairings_list`], [`pairings_revoke`], [`list_network_interfaces`]
//!   — phase 8 admin surface for the "Allow remote ingestors" page; thin proxies over
//!   the local API's `/api/admin/distributed/*` and `/api/pairings*` endpoints.
//! * [`app_mode`], [`ingestor_test_connection`], [`ingestor_pair`],
//!   [`ingestor_config_get`], [`ingestor_config_clear`], [`host_id_get`]
//!   — phase 9 surface for the **secondary** machine's pairing wizard. `app_mode` flips
//!   the React shell into a stripped-down view when the install is ingestor-only, the
//!   rest implement the TLS-pin → auth-probe → POST /api/pairings → persist flow.

mod app_mode;
mod distributed;
mod ingestor;
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
    // Phase 8 enable flow re-uses this for the "Restart now" affordance, so the wait is
    // sized generously enough to clear most reasonable Kestrel teardowns.
    std::thread::sleep(std::time::Duration::from_millis(1500));
    services::start(name)
}

// ==================== Distributed-mode admin (phase 8) ====================
//
// These are async tauri commands so the blocking reqwest calls execute on Tauri's command
// thread pool rather than blocking the UI thread. Each one returns a JSON value that the
// React side parses into the typed shapes declared in `apps/desktop/src/api/distributed.ts`.

#[tauri::command]
async fn distributed_status() -> Result<serde_json::Value, DesktopError> {
    distributed::status()
}

#[tauri::command]
async fn distributed_enable(args: distributed::EnableArgs) -> Result<serde_json::Value, DesktopError> {
    distributed::enable(args)
}

#[tauri::command]
async fn distributed_disable() -> Result<serde_json::Value, DesktopError> {
    distributed::disable()
}

#[tauri::command]
async fn pairings_list() -> Result<serde_json::Value, DesktopError> {
    distributed::pairings_list()
}

#[tauri::command]
async fn pairings_revoke(id: String) -> Result<serde_json::Value, DesktopError> {
    distributed::pairings_revoke(&id)
}

#[tauri::command]
fn list_network_interfaces() -> Vec<distributed::NetworkInterface> {
    distributed::list_network_interfaces()
}

// ==================== Ingestor pairing wizard (phase 9) ====================
//
// Surfaces the secondary-side wizard to React: app-mode detection, host_id resolution,
// TLS pin + auth probe, end-to-end pair, persisted-config inspection, and re-pair clear.

#[tauri::command]
fn app_mode() -> app_mode::AppMode {
    app_mode::detect()
}

#[tauri::command]
async fn ingestor_test_connection(args: ingestor::PairArgs) -> Result<(), ingestor::PairError> {
    ingestor::test_connection(args)
}

#[tauri::command]
async fn ingestor_pair(args: ingestor::PairArgs) -> Result<ingestor::PairingResult, ingestor::PairError> {
    ingestor::pair(args)
}

#[tauri::command]
async fn ingestor_config_get() -> Result<ingestor::IngestorPairingSnapshot, DesktopError> {
    ingestor::config_get()
}

#[tauri::command]
async fn ingestor_config_clear() -> Result<(), DesktopError> {
    ingestor::config_clear()
}

#[tauri::command]
async fn host_id_get() -> Result<String, DesktopError> {
    ingestor::host_id_get()
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
            distributed_status,
            distributed_enable,
            distributed_disable,
            pairings_list,
            pairings_revoke,
            list_network_interfaces,
            app_mode,
            ingestor_test_connection,
            ingestor_pair,
            ingestor_config_get,
            ingestor_config_clear,
            host_id_get,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
