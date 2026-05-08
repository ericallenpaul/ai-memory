using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIMemory.Identity;

/// <summary>
/// Default <see cref="IProjectIdResolver"/>. See design doc §2.2 for the exact derivation rules.
/// </summary>
public sealed class ProjectIdResolver : IProjectIdResolver
{
    private static readonly Regex SshFormRegex =
        new(@"^(?:ssh://)?(?:[^@/:]+@)?([^:/]+)[:/](.+)$", RegexOptions.Compiled);

    private readonly ILogger<ProjectIdResolver> _logger;

    public ProjectIdResolver(ILogger<ProjectIdResolver>? logger = null)
    {
        _logger = logger ?? NullLogger<ProjectIdResolver>.Instance;
    }

    public ProjectIdentity Resolve(string absolutePath, string hostId)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            throw new ArgumentException("absolutePath must be non-empty", nameof(absolutePath));
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("hostId must be non-empty", nameof(hostId));

        var fullPath = Path.GetFullPath(absolutePath);

        if (!Directory.Exists(fullPath))
        {
            _logger.LogDebug("Path '{Path}' does not exist; using fallback identity", fullPath);
            return BuildFallback(hostId, fullPath, "path does not exist");
        }

        if (!Repository.IsValid(fullPath))
        {
            _logger.LogDebug("Path '{Path}' is not a git repository; using fallback identity", fullPath);
            return BuildFallback(hostId, fullPath, "not a git repository");
        }

        try
        {
            using var repo = new Repository(fullPath);

            if (repo.Info.IsShallow)
            {
                _logger.LogWarning(
                    "Repository '{Path}' is a shallow clone; falling back to non-git identity. " +
                    "Run `git fetch --unshallow` to enable cross-host project dedupe.",
                    fullPath);
                return BuildFallback(hostId, fullPath, "shallow clone");
            }

            var rootCommit = FindRootCommit(repo);
            if (rootCommit == null)
            {
                _logger.LogDebug("Repository '{Path}' has no commits; using fallback identity", fullPath);
                return BuildFallback(hostId, fullPath, "no commits");
            }

            var canonical = ResolveCanonicalRemoteUrl(repo);
            if (canonical == null)
            {
                _logger.LogDebug("Repository '{Path}' has no remotes; using fallback identity", fullPath);
                return BuildFallback(hostId, fullPath, "no remote configured");
            }

            var rootSha = rootCommit.Sha.ToLowerInvariant();
            var projectId = ComputeGitProjectId(rootSha, canonical);
            return new ProjectIdentity(projectId, "git", canonical, rootSha, null);
        }
        catch (LibGit2SharpException ex)
        {
            _logger.LogWarning(ex, "libgit2 error resolving project identity for '{Path}'; using fallback", fullPath);
            return BuildFallback(hostId, fullPath, $"libgit2 error: {ex.Message}");
        }
    }

    /// <summary>
    /// Walks the first-parent chain from HEAD down to a commit with no parents — the
    /// initial commit. Returns null if HEAD is unborn (no commits at all).
    /// </summary>
    internal static Commit? FindRootCommit(Repository repo)
    {
        var head = repo.Head?.Tip;
        if (head == null) return null;

        var current = head;
        while (current.Parents.Any())
            current = current.Parents.First();
        return current;
    }

    /// <summary>
    /// Picks a remote URL: <c>origin</c> if it exists, otherwise the first remote in
    /// alphabetical order. Returns null if there are no remotes. The returned URL is
    /// already canonicalized via <see cref="NormalizeRemoteUrl"/>.
    /// </summary>
    internal static string? ResolveCanonicalRemoteUrl(Repository repo)
    {
        var origin = repo.Network.Remotes["origin"];
        var picked = origin ?? repo.Network.Remotes.OrderBy(r => r.Name).FirstOrDefault();
        if (picked == null || string.IsNullOrEmpty(picked.Url))
            return null;
        return NormalizeRemoteUrl(picked.Url);
    }

    /// <summary>
    /// Applies the §2.2 normalization rules: lowercase, strip credentials, strip trailing
    /// <c>.git</c>, strip trailing <c>/</c>, convert SSH form to HTTPS, default scheme.
    /// </summary>
    public static string NormalizeRemoteUrl(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            throw new ArgumentException("remoteUrl must be non-empty", nameof(remoteUrl));

        var s = remoteUrl.Trim().ToLowerInvariant();

        // SSH form: git@host:owner/repo  OR  ssh://git@host[:port]/owner/repo  OR  ssh://host/path
        var ssh = SshFormRegex.Match(s);
        if (ssh.Success && !s.Contains("://"))
        {
            // Plain `git@host:path` (no scheme) — convert to https.
            var host = ssh.Groups[1].Value;
            var path = ssh.Groups[2].Value;
            s = $"https://{host}/{path}";
        }
        else if (s.StartsWith("ssh://"))
        {
            // ssh://[user@]host[:port]/path → https://host/path
            // Strip "ssh://" then re-parse host/path manually because the user/port complicate regex.
            var rest = s["ssh://".Length..];
            var atIdx = rest.IndexOf('@');
            if (atIdx >= 0) rest = rest[(atIdx + 1)..];
            var slashIdx = rest.IndexOf('/');
            string host, path;
            if (slashIdx < 0) { host = rest; path = ""; }
            else { host = rest[..slashIdx]; path = rest[(slashIdx + 1)..]; }
            // Drop port if present.
            var colonIdx = host.IndexOf(':');
            if (colonIdx >= 0) host = host[..colonIdx];
            s = $"https://{host}/{path}";
        }

        // Strip credentials between `://` and host.
        var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            var scheme = s[..schemeIdx];
            var afterScheme = s[(schemeIdx + 3)..];
            var atIdx = afterScheme.IndexOf('@');
            if (atIdx >= 0)
            {
                // user[:pass]@host…
                afterScheme = afterScheme[(atIdx + 1)..];
            }
            s = $"{scheme}://{afterScheme}";
        }
        else
        {
            // No scheme survived — default to https.
            s = $"https://{s}";
        }

        // Strip trailing .git
        if (s.EndsWith(".git", StringComparison.Ordinal))
            s = s[..^4];

        // Strip trailing slashes
        while (s.EndsWith('/'))
            s = s[..^1];

        return s;
    }

    /// <summary>
    /// Computes <c>sha256_hex(root_commit_sha + "\n" + canonical_remote_url)</c>.
    /// Inputs are expected lowercase ASCII; this method does not re-lowercase them.
    /// </summary>
    public static string ComputeGitProjectId(string rootCommitSha, string canonicalRemoteUrl)
    {
        var input = $"{rootCommitSha}\n{canonicalRemoteUrl}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Computes <c>sha256_hex("fallback\n" + host_id + "\n" + absolute_path)</c>.
    /// Including <c>host_id</c> makes it explicit that this id will not match across hosts.
    /// </summary>
    public static string ComputeFallbackProjectId(string hostId, string absolutePath)
    {
        var input = $"fallback\n{hostId}\n{absolutePath}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }

    private static ProjectIdentity BuildFallback(string hostId, string fullPath, string reason)
    {
        var pid = ComputeFallbackProjectId(hostId, fullPath);
        return new ProjectIdentity(pid, "fallback", null, null, reason);
    }
}
