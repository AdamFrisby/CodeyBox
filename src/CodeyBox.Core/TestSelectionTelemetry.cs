using System.Globalization;

namespace CodeyBox.Core;

/// <summary>
/// Per-run test-selection telemetry for the <c>csharp:test-pass</c> audit.
/// Recorded on every run (shadow AND full-suite) and persisted on the
/// <see cref="AuditReport"/> so operators can see, per work item and in the
/// admin dashboard, what the selector WOULD have run, how much time that
/// would have saved, which selector layer produced it, and which fallbacks
/// fired.
/// </summary>
/// <remarks>
/// SHADOW-BEFORE-ENFORCE: for advisory (shadow) records the full suite always
/// ran — <see cref="SelectedCount"/> is the WOULD-BE subset, never the executed
/// set. For ENFORCING runs (assessment <c>enforced-subset</c>,
/// <see cref="TestSelectionTelemetryComputer.AssessmentEnforced"/>) the subset
/// actually executed: <see cref="SelectedCount"/> is the executed filter count
/// (clamped to the universe) and <see cref="EstimatedSavedFraction"/> estimates
/// the executed saving. <see cref="EstimatedSavedFraction"/> is always a
/// proportional estimate (deselected / total), not a measured duration: the
/// dashboard multiplies it by the run's <c>durationMs</c> for display.
/// </remarks>
public sealed record TestSelectionTelemetry
{
    /// <summary>
    /// Live selection mode name (e.g. "All", "CoverageShadow", "ProjectGraph").</summary>
    public required string Mode { get; init; }

    /// <summary>
    /// Selector that produced the decision (e.g. "coverage", "project-graph");
    /// "none" when no selector ran and the full suite ran by configuration.
    /// </summary>
    public required string Selector { get; init; }

    /// <summary>
    /// Selector layers consulted, innermost first (e.g. ["project-graph", "coverage"]
    /// for the coverage selector, which refines the project-graph superset).
    /// Empty when no selector ran.
    /// </summary>
    public required IReadOnlyList<string> Layers { get; init; }

    /// <summary>
    /// Selected test count: for advisory shadow records the WOULD-BE subset
    /// (equals <see cref="TotalCount"/> for a full suite); for enforcing runs
    /// the EXECUTED subset filter count.
    /// </summary>
    public required int SelectedCount { get; init; }

    /// <summary>
    /// Known test-universe size (selected + deselected). Zero when the universe
    /// is unknown (no baseline) — the dashboard then shows "full suite" rather
    /// than "0/0".
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// Proportional estimate of the run time the subset saved or would have saved:
    /// deselected / total, in [0, 1]. Zero for full-suite and unknown-universe runs.
    /// </summary>
    public required double EstimatedSavedFraction { get; init; }

    /// <summary>
    /// Assessment for this run: "safe-for-this-run" | "unsafe-skips-observed" |
    /// "full-suite" | "unverifiable" (same vocabulary as
    /// <see cref="TestSelectionShadowRecord"/>), plus "enforced-subset" for an
    /// enforcing run that executed a narrowed subset (safety unverifiable —
    /// the soundness gate ignores these runs).
    /// </summary>
    public required string Assessment { get; init; }

    /// <summary>
    /// Fallback reasons that fired for this run (e.g. "no baseline", "stale baseline").
    /// Empty when the selector narrowed without falling back.
    /// </summary>
    public required IReadOnlyList<string> Fallbacks { get; init; }

    /// <summary>Operator-facing selection detail (truncated at construction).</summary>
    public required string Detail { get; init; }
}

/// <summary>
/// Pure computer behind <see cref="TestSelectionTelemetry"/>. All methods are
/// total functions of their inputs; the only impure step (reading the run's
/// mode/decision/universe) stays in the test-runner auditor.
/// </summary>
public static class TestSelectionTelemetryComputer
{
    /// <summary>Max chars retained in <see cref="TestSelectionTelemetry.Detail"/>.</summary>
    public const int MaxDetailChars = 4000;

    /// <summary>Max fallback entries retained per run.</summary>
    public const int MaxFallbacks = 8;

    /// <summary>Max chars retained per fallback entry.</summary>
    public const int MaxFallbackChars = 1000;

    /// <summary>
    /// Selector layers consulted for a selector name. The coverage selector
    /// refines the project-graph superset, so it reports both layers;
    /// the project-graph selector reports one; anything else (including
    /// "none") reports just itself, or nothing for "none".
    /// </summary>
    public static IReadOnlyList<string> LayersForSelector(string selectorName)
    {
        if (string.IsNullOrWhiteSpace(selectorName)
            || string.Equals(selectorName, "none", StringComparison.OrdinalIgnoreCase))
            return [];
        if (string.Equals(selectorName, CoverageTestSelector.SelectorName, StringComparison.OrdinalIgnoreCase))
            return [ProjectGraphTestSelector.SelectorName, CoverageTestSelector.SelectorName];
        if (string.Equals(selectorName, ProjectGraphTestSelector.SelectorName, StringComparison.OrdinalIgnoreCase))
            return [ProjectGraphTestSelector.SelectorName];
        return [selectorName];
    }

    /// <summary>
    /// Builds telemetry from a shadow record plus the known universe size.
    /// For full-suite records the selected count equals the universe size
    /// (everything ran); for an unknown universe (size 0) both counts are 0.
    /// </summary>
    public static TestSelectionTelemetry FromShadowRecord(
        TestSelectionShadowRecord record,
        int universeCount)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (universeCount < 0)
            throw new ArgumentOutOfRangeException(nameof(universeCount), "must be non-negative");

