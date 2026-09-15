using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// Shared builders. Every test passes an explicit <see cref="FleetSnapshot.Now"/> —
/// no test reads the clock, the network, or the filesystem.
/// </summary>
internal static class Fixtures
{
    public static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    public static AdminWorkItem Item(
        string id,
        string title = "Work",
        string state = "Queued",
        string agent = "Claude",
        IReadOnlyList<string>? dependsOn = null,
        bool dependsOnSatisfied = true,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null) => new()
        {
            Id = id,
            Title = title,
            State = state,
            Agent = agent,
            CreatedAt = createdAt ?? Now.AddHours(-1),
            UpdatedAt = updatedAt ?? Now.AddMinutes(-5),
            DependsOn = dependsOn ?? [],
            DependsOnSatisfied = dependsOnSatisfied,
        };

    public static FleetSnapshot Snapshot(
        IReadOnlyList<AdminWorkItem> items,
        IReadOnlyList<AdminAgentStatus>? agents = null,
        AdminWorkerCapacity? workers = null,
        VitalHistories? history = null) => new()
        {
            Now = Now,
            Items = items,
            Agents = agents ?? [],
            Workers = workers ?? new AdminWorkerCapacity
            {
                GlobalMaxConcurrent = 8,
                GlobalRunning = 0,
            },
            History = history ?? new VitalHistories(),
        };

    public static VitalHistories FlatHistory(
        double queue = 4, double inFlight = 3, double failure = 0,
        double throughput = 2, double parked = 0) => new()
        {
            QueueDepth = [queue, queue, queue, queue, queue],
            InFlight = [inFlight, inFlight, inFlight, inFlight, inFlight],
            FailureRate = [failure, failure, failure, failure, failure],
            ThroughputPerHour = [throughput, throughput, throughput, throughput, throughput],
            Parked = [parked, parked, parked, parked, parked],
        };
}
