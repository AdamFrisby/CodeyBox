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
/// tests whose recorded per-test coverage intersects them, NESTED INSIDE the
/// project-graph superset passed in: every candidate (coverage hit, test
/// defined in a changed file, test with NO coverage record) is kept only when
/// it is already a member of that superset. Coverage can only shrink the
/// superset, never grow beyond it — a poisoned or stale coverage map cannot
/// widen the executed set past the project-graph bound (defense in depth).
///
/// <para>Fail-safe: a missing or stale baseline, an unknown changeset, a
/// global target, a whole-file change, a changed test file (which may define
/// unrecorded tests), a changed file no coverage record references, a change
/// no recorded coverage intersects (no signal — the project-graph rung owns
/// it), or zero coverage hits all resolve to the full suite at THIS layer.
/// The <see cref="CoverageTestSelector"/> then descends the fallback ladder
/// (coverage → project-graph → all) rather than running the full suite
/// directly when the superset still narrows.</para>
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
        var selected = new SortedSet<string>(StringComparer.Ordinal);
        var viaCoverage = 0;
        var viaDefiningFile = 0;
        var viaNoRecord = 0;

        foreach (var (name, entry) in baseline.Tests)
        {
            // Nested-inside-superset: a test outside the project-graph bound is
            // never added, however its coverage reads. The superset's soundness
            // is the floor; the shadow/soundness gate validates the narrowing.
            if (!superset.Tests.Contains(name))
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

        if (viaCoverage == 0)
        {
            return Full(
                "no recorded per-test coverage intersects the changed lines " +
                $"({selected.Count} must-include test(s) inside the superset carry no signal)");
        }

        var deselected = Math.Max(0, superset.Tests.Count - selected.Count);
        return new CoverageSelection(false, selected, string.Create(
            CultureInfo.InvariantCulture,
            $"{selected.Count} test(s) ({viaCoverage} via coverage, " +
            $"{viaDefiningFile} defined in changed files, {viaNoRecord} without a coverage record; " +
            $"{deselected} project-graph test(s) not covering the change deselected)"));
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
/// Coverage-guided regression-test selector. Narrows the project-graph
/// superset (<see cref="ProjectGraphTestSelector"/>) by coverage intersection:
/// the emitted filters are always a subset of that superset — coverage can
/// only shrink it, never grow beyond it. Implements the fallback ladder
/// coverage → project-graph → all: when the coverage rung cannot narrow
/// (missing/stale data, global target, no intersecting coverage, selector
/// error) but the superset still narrows, the superset decision is returned
/// verbatim; only when both rungs fail does the selector fall back to
/// <see cref="TestSelection.All"/>.
/// </summary>
public sealed class CoverageTestSelector : ITestSelector
{
    /// <summary>Selector name used in justifications and shadow records.</summary>
    public const string SelectorName = "coverage";

    /// <summary>
    /// Marker stamped into a project-graph-rung justification: the coverage
    /// rung fell back and the superset decision is executed verbatim. The
    /// enforcing auditor reads this (exact ordinal match) to attribute the
    /// fallback in per-run telemetry.
    /// </summary>
    public const string ProjectGraphRungMarker = "project-graph rung:";

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

        TestSelectionDecision supersetDecision;
        try
        {
            supersetDecision = _supersetSelector.Select(request);
        }
        catch (Exception ex)
        {
            // Fail-safe: a broken superset selector bottoms the ladder at the
            // full run — there is no narrower rung to trust.
            return new TestSelectionDecision(
                TestSelection.All,
                $"{SelectorName}: full suite (superset selector error ({ex.GetType().Name}))");
        }
        if (supersetDecision.Selection.IsAll)
        {
            return new TestSelectionDecision(
                TestSelection.All,
                $"{SelectorName}: full suite (superset selector chose the full suite: {supersetDecision.Justification})");
        }

        CoverageTestSelectionOptions options;
        try
        {
            options = _optionsProvider();
        }
        catch (Exception)
        {
            // The coverage rung cannot read its knobs, but the superset
            // decision above already narrowed without them — descend to it.
            return ProjectGraphRung(supersetDecision, "selection options unavailable");
        }

        // A superset carrying raw filter expressions (operators the bare-name
        // set cannot express) cannot be provably nested inside — execute it
        // verbatim rather than risk a false "shrunk" claim.
        if (HasRawExpressions(supersetDecision.Selection))
            return ProjectGraphRung(supersetDecision, "superset carries raw filter expressions");

        var utcNow = _clock.GetUtcNow();
        var superset = ProjectGraphSelectorCore.SelectTests(
            request.ChangedFiles,
            request.Baseline,
            options,
            utcNow,
            request.CurrentCommit);
        var resolved = CoverageSelectionCore.SelectTests(
            request.ChangedFiles,
            request.Baseline,
            options,
            utcNow,
            superset,
            request.CurrentCommit);

        if (!resolved.IsFullSuite)
        {
            foreach (var test in resolved.Tests)
            {
                if (!supersetDecision.Selection.Filters.Contains(test))
                {
                    // Structural defense-in-depth tripwire: the core promises a
                    // subset of the recomputed superset; the executed superset
                    // decision must agree. Any drift falls down the ladder.
                    return ProjectGraphRung(supersetDecision, "coverage result escapes the superset");
                }
            }

            return new TestSelectionDecision(
                new TestSelection([.. resolved.Tests]),
                $"{SelectorName}: {resolved.Reason}; superset: {supersetDecision.Justification}");
        }

        if (superset.IsFullSuite)
        {
            return new TestSelectionDecision(
                TestSelection.All,
                $"{SelectorName}: full suite ({resolved.Reason}); superset: {superset.Reason}");
        }

        return ProjectGraphRung(supersetDecision, resolved.Reason);
    }

    private static TestSelectionDecision ProjectGraphRung(
        TestSelectionDecision supersetDecision, string coverageReason)
        => new(
            supersetDecision.Selection,
            $"{SelectorName}: full suite ({coverageReason}); {ProjectGraphRungMarker} {supersetDecision.Justification}");

    private static bool HasRawExpressions(TestSelection selection)
        => selection.Filters.Any(f =>
            f.Contains('=', StringComparison.Ordinal) || f.Contains('~', StringComparison.Ordinal));
}
