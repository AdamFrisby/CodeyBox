namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// A point-in-time sample of an Incus guest's cumulative CPU consumption — the
/// monotonic <c>cpu.usage</c> nanoseconds counter reported by
/// <c>incus query /1.0/instances/&lt;name&gt;/state</c>.
/// </summary>
/// <param name="CpuUsageNanoseconds">Cumulative guest CPU time in nanoseconds
/// since the instance started; resets when the instance restarts.</param>
/// <param name="Timestamp">When the sample was read. The sampling interval is
/// derived from consecutive sample timestamps.</param>
public readonly record struct IncusGuestCpuSample(
    long CpuUsageNanoseconds,
    DateTimeOffset Timestamp = default);

/// <summary>
/// Evaluation result of guest CPU activity over an elapsed interval.
/// </summary>
/// <param name="IsActive">Whether the guest met the configured activity
/// threshold.</param>
/// <param name="CpuFraction">Measured guest CPU as a fraction of one core
/// averaged over the sample interval (1.0 = one fully busy core).</param>
public readonly record struct IncusCpuActivityEvaluation(
    bool IsActive,
    double CpuFraction);
