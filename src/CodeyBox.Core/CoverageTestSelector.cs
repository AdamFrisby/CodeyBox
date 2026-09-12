using System.Globalization;

namespace CodeyBox.Core;

/// <summary>
/// Outcome of coverage resolution: either the full suite (with the reason why
/// coverage narrowing was unsound) or the exact set of selected test names.
/// </summary>
public sealed record CoverageSelection(
    bool IsFullSuite,
    IReadOnlySet<string> Tests,
    string Reason);

/// <summary>
/// Pure core of coverage-guided selection. Given the changed lines, selects the
/// tests whose recorded per-test coverage intersects them, and ALWAYS also
/// selects: the tests defined in the changed files, the tests with NO coverage
/// record (new/uninstrumented), and the project-graph superset passed in.
/// Coverage may only refine WITHIN that superset — the result is always a
/// superset of it, never less.
///
/// <para>Fail-safe: a missing or stale baseline, an unknown changeset, a
/// global target, a whole-file change, a changed test file (which may define
/// unrecorded tests), or a changed file no coverage record references all
/// resolve to the full suite.</para>
/// </summary>
public static class CoverageSelectionCore
{
    public static CoverageSelection SelectTests(
        IReadOnlyList<TestSelectionChangedFile> changedFiles,
        TestSelectionBaseline? baseline,
        CoverageTestSelectionOptions options,
        DateTimeOffset nowUtc,
        ProjectGraphSelection superset,
        string? currentCommit = null)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(superset);

        if (superset.IsFullSuite)
            return Full($"project-graph superset is already the full suite ({superset.Reason})");
        if (baseline is null)
            return Full("no per-test coverage baseline is available");
        if (!CoverageTestSelectionOptions.IsValid(options))
            return Full("coverage selection options are invalid");
        if (changedFiles.Count == 0)
            return Full("the changeset could not be determined");

        var freshness = TestSelectionBaselineFreshness.Check(baseline, nowUtc, options.MaxBaselineAge, currentCommit);
        if (!freshness.IsFresh)
            return Full($"stale coverage baseline ({freshness.Reason})");

        var changedLines = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        foreach (var file in changedFiles)
        {
            var path = TestSelectionPaths.Normalize(file.Path);
            if (options.IsGlobalTarget(path))
                return Full($"change touches global target '{path}'");
            if (file.ChangedRanges.Count == 0)
                return Full($"whole-file change to '{path}' (no line granularity)");
            if (TestSelectionPaths.IsProbableTestFile(path))
                return Full($"changed test file '{path}' may define unrecorded tests");
            if (!IsReferencedByCoverage(path, baseline.Tests))
                return Full($"no coverage record references '{path}'");

            if (!changedLines.TryGetValue(path, out var lines))
                changedLines[path] = lines = new SortedSet<int>();
            foreach (var range in file.ChangedRanges)
            {
                if (range.LineCount == 0)
                    lines.Add(range.StartLine);
                else
                {
                    for (var line = range.StartLine; line < range.StartLine + range.LineCount; line++)
                        lines.Add(line);
                }
            }
        }

        var changedFilesSet = new HashSet<string>(changedLines.Keys, StringComparer.Ordinal);
        var selected = new SortedSet<string>(superset.Tests, StringComparer.Ordinal);
        var viaCoverage = 0;
        var viaDefiningFile = 0;
        var viaNoRecord = 0;

        foreach (var (name, entry) in baseline.Tests)
        {
            if (selected.Contains(name))
                continue;

            if (entry.Covers.Count == 0)
            {
                selected.Add(name);
                viaNoRecord++;
                continue;
            }

            var defining = TestSelectionPaths.Normalize(entry.DefiningFile);
            if (!string.IsNullOrWhiteSpace(defining) && changedFilesSet.Contains(defining))
            {
                selected.Add(name);
                viaDefiningFile++;
                continue;
            }

            if (IntersectsChanged(entry.Covers, changedLines))
            {
                selected.Add(name);
                viaCoverage++;
            }
        }

