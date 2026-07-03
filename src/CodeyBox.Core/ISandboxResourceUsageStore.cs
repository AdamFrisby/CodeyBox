namespace CodeyBox.Core;

/// <summary>
/// Durable store for per-VM resource usage captured at sandbox teardown.
/// </summary>
public interface ISandboxResourceUsageStore
{
    Task RecordAsync(SandboxResourceUsageRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<SandboxResourceUsageRecord>> ListRecentAsync(
        int limit,
        DateTimeOffset? sinceUtc = null,
        CancellationToken ct = default);
}

public sealed record SandboxResourceUsageRecord
{
    public required WorkItemId WorkItemId { get; init; }
    public required string Phase { get; init; }
    public required string VmName { get; init; }
    public double? DurationSeconds { get; init; }
    public double? AvgCpuPercent { get; init; }
    public double? PeakRamMb { get; init; }
    public double? NetRxMb { get; init; }
    public double? NetTxMb { get; init; }
    public string? BaselineRef { get; init; }
    public string? NetworkProfile { get; init; }
    public double? LoadAvg1 { get; init; }
    public double? LoadAvg5 { get; init; }
    public double? LoadAvg15 { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
}
