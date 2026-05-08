namespace AIMemory.Models.Entities;

/// <summary>
/// Cross-host project identity. One row per logical project regardless of how many
/// hosts have a copy of it.
///
/// <para><c>ProjectId</c> for git projects: <c>sha256_hex(root_commit_sha + "\n" + canonical_remote_url)</c>.
/// Non-git fallback: <c>sha256_hex("fallback\n" + host_id + "\n" + absolute_path)</c>.
/// See <c>AIMemory.Identity.ProjectIdResolver</c>.</para>
/// </summary>
public class Project
{
    public string ProjectId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? CanonicalRemoteUrl { get; set; }
    public string? RootCommitSha { get; set; }
    public string IdentityKind { get; set; } = "git"; // "git" | "fallback"

    // Carried over from the legacy code_repositories table for compatibility.
    // Filled at backfill time from CodeRepository.SourceType and SourcePath; written by the
    // ingest path when a project is first observed. Not part of identity — purely metadata.
    public string SourceType { get; set; } = "local";
    public string SourcePath { get; set; } = string.Empty;
    public string? DefaultBranch { get; set; }

    public int FileCount { get; set; }
    public int SymbolCount { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
