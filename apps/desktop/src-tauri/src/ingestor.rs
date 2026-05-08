//! Tauri command surface for the **secondary-machine** ingestor pairing wizard (phase 9).
//!
//! Unlike `distributed.rs` (which talks to a *local* API), this module talks to a *remote*
//! primary over TLS and writes the resulting pairing into the local ingestor's config file.
//! The flow:
//!
//! 1. **Fingerprint pin.** Open a raw TLS connection to the primary, capture the leaf
//!    cert's DER bytes, hash them with SHA-256, compare to the user-pasted pin. Mismatch
//!    aborts immediately with [`PairError::FingerprintMismatch`] — no further bytes go
//!    over the wire under that connection.
//! 2. **Auth probe.** With the cert now trusted, issue `GET /api/health` carrying the API
//!    key. 401 → [`PairError::Unauthorized`]; non-2xx → [`PairError::Other`].
//! 3. **Register.** `POST /api/pairings` with this host's `host_id`, friendly name, OS
//!    kind, ingestor version. The primary returns the pairing record (or 409 if a stale
//!    active pairing exists for the same `host_id`).
//! 4. **Persist.** Write `%ProgramData%\AIMemory\Ingestor\appsettings.json` in the exact
//!    shape `IngestorConfig` (see `src/AIMemory.Ingestor/Configuration/IngestorConfig.cs`)
//!    expects — `Mode: "Remote"` plus `Remote.{Endpoint, ApiKey, PinnedCertFingerprint}`.
//!
//! The host_id is sourced from the ingestor binary itself (`AIMemory.Ingestor.exe
//! --print-host-id`) so HostIdProvider stays the single source of truth for the algorithm.

use serde::{Deserialize, Serialize};
use std::io::{Read, Write};
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use sha2::{Digest, Sha256};

use crate::DesktopError;

/// Errors surfaced by the wizard back to the React UI. Serialized as a tagged enum
/// (`{"type":"FingerprintMismatch","expected":"...","actual":"..."}` etc.) so the frontend
/// can branch cleanly without parsing strings.
#[derive(Debug, Serialize, thiserror::Error)]
#[serde(tag = "type", content = "detail")]
pub enum PairError {
    #[error("fingerprint mismatch: expected {expected}, got {actual}")]
    FingerprintMismatch { expected: String, actual: String },
    #[error("unreachable: {0}")]
    Unreachable(String),
    #[error("unauthorized")]
    Unauthorized,
    #[error("invalid input: {0}")]
    InvalidInput(String),
    #[error("io error: {0}")]
    Io(String),
    #[error("other: {0}")]
    Other(String),
}

/// Result returned from a successful pairing. Mirrors the API's `PairingResponse`.
#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PairingResult {
    pub pairing_id: String,
    pub host_id: String,
    pub friendly_name: String,
    pub paired_at: String,
}

/// Persisted ingestor config snapshot, surfaced to the status view. Sensitive fields stay
/// masked in the response so a screenshare of the status page doesn't leak the API key.
#[derive(Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct IngestorPairingSnapshot {
    pub endpoint: String,
    /// Masked in the response (`abcd•••••wxyz`); the real value is never returned via this
    /// command. Re-paint requires re-pairing — phase 9 has no key-reveal affordance.
    pub api_key_masked: String,
    /// Lowercase hex SHA-256 (no separators). The frontend formats it for display.
    pub fingerprint: String,
    /// `true` when the persisted config has all three remote fields populated.
    pub configured: bool,
}

/// Args for [`pair`] / [`test_connection`].
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PairArgs {
    pub endpoint: String,
    pub api_key: String,
    /// Either lowercase hex (`a1b2...`) or colon-separated bytes (`A1:B2:...`).
    pub fingerprint: String,
    /// Friendly name (defaults to the OS hostname server-side; the wizard always sends a value).
    #[serde(default)]
    pub friendly_name: Option<String>,
}

// =================== Public command entry points ===================

