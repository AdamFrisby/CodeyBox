using System.Globalization;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Core;

/// <summary>
/// One SHADOW-BEFORE-ENFORCE validation record: a new selector first ships
/// advisory — it computes the selection it WOULD run, the FULL suite still
/// runs, and this record captures whether any DESELECTED test failed this run.
/// Real skipping is gated on accumulated records showing zero unsafe skips.
/// This is the shared shadow-validation harness every selector (project-graph,
/// coverage, and later ones) reports through.
/// </summary>
public sealed record TestSelectionShadowRecord(
    string SelectorName,
    string Mode,
    string BaseRef,
    bool WasFullSuite,
    string SelectionDetail,
    IReadOnlyList<string> SelectedTests,
    int TotalSelected,
    IReadOnlyList<string> DeselectedTests,
    int TotalDeselected,
    IReadOnlyList<string> FailedTests,
    int TotalFailed,
    IReadOnlyList<string> UnsafeSkips,
    string Assessment,
    string WouldBeArgv)
{
    /// <summary>No test that would have been skipped failed this run.</summary>
    public const string AssessmentSafe = "safe-for-this-run";

    /// <summary>A test that would have been skipped FAILED this run — skipping would have been unsafe.</summary>
    public const string AssessmentUnsafe = "unsafe-skips-observed";

    /// <summary>The selector chose the full suite (or fell back to it): nothing would have been skipped.</summary>
    public const string AssessmentFullSuite = "full-suite";

    /// <summary>The verdict cannot be assessed (unknown test universe or unresolvable filters).</summary>
    public const string AssessmentUnverifiable = "unverifiable";
}

/// <summary>
/// Pure evaluator behind the shadow harness. Given the WOULD-BE selection
/// (resolved to test names), the known test universe, and the FULL run's
/// failed tests, computes the deselected set and the unsafe skips
/// (deselected ∩ failed). All lists are sorted deterministically and capped so
/// a pathological suite cannot blow up the record.
/// </summary>
public static class TestSelectionShadowEvaluator
{
    public const int DefaultListCap = 1024;

    public static TestSelectionShadowRecord Evaluate(
        string selectorName,
        string mode,
        string baseRef,
        bool wasFullSuite,
        IReadOnlySet<string> selectedTests,
        IReadOnlyList<string> universe,
        IReadOnlyList<string> failedTests,
        string wouldBeArgv,
        string selectionDetail,
        int listCap = DefaultListCap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectorName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);
        ArgumentNullException.ThrowIfNull(selectedTests);
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(failedTests);
        ArgumentNullException.ThrowIfNull(wouldBeArgv);
        ArgumentNullException.ThrowIfNull(selectionDetail);
        if (listCap <= 0)
            throw new ArgumentOutOfRangeException(nameof(listCap), "must be positive");

        var failed = failedTests
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (wasFullSuite)
        {
            return new TestSelectionShadowRecord(
                selectorName, mode, baseRef, true, selectionDetail,
                [], 0, [], 0,
                Take(failed, listCap), failed.Count, [],
                TestSelectionShadowRecord.AssessmentFullSuite, wouldBeArgv);
        }

        if (universe.Count == 0)
        {
            return new TestSelectionShadowRecord(
                selectorName, mode, baseRef, false, selectionDetail,
                Take(selectedTests.OrderBy(t => t, StringComparer.Ordinal).ToList(), listCap),
                selectedTests.Count, [], 0,
                Take(failed, listCap), failed.Count, [],
                TestSelectionShadowRecord.AssessmentUnverifiable, wouldBeArgv);
        }

        var selected = new HashSet<string>(selectedTests, StringComparer.Ordinal);
        var deselected = universe
            .Where(t => !selected.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        var failedSet = new HashSet<string>(failed, StringComparer.Ordinal);
        var unsafeSkips = deselected.Where(failedSet.Contains).ToList();

        return new TestSelectionShadowRecord(
            selectorName, mode, baseRef, false, selectionDetail,
            Take(selected.OrderBy(t => t, StringComparer.Ordinal).ToList(), listCap), selected.Count,
            Take(deselected, listCap), deselected.Count,
            Take(failed, listCap), failed.Count,
            Take(unsafeSkips, listCap),
            unsafeSkips.Count > 0
                ? TestSelectionShadowRecord.AssessmentUnsafe
                : TestSelectionShadowRecord.AssessmentSafe,
            wouldBeArgv);
    }

