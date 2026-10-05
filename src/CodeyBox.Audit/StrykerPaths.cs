using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Single source of truth for repository-relative path validation shared by
/// the Stryker runner, its project selection, and its options validation.
/// Changed-file lists arrive from <c>git diff</c> output and discovered csproj
/// paths from sandbox directory listings — both are untrusted input that must
/// never reach a filesystem/process sink unvalidated. Pure: no I/O.
/// </summary>
public static class StrykerPaths
{
    /// <summary>Maximum accepted repository-relative path length.</summary>
    public const int MaxPathLength = 1024;

    /// <summary>
    /// Normalizes <paramref name="path"/> to a repository-relative forward-
    /// slash form, or returns null when the path is hostile or malformed:
    /// absolute, escaping via <c>..</c>, empty, over-long, or carrying NUL /
    /// control characters. A single <c>.</c> segment is dropped; anything
    /// else must be an ordinary relative path that stays inside the repo.
    /// </summary>
    public static string? NormalizeRepoPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength)
            return null;
        foreach (var c in path)
        {
            if (c == '\0' || char.IsControl(c))
                return null;
        }
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.StartsWith("~/", StringComparison.Ordinal))
            return null;
        if (System.IO.Path.IsPathRooted(path))
            return null;
        var segments = new List<string>();
        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
                return null;
            if (segment is "-" or "--")
                return null;
            // Any segment starting with '-' (e.g. "-evil.cs") would parse
            // as a CLI flag where the path later reaches a process argv
            // sink (Stryker -p/-tp/-m values). Stryker's end-of-options
            // handling is version-dependent, so reject such names
            // fail-closed here instead of relying on a "--" separator.
            if (segment[0] == '-')
                return null;
            segments.Add(segment);
        }
        if (segments.Count == 0)
            return null;
        var result = string.Join('/', segments);
        if (result.Length > MaxPathLength)
            return null;
        return result;
    }

    /// <summary>
    /// Returns <paramref name="path"/> (a validated repo-relative path)
    /// relative to <paramref name="baseDir"/> (a validated repo-relative
    /// directory, "" for the root), or null when <paramref name="path"/> is
    /// not under <paramref name="baseDir"/>. Both inputs must already be
    /// normalized; comparison is ordinal.
    /// </summary>
    public static string? Relativize(string path, string baseDir)
    {
        if (baseDir.Length == 0)
            return path;
        if (path.Length <= baseDir.Length)
            return null;
        if (!path.StartsWith(baseDir, StringComparison.Ordinal) || path[baseDir.Length] != '/')
            return null;
        return path[(baseDir.Length + 1)..];
    }

    /// <summary>
    /// Directory portion of a normalized repo-relative path ("" for root).
    /// </summary>
    public static string DirectoryOf(string normalizedPath)
    {
        var index = normalizedPath.LastIndexOf('/');
        return index < 0 ? "" : normalizedPath[..index];
    }

    /// <summary>
    /// Redacts a possibly-hostile string for logs and findings: control
    /// characters become <c>?</c> and the value is truncated. Untrusted
    /// strings never reach logs or findings unescaped.
    /// </summary>
    public static string SanitizeForLog(string? value, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(value))
            return "(empty)";
        var cleaned = new string(value.Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength] + "…";
    }
}

/// <summary>
/// Argv-element validation for the operator-configured Stryker tool command.
/// Elements are either bare binary names (validated by the shared
/// <see cref="ExternalToolNamePolicy"/> single source of truth) or absolute
/// paths to a provisioned shim. Pure: no I/O.
/// </summary>
public static class StrykerArgv
{
    /// <summary>Maximum accepted length of one tool-command element.</summary>
    public const int MaxElementLength = 256;

    /// <summary>
    /// Returns true when <paramref name="element"/> is safe to place
    /// verbatim in a sandbox argv array: a bare binary name, or a rooted
    /// absolute path without <c>..</c> escapes, over-length, NUL, or control
    /// characters. Never a shell string.
    /// </summary>
    public static bool IsValidToolCommandElement(string? element)
    {
        if (string.IsNullOrWhiteSpace(element) || element.Length > MaxElementLength)
            return false;
        foreach (var c in element)
        {
            if (c == '\0' || char.IsControl(c))
                return false;
        }
        if (ExternalToolNamePolicy.IsValidBinaryName(element))
            return true;
        if (!System.IO.Path.IsPathRooted(element))
            return false;
        var normalized = element.Replace('\\', '/');
        foreach (var segment in normalized.Split('/'))
        {
            if (segment == "..")
                return false;
        }
        return true;
    }
}
