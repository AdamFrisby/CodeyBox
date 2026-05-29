namespace CodeyBox.Orchestrator;

/// <summary>
/// Exposes live worker-pool occupancy — the number of worker slots currently
/// held by an in-flight pipeline run, against
/// <see cref="OrchestratorOptions.MaxConcurrentWorkers"/>. Implemented by
/// <see cref="OrchestratorService"/> (which owns the semaphore-backed pool) and
/// consumed by the OpenTelemetry <c>codeybox.workers.in_use</c> gauge, so the
/// gauge measures pool-level occupancy rather than per-agent routing
/// reservations.
/// </summary>
public interface IWorkerPoolOccupancy
{
    /// <summary>
    /// Number of worker slots currently occupied by an in-flight pipeline run.
    /// </summary>
    int CurrentlyRunningTotal { get; }
}

/// <summary>
/// Resolves <see cref="IWorkerPoolOccupancy"/> through a lazy delegate so the
/// observable-metrics hosted service can read pool occupancy without forcing
/// <see cref="OrchestratorService"/> to be constructed at registration time.
/// The delegate is invoked on every read and should return the cached singleton.
/// </summary>
public sealed class DeferredWorkerPoolOccupancy : IWorkerPoolOccupancy
{
    private readonly Func<IWorkerPoolOccupancy> _resolve;

    public DeferredWorkerPoolOccupancy(Func<IWorkerPoolOccupancy> resolve) => _resolve = resolve;

    public int CurrentlyRunningTotal => _resolve().CurrentlyRunningTotal;
}
