using System.Text;
using System.Text.Json;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// Shared low-level helpers for <see cref="IExternalToolOutputParser"/>
/// implementations: JSON property readers, single-line flattening and
/// truncation for untrusted tool text, and the path normalization tools need
/// before findings reach the audit pipeline. Plugin report parsers share
/// these rather than re-implementing them per assembly.
/// </summary>
public static class ExternalToolJsonHelpers
{
    /// <summary>
    /// Scheme prefix parsers re-apply to a reported path that cannot be made
    /// repository-relative: the base trims a bare leading '/' from finding
    /// paths, so an unmarked out-of-root absolute path — or a relative path
    /// still carrying '..' — would read as repository-relative and could
    /// accidentally match repo-relative
    /// <see cref="ExternalToolAuditorOptions.ExcludePaths"/> entries.
    /// </summary>
    public const string FileSchemePrefix = "file://";

    /// <summary>
    /// Reads a string-valued property; null when the property is absent or is
    /// not a JSON string. <paramref name="utf8Name"/> is the UTF-8 property
    /// name (e.g. <c>"rule"u8</c>).
    /// </summary>
    public static string? GetString(JsonElement element, ReadOnlySpan<byte> utf8Name)
        => element.TryGetProperty(utf8Name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Returns the string value when the element is a JSON string; null otherwise.</summary>
    public static string? CoerceString(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    /// <summary>Null for null, empty, or whitespace; the value otherwise.</summary>
    public static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Flattens untrusted text to a single line: every control character —
    /// newlines, tabs, ESC and other terminal escape bytes — becomes a space
    /// so tool output cannot inject sequences into logged messages or
    /// persisted failure reasons.
    /// </summary>
    public static string SingleLine(string message)
    {
        var builder = new StringBuilder(message.Length);
        foreach (var c in message)
            builder.Append(char.IsControl(c) ? ' ' : c);
        return builder.ToString().Trim();
    }

    /// <summary>Truncates to <paramref name="maxChars"/> with an ellipsis suffix.</summary>
    public static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars] + "...";

    /// <summary>
    /// Normalizes a tool-reported path for comparison and relativization:
    /// forward slashes, trimmed. Returns empty for null.
    /// </summary>
    public static string NormalizePath(string? path)
        => (path ?? string.Empty).Replace('\\', '/').Trim();

    /// <summary>
    /// Strips the scan-root prefix from a tool-reported path so the finding
    /// is repository-relative. The reported path is normalized (forward
    /// slashes, trimmed) so raw tool output can be passed directly, but the
    /// root is compared VERBATIM (forward slashes, trailing slash dropped,
    /// never whitespace-trimmed): a canonical root whose name legitimately
    /// ends in whitespace must still relativize the paths under it rather
    /// than silently diverging from what the probe emitted. A path not
    /// under the root — or the root itself — is returned normalized but
    /// otherwise untouched; nothing is silently rewritten.
    /// </summary>
    public static string RelativizeToRoot(string? path, string? root)
    {
        var normalized = NormalizePath(path);
        var normalizedRoot = (root ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        if (normalizedRoot.Length == 0
            || normalized.Length <= normalizedRoot.Length + 1
            || !normalized.StartsWith(normalizedRoot + "/", StringComparison.Ordinal))
            return normalized;
        return normalized[(normalizedRoot.Length + 1)..];
    }

    /// <summary>
    /// The shared reported-path policy for tool JSON reports: forward-slash
    /// normalization, an incoming <see cref="FileSchemePrefix"/> prefix
    /// stripped, and dot segments collapsed lexically BEFORE relativization —
    /// otherwise "/root/../outside" would strip the root prefix and reach
    /// findings as the pseudo repo-relative "../outside". Absolute paths are
    /// relativized against <paramref name="scanRoot"/> first, then the exec
    /// <paramref name="workingDirectory"/>; a path that stays absolute — and
    /// a relative path still carrying '..' — is re-marked with
    /// <see cref="FileSchemePrefix"/> so it stays distinguishable from a
    /// repository-relative location. Returns null for empty input.
    /// </summary>
    public static string? NormalizeReportedPath(string? raw, string? scanRoot, string? workingDirectory)
    {
        var path = NormalizePath(raw);
        if (path.Length == 0)
            return null;
        if (path.StartsWith(FileSchemePrefix, StringComparison.OrdinalIgnoreCase))
            path = path[FileSchemePrefix.Length..];

        path = CollapseDotSegments(path);
        if (path.Length == 0)
            return null;

        if (!path.StartsWith("/", StringComparison.Ordinal))
            return HasDotDotSegment(path) ? FileSchemePrefix + path : path;

        var relative = RelativizeToRoot(path, scanRoot);
        if (!relative.StartsWith("/", StringComparison.Ordinal))
            return relative;
        relative = RelativizeToRoot(path, workingDirectory);
        return relative.StartsWith("/", StringComparison.Ordinal)
            ? FileSchemePrefix + path
            : relative;
    }

    /// <summary>
    /// Lexically resolves '.' and '..' segments on a '/'-separated path —
    /// '..' past the root clamps for absolute paths, and stays a leading
    /// segment for relative ones (a genuine escape the caller marks). Tool
    /// report paths are untrusted text: collapsing here keeps a report from
    /// smuggling traversal segments into finding locations.
    /// </summary>
    public static string CollapseDotSegments(string path)
    {
        var absolute = path.Length > 0 && path[0] == '/';
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count > 0 && segments[^1] != "..")
                    segments.RemoveAt(segments.Count - 1);
                else if (!absolute)
                    segments.Add(segment);
                continue;
            }
            segments.Add(segment);
        }
        var joined = string.Join('/', segments);
        return absolute ? "/" + joined : joined;
    }

    private static bool HasDotDotSegment(string path)
        => path.Split('/').Contains("..", StringComparer.Ordinal);
}
