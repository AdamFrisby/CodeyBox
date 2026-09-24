namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// Pure evaluation functions determining if an Incus guest consumed sufficient
/// CPU over an interval to count as active progress.
/// </summary>
public static class IncusCpuActivityEvaluator
{
    public static bool IsActive(
        IncusGuestCpuSample? previousSample,
        IncusGuestCpuSample currentSample,
        TimeSpan elapsed,
        double thresholdPercent) =>
        Evaluate(previousSample, currentSample, elapsed, thresholdPercent).IsActive;

    public static bool IsActive(
        long? previousCpuNanoseconds,
        long currentCpuNanoseconds,
        TimeSpan elapsed,
        double thresholdPercent) =>
        Evaluate(
            previousCpuNanoseconds is { } prev ? new IncusGuestCpuSample(prev) : null,
            new IncusGuestCpuSample(currentCpuNanoseconds),
            elapsed,
            thresholdPercent).IsActive;

    public static bool IsActiveFraction(
        IncusGuestCpuSample? previousSample,
        IncusGuestCpuSample currentSample,
        TimeSpan elapsed,
        double thresholdFraction) =>
        EvaluateFraction(previousSample, currentSample, elapsed, thresholdFraction).IsActive;

    public static bool IsActiveFraction(
        long? previousCpuNanoseconds,
        long currentCpuNanoseconds,
        TimeSpan elapsed,
        double thresholdFraction) =>
        EvaluateFraction(
            previousCpuNanoseconds is { } prev ? new IncusGuestCpuSample(prev) : null,
            new IncusGuestCpuSample(currentCpuNanoseconds),
            elapsed,
            thresholdFraction).IsActive;

    public static IncusCpuActivityEvaluation Evaluate(
        IncusGuestCpuSample? previousSample,
        IncusGuestCpuSample currentSample,
        TimeSpan elapsed,
        double thresholdPercent)
    {
        if (previousSample is null)
            return new IncusCpuActivityEvaluation(IsActive: false, CpuFraction: 0.0, CpuPercent: 0.0);

        if (elapsed <= TimeSpan.Zero)
            return new IncusCpuActivityEvaluation(IsActive: false, CpuFraction: 0.0, CpuPercent: 0.0);

        if (currentSample.CpuUsageNanoseconds < previousSample.Value.CpuUsageNanoseconds)
            return new IncusCpuActivityEvaluation(IsActive: false, CpuFraction: 0.0, CpuPercent: 0.0);

        if (currentSample.CpuUsageNanoseconds < 0 || previousSample.Value.CpuUsageNanoseconds < 0)
            return new IncusCpuActivityEvaluation(IsActive: false, CpuFraction: 0.0, CpuPercent: 0.0);

        long deltaNs = currentSample.CpuUsageNanoseconds - previousSample.Value.CpuUsageNanoseconds;
        if (deltaNs == 0)
            return new IncusCpuActivityEvaluation(IsActive: false, CpuFraction: 0.0, CpuPercent: 0.0);

        if (!double.IsFinite(thresholdPercent) || thresholdPercent <= 0)
            return new IncusCpuActivityEvaluation(IsActive: false, CpuFraction: 0.0, CpuPercent: 0.0);

        double elapsedSeconds = elapsed.TotalSeconds;
        if (elapsedSeconds <= 0)
            return new IncusCpuActivityEvaluation(IsActive: false, CpuFraction: 0.0, CpuPercent: 0.0);

        double cpuFraction = (double)deltaNs / (elapsedSeconds * 1_000_000_000.0);
        double cpuPercent = cpuFraction * 100.0;

        bool isActive = cpuPercent >= thresholdPercent;
        return new IncusCpuActivityEvaluation(isActive, cpuFraction, cpuPercent);
    }

    public static IncusCpuActivityEvaluation EvaluateFraction(
        IncusGuestCpuSample? previousSample,
        IncusGuestCpuSample currentSample,
        TimeSpan elapsed,
        double thresholdFraction) =>
        Evaluate(previousSample, currentSample, elapsed, thresholdFraction * 100.0);
}