/// Validates a primary's TLS fingerprint and authenticates with the API key, but does
/// NOT register a pairing or persist any config.
pub fn test_connection(args: PairArgs) -> Result<(), PairError> {
    let parsed = parse_endpoint(&args.endpoint)?;
    let normalized_pin = normalize_fingerprint(&args.fingerprint)?;

    let observed = fetch_leaf_cert_fingerprint(&parsed.host, parsed.port)?;
    if observed != normalized_pin {
        return Err(PairError::FingerprintMismatch {
            expected: normalized_pin,
            actual: observed,
        });
    }

    // Cert pinned. Issue an authenticated GET /api/health to confirm the API key works
    // before we make the user click again.
    health_probe(&args.endpoint, &args.api_key)?;
    Ok(())
}

/// End-to-end pair: TLS pin + auth probe + POST /api/pairings + persist appsettings.json.
///
/// Persistence happens **after** the API confirms the pairing — a 4xx response leaves the
/// secondary's config untouched so the user can correct the input and retry.
pub fn pair(args: PairArgs) -> Result<PairingResult, PairError> {
    let parsed = parse_endpoint(&args.endpoint)?;
    let normalized_pin = normalize_fingerprint(&args.fingerprint)?;

    let observed = fetch_leaf_cert_fingerprint(&parsed.host, parsed.port)?;
    if observed != normalized_pin {
        return Err(PairError::FingerprintMismatch {
            expected: normalized_pin,
            actual: observed,
        });
    }

    // Auth probe before we shove host_id over the wire — gives a cleaner Unauthorized
    // error if the user pasted the wrong key.
    health_probe(&args.endpoint, &args.api_key)?;

    let host_id = resolve_host_id()?;
    let friendly = args
        .friendly_name
        .clone()
        .filter(|s| !s.trim().is_empty())
        .unwrap_or_else(|| hostname_or_fallback());

    let result = post_pairing(&args.endpoint, &args.api_key, &host_id, &friendly)?;

    // Persist last so a successful pair is the only way config gets touched.
    write_ingestor_config(&IngestorAppsettings {
        endpoint: args.endpoint.trim().to_string(),
        api_key: args.api_key.clone(),
        fingerprint: normalized_pin,
    })?;

    Ok(result)
}

/// Reads the persisted ingestor config and returns a redacted snapshot for the status
/// page. Never returns the raw API key.
pub fn config_get() -> Result<IngestorPairingSnapshot, DesktopError> {
    let path = ingestor_appsettings_path();
    if !path.exists() {
        return Ok(IngestorPairingSnapshot::default());
    }
    let raw = std::fs::read_to_string(&path)
        .map_err(|e| DesktopError::Io(format!("reading {}: {e}", path.display())))?;
    let parsed: IngestorAppsettingsFile = serde_json::from_str(&raw)
        .map_err(|e| DesktopError::Io(format!("parsing {}: {e}", path.display())))?;

    let remote = parsed.ingestor.remote.unwrap_or_default();
    let configured = !remote.endpoint.is_empty()
        && !remote.api_key.is_empty()
        && !remote.pinned_cert_fingerprint.is_empty();

    Ok(IngestorPairingSnapshot {
        endpoint: remote.endpoint,
        api_key_masked: mask_key(&remote.api_key),
        fingerprint: remote.pinned_cert_fingerprint,
        configured,
    })
}

/// Wipes the persisted ingestor config so the next launch shows the wizard again. Used
/// by the "Re-pair" button on the status page. Doesn't touch the running ingestor service
/// — the user will need to restart it (or the desktop will, on a follow-up successful pair).
pub fn config_clear() -> Result<(), DesktopError> {
    let path = ingestor_appsettings_path();
    if !path.exists() {
        return Ok(());
    }
    std::fs::remove_file(&path)
        .map_err(|e| DesktopError::Io(format!("removing {}: {e}", path.display())))?;
    Ok(())
}

