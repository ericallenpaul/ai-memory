//! App mode detection for the Tauri shell.
//!
//! The same desktop binary ships two faces:
//! * **Full** mode (default) — the existing dashboard, Distributed admin, Services, etc.
//! * **Ingestor-only** mode — only the pairing wizard / status view is reachable. Used on
//!   secondary machines that run the ingestor service against a remote primary; they have
//!   no local API to talk to and the full dashboard would be confusing.
//!
//! Mode is read from a small JSON file at `%ProgramData%\AIMemory\app-mode.json` (Windows)
//! or `/etc/aimemory/app-mode.json` (Unix). Phase 10's NSIS installer writes the file based
//! on the user's component selection; phase 9 only consumes it. Missing / unparseable file
//! falls back to "full" — back-compat for existing installs.

use serde::{Deserialize, Serialize};
use std::path::PathBuf;

#[derive(Debug, Serialize, Deserialize, Clone, Copy, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum AppMode {
    Full,
    IngestorOnly,
}

impl Default for AppMode {
    fn default() -> Self {
        AppMode::Full
    }
}

#[derive(Debug, Deserialize)]
struct AppModeFile {
    /// `"full"` or `"ingestor-only"` (kebab-case in JSON).
    #[serde(default)]
    mode: String,
}

pub fn app_mode_path() -> PathBuf {
    #[cfg(windows)]
    {
        let program_data = std::env::var("ProgramData").unwrap_or_else(|_| "C:\\ProgramData".into());
        PathBuf::from(program_data).join("AIMemory").join("app-mode.json")
    }

    #[cfg(not(windows))]
    {
        PathBuf::from("/etc/aimemory/app-mode.json")
    }
}

/// Reads the app-mode marker file and returns the resolved mode.
///
/// Errors are non-fatal: a missing file, IO error, or malformed JSON all degrade silently
/// to [`AppMode::Full`]. The wizard pages need a positive signal (`"ingestor-only"`) to
/// hide the rest of the shell — anything else means we want the full dashboard.
pub fn detect() -> AppMode {
    let path = app_mode_path();
    if !path.exists() {
        return AppMode::Full;
    }
    match std::fs::read_to_string(&path) {
        Ok(raw) => match serde_json::from_str::<AppModeFile>(&raw) {
            Ok(file) => match file.mode.as_str() {
                "ingestor-only" => AppMode::IngestorOnly,
                _ => AppMode::Full,
            },
            Err(_) => AppMode::Full,
        },
        Err(_) => AppMode::Full,
    }
}
