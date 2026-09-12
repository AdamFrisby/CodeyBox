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
/// SHADOW-BEFORE-ENFORCE: the full suite always ran — <see cref="SelectedCount"/>
/// is the WOULD-BE subset, never the executed set. <see cref="EstimatedSavedFraction"/>
/// is a proportional estimate (deselected / total), not a measured duration:
/// the dashboard multiplies it by the run's <c>durationMs</c> for display.
/// </remarks>
public sealed record TestSelectionTelemetry
{
    /// <summary>Live selection mode name (e.g. "All", "CoverageShadow").</summary>
    public required string Mode { get; init; }

    /// <summary>
    /// Selector that produced the decision (e.g. "coverage"); "none" when the
    /// shadow was not active and the full suite ran by configuration.
    /// </summary>
    public required string Selector { get; init; }

    /// <summary>
    /// Selector layers consulted, innermost first (e.g. ["project-graph", "coverage"]
    /// for the coverage selector, which refines the project-graph superset).
    /// Empty when no selector ran.
    /// </summary>
    public required IReadOnlyList<string> Layers { get; init; }

    /// <summary>WOULD-BE selected test count (equals <see cref="TotalCount"/> for a full suite).</summary>
    public required int SelectedCount { get; init; }

    /// <summary>
    /// Known test-universe size (selected + deselected). Zero when the universe
    /// is unknown (no baseline) — the dashboard then shows "full suite" rather
    /// than "0/0".
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// Proportional estimate of the run time the WOULD-BE subset would have saved:
    /// deselected / total, in [0, 1]. Zero for full-suite and unknown-universe runs.
    /// </summary>
    public required double EstimatedSavedFraction { get; init; }

    /// <summary>
    /// Shadow assessment for this run: "safe-for-this-run" | "unsafe-skips-observed" |
    /// "full-suite" | "unverifiable" (same vocabulary as
    /// <see cref="TestSelectionShadowRecord"/>).
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
/// mode/decision/universe) stays in <c>DotnetTestAuditor</c>.
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