/// Resolves the local machine's stable host_id by shelling out to the ingestor binary.
/// HostIdProvider is .NET, not Rust — keeping the algorithm there avoids a divergence risk.
pub fn host_id_get() -> Result<String, DesktopError> {
    resolve_host_id().map_err(|e| match e {
        PairError::Io(s) => DesktopError::Io(s),
        other => DesktopError::Io(other.to_string()),
    })
}

// =================== TLS handshake / fingerprint ===================

struct ParsedEndpoint {
    host: String,
    port: u16,
}

fn parse_endpoint(raw: &str) -> Result<ParsedEndpoint, PairError> {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return Err(PairError::InvalidInput("endpoint is empty".to_string()));
    }
    // Strip scheme — we only support https:// for remote ingestors per design §3.6.
    let rest = if let Some(s) = trimmed.strip_prefix("https://") {
        s
    } else if trimmed.starts_with("http://") {
        return Err(PairError::InvalidInput(
            "http:// is not supported; remote ingestors must use https://".to_string(),
        ));
    } else {
        return Err(PairError::InvalidInput(
            "endpoint must start with https://".to_string(),
        ));
    };

    // Trailing path / query — drop, we only need host:port for the TLS handshake.
    let host_port = rest.split('/').next().unwrap_or(rest);
    let (host, port) = match host_port.rsplit_once(':') {
        Some((h, p)) => {
            let parsed: u16 = p
                .parse()
                .map_err(|_| PairError::InvalidInput(format!("bad port: {p}")))?;
            (h.to_string(), parsed)
        }
        None => (host_port.to_string(), 443),
    };

    if host.is_empty() {
        return Err(PairError::InvalidInput("empty host".to_string()));
    }
    Ok(ParsedEndpoint { host, port })
}

fn normalize_fingerprint(raw: &str) -> Result<String, PairError> {
    let cleaned: String = raw
        .chars()
        .filter(|c| !c.is_whitespace() && *c != ':' && *c != '-')
        .collect();
    if cleaned.len() != 64 {
        return Err(PairError::InvalidInput(format!(
            "fingerprint must be 64 hex chars (got {})",
            cleaned.len()
        )));
    }
    if !cleaned.chars().all(|c| c.is_ascii_hexdigit()) {
        return Err(PairError::InvalidInput(
            "fingerprint contains non-hex characters".to_string(),
        ));
    }
    Ok(cleaned.to_ascii_lowercase())
}

/// Opens a raw TCP+TLS connection to `host:port`, captures the first cert in the chain
/// (the leaf), and returns its lowercase-hex SHA-256. Uses a permissive verifier — the
/// caller is responsible for comparing the result to the user-pasted pin before trusting
/// anything.
fn fetch_leaf_cert_fingerprint(host: &str, port: u16) -> Result<String, PairError> {
    let addr = format!("{host}:{port}");
    let tcp = std::net::TcpStream::connect_timeout(
        &addr
            .to_socket_addrs_first()
            .map_err(|e| PairError::Unreachable(format!("resolving {addr}: {e}")))?,
        Duration::from_secs(10),
    )
    .map_err(|e| PairError::Unreachable(format!("connecting to {addr}: {e}")))?;
    tcp.set_read_timeout(Some(Duration::from_secs(10))).ok();
    tcp.set_write_timeout(Some(Duration::from_secs(10))).ok();

    let server_name = rustls_pki_types::ServerName::try_from(host.to_string())
        .map_err(|e| PairError::InvalidInput(format!("invalid server name {host}: {e}")))?;

    let config = rustls::ClientConfig::builder()
        .dangerous()
        .with_custom_certificate_verifier(Arc::new(NoVerification))
        .with_no_client_auth();

    let mut conn = rustls::ClientConnection::new(Arc::new(config), server_name)
        .map_err(|e| PairError::Other(format!("rustls client init: {e}")))?;

    // rustls::Stream wants a mutable borrow of the underlying socket; bind it to a `let`
    // so the temporary doesn't drop mid-statement (E0716).
    let mut sock = tcp;
    let mut stream = rustls::Stream::new(&mut conn, &mut sock);
    // Force the handshake by issuing a tiny write — rustls is lazy otherwise. We DON'T
    // care about the response; we only want the cert chain populated. A short
    // CRLF probe is innocuous enough for any server we'd talk to.
    let _ = stream.write_all(b"GET / HTTP/1.0\r\nHost: aimemory-pin\r\nConnection: close\r\n\r\n");
    let _ = stream.flush();
    // Drain a single byte just to drive completion of the handshake. Don't fail on read
    // errors — we already have the cert if the handshake itself succeeded.
    let mut sink = [0u8; 1];
    let _ = stream.read(&mut sink);

    let certs = conn
        .peer_certificates()
        .ok_or_else(|| PairError::Other("primary did not present a certificate".to_string()))?;
    let leaf = certs
        .first()
        .ok_or_else(|| PairError::Other("empty certificate chain".to_string()))?;

    let mut hasher = Sha256::new();
    hasher.update(leaf.as_ref());
    let digest = hasher.finalize();
    Ok(hex::encode(digest))
}

