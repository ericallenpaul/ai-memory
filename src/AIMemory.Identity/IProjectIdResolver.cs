namespace AIMemory.Identity;

/// <summary>
/// Resolves a directory path to a stable cross-host project identity. See design doc §2.2.
/// </summary>
public interface IProjectIdResolver
{
    /// <summary>
    /// Computes <c>project_id</c> for <paramref name="absolutePath"/>.
    ///
    /// <para>For non-shallow git repos with a remote, identity is derived from
    /// <c>sha256_hex(root_commit_sha + "\n" + canonical_remote_url)</c>.</para>
    ///
    /// <para>For non-git directories, shallow clones, or repos without a remote, falls back
    /// to <c>sha256_hex("fallback\n" + host_id + "\n" + absolute_path)</c>. Fallback ids do
    /// not dedupe across hosts.</para>
    /// </summary>
    /// <param name="absolutePath">Absolute, normalized path to the project root.</param>
    /// <param name="hostId">The local host's id, for use in the non-git fallback formula.</param>
    /// <returns>A <see cref="ProjectIdentity"/> describing the kind, id, and contributing inputs.</returns>
    ProjectIdentity Resolve(string absolutePath, string hostId);
}

/// <summary>
/// The resolved identity of a project at a given path.
/// </summary>
/// <param name="ProjectId">64-char lowercase hex SHA-256.</param>
/// <param name="IdentityKind"><c>"git"</c> or <c>"fallback"</c>.</param>
/// <param name="CanonicalRemoteUrl">Normalized remote URL when <c>IdentityKind == "git"</c>; else null.</param>
/// <param name="RootCommitSha">Initial commit SHA-1 when <c>IdentityKind == "git"</c>; else null.</param>
/// <param name="Reason">Human-readable explanation when <c>IdentityKind == "fallback"</c>.</param>
public sealed record ProjectIdentity(
    string ProjectId,
    string IdentityKind,
    string? CanonicalRemoteUrl,
    string? RootCommitSha,
    string? Reason);
