using System.Text.RegularExpressions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Extracts the file paths compiler/build errors point at from captured
/// <c>dotnet build</c> output. Pure string parsing — the only inputs are the
/// already-redacted build log lines. Used to decide whether a required-build
/// failure can be attributed to the item's diff (any error file inside the
/// diff) or warrants a base-branch build comparison.
/// </summary>
internal static partial class BuildErrorLocationParser
{
    /// <summary>
    /// MSBuild / compiler diagnostic line shapes this matches:
    /// <list type="bullet">
    ///   <item><c>src/Foo.cs(12,3): error CS0108: message</c> (CSC/Roslyn)</item>
    ///   <item><c>/abs/path/Foo.cs(12,3): error CS0108: message</c></item>
    ///   <item><c>src/Foo.csproj : error NU1101: message</c> (project-level, no line info)</item>
    ///   <item><c>C:\src\Foo.cs(12,3): error CS0108: message</c> (Windows-style, normalized)</item>
    /// </list>
    /// The path group is deliberately non-greedy and the <c>: error CODE:</c>
    /// anchor is required, so a Windows drive colon inside the path
    /// backtracks to the real <c>: error</c> boundary instead of truncating
    /// the path at the drive letter.
    /// </summary>
    [GeneratedRegex(
        @"^(?<path>.+?)(?:\(\d+(?:,\d+)?\))?\s*:\s*error\s+[A-Za-z]+\d*\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorLineRegex();

    /// <summary>
    /// Returns the distinct normalized (forward-slash, no leading <c>./</c>)
    /// file paths that carry a compiler error diagnostic, in first-seen
    /// order. Non-error lines, warnings, and bare "Build FAILED" summaries
    /// contribute nothing.
    /// </summary>
    public static IReadOnlyList<string> ParseErrorPaths(string? buildOutput)
    {
        if (string.IsNullOrEmpty(buildOutput))
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        foreach (var rawLine in buildOutput.Split('\n', StringSplitOptions.None))
        {
            var line = rawLine.TrimEnd('\r');
            var match = ErrorLineRegex().Match(line);
            if (!match.Success)
                continue;

            var normalized = NormalizePath(match.Groups["path"].Value);
            if (normalized is null)
                continue;
            if (seen.Add(normalized))
                paths.Add(normalized);
        }

        return paths;
    }

    /// <summary>
    /// True when <paramref name="errorPath"/> refers to the same repository
    /// file as <paramref name="diffPath"/> (a <c>git diff</c> path). Both are
    /// normalized forward-slash paths. Equality is segment-aware: an exact
    /// match, or one path being a proper suffix of the other on a <c>/</c>
    /// boundary (sandbox builds emit absolute or project-relative paths
    /// while the diff is repo-relative). Never substring — <c>Foo.cs</c>
    /// must not match <c>xFoo.cs</c>.
    /// </summary>
    public static bool PathsReferToSameFile(string errorPath, string diffPath)
    {
        if (string.Equals(errorPath, diffPath, StringComparison.OrdinalIgnoreCase))
            return true;
        if (errorPath.Length > diffPath.Length)
            return errorPath.EndsWith("/" + diffPath, StringComparison.OrdinalIgnoreCase);
        return diffPath.EndsWith("/" + errorPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when any parsed error path matches any path in
    /// <paramref name="diffPaths"/> under <see cref="PathsReferToSameFile"/>.
    /// </summary>
    public static bool AnyErrorPathInDiff(
        IReadOnlyList<string> errorPaths,
        IReadOnlyCollection<string> diffPaths)
    {
        foreach (var errorPath in errorPaths)
        {
            foreach (var diffPath in diffPaths)
            {
                if (PathsReferToSameFile(errorPath, diffPath))
                    return true;
            }
        }
        return false;
    }

    private static string? NormalizePath(string raw)
    {
        var trimmed = raw.Trim().Trim('"');
        if (trimmed.Length == 0)
            return null;

        var path = trimmed.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        return path.Length == 0 ? null : path;
    }
}