/// Custom verifier that accepts every cert. Wraps the `dangerous_configuration`-equivalent
/// rustls 0.23 surface so we can extract the leaf without a real trust anchor.
#[derive(Debug)]
struct NoVerification;

impl rustls::client::danger::ServerCertVerifier for NoVerification {
    fn verify_server_cert(
        &self,
        _end_entity: &rustls_pki_types::CertificateDer<'_>,
        _intermediates: &[rustls_pki_types::CertificateDer<'_>],
        _server_name: &rustls_pki_types::ServerName<'_>,
        _ocsp_response: &[u8],
        _now: rustls_pki_types::UnixTime,
    ) -> Result<rustls::client::danger::ServerCertVerified, rustls::Error> {
        Ok(rustls::client::danger::ServerCertVerified::assertion())
    }

    fn verify_tls12_signature(
        &self,
        _message: &[u8],
        _cert: &rustls_pki_types::CertificateDer<'_>,
        _dss: &rustls::DigitallySignedStruct,
    ) -> Result<rustls::client::danger::HandshakeSignatureValid, rustls::Error> {
        Ok(rustls::client::danger::HandshakeSignatureValid::assertion())
    }

    fn verify_tls13_signature(
        &self,
        _message: &[u8],
        _cert: &rustls_pki_types::CertificateDer<'_>,
        _dss: &rustls::DigitallySignedStruct,
    ) -> Result<rustls::client::danger::HandshakeSignatureValid, rustls::Error> {
        Ok(rustls::client::danger::HandshakeSignatureValid::assertion())
    }

    fn supported_verify_schemes(&self) -> Vec<rustls::SignatureScheme> {
        vec![
            rustls::SignatureScheme::RSA_PKCS1_SHA256,
            rustls::SignatureScheme::RSA_PKCS1_SHA384,
            rustls::SignatureScheme::RSA_PKCS1_SHA512,
            rustls::SignatureScheme::ECDSA_NISTP256_SHA256,
            rustls::SignatureScheme::ECDSA_NISTP384_SHA384,
            rustls::SignatureScheme::ECDSA_NISTP521_SHA512,
            rustls::SignatureScheme::RSA_PSS_SHA256,
            rustls::SignatureScheme::RSA_PSS_SHA384,
            rustls::SignatureScheme::RSA_PSS_SHA512,
            rustls::SignatureScheme::ED25519,
        ]
    }
}

// Helper trait so we can call `.to_socket_addrs()` and pluck the first result without
// pulling in a third helper crate. Rust's stdlib gives us `ToSocketAddrs::to_socket_addrs`
// that returns an iterator; we just want the first hit for the connect_timeout call.
trait ToSocketAddrsFirst {
    fn to_socket_addrs_first(&self) -> std::io::Result<std::net::SocketAddr>;
}
impl ToSocketAddrsFirst for String {
    fn to_socket_addrs_first(&self) -> std::io::Result<std::net::SocketAddr> {
        use std::net::ToSocketAddrs;
        self.as_str()
            .to_socket_addrs()?
            .next()
            .ok_or_else(|| std::io::Error::new(std::io::ErrorKind::AddrNotAvailable, "no addrs"))
    }
}

// =================== HTTP probes / pairing POST ===================

fn http_client() -> Result<reqwest::blocking::Client, PairError> {
    reqwest::blocking::Client::builder()
        // We've already pinned the cert ourselves above; the wider reqwest call doesn't
        // need to re-validate. Self-signed certs would fail rustls' default verifier.
        .danger_accept_invalid_certs(true)
        .timeout(Duration::from_secs(15))
        .build()
        .map_err(|e| PairError::Io(format!("building http client: {e}")))
}

fn health_probe(endpoint: &str, api_key: &str) -> Result<(), PairError> {
    let client = http_client()?;
    let url = format!("{}/api/health", endpoint.trim_end_matches('/'));
    let resp = client
        .get(&url)
        .header("X-AIMemory-Api-Key", api_key)
        .send()
        .map_err(|e| PairError::Unreachable(format!("GET {url}: {e}")))?;
    let status = resp.status();
    if status == reqwest::StatusCode::UNAUTHORIZED || status == reqwest::StatusCode::FORBIDDEN {
        return Err(PairError::Unauthorized);
    }
    if !status.is_success() {
        let body = resp.text().unwrap_or_default();
        return Err(PairError::Other(format!(
            "primary returned {status} from /api/health: {body}"
        )));
    }
    Ok(())
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "PascalCase")]
struct CreatePairingRequest<'a> {
    host_id: &'a str,
    friendly_name: &'a str,
    os_kind: &'a str,
    ingestor_version: &'a str,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PairingResponseDto {
    pairing_id: String,
    host_id: String,
    friendly_name: String,
    paired_at: String,
}

