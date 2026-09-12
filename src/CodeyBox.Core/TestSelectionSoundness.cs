namespace CodeyBox.Core;

/// <summary>
/// Hot-reloadable options for the test-selection soundness report, bound from
/// the <c>Audit:TestSelection:Soundness</c> configuration section via
/// <c>IOptionsMonitor</c>. Every operational value is a knob, not a source
/// literal: the calibration window (how many recent <c>csharp:test-pass</c>
/// runs the report evaluates) and the hard API limit bound.
/// </summary>
public sealed class TestSelectionSoundnessOptions
{
    public const string SectionName = "Audit:TestSelection:Soundness";

    /// <summary>
    /// How many recent test-selection runs form the calibration window the
    /// enforce-readiness gate evaluates. Default 100.
    /// </summary>
    public int CalibrationWindowSize { get; set; } = 100;

    /// <summary>
    /// Hard upper bound clamped onto the API/CLI <c>limit</c> parameter so one
    /// report cannot buffer an unbounded slice of audit history. Default 1000.
    /// </summary>
    public int MaxLimit { get; set; } = 1000;

    public static bool IsValid(TestSelectionSoundnessOptions? options)
    {
        if (options is null)
            return false;
        if (options.CalibrationWindowSize <= 0)
            return false;
        if (options.MaxLimit <= 0)
            return false;
        return true;
    }
}

/// <summary>
/// One evaluated run feeding the soundness report: the persisted per-run
/// telemetry plus the run's measured wall-clock duration. Both come from a
/// real <see cref="AuditReport"/> row (telemetry block + <c>durationMs</c>) —
/// never from mocks or projections.
/// </summary>
public sealed record TestSelectionSoundnessSample
{
    public required string Selector { get; init; }
    public required string Assessment { get; init; }
    public required int SelectedCount { get; init; }
    public required int TotalCount { get; init; }
    public required double EstimatedSavedFraction { get; init; }
    public required long DurationMs { get; init; }

    public static TestSelectionSoundnessSample FromReport(AuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var telemetry = report.TestSelection
            ?? throw new ArgumentException("Report has no test-selection telemetry.", nameof(report));
        return new TestSelectionSoundnessSample
        {
            Selector = telemetry.Selector,
            Assessment = telemetry.Assessment,
            SelectedCount = telemetry.SelectedCount,
            TotalCount = telemetry.TotalCount,
            EstimatedSavedFraction = telemetry.EstimatedSavedFraction,
            DurationMs = report.DurationMs,
        };
    }
}

/// <summary>
/// Per-selector slice of the soundness report.
/// </summary>
public sealed record TestSelectionSoundnessSelectorSlice
{
    public required string Selector { get; init; }
    public required int Runs { get; init; }
    public required int UnsafeSkipCount { get; init; }
    public required int SafeCount { get; init; }
    public required long TestsSaved { get; init; }
    public required long EstimatedSavedMs { get; init; }
}

/// <summary>
/// The explicit enforce-readiness gate. Machine-checkable: consumers branch on
/// <see cref="ReadyForEnforcement"/> rather than reinterpreting prose. The
/// threshold is fixed at zero unsafe skips — any recorded
/// <c>unsafe-skips-observed</c> assessment in the window blocks enforcement.
/// </summary>
public sealed record TestSelectionSoundnessGate
{
    /// <summary>Hard safety threshold: the maximum unsafe skips the gate tolerates. Always zero.</summary>
    public required int MaxAllowedUnsafeSkips { get; init; }

    /// <summary>Calibration window the gate was evaluated over.</summary>
    public required int CalibrationWindowSize { get; init; }

    /// <summary>
    /// Assessable runs in the window (verdicts <c>safe-for-this-run</c> or
    /// <c>unsafe-skips-observed</c>). Full-suite and unverifiable runs carry no
    /// safety evidence and do not count toward calibration.
    /// </summary>
    public required int AssessableCount { get; init; }

    public required bool ReadyForEnforcement { get; init; }

    public required string Reason { get; init; }
}

/// <summary>
/// Soundness report over the last N <c>csharp:test-pass</c> runs: (1) the
/// unsafe-skip count — times the selector would have deselected a test that
/// actually FAILED — which must be ~0 for selection to be safe, and (2) the
/// wall-clock/test-count the WOULD-BE subset would have saved. Selector-
/// agnostic: an optional exact-match selector filter narrows the window, and
/// the per-selector breakdown always covers every selector present.
/// </summary>
public sealed record TestSelectionSoundnessReport
{
    public required int WindowSize { get; init; }
    public required int EvaluatedCount { get; init; }
    public required string? SelectorFilter { get; init; }
    public required int UnsafeSkipCount { get; init; }
    public required int SafeCount { get; init; }
    public required int FullSuiteCount { get; init; }
    public required int UnverifiableCount { get; init; }
    public required long TotalTestsSaved { get; init; }
    public required long EstimatedSavedMs { get; init; }
    public required IReadOnlyList<TestSelectionSoundnessSelectorSlice> BySelector { get; init; }
    public required TestSelectionSoundnessGate Gate { get; init; }
}

