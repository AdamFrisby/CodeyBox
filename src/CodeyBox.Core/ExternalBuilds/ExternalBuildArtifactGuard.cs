using System.Security.Cryptography;
using System.Text;

namespace CodeyBox.Core.ExternalBuilds;

/// <summary>
/// Neutral artifact-ingestion guard. Enforces size, decompression, and entry
/// limits BEFORE buffering, rejects archive traversal/symlinks, verifies
/// digests, enforces host URL/redirect allowlists, redacts secrets from
/// diagnostics text, and never executes downloaded artifacts on the host.
/// </summary>
public static class ExternalBuildArtifactGuard
{
    public const int MaxArtifactNameChars = 256;

    public static string? ValidateRef(ExternalBuildArtifactRef artifact, ExternalBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(artifact.Name) || artifact.Name.Length > MaxArtifactNameChars)
            return "artifact name missing or too long";
        var normalized = artifact.Name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains("..", StringComparison.Ordinal))
            return "artifact name escapes its directory";
        if (artifact.SizeBytes < 0 || artifact.SizeBytes > options.MaxArtifactBytes)
            return $"artifact size {artifact.SizeBytes} exceeds {options.MaxArtifactBytes}-byte cap";
        if (artifact.ContentDigestSha256.Length != 64
            || !artifact.ContentDigestSha256.All(c => Uri.IsHexDigit(c)))
            return "artifact digest is not a 64-hex sha256";
        return null;
    }

    public static string? ValidatePayload(ExternalBuildArtifactPayload payload, ExternalBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        if (payload.Content.LongLength > options.MaxArtifactBytes)
            return "artifact payload exceeds size cap";
        var actual = Convert.ToHexString(SHA256.HashData(payload.Content)).ToLowerInvariant();
        if (!string.Equals(actual, payload.ContentDigestSha256, StringComparison.OrdinalIgnoreCase))
            return "artifact digest mismatch";
        return null;
    }

    /// <summary>Scans a tar/zip-style entry list without extracting: traversal, symlink, entry-count and expansion-ratio guards.</summary>
    public static string? ValidateArchiveEntries(
        IReadOnlyList<(string Path, long CompressedSize, long UncompressedSize, bool IsSymlink)> entries,
        ExternalBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);
        if (entries.Count > options.MaxArchiveEntries)
            return $"archive has {entries.Count} entries, exceeding the {options.MaxArchiveEntries} cap";
        long totalUncompressed = 0;
        long totalCompressed = 0;
        foreach (var (path, compressed, uncompressed, isSymlink) in entries)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "archive entry has a blank path";
            var normalized = path.Replace('\\', '/').Trim();
            if (normalized.StartsWith('/') || normalized.Contains("..", StringComparison.Ordinal)
                || Path.IsPathRooted(normalized))
                return $"archive entry escapes its directory: '{path}'";
            if (isSymlink)
                return $"archive entry is a symlink: '{path}'";
            if (compressed < 0 || uncompressed < 0)
                return "archive entry has a negative size";
            totalUncompressed = checked(totalUncompressed + uncompressed);
            totalCompressed = checked(totalCompressed + Math.Max(0, compressed));
            if (totalUncompressed > options.MaxDecompressedBytes)
                return "archive exceeds decompression cap";
        }
        if (totalCompressed > 0 && totalUncompressed > totalCompressed * 100)
            return "archive expansion ratio exceeds 100x (zip-bomb guard)";
        return null;
    }

    /// <summary>Host URL/redirect allowlist: exact-host match, https only, no credentials in URL.</summary>
    public static string? ValidateArtifactUrl(string url, IReadOnlyList<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(allowedHosts);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "artifact URL is not absolute";
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return "artifact URL must be https";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "artifact URL must not embed credentials";
        foreach (var host in allowedHosts)
            if (string.Equals(uri.Host, host.Trim(), StringComparison.OrdinalIgnoreCase))
                return null;
        return $"artifact host '{uri.Host}' is not allowlisted";
    }

    /// <summary>Redacts secret-looking lines from diagnostics text before retention/display.</summary>
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var lines = value.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var lower = lines[i].ToLowerInvariant();
            if (lower.Contains("bearer ") || lower.Contains("api_key") || lower.Contains("apikey")
                || lower.Contains("secret") || lower.Contains("password") || lower.Contains("token="))
                lines[i] = "[redacted]";
        }
        return string.Join('\n', lines);
    }

    public static string TruncateBounded(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars) return value ?? string.Empty;
        return value[..maxChars] + "[...truncated]";
    }
}

/// <summary>Provenance helpers reusing existing artifact digest conventions.</summary>
public static class ExternalBuildProvenance
{
    public static string DigestBytes(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    public static string DigestText(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }
}
