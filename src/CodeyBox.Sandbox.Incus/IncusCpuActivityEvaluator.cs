namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// Pure evaluation of whether an Incus guest consumed enough CPU over an
/// interval to count as watchdog progress.
/// </summary>
public static class IncusCpuActivityEvaluator
{
    /// <summary>
    /// Compares the cumulative guest-CPU counters of two consecutive samples.
    /// The guest counts as active only when the counter delta, expressed as a
    /// percentage of one CPU core over <paramref name="elapsed"/>, reaches
    /// <paramref name="thresholdPercent"/>. The first sample (no previous), a
    /// counter reset or regression (current below previous), negative counters,
    /// a non-positive elapsed interval, and a non-positive or non-finite
    /// threshold all evaluate to inactive.
    /// </summary>
    /// <param name="previousSample">The earlier sample, or null when this is
    /// the first observation (which always establishes a baseline and reports
    /// inactive).</param>
    /// <param name="currentSample">The latest sample.</param>
    /// <param name="elapsed">Wall time between the two samples.</param>
    /// <param name="thresholdPercent">Activity threshold as a percentage of one
    /// CPU core (5 = 5% of a core, i.e. 0.05 CPU fraction).</param>
    public static IncusCpuActivityEvaluation Evaluate(
        IncusGuestCpuSample? previousSample,
        IncusGuestCpuSample currentSample,
        TimeSpan elapsed,
        double thresholdPercent)
    {
        if (previousSample is not { } previous
            || elapsed <= TimeSpan.Zero
            || currentSample.CpuUsageNanoseconds < 0
            || previous.CpuUsageNanoseconds < 0
            || currentSample.CpuUsageNanoseconds < previous.CpuUsageNanoseconds
            || !double.IsFinite(thresholdPercent)
            || thresholdPercent <= 0)
        {
            return new IncusCpuActivityEvaluation(IsActive: false, CpuFraction: 0.0);
        }

        var deltaNs = currentSample.CpuUsageNanoseconds - previous.CpuUsageNanoseconds;
        var cpuFraction = deltaNs / (elapsed.TotalSeconds * 1_000_000_000.0);
        return new IncusCpuActivityEvaluation(cpuFraction * 100.0 >= thresholdPercent, cpuFraction);
    }

    /// <summary>
    /// Convenience wrapper over <see cref="Evaluate"/> reporting only the
    /// active/inactive decision.
    /// </summary>
    public static bool IsActive(
        IncusGuestCpuSample? previousSample,
        IncusGuestCpuSample currentSample,
        TimeSpan elapsed,
        double thresholdPercent) =>
        Evaluate(previousSample, currentSample, elapsed, thresholdPercent).IsActive;
}