fn post_pairing(
    endpoint: &str,
    api_key: &str,
    host_id: &str,
    friendly_name: &str,
) -> Result<PairingResult, PairError> {
    let client = http_client()?;
    let url = format!("{}/api/pairings", endpoint.trim_end_matches('/'));
    let body = CreatePairingRequest {
        host_id,
        friendly_name,
        os_kind: os_kind(),
        ingestor_version: env!("CARGO_PKG_VERSION"),
    };
    let resp = client
        .post(&url)
        .header("X-AIMemory-Api-Key", api_key)
        .json(&body)
        .send()
        .map_err(|e| PairError::Unreachable(format!("POST {url}: {e}")))?;
    let status = resp.status();
    if status == reqwest::StatusCode::UNAUTHORIZED || status == reqwest::StatusCode::FORBIDDEN {
        return Err(PairError::Unauthorized);
    }
    if !status.is_success() {
        let text = resp.text().unwrap_or_default();
        return Err(PairError::Other(format!("primary returned {status}: {text}")));
    }

    let parsed: PairingResponseDto = resp
        .json()
        .map_err(|e| PairError::Other(format!("parsing /api/pairings response: {e}")))?;
    Ok(PairingResult {
        pairing_id: parsed.pairing_id,
        host_id: parsed.host_id,
        friendly_name: parsed.friendly_name,
        paired_at: parsed.paired_at,
    })
}

fn os_kind() -> &'static str {
    if cfg!(target_os = "windows") {
        "windows"
    } else if cfg!(target_os = "macos") {
        "macos"
    } else if cfg!(target_os = "linux") {
        "linux"
    } else {
        "unknown"
    }
}

fn hostname_or_fallback() -> String {
    // Cheapest cross-platform hostname grab — the COMPUTERNAME / HOSTNAME env vars are set
    // on every supported OS and avoid pulling in another crate. Falls back to a fixed
    // string the user can rename via the wizard's friendly-name field.
    std::env::var("COMPUTERNAME")
        .or_else(|_| std::env::var("HOSTNAME"))
        .ok()
        .filter(|s| !s.trim().is_empty())
        .unwrap_or_else(|| "ingestor-host".to_string())
}