    /// <summary>
    /// Resolves a decision's filters to test names against the known universe.
    /// Bare names resolve by exact match; raw expressions (carrying
    /// <c>=</c>/<c>~</c> operators) cannot be resolved to a name set — when any
    /// are present the selection is unresolvable and the caller must record the
    /// run as unverifiable rather than risk a false "safe".
    /// </summary>
    public static bool TryResolveSelectedTests(
        TestSelectionDecision decision,
        IReadOnlyList<string> universe,
        out HashSet<string> selected)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(universe);
        selected = new HashSet<string>(StringComparer.Ordinal);
        if (decision.Selection.IsAll)
            return true;
        var universeSet = new HashSet<string>(universe, StringComparer.Ordinal);
        foreach (var filter in decision.Selection.Filters)
        {
            if (filter.Contains('=', StringComparison.Ordinal) || filter.Contains('~', StringComparison.Ordinal))
                return false;
            if (universeSet.Contains(filter))
                selected.Add(filter);
        }
        return true;
    }

    private static IReadOnlyList<string> Take(IReadOnlyList<string> items, int cap)
        => items.Count <= cap ? items : [.. items.Take(cap)];
}

/// <summary>
/// Sink for shadow records. Production emits structured logs; tests use the
/// in-memory sink and assert on the recorded verdicts.
/// </summary>
public interface ITestSelectionShadowSink
{
    void Emit(TestSelectionShadowRecord record);
}

/// <summary>
/// Production sink: one structured log line per shadow evaluation, carrying
/// the verdict, counts, and the (capped, newline-stripped) unsafe-skip names.
/// Test names are repository content (untrusted) — entries are stripped of
/// carriage returns and newlines before logging so a hostile test name cannot
/// forge log lines.
/// </summary>
public sealed class LoggerTestSelectionShadowSink(ILogger<LoggerTestSelectionShadowSink> logger) : ITestSelectionShadowSink
{
    private readonly ILogger<LoggerTestSelectionShadowSink> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public void Emit(TestSelectionShadowRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _logger.LogInformation(
            "test-selection shadow: selector {Selector} mode {Mode} assessment {Assessment} " +
            "selected {SelectedCount} deselected {DeselectedCount} failed {FailedCount} " +
            "unsafe {UnsafeSkips} detail {Detail}",
            Sanitize(record.SelectorName),
            Sanitize(record.Mode),
            Sanitize(record.Assessment),
            record.TotalSelected,
            record.TotalDeselected,
            record.TotalFailed,
            string.Join(",", record.UnsafeSkips.Select(Sanitize)),
            Sanitize(record.SelectionDetail));
    }

    private static string Sanitize(string value)
        => value.Replace('\r', '_').Replace('\n', '_');
}

/// <summary>Test sink: retains every emitted record for assertions.</summary>
public sealed class InMemoryTestSelectionShadowSink : ITestSelectionShadowSink
{
    private readonly List<TestSelectionShadowRecord> _records = new();

    public IReadOnlyList<TestSelectionShadowRecord> Records
    {
        get
        {
            lock (_records)
                return [.. _records];
        }
    }

    public void Emit(TestSelectionShadowRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_records)
            _records.Add(record);
    }
}

/// <summary>
/// Advisory/shadow configuration for the <c>csharp:test-pass</c> runner. All
/// members are injected (hot-reloadable accessors where operational); null
/// (the default) disables the shadow entirely — byte-identical legacy runs.
/// </summary>
public sealed record TestSelectionShadowConfig
{
    /// <summary>The configured selector (dispatches on the live mode).</summary>
    public required ITestSelector Selector { get; init; }

    /// <summary>Where shadow records go.</summary>
    public required ITestSelectionShadowSink Sink { get; init; }

    /// <summary>
    /// Live mode reader (backed by <c>IOptionsMonitor</c>). The shadow runs
    /// ONLY for <see cref="TestSelectionMode.CoverageShadow"/>; every other
    /// mode — including the <c>all</c> kill-switch — skips it.
    /// </summary>
    public required Func<TestSelectionMode> ModeAccessor { get; init; }

    /// <summary>Live coverage-selection options (caps, global targets, baseline path).</summary>
    public required Func<CoverageTestSelectionOptions> OptionsAccessor { get; init; }

    /// <summary>
    /// Which selector's name to stamp on shadow records. Defaults to the
    /// coverage selector's name.
    /// </summary>
    public string SelectorName { get; init; } = CoverageTestSelector.SelectorName;
}
