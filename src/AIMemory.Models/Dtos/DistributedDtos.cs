namespace AIMemory.Models.Dtos;

/// <summary>
/// Response from <c>POST /api/admin/distributed/enable</c>. Bundles the values the user has
/// to paste into the secondary's wizard: endpoint URL, the freshly-issued ingest API key, and
/// the cert fingerprint to pin.
///
/// <para>The raw <c>ApiKey</c> is shown ONCE. The primary stores only the SHA-256 hash; if the
/// user dismisses without copying, they must issue a new key.</para>
/// </summary>
public class DistributedEnableResponse
{
    /// <summary>The HTTPS endpoint secondaries should target, e.g. <c>https://192.168.1.5:5219</c>.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the leaf cert's DER bytes — the value the secondary pins.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>One-time-reveal raw API key. Hashed in the DB; never re-shown.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>True if the listener configuration changed and a service restart is required to apply.</summary>
    public bool RestartRequired { get; set; }
}

/// <summary>
/// Response from <c>GET /api/admin/distributed/status</c>. Polled by the desktop UI to render
/// the Distributed settings page.
/// </summary>
public class DistributedStatusResponse
{
    /// <summary>Whether remote-ingestor mode is currently configured.</summary>
    public bool Enabled { get; set; }

    /// <summary>Currently configured bind interface, e.g. <c>127.0.0.1</c> or <c>0.0.0.0</c>.</summary>
    public string BindAddress { get; set; } = string.Empty;

    /// <summary>Currently configured listener port.</summary>
    public int BindPort { get; set; }

    /// <summary>HTTPS endpoint URL the secondary uses, when distributed mode is enabled.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Cert fingerprint (lowercase hex SHA-256 of DER), or null when distributed mode is off.</summary>
    public string? Fingerprint { get; set; }

    /// <summary>Number of active (non-revoked) pairings on this primary.</summary>
    public int PairedHostCount { get; set; }
}
