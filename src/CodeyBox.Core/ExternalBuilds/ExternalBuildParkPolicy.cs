namespace CodeyBox.Core.ExternalBuilds;

/// <summary>
/// One comparable duration sample. Incomplete runs (failed/cancelled/
/// truncated) are censored: recorded for observability but never treated as
/// successful short builds.
/// </summary>
public sealed record ExternalBuildDurationSample(
    ExternalBuildTargetKey Target,
    TimeSpan Duration,
    bool CompletedSuccessfully,
    DateTimeOffset CompletedAt,
    bool ColdCache);

/// <summary>Park decision with persisted reason/estimate/sample count.</summary>
public sealed record ExternalBuildParkDecision(
    bool ShouldPark,
    TimeSpan? PredictedDuration,
    int SampleCount,
    string Reason);

/// <summary>
/// Neutral park/resume predictor agreed with Adam: predict from the last N
/// comparable durations matched by provider/target/configuration/toolchain/
/// platform/cache class; park immediately only when the prediction is
/// STRICTLY greater than 10 minutes. N, estimator and minimum-sample policy
/// are configurable (see <see cref="ExternalBuildOptions"/>).
/// Old/stale history, outliers, failed/cancelled/truncated runs and
/// cold/warm differences are handled explicitly.
/// </summary>
public static class ExternalBuildParkPolicy
{
    /// <summary>Park threshold: strictly greater than 10 minutes parks.</summary>
    public static readonly TimeSpan ParkThreshold = TimeSpan.FromMinutes(10);

    /// <summary>Samples older than this are stale and ignored. Default 30 days.</summary>
    public static readonly TimeSpan MaxSampleAge = TimeSpan.FromDays(30);

    /// <summary>Samples at or above this duration are truncated-run outliers, never successful builds.</summary>
    public static readonly TimeSpan MaxSampleDuration = TimeSpan.FromHours(24);

    public static ExternalBuildParkDecision Decide(
        ExternalBuildTargetKey target,
        IReadOnlyList<ExternalBuildDurationSample> history,
        int sampleSize,
        int minSamples,
        ParkEstimator estimator,
        DateTimeOffset now,
        bool? coldCache = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(history);
        if (sampleSize <= 0) throw new ArgumentOutOfRangeException(nameof(sampleSize));
        if (minSamples <= 0) throw new ArgumentOutOfRangeException(nameof(minSamples));

        var comparable = history
            .Where(s => s.Target.Matches(target)
                && s.CompletedSuccessfully
                && s.CompletedAt <= now
                && now - s.CompletedAt <= MaxSampleAge
                && (!coldCache.HasValue || s.ColdCache == coldCache.Value)
                && s.Duration > TimeSpan.Zero
                && s.Duration < MaxSampleDuration)
            .OrderByDescending(s => s.CompletedAt)
            .Take(sampleSize)
            .Select(s => s.Duration)
            .ToList();

        if (comparable.Count < minSamples)
            return new ExternalBuildParkDecision(
                false, null, comparable.Count,
                $"insufficient history ({comparable.Count}/{minSamples}); stay active with elapsed-time fallback");

        var trimmed = TrimOutliers(comparable);
        var predicted = estimator == ParkEstimator.Mean
            ? TimeSpan.FromTicks((long)trimmed.Average(d => (double)d.Ticks))
            : Median(trimmed);
        var shouldPark = predicted > ParkThreshold;
        return new ExternalBuildParkDecision(
            shouldPark, predicted, comparable.Count,
            shouldPark
                ? $"predicted {predicted:mm\\:ss} strictly exceeds 10:00 from {comparable.Count} sample(s) ({estimator})"
                : $"predicted {predicted:mm\\:ss} does not exceed 10:00 from {comparable.Count} sample(s) ({estimator})");
    }

    /// <summary>
    /// Elapsed-time fallback with insufficient history: stay active initially,
    /// park only once waiting exceeds 10 minutes while still incomplete.
    /// </summary>
    public static bool ShouldParkOnElapsed(TimeSpan elapsed) => elapsed > ParkThreshold;

    private static List<TimeSpan> TrimOutliers(List<TimeSpan> samples)
    {
        if (samples.Count < 4) return samples;
        var ordered = samples.OrderBy(d => d).ToList();
        return ordered.Skip(1).Take(ordered.Count - 2).ToList();
    }

    private static TimeSpan Median(List<TimeSpan> samples)
    {
        var ordered = samples.OrderBy(d => d).ToList();
        var mid = ordered.Count / 2;
        return ordered.Count % 2 == 1
            ? ordered[mid]
            : TimeSpan.FromTicks((ordered[mid - 1].Ticks + ordered[mid].Ticks) / 2);
    }
}

/// <summary>In-memory comparable-duration history (production uses a durable store).</summary>
public sealed class InMemoryExternalBuildHistory
{
    /// <summary>
    /// Absolute safety bound so even a misconfigured caller cannot grow
    /// memory without limit. The coordinator prunes to the configured
    /// <c>MaxHistorySamples</c> (which never exceeds this) on every write.
    /// </summary>
    public const int AbsoluteMaxSamples = 1_000_000;

    private readonly object _gate = new();
    private readonly List<ExternalBuildDurationSample> _samples = [];

    public void Record(ExternalBuildDurationSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate)
        {
            _samples.Add(sample);
            if (_samples.Count > AbsoluteMaxSamples)
                _samples.RemoveRange(0, _samples.Count - AbsoluteMaxSamples);
        }
    }

    public IReadOnlyList<ExternalBuildDurationSample> Snapshot()
    {
        lock (_gate) return _samples.ToList();
    }

    /// <summary>
    /// Drops samples older than <paramref name="cutoff"/>, then trims
    /// oldest-first so at most <paramref name="maxRows"/> remain. Called by
    /// the coordinator after every record so the per-completion growth path
    /// stays bounded even when scheduled cleanup never runs. Idempotent.
    /// </summary>
    public int Prune(DateTimeOffset cutoff, int maxRows)
    {
        if (maxRows < 0) throw new ArgumentOutOfRangeException(nameof(maxRows));
        lock (_gate)
        {
            var removed = _samples.RemoveAll(s => s.CompletedAt < cutoff);
            var excess = _samples.Count - maxRows;
            if (excess > 0)
            {
                _samples.Sort(static (a, b) => a.CompletedAt.CompareTo(b.CompletedAt));
                _samples.RemoveRange(0, excess);
                removed += excess;
            }
            return removed;
        }
    }
}
