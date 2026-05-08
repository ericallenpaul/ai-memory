//! Reads the runtime config the API service publishes on startup. The file lives at
//! `%ProgramData%\AIMemory\Api\runtime.json` on Windows; on Unix it'd be under
//! `/etc/aimemory/runtime.json` or `~/.config/aimemory/runtime.json` (Phase 4 wiring).

use serde::{Deserialize, Serialize};
use std::path::PathBuf;

use crate::DesktopError;

#[derive(Debug, Serialize, Deserialize)]
pub struct RuntimeConfig {
    /// Base URL of the local API, e.g. `http://127.0.0.1:5219` (or `https://...` when
    /// distributed mode is on).
    #[serde(rename = "baseUrl")]
    pub base_url: String,

    /// API key the frontend should send via `X-AIMemory-Api-Key`. Generated on first start
    /// of the API service and persisted as a regular row in the api_keys table.
    #[serde(rename = "apiKey")]
    pub api_key: String,

    /// Port number split out for convenience (some callers prefer the bare port).
    pub port: u16,

    /// Bind interface the API listener is currently using. Phase 7a writes this from
    /// `distributed.json`'s `BindAddress` so the desktop UI can render the Distributed
    /// settings page without re-reading that file. Defaults to `"127.0.0.1"` for back-compat
    /// with older runtime.json files.
    #[serde(default = "default_bind_interface")]
    pub bind_interface: String,

    /// Bind port (mirrors `port`, kept here for symmetry with the on-disk schema).
    #[serde(default)]
    pub bind_port: u16,

    /// Lowercase-hex SHA-256 of the leaf TLS cert's DER bytes, or empty when distributed
    /// mode is disabled (no cert generated yet).
    #[serde(default)]
    pub tls_fingerprint: String,
}

fn default_bind_interface() -> String {
    "127.0.0.1".to_string()
}

pub fn runtime_config_path() -> PathBuf {
    #[cfg(windows)]
    {
        // Falls back to current dir on the off chance ProgramData isn't set.
        let program_data = std::env::var("ProgramData").unwrap_or_else(|_| "C:\\ProgramData".into());
        PathBuf::from(program_data).join("AIMemory").join("Api").join("runtime.json")
    }

    #[cfg(not(windows))]
    {
        if let Ok(home) = std::env::var("HOME") {
            PathBuf::from(home).join(".config").join("aimemory").join("runtime.json")
        } else {
            PathBuf::from("/etc/aimemory/runtime.json")
        }
    }
}

pub fn read_runtime_config() -> Result<RuntimeConfig, DesktopError> {
    let path = runtime_config_path();
    if !path.exists() {
        return Err(DesktopError::NotFound);
    }
    let raw = std::fs::read_to_string(&path)
        .map_err(|e| DesktopError::Io(format!("reading {}: {e}", path.display())))?;
    let cfg: RuntimeConfig = serde_json::from_str(&raw)
        .map_err(|e| DesktopError::Io(format!("parsing {}: {e}", path.display())))?;
    Ok(cfg)
}
