using System.Security.Cryptography;
using System.Text;

namespace CodeyBox.Core.ExternalBuilds;

/// <summary>
/// One captured source file entry (relative path, bytes, mode).
/// </summary>
public sealed record ExternalBuildFileEntry(string RelativePath, long ByteSize, string Mode, string ContentDigestSha256);

/// <summary>
/// Frozen immutable candidate snapshot captured from a work tree that may
/// still contain uncommitted changes.
/// </summary>
public sealed record ExternalBuildSnapshot(
    string SnapshotId,
    string SourceDigestSha256,
    IReadOnlyList<ExternalBuildFileEntry> Files,
    long TotalBytes,
    bool HasDeletions,
    string PolicyDigest);

/// <summary>
/// Snapshot capture policy: explicit excludes, size/path bounds, LFS and
/// submodule pin/materialization policy, and Git-publication rules.
/// </summary>
public sealed record ExternalBuildSnapshotPolicy
{
    public IReadOnlyList<string> ExcludePrefixes { get; init; } = [
        ".git/", ".codeybox/secrets", ".codeybox/credentials",
        ".agent-scratch/", ".transcripts/", ".cache/", "node_modules/.cache/",
    ];
    public IReadOnlyList<string> ExcludeExactNames { get; init; } = [".env", ".env.local", "secrets.json"];
    public int MaxPathChars { get; init; } = 512;
    public long MaxTotalBytes { get; init; } = 256L * 1024 * 1024;
    public int MaxFiles { get; init; } = 50_000;
    public long MaxFileBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>LFS: only pinned pointer blobs are captured; large binaries need explicit materialization.</summary>
    public bool MaterializeLfsBlobs { get; init; }
    /// <summary>Submodules: pinned commits are recorded; contents captured only when listed here.</summary>
    public IReadOnlyList<string> MaterializedSubmodules { get; init; } = [];
}

/// <summary>
/// Pure snapshot builder over caller-supplied file bytes. The host supplies
/// the file enumeration (tracked changes, deletions, modes, intended untracked
/// source); this core enforces excludes, bounds, and digest computation so
/// every adapter reuses one policy instead of forking it.
/// </summary>
public static class ExternalBuildSnapshotBuilder
{
    public static ExternalBuildSnapshot Freeze(
        IReadOnlyList<(string RelativePath, byte[] Content, string Mode)> tracked,
        IReadOnlyList<string> deletions,
        IReadOnlyList<(string RelativePath, byte[] Content, string Mode)> untracked,
        ExternalBuildSnapshotPolicy? policy = null)
    {
        policy ??= new ExternalBuildSnapshotPolicy();
        ArgumentNullException.ThrowIfNull(tracked);
        ArgumentNullException.ThrowIfNull(deletions);
        ArgumentNullException.ThrowIfNull(untracked);

        var files = new List<ExternalBuildFileEntry>();
        long total = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string rel, byte[] content, string mode)
        {
            var normalized = Normalize(rel);
            if (IsExcluded(normalized, policy))
                return;
            if (normalized.Length > policy.MaxPathChars)
                throw new InvalidOperationException($"Snapshot path exceeds {policy.MaxPathChars} chars: '{normalized}'.");
            if (content.LongLength > policy.MaxFileBytes)
                throw new InvalidOperationException($"Snapshot file '{normalized}' exceeds per-file bound.");
            total = checked(total + content.LongLength);
            if (total > policy.MaxTotalBytes)
                throw new InvalidOperationException("Snapshot exceeds total-bytes bound.");
            if (!seen.Add(normalized))
                throw new InvalidOperationException($"Snapshot captures '{normalized}' twice (edit race).");
            files.Add(new ExternalBuildFileEntry(
                normalized, content.LongLength, mode,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()));
            if (files.Count > policy.MaxFiles)
                throw new InvalidOperationException("Snapshot exceeds file-count bound.");
        }

        foreach (var (rel, content, mode) in tracked) Add(rel, content, mode);
        foreach (var (rel, content, mode) in untracked) Add(rel, content, mode);

