using System.Globalization;
using System.Text.Json;

namespace CodeyBox.Core;

/// <summary>
/// One test in the selection baseline: the file that defines it plus the
/// executable lines it covers (per file). A test with an empty
/// <see cref="Covers"/> map has NO coverage record (new or uninstrumented) and
/// must always be selected — see <see cref="CoverageTestSelector"/>.
/// </summary>
public sealed record BaselineTestEntry(
    string DefiningFile,
    IReadOnlyDictionary<string, IReadOnlyList<int>> Covers);

/// <summary>
/// Project-graph section of the baseline: which MSBuild project owns each
/// source file, and — precomputed by the producer — which tests are affected
/// when each project changes (transitive dependents included).
/// </summary>
public sealed record BaselineProjectGraph(
    IReadOnlyDictionary<string, string> FileProject,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AffectedTestsByProject);

/// <summary>
/// Test-selection baseline consumed by the project-graph and coverage
/// selectors. Produced from the mandatory full-suite-on-main run (per-test
/// XPlat/Cobertura collection parsed with the same executable-line semantics
/// as the diff-scoped coverage gate, plus <c>dotnet sln/project-reference</c>
/// graph data and <c>dotnet test --list-tests</c> enumeration), stored as one
/// JSON artifact.
///
/// <para>DISTRIBUTION (see <c>docs/quality/test-selection.md</c>): the CI job
/// that runs the full suite on <c>main</c> after every merge writes this file;
/// operators EITHER bake it into the audit baseline image at
/// <c>/opt/codeybox/test-selection/baseline.json</c> OR fetch the artifact to
/// that sandbox path at sandbox setup. The per-item shadow hook reads it from
/// the sandbox. STALENESS BOUND: regeneration on every merge to main means the
/// map is at most one merge stale; <see cref="CoverageTestSelectionOptions"/>
/// carries an age backstop on top.</para>
/// </summary>
public sealed record TestSelectionBaseline(
    string Commit,
    DateTimeOffset ProducedAtUtc,
    BaselineProjectGraph ProjectGraph,
    IReadOnlyDictionary<string, BaselineTestEntry> Tests)
{
    /// <summary>Format marker the parser requires (exact match).</summary>
    public const string FormatMarker = "codeybox-test-selection-baseline/1";
}

/// <summary>
/// Limits applied while reading a baseline. Untrusted input (bytes produced
/// inside a sandbox / fetched as an artifact), so every unbounded dimension is
/// capped BEFORE it can exhaust memory. Sourced from
/// <see cref="CoverageTestSelectionOptions"/> (hot-reloadable), not literals.
/// </summary>
public sealed record BaselineReadLimits(
    long MaxBytes,
    int MaxTests,
    long MaxCoveredLines)
{
    public static BaselineReadLimits FromOptions(CoverageTestSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new BaselineReadLimits(options.MaxBaselineBytes, options.MaxBaselineTests, options.MaxBaselineCoveredLines);
    }
}

