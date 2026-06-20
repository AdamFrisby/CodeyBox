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

    /// <summary>
    /// Live admission ceiling — equivalent to the current
    /// <c>CodeyBox:WorkerPool:MaxConcurrentWorkers</c> after any hot-reload.
    /// The <c>codeybox.workers.max</c> gauge reads this so dashboards
    /// reflect a resized pool without an orchestrator restart.
    /// </summary>
    int MaxConcurrent { get; }
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

    public int CurrentlyRunningTotal
    {
        get
        {
            // The OTel SDK retains observable instruments via a weak reference,
            // so a CodeyBoxObservableMetrics built against a Host that was later
            // torn down can outlive its DI container until GC reclaims it. When
            // another MeterListener triggers RecordObservableInstruments in that
            // window the delegate resolves against a disposed ServiceProvider —
            // surface that as zero rather than propagating an ObjectDisposedException
            // through every concurrent listener.
            try { return _resolve().CurrentlyRunningTotal; }
            catch (ObjectDisposedException) { return 0; }
        }
    }

    public int MaxConcurrent
    {
        get
        {
            try { return _resolve().MaxConcurrent; }
            catch (ObjectDisposedException) { return 0; }
        }
    }
}