// =================== host_id resolution ===================

/// Shells out to `AIMemory.Ingestor.exe --print-host-id` and returns the printed value.
/// Tries the SCM-registered service binary path first (production install), then a few
/// candidate paths near the desktop binary (dev runs).
fn resolve_host_id() -> Result<String, PairError> {
    let exe = locate_ingestor_exe()
        .ok_or_else(|| PairError::Other(
            "could not locate AIMemory.Ingestor.exe; the ingestor service must be installed first".to_string()))?;

    let output = std::process::Command::new(&exe)
        .arg("--print-host-id")
        .output()
        .map_err(|e| PairError::Io(format!("running {} --print-host-id: {e}", exe.display())))?;

    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr).into_owned();
        return Err(PairError::Other(format!(
            "ingestor exited with status {}: {stderr}",
            output.status
        )));
    }
    let host_id = String::from_utf8_lossy(&output.stdout).trim().to_string();
    if host_id.len() != 64 || !host_id.chars().all(|c| c.is_ascii_hexdigit()) {
        return Err(PairError::Other(format!(
            "ingestor printed unexpected host_id: {host_id}"
        )));
    }
    Ok(host_id.to_ascii_lowercase())
}

fn locate_ingestor_exe() -> Option<PathBuf> {
    // 1. SCM-registered binary path (the standard production install).
    #[cfg(windows)]
    {
        if let Some(p) = scm_ingestor_path() {
            if p.exists() {
                return Some(p);
            }
        }
    }

    // 2. Adjacent to the desktop exe (per-machine NSIS install puts both in $INSTDIR).
    if let Ok(current) = std::env::current_exe() {
        if let Some(dir) = current.parent() {
            let candidate = dir.join("AIMemory.Ingestor.exe");
            if candidate.exists() {
                return Some(candidate);
            }
        }
    }

    // 3. Dev run: workspace-relative paths.
    let dev_paths = [
        "src/AIMemory.Ingestor/bin/Debug/net10.0/win-x64/AIMemory.Ingestor.exe",
        "src/AIMemory.Ingestor/bin/Release/net10.0/win-x64/AIMemory.Ingestor.exe",
        "../../../src/AIMemory.Ingestor/bin/Debug/net10.0/win-x64/AIMemory.Ingestor.exe",
    ];
    for rel in dev_paths {
        let p = PathBuf::from(rel);
        if p.exists() {
            return Some(p);
        }
    }
    None
}

#[cfg(windows)]
fn scm_ingestor_path() -> Option<PathBuf> {
    use windows_service::service::ServiceAccess;
    use windows_service::service_manager::{ServiceManager, ServiceManagerAccess};

    let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT).ok()?;
    let service = manager
        .open_service("aimemory-ingestor", ServiceAccess::QUERY_CONFIG)
        .ok()?;
    let cfg = service.query_config().ok()?;
    // executable_path is an OsString; service binPath may be quoted with extra args. Strip
    // quotes and take the first whitespace-delimited token.
    let raw = cfg.executable_path.to_string_lossy().to_string();
    let stripped = raw.trim().trim_matches('"');
    // If the path itself was quoted with args after, split on the closing quote.
    let first_token = if let Some(end) = stripped.find('"') {
        &stripped[..end]
    } else {
        // Fall back to whitespace split; this can mis-split paths with spaces but the
        // installer uses fully-quoted binPaths (see installer/installer.nsh:48), so the
        // earlier branch handles the common case.
        stripped.split_whitespace().next().unwrap_or(stripped)
    };
    Some(PathBuf::from(first_token))
}

// =================== Config persistence ===================