/// <summary>
/// Strict reader for the <see cref="TestSelectionBaseline"/> JSON artifact.
/// Throws <see cref="FormatException"/> (wrong format marker, missing/invalid
/// timestamps) or <see cref="JsonException"/> (malformed JSON) on any defect;
/// callers treat every failure as "no baseline" and fall back to the full suite
/// (fail-safe). Line numbers are normalised (positive only, deduplicated,
/// sorted) so selection is deterministic.
/// </summary>
public static class TestSelectionBaselineParser
{
    public static TestSelectionBaseline Parse(string json, BaselineReadLimits limits)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "MaxBytes must be positive.");
        if (json.Length > limits.MaxBytes)
            throw new FormatException(string.Create(
                CultureInfo.InvariantCulture,
                $"Baseline exceeds the size cap ({json.Length} chars > {limits.MaxBytes})."));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var formatName = root.TryGetProperty("format", out var formatEl)
            && formatEl.ValueKind == JsonValueKind.String
            ? formatEl.GetString()
            : null;
        if (!string.Equals(formatName, TestSelectionBaseline.FormatMarker, StringComparison.Ordinal))
        {
            throw new FormatException(
                $"Unsupported test-selection baseline format '{formatName ?? "<missing>"}'. " +
                $"Expected '{TestSelectionBaseline.FormatMarker}'.");
        }

        var commit = root.TryGetProperty("commit", out var commitEl) ? commitEl.GetString() ?? "" : "";
        if (!root.TryGetProperty("producedAtUtc", out var producedEl)
            || !producedEl.TryGetDateTimeOffset(out var producedAt))
        {
            throw new FormatException("Baseline is missing a valid 'producedAtUtc' timestamp.");
        }

        var fileProject = ReadStringMap(root, "fileProject");
        var affected = ReadStringListMap(root, "projects");
        var tests = ReadTests(root, limits);

        return new TestSelectionBaseline(
            commit,
            producedAt,
            new BaselineProjectGraph(fileProject, affected),
            tests);
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement root, string property)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Object)
            return map;
        foreach (var kvp in el.EnumerateObject())
        {
            if (kvp.Value.ValueKind == JsonValueKind.String && kvp.Value.GetString() is { } value)
                map[kvp.Name] = value;
        }
        return map;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadStringListMap(JsonElement root, string property)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (!root.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Object)
            return map;
        foreach (var kvp in el.EnumerateObject())
        {
            if (kvp.Value.ValueKind != JsonValueKind.Array)
                continue;
            var list = new List<string>();
            foreach (var item in kvp.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
                    list.Add(value);
            }
            map[kvp.Name] = list;
        }
        return map;
    }

    private static IReadOnlyDictionary<string, BaselineTestEntry> ReadTests(JsonElement root, BaselineReadLimits limits)
    {
        var tests = new Dictionary<string, BaselineTestEntry>(StringComparer.Ordinal);
        if (!root.TryGetProperty("tests", out var el) || el.ValueKind != JsonValueKind.Object)
            return tests;

        long coveredLines = 0;
        foreach (var kvp in el.EnumerateObject())
        {
            if (tests.Count >= limits.MaxTests)
            {
                throw new FormatException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Baseline exceeds the test cap ({limits.MaxTests} tests)."));
            }
            if (kvp.Value.ValueKind != JsonValueKind.Object)
                continue;

            var file = "";
            if (kvp.Value.TryGetProperty("file", out var fileEl)
                && fileEl.ValueKind == JsonValueKind.String)
            {
                file = fileEl.GetString() ?? "";
            }

            var covers = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
            if (kvp.Value.TryGetProperty("covers", out var coversEl)
                && coversEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var cover in coversEl.EnumerateObject())
                {
                    if (cover.Value.ValueKind != JsonValueKind.Array)
                        continue;
                    var lines = new SortedSet<int>();
                    foreach (var line in cover.Value.EnumerateArray())
                    {
                        if (line.ValueKind == JsonValueKind.Number && line.TryGetInt32(out var n) && n >= 1)
                            lines.Add(n);
                    }
                    coveredLines += lines.Count;
                    if (coveredLines > limits.MaxCoveredLines)
                    {
                        throw new FormatException(string.Create(
                            CultureInfo.InvariantCulture,
                            $"Baseline exceeds the covered-line cap ({limits.MaxCoveredLines} lines)."));
                    }
                    covers[cover.Name] = [.. lines];
                }
            }

            tests[kvp.Name] = new BaselineTestEntry(file, covers);
        }
        return tests;
    }
}

/// <summary>
/// Freshness verdict for a baseline: fresh only when young enough AND (when
/// the caller knows the current commit) recorded at that commit.
/// </summary>
public sealed record BaselineFreshness(bool IsFresh, string Reason);

/// <summary>
/// Pure freshness policy. A baseline is FRESH when (a) it is not dated in the
/// future (5-minute clock-skew tolerance), (b) its age is within
/// <paramref name="maxAge"/>, and (c) when <paramref name="currentCommit"/>
/// is known and the baseline records a commit, the two match exactly
/// (<see cref="StringComparison.Ordinal"/>).
///
/// <para>The structural bound: the mandatory full-suite-on-main run regenerates
/// the baseline after every merge, so a fresh-by-age baseline is at most one
/// merge stale. The age backstop (default 7 days) covers a quiet main.</para>
/// </summary>
public static class TestSelectionBaselineFreshness
{
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public static BaselineFreshness Check(
        TestSelectionBaseline baseline,
        DateTimeOffset nowUtc,
        TimeSpan maxAge,
        string? currentCommit)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (maxAge <= TimeSpan.Zero)
            return new BaselineFreshness(false, "the configured maximum baseline age is not positive");

        if (baseline.ProducedAtUtc > nowUtc + FutureTolerance)
            return new BaselineFreshness(false, "baseline is dated in the future (clock skew or tampering)");

        var age = nowUtc - baseline.ProducedAtUtc;
        if (age > maxAge)
        {
            return new BaselineFreshness(false, string.Create(
                CultureInfo.InvariantCulture,
                $"baseline is {age.TotalDays:F1} days old (cap {maxAge.TotalDays:F1} days)"));
        }

        if (!string.IsNullOrWhiteSpace(currentCommit)
            && !string.IsNullOrWhiteSpace(baseline.Commit)
            && !string.Equals(baseline.Commit, currentCommit.Trim(), StringComparison.Ordinal))
        {
            return new BaselineFreshness(false, "baseline commit does not match the current commit");
        }

        return new BaselineFreshness(true, string.Create(
            CultureInfo.InvariantCulture,
            $"baseline at commit '{baseline.Commit}' is {age.TotalHours:F1}h old"));
    }
}

