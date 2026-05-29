using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Registers the OpenTelemetry observable gauges that have to poll live process
/// or store state: work items by state, worker-pool occupancy, active sandboxes,
/// and per-agent subscription quota headroom.
///
/// <para>Registered only when OTel is enabled, so the disabled path keeps its
/// zero-overhead guarantee. The store-backed gauge reads from a snapshot that a
/// background loop refreshes on a fixed cadence, so the metrics-collection thread
/// never blocks on SQLite. The in-memory gauges (workers, sandboxes, quota) read
/// directly in the callback because those reads are cheap and lock-free.</para>
/// </summary>
public sealed class CodeyBoxObservableMetrics : IHostedService, IDisposable
{
    private readonly IWorkItemStore _store;
    private readonly ISandboxProvider _sandboxes;
    private readonly IAgentRunningCounters? _running;
    private readonly AgentClassRouter? _router;
    private readonly int _maxWorkers;
    private readonly ILogger<CodeyBoxObservableMetrics> _log;
    private readonly TimeSpan _refreshInterval;

    // The SDK keeps only a weak reference to an observable instrument, so the
    // gauges are held in fields for the lifetime of this singleton.
    private readonly ObservableGauge<long> _workItemsActive;
    private readonly ObservableGauge<long> _workersInUse;
    private readonly ObservableGauge<long> _workersMax;
    private readonly ObservableGauge<long> _sandboxActive;
    private readonly ObservableGauge<double>? _quotaAvailable;

    // Refreshed off-thread; read by the work-item gauge callback.
    private volatile IReadOnlyList<Measurement<long>> _workItemStateCounts = [];
    private Timer? _refreshTimer;

    public CodeyBoxObservableMetrics(
        IWorkItemStore store,
        ISandboxProvider sandboxes,
        OrchestratorOptions orchestratorOptions,
        ILogger<CodeyBoxObservableMetrics> log,
        IAgentRunningCounters? running = null,
        AgentClassRouter? router = null,
        TimeSpan? refreshInterval = null)
    {
        _store = store;
        _sandboxes = sandboxes;
        _running = running;
        _router = router;
        _maxWorkers = orchestratorOptions.MaxConcurrentWorkers;
        _log = log;
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(15);

        _workItemsActive = CodeyBoxMeters.CreatePipelineObservableGauge<long>(
            "codeybox.work_item.active",
            () => _workItemStateCounts,
            unit: "{work_item}",
            description: "Work items currently persisted in each state.");

        _workersInUse = CodeyBoxMeters.CreatePipelineObservableGauge<long>(
            "codeybox.workers.in_use",
            () => [new Measurement<long>(CurrentWorkersInUse())],
            unit: "{worker}",
            description: "Worker slots currently occupied by an in-flight pipeline run.");

        _workersMax = CodeyBoxMeters.CreatePipelineObservableGauge<long>(
            "codeybox.workers.max",
            () => [new Measurement<long>(_maxWorkers)],
            unit: "{worker}",
            description: "Configured MaxConcurrentWorkers ceiling for the worker pool.");

        _sandboxActive = CodeyBoxMeters.CreateSandboxObservableGauge<long>(
            "codeybox.sandbox.active",
            ObserveActiveSandboxes,
            unit: "{sandbox}",
            description: "Sandboxes/VMs the current process is actively tracking.");

        if (_router is not null)
        {
            _quotaAvailable = CodeyBoxMeters.CreatePipelineObservableGauge<double>(
                "codeybox.agent.quota.available_pct",
                ObserveQuotaAvailability,
                unit: "%",
                description: "Most-recent subscription quota headroom observed per agent/model (-1 = unknown).");
        }
    }

    private long CurrentWorkersInUse()
    {
        if (_running is null) return 0;
        long total = 0;
        foreach (var n in _running.Snapshot().Values) total += n;
        return total;
    }

    private IEnumerable<Measurement<long>> ObserveActiveSandboxes()
    {
        // Only providers with a persistent VM lifecycle (multipass) report a
        // meaningful count; process/bubblewrap sandboxes are ephemeral and the
        // capability is absent, so the gauge reports 0 for them.
        var count = _sandboxes is ISuspendingSandboxProvider suspending
            ? suspending.SnapshotSuspendableActive().Count
            : 0;
        return [new Measurement<long>(count, new KeyValuePair<string, object?>("provider", _sandboxes.Name))];
    }

    private IEnumerable<Measurement<double>> ObserveQuotaAvailability()
    {
        if (_router is null) yield break;
        foreach (var (agent, model, pct) in _router.SnapshotQuotaAvailability())
        {
            yield return new Measurement<double>(
                pct,
                new KeyValuePair<string, object?>("agent.kind", agent.Value),
                new KeyValuePair<string, object?>("model", model ?? "(default)"));
        }
    }

    private async Task RefreshWorkItemStateCountsAsync()
    {
        try
        {
            var rows = await _store.GetFleetStateCountsAsync(CancellationToken.None);
            var byState = new Dictionary<WorkItemState, long>();
            foreach (var (_, state, count, _) in rows)
            {
                var ws = (WorkItemState)state;
                byState[ws] = byState.TryGetValue(ws, out var existing) ? existing + count : count;
            }

            var measurements = new List<Measurement<long>>(byState.Count);
            foreach (var (state, count) in byState)
            {
                measurements.Add(new Measurement<long>(
                    count, new KeyValuePair<string, object?>("state", state.ToString())));
            }

            _workItemStateCounts = measurements;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Observable metrics: failed to refresh work-item state counts");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshWorkItemStateCountsAsync();
        _refreshTimer = new Timer(
            static state => _ = ((CodeyBoxObservableMetrics)state!).RefreshWorkItemStateCountsAsync(),
            this, _refreshInterval, _refreshInterval);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _refreshTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // ObservableGauge instances are owned by the Meter, not separately
        // disposable; only the refresh timer needs teardown here. The gauge
        // fields are retained for the process lifetime to keep the SDK's weak
        // reference alive (touched here so the references are observably used).
        GC.KeepAlive(_workItemsActive);
        GC.KeepAlive(_workersInUse);
        GC.KeepAlive(_workersMax);
        GC.KeepAlive(_sandboxActive);
        GC.KeepAlive(_quotaAvailable);
        _refreshTimer?.Dispose();
    }
}
