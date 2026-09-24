namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// A point-in-time sample of an Incus guest's cumulative CPU nanoseconds.
/// </summary>
public readonly record struct IncusGuestCpuSample(
    long CpuUsageNanoseconds,
    DateTimeOffset Timestamp = default);

/// <summary>
/// Evaluation result of guest CPU activity over an elapsed interval.
/// </summary>
public readonly record struct IncusCpuActivityEvaluation(
    bool IsActive,
    double CpuFraction,
    double CpuPercent)
{
    public static implicit operator bool(IncusCpuActivityEvaluation evaluation) => evaluation.IsActive;
}