/// <summary>
/// Repository-relative path helpers shared by the selectors. Normalisation is
/// ordinal and allocation-free of culture: backslashes become forward slashes
/// (coverlet emits Windows separators on Windows-built baselines); surroundings
/// whitespace is trimmed.
/// </summary>
public static class TestSelectionPaths
{
    public static string Normalize(string path)
        => path.Replace('\\', '/').Trim();

    /// <summary>
    /// Fail-safe tripwire: does this changed path probably define tests whose
    /// names the baseline may not enumerate (new test methods in an edited test
    /// file)? True when a directory segment is exactly <c>test</c>/<c>tests</c>
    /// or the file name contains <c>test</c> (case-insensitive). Over-broad by
    /// design: a false positive only falls back to the full suite (more tests),
    /// never to fewer.
    /// </summary>
    public static bool IsProbableTestFile(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return false;
        var segments = normalizedPath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("test", StringComparison.OrdinalIgnoreCase)
                || segments[i].Equals("tests", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return segments[^1].Contains("test", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Hot-reloadable options for coverage-guided test selection, bound from the
/// <c>Audit:TestSelection:Coverage</c> configuration section via
/// <c>IOptionsMonitor</c>. Every operational value (baseline location, age and
/// size caps, global targets) is a knob, not a source literal.
/// </summary>
public sealed class CoverageTestSelectionOptions
{
    public const string SectionName = "Audit:TestSelection:Coverage";

    /// <summary>
    /// Sandbox-absolute path of the baseline JSON artifact. Default is the
    /// baseline-image bake location
    /// (<c>/opt/codeybox/test-selection/baseline.json</c>); a fetched artifact
    /// must be placed at this path at sandbox setup. Empty disables baseline
    /// loading (selectors fall back to the full suite).
    /// </summary>
    public string BaselineSandboxPath { get; set; } = "/opt/codeybox/test-selection/baseline.json";

    /// <summary>
    /// Age backstop for the baseline. The structural staleness bound is "at
    /// most one merge stale" (regenerated by the full-suite-on-main run);
    /// this caps a quiet main. Default 7 days.
    /// </summary>
    public TimeSpan MaxBaselineAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Max bytes read from the baseline artifact. Default 64 MiB.</summary>
    public long MaxBaselineBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Max test entries parsed from the baseline. Default 200,000.</summary>
    public int MaxBaselineTests { get; set; } = 200_000;

    /// <summary>Max total covered-line entries parsed. Default 10,000,000.</summary>
    public long MaxBaselineCoveredLines { get; set; } = 10_000_000;

    /// <summary>
    /// File NAMES (ordinals, case-sensitive) that are global build targets: a
    /// change to any file with one of these names falls back to the full suite.
    /// </summary>
    public IList<string> GlobalFileNames { get; set; } = new List<string>
    {
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "NuGet.Config",
        "nuget.config",
    };

    /// <summary>Exact repository-relative paths that are global targets.</summary>
    public IList<string> GlobalPaths { get; set; } = new List<string>
    {
        "CodeyBox.slnx",
    };

    /// <summary>
    /// Directory prefixes (each ending in <c>/</c>) whose every file is a
    /// global target — e.g. CI workflow edits can change what "the suite" means.
    /// </summary>
    public IList<string> GlobalDirectoryPrefixes { get; set; } = new List<string>
    {
        ".github/workflows/",
    };

    /// <summary>
    /// True when every cap is positive and every global entry is well-formed.
    /// Used behind the options validator so a bad config edit fails fast at
    /// load rather than silently disabling selection at audit time.
    /// </summary>
    public static bool IsValid(CoverageTestSelectionOptions? options)
    {
        if (options is null)
            return false;
        if (options.MaxBaselineAge <= TimeSpan.Zero)
            return false;
        if (options.MaxBaselineBytes <= 0 || options.MaxBaselineTests <= 0 || options.MaxBaselineCoveredLines <= 0)
            return false;
        if (options.GlobalFileNames.Any(string.IsNullOrWhiteSpace))
            return false;
        if (options.GlobalPaths.Any(string.IsNullOrWhiteSpace))
            return false;
        if (options.GlobalDirectoryPrefixes.Any(p => string.IsNullOrWhiteSpace(p) || !p.EndsWith('/')))
            return false;
        return true;
    }

    /// <summary>
    /// True when the changed path touches a global target: exact full-path
    /// match, exact file-name match, or directory-prefix match — all
    /// <see cref="StringComparison.Ordinal"/>. Compared by exact equality,
    /// never by substring.
    /// </summary>
    public bool IsGlobalTarget(string normalizedPath)
    {
        if (GlobalPaths.Any(p => string.Equals(p, normalizedPath, StringComparison.Ordinal)))
            return true;
        var fileName = normalizedPath.Split('/')[^1];
        if (GlobalFileNames.Any(n => string.Equals(n, fileName, StringComparison.Ordinal)))
            return true;
        return GlobalDirectoryPrefixes.Any(prefix =>
            normalizedPath.StartsWith(prefix, StringComparison.Ordinal));
    }
}