/// <summary>
/// Pure computer behind the soundness report. Total function of its inputs:
/// the samples (newest first, already bounded by the caller), the requested
/// window size, the optional selector filter, and the calibration window the
/// gate enforces. The caller enforces all bounds BEFORE buffering; this type
/// never touches I/O.
/// </summary>
public static class TestSelectionSoundnessComputer
{
    public const int MaxAllowedUnsafeSkips = 0;

    public static TestSelectionSoundnessReport Build(
        IReadOnlyList<TestSelectionSoundnessSample> samples,
        int windowSize,
        string? selectorFilter,
        int calibrationWindowSize)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (windowSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowSize), "must be positive");
        if (calibrationWindowSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(calibrationWindowSize), "must be positive");

        var normalizedFilter = string.IsNullOrWhiteSpace(selectorFilter) ? null : selectorFilter.Trim();
        var window = samples.Take(windowSize).ToList();
        if (normalizedFilter is not null)
            window = window
                .Where(s => string.Equals(s.Selector, normalizedFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var unsafeCount = 0;
        var safeCount = 0;
        var fullSuiteCount = 0;
        var unverifiableCount = 0;
        long testsSaved = 0;
        long savedMs = 0;
        var bySelector = new Dictionary<string, MutableSlice>(StringComparer.OrdinalIgnoreCase);

        foreach (var sample in window)
        {
            if (!bySelector.TryGetValue(sample.Selector, out var slice))
                bySelector[sample.Selector] = slice = new MutableSlice(sample.Selector);

            slice.Runs++;

            if (string.Equals(sample.Assessment, TestSelectionShadowRecord.AssessmentUnsafe, StringComparison.Ordinal))
            {
                unsafeCount++;
                slice.Unsafe++;
            }
            else if (string.Equals(sample.Assessment, TestSelectionShadowRecord.AssessmentSafe, StringComparison.Ordinal))
            {
                safeCount++;
                slice.Safe++;
            }
            else if (string.Equals(sample.Assessment, TestSelectionShadowRecord.AssessmentFullSuite, StringComparison.Ordinal))
            {
                fullSuiteCount++;
                continue;
            }
            else
            {
                unverifiableCount++;
                continue;
            }

            var deselected = Math.Max(0, sample.TotalCount - sample.SelectedCount);
            testsSaved += deselected;
            slice.TestsSaved += deselected;
            var estimatedMs = (long)(sample.DurationMs * Clamp01(sample.EstimatedSavedFraction));
            savedMs += estimatedMs;
            slice.SavedMs += estimatedMs;
        }

        var assessable = safeCount + unsafeCount;
        var ready = unsafeCount == MaxAllowedUnsafeSkips && assessable >= calibrationWindowSize;
        var reason = unsafeCount > 0
            ? $"{unsafeCount} unsafe skip(s) observed in the window — selection must stay shadow-only"
            : assessable < calibrationWindowSize
                ? $"calibration incomplete: {assessable}/{calibrationWindowSize} assessable runs — selection must stay shadow-only"
                : $"zero unsafe skips across {assessable} assessable runs — calibration window satisfied";

        return new TestSelectionSoundnessReport
        {
            WindowSize = windowSize,
            EvaluatedCount = window.Count,
            SelectorFilter = normalizedFilter,
            UnsafeSkipCount = unsafeCount,
            SafeCount = safeCount,
            FullSuiteCount = fullSuiteCount,
            UnverifiableCount = unverifiableCount,
            TotalTestsSaved = testsSaved,
            EstimatedSavedMs = savedMs,
            BySelector = bySelector.Values
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new TestSelectionSoundnessSelectorSlice
                {
                    Selector = s.Name,
                    Runs = s.Runs,
                    UnsafeSkipCount = s.Unsafe,
                    SafeCount = s.Safe,
                    TestsSaved = s.TestsSaved,
                    EstimatedSavedMs = s.SavedMs,
                })
                .ToList(),
            Gate = new TestSelectionSoundnessGate
            {
                MaxAllowedUnsafeSkips = MaxAllowedUnsafeSkips,
                CalibrationWindowSize = calibrationWindowSize,
                AssessableCount = assessable,
                ReadyForEnforcement = ready,
                Reason = reason,
            },
        };
    }

    private static double Clamp01(double value)
        => value < 0 ? 0 : value > 1 ? 1 : value;

    private sealed class MutableSlice(string name)
    {
        public string Name { get; } = name;
        public int Runs { get; set; }
        public int Unsafe { get; set; }
        public int Safe { get; set; }
        public long TestsSaved { get; set; }
        public long SavedMs { get; set; }
    }
}
