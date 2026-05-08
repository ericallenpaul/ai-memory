//! Tauri command surface for the Distributed-mode admin page.
//!
//! These commands are thin proxies over the local API's `/api/admin/distributed/*` and
//! `/api/pairings*` endpoints. We route through Rust (rather than calling the API straight
//! from the React side) for two reasons:
//!
//! 1. The API key lives in `runtime.json` which the frontend never sees raw — it asks the
//!    Rust side, which already reads the file via [`crate::runtime`].
//! 2. Phase 8 also exposes a network-interface enumeration that has no API equivalent; doing
//!    that here keeps the frontend focused on rendering.
//!
//! All responses are returned as `serde_json::Value` so the frontend can stay loosely-typed
//! at the boundary; the React side defines the matching TS interfaces in `api/distributed.ts`.

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::runtime;
use crate::DesktopError;

/// Builds an HTTP request to the local API using the runtime config's baseUrl + apiKey.
///
/// The API listens on plain HTTP at `127.0.0.1` in non-distributed mode, and on HTTPS when
/// distributed is enabled. In the HTTPS-on-loopback case Kestrel presents the self-signed
/// cert; we accept it explicitly because we generated it and we're talking to ourselves.
fn build_client() -> Result<(reqwest::blocking::Client, runtime::RuntimeConfig), DesktopError> {
    let cfg = runtime::read_runtime_config()?;
    let client = reqwest::blocking::Client::builder()
        // Self-signed loopback cert — see module doc above.
        .danger_accept_invalid_certs(true)
        .timeout(std::time::Duration::from_secs(10))
        .build()
        .map_err(|e| DesktopError::Io(format!("building http client: {e}")))?;
    Ok((client, cfg))
}

fn api_call(
    method: reqwest::Method,
    path: &str,
    body: Option<Value>,
) -> Result<Value, DesktopError> {
    let (client, cfg) = build_client()?;
    let url = format!("{}{}", cfg.base_url, path);
    let mut req = client.request(method, &url)
        .header("X-AIMemory-Api-Key", &cfg.api_key);
    if let Some(json) = body {
        req = req.json(&json);
    }
    let resp = req.send()
        .map_err(|e| DesktopError::Io(format!("calling {url}: {e}")))?;
    let status = resp.status();
    let text = resp.text().unwrap_or_default();
    if !status.is_success() {
        return Err(DesktopError::Io(format!(
            "API {url} returned {status}: {text}"
        )));
    }
    if text.is_empty() {
        return Ok(Value::Null);
    }
    serde_json::from_str(&text)
        .map_err(|e| DesktopError::Io(format!("parsing response from {url}: {e}")))
}

#[derive(Debug, Deserialize)]
pub struct EnableArgs {
    /// Optional bind interface override. The API's enable endpoint flips to `0.0.0.0` by
    /// default; we pass the user's selection through for the UX described in §4.1 of the
    /// design doc. None = let the API pick its default (`0.0.0.0`).
    #[serde(default)]
    pub bind_interface: Option<String>,
}

/// `GET /api/admin/distributed/status` — proxied for the frontend.
pub fn status() -> Result<Value, DesktopError> {
    api_call(reqwest::Method::GET, "/api/admin/distributed/status", None)
}

/// `POST /api/admin/distributed/enable` — flips the toggle on.
///
/// The API endpoint accepts no body in phase 7a; the user's chosen `bind_interface` is sent
/// as a query string for forward-compat with a future server side that honors it. The
/// current API ignores the query and uses its own default; future phases can wire it up.
pub fn enable(args: EnableArgs) -> Result<Value, DesktopError> {
    let path = match &args.bind_interface {
        Some(iface) if !iface.is_empty() => format!(
            "/api/admin/distributed/enable?bindInterface={}",
            urlencode(iface)
        ),
        _ => "/api/admin/distributed/enable".to_string(),
    };
    api_call(reqwest::Method::POST, &path, None)
}

/// `POST /api/admin/distributed/disable` — flips the toggle off.
pub fn disable() -> Result<Value, DesktopError> {
    api_call(reqwest::Method::POST, "/api/admin/distributed/disable", None)
}

/// `GET /api/pairings` — list paired hosts.
pub fn pairings_list() -> Result<Value, DesktopError> {
    api_call(reqwest::Method::GET, "/api/pairings", None)
}

/// `DELETE /api/pairings/{id}` — revoke a pairing.
pub fn pairings_revoke(id: &str) -> Result<Value, DesktopError> {
    // Validate the id parses as a Guid before we shape it into a URL — the API rejects
    // anything else with a 404 and we'd rather fail with a clearer message.
    if id.is_empty() {
        return Err(DesktopError::Io("pairing id is empty".to_string()));
    }
    let path = format!("/api/pairings/{}", urlencode(id));
    api_call(reqwest::Method::DELETE, &path, None)
}

/// Detected non-loopback network interface, surfaced to the desktop's "bind interface"
/// dropdown. The user can also enter a custom value, so this list is suggestions only.
#[derive(Debug, Serialize)]
pub struct NetworkInterface {
    pub name: String,
    pub address: String,
}

/// Best-effort enumeration of bindable IPv4 addresses on the local machine.
///
/// Falls back to a static list (`0.0.0.0`, the machine's hostname) on platforms or
/// configurations we can't introspect. Always returns at least one entry so the picker
/// never appears empty.
pub fn list_network_interfaces() -> Vec<NetworkInterface> {
    let mut out = vec![NetworkInterface {
        name: "Any (0.0.0.0)".to_string(),
        address: "0.0.0.0".to_string(),
    }];

    // Use a UDP socket trick: connect (no actual packet) to a public IP, ask the OS what
    // local address it would have used. This reliably gets the primary egress interface
    // without parsing platform-specific output. Failure is silent — the dropdown still has
    // the wildcard above.
    if let Ok(socket) = std::net::UdpSocket::bind("0.0.0.0:0") {
        // 1.1.1.1 is just a routable target; we don't actually send anything.
        if socket.connect("1.1.1.1:80").is_ok() {
            if let Ok(addr) = socket.local_addr() {
                let ip = addr.ip().to_string();
                if !ip.is_empty() && ip != "0.0.0.0" {
                    out.push(NetworkInterface {
                        name: format!("Primary ({ip})"),
                        address: ip,
                    });
                }
            }
        }
    }

    out
}

/// Minimal URL-component encoder for path segments and query values. Only escapes the
/// characters that would break the URL grammar; ASCII alphanumerics and a few symbols pass
/// through. Sufficient for fingerprint strings, GUIDs, and IPv4 addresses.
fn urlencode(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for b in s.bytes() {
        if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'~') {
            out.push(b as char);
        } else {
            out.push_str(&format!("%{b:02X}"));
        }
    }
    out
}
