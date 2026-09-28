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
    /// Scheme prepended to a tool-reported path that resolves outside the
    /// audited root. <see cref="ExternalToolAuditorBase"/> trims a bare
    /// leading <c>/</c> from finding paths, which would de-root
    /// <c>/opt/…</c> into a string indistinguishable from a
    /// repository-relative path — able to match repo-relative
    /// <c>ExcludePaths</c> entries and to read as evidence inside the
    /// audited tree. The marker keeps out-of-tree locations
    /// distinguishable.
    /// </summary>
    public const string FileUriSchemePrefix = "file://";

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
    /// is repository-relative. Both inputs are normalized (forward slashes,
    /// trimmed) before comparison and the root loses any trailing slash, so
    /// raw tool output can be passed directly. A path not under the root —
    /// or the root itself — is returned normalized but otherwise untouched;
    /// nothing is silently rewritten.
    /// </summary>
    public static string RelativizeToRoot(string? path, string? root)
    {
        var normalized = NormalizePath(path);
        var normalizedRoot = NormalizePath(root).TrimEnd('/');
        if (normalizedRoot.Length == 0
            || normalized.Length <= normalizedRoot.Length + 1
            || !normalized.StartsWith(normalizedRoot + "/", StringComparison.Ordinal))
            return normalized;
        return normalized[(normalizedRoot.Length + 1)..];
    }

    /// <summary>
    /// Normalizes a tool-reported path into a finding location. An absolute
    /// path under <paramref name="scanRoot"/> comes back repository-relative;
    /// an absolute path outside it returns re-marked with
    /// <see cref="FileUriSchemePrefix"/> so it can never masquerade as a
    /// repository path once the base trims bare leading slashes. A relative
    /// path passes through normalized — except one carrying <c>..</c>
    /// segments, which is resolved lexically against the root first so a
    /// name that escapes the root gets the same out-of-tree marking rather
    /// than surviving as a repository-relative-looking traversal. Null or
    /// empty input returns null.
    /// </summary>
    public static string? RelativizeOrMarkFileUri(string? rawPath, string? scanRoot)
    {
        var path = NormalizePath(rawPath);
        if (path.Length == 0)
            return null;
        if (path.StartsWith(FileUriSchemePrefix, StringComparison.OrdinalIgnoreCase))
            path = path[FileUriSchemePrefix.Length..];

        if (path[0] == '/')
        {
            var underRoot = RelativizeToRoot(path, scanRoot);
            return string.Equals(underRoot, path, StringComparison.Ordinal)
                ? FileUriSchemePrefix + path
                : underRoot;
        }

        var root = NormalizePath(scanRoot).TrimEnd('/');
        if (!path.Split('/').Contains("..", StringComparer.Ordinal)
            || root.Length == 0
            || root[0] != '/')
            return path;

        // Resolve '..' lexically: segments pop the accumulated root/head
        // and one that escapes the filesystem root clamps at '/'.
        var resolved = new List<string> { string.Empty };
        resolved.AddRange(root.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".")
                continue;
            if (segment == "..")
            {
                if (resolved.Count > 1)
                    resolved.RemoveAt(resolved.Count - 1);
            }
            else
            {
                resolved.Add(segment);
            }
        }

        var absolute = string.Join('/', resolved);
        var relative = RelativizeToRoot(absolute, root);
        return string.Equals(relative, absolute, StringComparison.Ordinal)
            ? FileUriSchemePrefix + absolute
            : relative;
    }
}