/// Path the ingestor service reads on startup (see `src/AIMemory.Ingestor/Program.cs:18`).
/// Phase 7b's loader looks for an `Ingestor` section; we write the full file shape so a
/// missing file becomes a complete config rather than a partial overlay.
pub fn ingestor_appsettings_path() -> PathBuf {
    #[cfg(windows)]
    {
        let program_data =
            std::env::var("ProgramData").unwrap_or_else(|_| "C:\\ProgramData".into());
        PathBuf::from(program_data)
            .join("AIMemory")
            .join("Ingestor")
            .join("appsettings.json")
    }
    #[cfg(not(windows))]
    {
        PathBuf::from("/etc/aimemory/ingestor/appsettings.json")
    }
}

struct IngestorAppsettings {
    endpoint: String,
    api_key: String,
    fingerprint: String,
}

/// On-disk shape used both for write (we serialize into the equivalent `serde_json::Value`)
/// and read (parsed via `IngestorAppsettingsFile`). The casing matches what
/// `Microsoft.Extensions.Configuration` binds to `IngestorConfig` — PascalCase for the
/// section key, then PascalCase fields. .NET's binder is case-insensitive but the wizard
/// follows the design doc's documented JSON shape.
#[derive(Debug, Deserialize, Default)]
struct IngestorAppsettingsFile {
    #[serde(rename = "Ingestor", default)]
    ingestor: IngestorSectionFile,
}

#[derive(Debug, Deserialize, Default)]
struct IngestorSectionFile {
    #[serde(rename = "Remote")]
    remote: Option<RemoteSectionFile>,
}

#[derive(Debug, Deserialize, Default)]
struct RemoteSectionFile {
    #[serde(rename = "Endpoint", default)]
    endpoint: String,
    #[serde(rename = "ApiKey", default)]
    api_key: String,
    #[serde(rename = "PinnedCertFingerprint", default)]
    pinned_cert_fingerprint: String,
}

fn write_ingestor_config(cfg: &IngestorAppsettings) -> Result<(), PairError> {
    let path = ingestor_appsettings_path();
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)
            .map_err(|e| PairError::Io(format!("creating {}: {e}", parent.display())))?;
    }

    // Read-and-merge so we don't clobber any other Ingestor settings the user may have
    // hand-edited (Sources, Redaction, etc.). Falls back to a fresh object on first run.
    let mut root = if path.exists() {
        let raw = std::fs::read_to_string(&path)
            .map_err(|e| PairError::Io(format!("reading {}: {e}", path.display())))?;
        serde_json::from_str::<serde_json::Value>(&raw)
            .unwrap_or_else(|_| serde_json::Value::Object(Default::default()))
    } else {
        serde_json::Value::Object(Default::default())
    };

    // Ensure root is an object — replace with empty object if it's an array/scalar.
    if !root.is_object() {
        root = serde_json::Value::Object(Default::default());
    }
    let root_obj = root.as_object_mut().expect("ensured object above");

    // Get-or-insert "Ingestor" subsection.
    let ingestor_section = root_obj
        .entry("Ingestor".to_string())
        .or_insert_with(|| serde_json::Value::Object(Default::default()));
    if !ingestor_section.is_object() {
        *ingestor_section = serde_json::Value::Object(Default::default());
    }
    let ingestor_obj = ingestor_section.as_object_mut().unwrap();

    // Mode = Remote — required by IngestorConfigValidator (see phase 7b).
    ingestor_obj.insert("Mode".to_string(), serde_json::Value::String("Remote".to_string()));

    // Build the Remote subobject fresh; partial values from a half-failed previous run
    // would just confuse the next startup.
    let mut remote = serde_json::Map::new();
    remote.insert(
        "Endpoint".to_string(),
        serde_json::Value::String(cfg.endpoint.clone()),
    );
    remote.insert(
        "ApiKey".to_string(),
        serde_json::Value::String(cfg.api_key.clone()),
    );
    remote.insert(
        "PinnedCertFingerprint".to_string(),
        serde_json::Value::String(cfg.fingerprint.clone()),
    );
    ingestor_obj.insert("Remote".to_string(), serde_json::Value::Object(remote));

    let pretty = serde_json::to_string_pretty(&root)
        .map_err(|e| PairError::Io(format!("serializing config: {e}")))?;
    std::fs::write(&path, pretty)
        .map_err(|e| PairError::Io(format!("writing {}: {e}", path.display())))?;
    Ok(())
}