        var digestInput = new StringBuilder();
        foreach (var f in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
            digestInput.Append(f.RelativePath).Append('\0').Append(f.ContentDigestSha256).Append('\0').Append(f.Mode).Append('\0');
        foreach (var d in deletions.Select(Normalize).OrderBy(d => d, StringComparer.Ordinal))
            digestInput.Append("delete:").Append(d).Append('\0');
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(digestInput.ToString()))).ToLowerInvariant();
        return new ExternalBuildSnapshot(
            "snap-" + digest[..16], digest,
            files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList(),
            total, deletions.Count > 0, ComputePolicyDigest(policy));
    }

    public static string ComputePolicyDigest(ExternalBuildSnapshotPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var payload = string.Join('\0', policy.ExcludePrefixes)
            + '\0' + string.Join('\0', policy.ExcludeExactNames)
            + '\0' + policy.MaxPathChars
            + '\0' + policy.MaxTotalBytes
            + '\0' + policy.MaxFiles
            + '\0' + policy.MaxFileBytes
            + '\0' + policy.MaterializeLfsBlobs
            + '\0' + string.Join('\0', policy.MaterializedSubmodules);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static string Normalize(string rel)
    {
        ArgumentNullException.ThrowIfNull(rel);
        var n = rel.Replace('\\', '/').Trim();
        while (n.StartsWith("./", StringComparison.Ordinal)) n = n[3..];
        while (n.Contains("//", StringComparison.Ordinal)) n = n.Replace("//", "/", StringComparison.Ordinal);
        if (n.StartsWith('/')) n = n[1..];
        var parts = n.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p == ".."))
            throw new InvalidOperationException($"Snapshot path escapes the tree: '{rel}'.");
        return string.Join('/', parts);
    }

    public static bool IsExcluded(string normalized, ExternalBuildSnapshotPolicy policy)
    {
        foreach (var prefix in policy.ExcludePrefixes)
            if (normalized.StartsWith(prefix, StringComparison.Ordinal)
                || normalized.Equals(prefix.TrimEnd('/'), StringComparison.Ordinal))
                return true;
        var fileName = normalized.Contains('/') ? normalized[(normalized.LastIndexOf('/') + 1)..] : normalized;
        foreach (var exact in policy.ExcludeExactNames)
            if (string.Equals(fileName, exact, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Verifies a snapshot against the actual captured bytes (post-transfer
    /// check). Re-freezes the supplied content and compares the recomputed
    /// source digest with exact equality; any mismatch fails closed.
    /// </summary>
    public static bool VerifyAgainstContent(
        ExternalBuildSnapshot snapshot,
        IReadOnlyList<(string RelativePath, byte[] Content, string Mode)> tracked,
        IReadOnlyList<string> deletions,
        IReadOnlyList<(string RelativePath, byte[] Content, string Mode)> untracked,
        ExternalBuildSnapshotPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var rebuilt = Freeze(tracked, deletions, untracked, policy);
        return string.Equals(rebuilt.SourceDigestSha256, snapshot.SourceDigestSha256, StringComparison.Ordinal)
            && rebuilt.TotalBytes == snapshot.TotalBytes
            && rebuilt.Files.Count == snapshot.Files.Count
            && rebuilt.HasDeletions == snapshot.HasDeletions;
    }
}

/// <summary>
/// Policy for the host-only temporary scoped candidate commit/ref publication
/// used when a provider requires Git. The host alone may publish; the
/// work/base branch never moves and no delivery PR is published here.
/// </summary>
public sealed record ExternalBuildGitPublicationPolicy
{
    /// <summary>Allowed ref namespace prefix (exact prefix match). Default <c>refs/candidates/</c>.</summary>
    public string AllowedRefPrefix { get; init; } = "refs/candidates/";
    /// <summary>Ref TTL in seconds. Default 86400.</summary>
    public int RefTtlSeconds { get; init; } = 86400;

    public void ValidateRef(string candidateRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateRef);
        if (!candidateRef.StartsWith(AllowedRefPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Candidate ref '{candidateRef}' is outside the approved '{AllowedRefPrefix}' namespace.");
    }
}