        var total = universeCount;
        var selected = record.WasFullSuite ? total : record.TotalSelected;
        if (total == 0)
            selected = 0;

        var fraction = total > 0 && !record.WasFullSuite
            ? Clamp01((double)(total - selected) / total)
            : 0.0;

        var fallbacks = FallbacksFromRecord(record);

        return new TestSelectionTelemetry
        {
            Mode = record.Mode,
            Selector = record.SelectorName,
            Layers = LayersForSelector(record.SelectorName),
            SelectedCount = selected,
            TotalCount = total,
            EstimatedSavedFraction = fraction,
            Assessment = record.Assessment,
            Fallbacks = fallbacks,
            Detail = Truncate(record.SelectionDetail, MaxDetailChars),
        };
    }

    /// <summary>
    /// Builds full-suite telemetry for runs where the shadow did not evaluate
    /// (mode <c>all</c> kill-switch or shadow unconfigured). The universe is
    /// unknown here by construction, so counts are 0/0 and nothing was saved.
    /// </summary>
    public static TestSelectionTelemetry FullSuiteWithoutShadow(string mode)
    {
        var modeName = string.IsNullOrWhiteSpace(mode) ? TestSelectionMode.All.ToString() : mode;
        return new TestSelectionTelemetry
        {
            Mode = modeName,
            Selector = "none",
            Layers = [],
            SelectedCount = 0,
            TotalCount = 0,
            EstimatedSavedFraction = 0.0,
            Assessment = TestSelectionShadowRecord.AssessmentFullSuite,
            Fallbacks =
            [
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"test-selection shadow not active (mode '{modeName}') — full suite ran"),
            ],
            Detail = string.Create(
                CultureInfo.InvariantCulture,
                $"test-selection mode '{modeName}': running the entire suite"),
        };
    }

    /// <summary>
    /// Builds telemetry for an ENFORCING run: the narrowed subset actually
    /// executed (or the full suite when the selector fell back). A narrowed
    /// enforcing run reports <see cref="AssessmentEnforced"/> — the deselected
    /// tests were not executed, so no safe/unsafe verdict can be claimed and
    /// the soundness gate (which only counts safe/unsafe) ignores these runs.
    /// Full-suite fallbacks report <c>full-suite</c> with the fallback reason.
    /// An optional <paramref name="fallbacks"/> list records intermediate
    /// ladder rungs that fired on the way to a narrowed run (e.g. the coverage
    /// rung falling back to the executed project-graph superset); entries are
    /// truncated and capped like any fallback.
    /// </summary>
    public static TestSelectionTelemetry FromEnforcedSelection(
        string mode,
        string selectorName,
        TestSelectionDecision decision,
        int universeCount,
        string detail,
        IReadOnlyList<string>? fallbacks = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(detail);
        if (universeCount < 0)
            throw new ArgumentOutOfRangeException(nameof(universeCount), "must be non-negative");
        var modeName = string.IsNullOrWhiteSpace(mode) ? TestSelectionMode.ProjectGraph.ToString() : mode;
        var selector = string.IsNullOrWhiteSpace(selectorName) ? ProjectGraphTestSelector.SelectorName : selectorName;
        if (decision.Selection.IsAll || universeCount == 0)
        {
            return new TestSelectionTelemetry
            {
                Mode = modeName,
                Selector = selector,
                Layers = LayersForSelector(selector),
                SelectedCount = universeCount,
                TotalCount = universeCount,
                EstimatedSavedFraction = 0.0,
                Assessment = TestSelectionShadowRecord.AssessmentFullSuite,
                Fallbacks = [Truncate(detail, MaxFallbackChars)],
                Detail = Truncate(detail, MaxDetailChars),
            };
        }

        var selected = decision.Selection.Filters.Count;
        if (universeCount > 0)
            selected = Math.Min(selected, universeCount);
        var fraction = universeCount > 0 ? Clamp01((double)(universeCount - selected) / universeCount) : 0.0;
        return new TestSelectionTelemetry
        {
            Mode = modeName,
            Selector = selector,
            Layers = LayersForSelector(selector),
            SelectedCount = selected,
            TotalCount = universeCount,
            EstimatedSavedFraction = fraction,
            Assessment = AssessmentEnforced,
            Fallbacks = fallbacks is null
                ? []
                : [.. fallbacks
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Select(f => Truncate(f, MaxFallbackChars))
                    .Take(MaxFallbacks)],
            Detail = Truncate(detail, MaxDetailChars),
        };
    }

    /// <summary>
    /// Assessment for an enforcing run that executed a narrowed subset: the
    /// deselected tests were skipped, so safety cannot be assessed from this
    /// run. The soundness gate treats it like <c>unverifiable</c> (ignored).
    /// </summary>
    public const string AssessmentEnforced = "enforced-subset";

    private static IReadOnlyList<string> FallbacksFromRecord(TestSelectionShadowRecord record)
    {
        if (record.WasFullSuite
            || string.Equals(record.Assessment, TestSelectionShadowRecord.AssessmentFullSuite, StringComparison.Ordinal)
            || string.Equals(record.Assessment, TestSelectionShadowRecord.AssessmentUnverifiable, StringComparison.Ordinal))
        {
            return [Truncate(record.SelectionDetail, MaxFallbackChars)];
        }
        return [];
    }

    private static double Clamp01(double value)
        => value < 0 ? 0 : value > 1 ? 1 : value;

    private static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars)
            return value;
        return value[..maxChars] + "…";
    }
}