        return new CoverageSelection(false, selected, string.Create(
            CultureInfo.InvariantCulture,
            $"{selected.Count} test(s) ({viaCoverage} via coverage, " +
            $"{viaDefiningFile} defined in changed files, {viaNoRecord} without a coverage record)"));
    }

    private static CoverageSelection Full(string reason)
        => new(true, new HashSet<string>(StringComparer.Ordinal), reason);

    private static bool IsReferencedByCoverage(
        string path,
        IReadOnlyDictionary<string, BaselineTestEntry> tests)
    {
        foreach (var entry in tests.Values)
        {
            if (string.Equals(TestSelectionPaths.Normalize(entry.DefiningFile), path, StringComparison.Ordinal))
                return true;
            if (entry.Covers.ContainsKey(path))
                return true;
        }
        return false;
    }

    private static bool IntersectsChanged(
        IReadOnlyDictionary<string, IReadOnlyList<int>> covers,
        IReadOnlyDictionary<string, SortedSet<int>> changedLines)
    {
        foreach (var (file, lines) in covers)
        {
            if (!changedLines.TryGetValue(file, out var changed))
                continue;
            foreach (var line in lines)
            {
                if (changed.Contains(line))
                    return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Coverage-guided regression-test selector. Refines the project-graph
/// superset (<see cref="ProjectGraphTestSelector"/>) by coverage intersection
/// while preserving every test the superset picked: the emitted filters are
/// the superset's filters verbatim plus the coverage/must-include test names,
/// so the result is never less than the superset. Falls back to
/// <see cref="TestSelection.All"/> on any uncertainty — see
/// <see cref="CoverageSelectionCore"/>.
/// </summary>
public sealed class CoverageTestSelector : ITestSelector
{
    /// <summary>Selector name used in justifications and shadow records.</summary>
    public const string SelectorName = "coverage";

    private readonly ITestSelector _supersetSelector;
    private readonly Func<CoverageTestSelectionOptions> _optionsProvider;
    private readonly TimeProvider _clock;

    public CoverageTestSelector(
        ITestSelector supersetSelector,
        Func<CoverageTestSelectionOptions> optionsProvider,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(supersetSelector);
        ArgumentNullException.ThrowIfNull(optionsProvider);
        ArgumentNullException.ThrowIfNull(clock);
        _supersetSelector = supersetSelector;
        _optionsProvider = optionsProvider;
        _clock = clock;
    }

    public TestSelectionDecision Select(TestSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var supersetDecision = _supersetSelector.Select(request);
        if (supersetDecision.Selection.IsAll)
        {
            return new TestSelectionDecision(
                TestSelection.All,
                $"{SelectorName}: full suite (superset selector chose the full suite: {supersetDecision.Justification})");
        }

        var superset = ProjectGraphSelectorCore.SelectTests(request.ChangedFiles, request.Baseline);
        var resolved = CoverageSelectionCore.SelectTests(
            request.ChangedFiles,
            request.Baseline,
            _optionsProvider(),
            _clock.GetUtcNow(),
            superset);

        if (resolved.IsFullSuite)
        {
            return new TestSelectionDecision(
                TestSelection.All,
                $"{SelectorName}: full suite ({resolved.Reason})");
        }

        // Preserve the superset's filters verbatim (they may carry raw
        // expressions a bare-name set cannot express), then add the
        // coverage/must-include names. Union — never less than the superset.
        var filters = new SortedSet<string>(supersetDecision.Selection.Filters, StringComparer.Ordinal);
        foreach (var test in resolved.Tests)
            filters.Add(test);

        return new TestSelectionDecision(
            new TestSelection([.. filters]),
            $"{SelectorName}: {resolved.Reason}; superset: {supersetDecision.Justification}");
    }
}
