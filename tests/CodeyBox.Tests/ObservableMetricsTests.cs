using System.Diagnostics.Metrics;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises <see cref="CodeyBoxObservableMetrics"/> — the OTel observable
/// gauges that poll live store/process state. Uses a real SQLite store and the
/// built-in MeterListener so no OTel SDK or collector is required.
/// </summary>
public sealed class ObservableMetricsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cb-obs-metrics-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private static WorkItem Item(WorkItemState state) => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        State = state,
    };

    [Fact]
    public async Task WorkItemActiveGauge_ReportsCountPerState()
    {
        var store = new SqliteWorkItemStore(_dbPath);
        await store.CreateAsync(Item(WorkItemState.Queued));
        await store.CreateAsync(Item(WorkItemState.Queued));
        await store.CreateAsync(Item(WorkItemState.Working));

        var running = new FakeRunningCounters(new Dictionary<AgentKind, int>
        {
            [new AgentKind("claude")] = 2,
        });

        using var svc = new CodeyBoxObservableMetrics(
            store,
            new InertSandboxProvider(),
            new OrchestratorOptions { MaxConcurrentWorkers = 8 },
            NullLogger<CodeyBoxObservableMetrics>.Instance,
            running,
            router: null,
            refreshInterval: TimeSpan.FromMinutes(10));
        await svc.StartAsync(CancellationToken.None);

        var observed = new List<(string Instrument, long Value, string? State)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "CodeyBox.Pipeline" &&
                instrument.Name is "codeybox.work_item.active" or "codeybox.workers.in_use" or "codeybox.workers.max")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? state = null;
            for (var i = 0; i < tags.Length; i++)
                if (tags[i].Key == "state") state = tags[i].Value?.ToString();
            lock (observed) observed.Add((instrument.Name, value, state));
        });
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Contains(observed, m => m.Instrument == "codeybox.work_item.active" && m.State == "Queued" && m.Value == 2);
        Assert.Contains(observed, m => m.Instrument == "codeybox.work_item.active" && m.State == "Working" && m.Value == 1);
        Assert.Contains(observed, m => m.Instrument == "codeybox.workers.in_use" && m.Value == 2);
        Assert.Contains(observed, m => m.Instrument == "codeybox.workers.max" && m.Value == 8);

        await svc.StopAsync(CancellationToken.None);
    }

    private sealed class FakeRunningCounters(IReadOnlyDictionary<AgentKind, int> snapshot) : IAgentRunningCounters
    {
        public int GetRunning(AgentKind agent) => snapshot.TryGetValue(agent, out var n) ? n : 0;
        public IReadOnlyDictionary<AgentKind, int> Snapshot() => snapshot;
    }

    private sealed class InertSandboxProvider : ISandboxProvider
    {
        public string Name => "inert";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }
}