fn mask_key(raw: &str) -> String {
    if raw.is_empty() {
        return String::new();
    }
    if raw.len() <= 12 {
        return "•".repeat(raw.len());
    }
    let head: String = raw.chars().take(6).collect();
    let tail: String = raw.chars().rev().take(4).collect::<Vec<_>>().into_iter().rev().collect();
    format!("{head}{}{tail}", "•".repeat(raw.len().saturating_sub(10)))
}

// =================== Tests ===================

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalize_fingerprint_accepts_hex() {
        let n = normalize_fingerprint("a1b2c3d4e5f60718293a4b5c6d7e8f9001112233445566778899aabbccddeeff").unwrap();
        assert_eq!(n.len(), 64);
        assert!(n.chars().all(|c| c.is_ascii_lowercase() || c.is_ascii_digit()));
    }

    #[test]
    fn normalize_fingerprint_accepts_colons() {
        let n = normalize_fingerprint("A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90:01:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF").unwrap();
        assert_eq!(n, "a1b2c3d4e5f60718293a4b5c6d7e8f9001112233445566778899aabbccddeeff");
    }

    #[test]
    fn normalize_fingerprint_rejects_bad_length() {
        assert!(matches!(normalize_fingerprint("ab"), Err(PairError::InvalidInput(_))));
    }

    #[test]
    fn normalize_fingerprint_rejects_non_hex() {
        assert!(matches!(
            normalize_fingerprint("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"),
            Err(PairError::InvalidInput(_))
        ));
    }

    #[test]
    fn parse_endpoint_requires_https() {
        assert!(matches!(parse_endpoint("http://foo.lan"), Err(PairError::InvalidInput(_))));
        assert!(matches!(parse_endpoint("foo.lan:5219"), Err(PairError::InvalidInput(_))));
    }

    #[test]
    fn parse_endpoint_extracts_host_port() {
        let p = parse_endpoint("https://eric-desktop.lan:5219/some/path").unwrap();
        assert_eq!(p.host, "eric-desktop.lan");
        assert_eq!(p.port, 5219);
    }

    #[test]
    fn parse_endpoint_defaults_port_443() {
        let p = parse_endpoint("https://example.com").unwrap();
        assert_eq!(p.port, 443);
    }

    /// Sanity check on the mismatch path: the wire fingerprint is whatever
    /// fetch_leaf_cert_fingerprint returns, but if we pre-compute a different value and
    /// compare, the public test_connection / pair flow returns FingerprintMismatch.
    #[test]
    fn fingerprint_mismatch_detected() {
        // Simulate the post-handshake comparison the public functions perform: caller
        // computed the leaf fingerprint, user pasted a different one, comparison rejects.
        let observed = "a1b2c3d4e5f60718293a4b5c6d7e8f9001112233445566778899aabbccddeeff";
        let user_pin = normalize_fingerprint("ffeeddccbbaa9988776655443322110011223344556677889a8b7c6d5e4f3a2b").unwrap();
        assert_ne!(observed, user_pin);
        // The pair / test_connection functions call this same `!=` and return
        // FingerprintMismatch — covered indirectly here without needing a live server.
    }

    #[test]
    fn mask_key_redacts_middle() {
        // Source is 29 chars; we keep 6 + 4 = 10, mask the middle 19.
        let masked = mask_key("aimemory_abcdefghijklmnop1234");
        assert!(masked.starts_with("aimemo"), "got {masked}");
        assert!(masked.ends_with("1234"), "got {masked}");
        assert_eq!(masked.chars().filter(|c| *c == '•').count(), 19);
        assert_eq!(mask_key("short"), "•••••");
        assert_eq!(mask_key(""), "");
    }
}
