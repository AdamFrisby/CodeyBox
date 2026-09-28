namespace CodeyBox.Core;

/// <summary>
/// Host-side path policy for the orchestrator process.
/// <para>
/// Host/guest distinction: paths inside a sandbox guest are always Linux
/// paths (<c>/work</c>, <c>/run/codeybox/creds</c>) with forward slashes,
/// regardless of the orchestrator host OS. Helpers here apply only to
/// orchestrator-host paths (git roots, state DB, SSH key files, staging
/// dirs) and must be OS-neutral: <see cref="Path.Combine"/>, never
/// string-concatenated separators.
/// </para>
/// </summary>
public static class HostPathPolicy
{
    /// <summary>
    /// Default bare-repo root appropriate for the current host OS.
    /// Linux/macOS share the Filesystem Hierarchy path; Windows uses
    /// %PROGRAMDATA% (falling back to the user profile when unset).
    /// </summary>
    public static string DefaultGitRootDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetEnvironmentVariable("PROGRAMDATA");
            if (!string.IsNullOrWhiteSpace(programData))
                return Path.Combine(programData, "CodeyBox", "repos");
            var profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrWhiteSpace(profile))
                return Path.Combine(profile, "CodeyBox", "repos");
            return Path.Combine(Path.GetTempPath(), "CodeyBox", "repos");
        }

        return "/var/lib/codeybox/repos";
    }

    /// <summary>Default SQLite state path appropriate for the current host OS.</summary>
    public static string DefaultStateDatabasePath()
    {
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetEnvironmentVariable("PROGRAMDATA");
            if (!string.IsNullOrWhiteSpace(programData))
                return Path.Combine(programData, "CodeyBox", "state.db");
            var profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrWhiteSpace(profile))
                return Path.Combine(profile, "CodeyBox", "state.db");
        }

        return "/var/lib/codeybox/state.db";
    }

    /// <summary>
    /// Expands a leading <c>~</c> against the current user's home directory on
    /// any host OS (used for SSH key paths and similar operator config).
    /// Non-tilde inputs are returned unchanged; never null.
    /// </summary>
    public static string ExpandHomeDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        if (path[0] != '~')
            return path;
        if (path.Length > 1 && path[1] != '/' && path[1] != '\\')
            return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return path;
        if (path.Length == 1)
            return home;
        // Normalise the tilde suffix's separators: Path.Combine inserts the
        // platform separator before the suffix but leaves embedded '/' as-is,
        // producing a mixed-separator path on Windows.
        var suffix = path.Substring(2)
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        return Path.Combine(home, suffix);
    }

    /// <summary>
    /// True when a host path is absolute on the current OS. Wraps
    /// <see cref="Path.IsPathRooted"/> so call sites read as policy.
    /// </summary>
    public static bool IsAbsoluteHostPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path);

    /// <summary>
    /// Joins host-path segments with <see cref="Path.Combine"/> after
    /// expanding a leading <c>~</c> on the first segment. Throws on empty input.
    /// </summary>
    public static string CombineHostPath(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Length == 0)
            throw new ArgumentException("At least one path segment is required.", nameof(segments));
        var expanded = segments.ToArray();
        expanded[0] = ExpandHomeDirectory(expanded[0]);
        return Path.Combine(expanded);
    }

    /// <summary>
    /// Normalizes separators for display/comparison only. Comparison of host
    /// paths must use <see cref="StringComparison.OrdinalIgnoreCase"/> on
    /// Windows/macOS (case-insensitive filesystems) and
    /// <see cref="StringComparison.Ordinal"/> on Linux.
    /// </summary>
    public static StringComparison HostPathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Case-correct equality for two host paths on the current OS.</summary>
    public static bool HostPathsEqual(string? left, string? right) =>
        string.Equals(
            NormalizeSeparators(left),
            NormalizeSeparators(right),
            HostPathComparison);

    /// <summary>
    /// Replaces the alt separator with the platform separator so mixed
    /// <c>/</c> and <c>\</c> input compares and joins consistently.
    /// Null/empty input yields empty string, never null.
    /// </summary>
    public static string NormalizeSeparators(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        var alt = Path.DirectorySeparatorChar == '/' ? '\\' : '/';
        return path.Replace(alt, Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// The one canonical "is this path inside that directory" predicate:
    /// both inputs are normalized with <see cref="Path.GetFullPath"/> (dot
    /// segments collapsed), then <paramref name="path"/> is inside when it
    /// equals <paramref name="directory"/> or sits under it. Security
    /// guards must share this instead of re-implementing prefix checks with
    /// divergent edge semantics. Lexical only — symlink components are NOT
    /// resolved, so a boundary a symlink could cross must be checked
    /// against already-canonicalized inputs.
    /// <para>
    /// Non-rooted, empty, or unparseable inputs return false rather than
    /// resolving silently against the process's current directory — a
    /// containment verdict must never depend on ambient state. The escape
    /// check matches whole "<c>..</c>" segments only: a leaf whose name
    /// merely begins with "<c>..</c>" (e.g. <c>..evil</c>) inside
    /// <paramref name="directory"/> still counts as within.
    /// </para>
    /// </summary>
    public static bool IsWithinDirectory(string path, string directory)
        => ContainedRelativePath(path, directory) is not null;

    /// <summary>
    /// Strict form of <see cref="IsWithinDirectory"/> for guards that
    /// require a real child entry: <paramref name="path"/> equal to
    /// <paramref name="directory"/> itself is NOT within.
    /// </summary>
    public static bool IsStrictlyWithinDirectory(string path, string directory)
    {
        var relative = ContainedRelativePath(path, directory);
        return relative is not null && relative != ".";
    }

    /// <summary>
    /// <paramref name="path"/>'s <see cref="Path.GetRelativePath"/> location
    /// under <paramref name="directory"/> — "<c>.</c>" for equality — when
    /// contained, or null when it escapes or the inputs cannot be judged.
    /// </summary>
    private static string? ContainedRelativePath(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(directory)
            || !Path.IsPathRooted(path)
            || !Path.IsPathRooted(directory))
            return null;

        string relative;
        try
        {
            relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }

        var escapes = Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        return escapes ? null : relative;
    }
}
