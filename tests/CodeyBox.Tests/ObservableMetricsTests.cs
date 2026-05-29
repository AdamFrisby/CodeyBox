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

    private static List<(string Instrument, long Value, string? Tag)> CollectLong(
        CodeyBoxObservableMetrics svc, string tagKey, params string[] instruments)
    {
        var observed = new List<(string, long, string?)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name is "CodeyBox.Pipeline" or "CodeyBox.Sandbox" &&
                instruments.Contains(instrument.Name))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? tag = null;
            for (var i = 0; i < tags.Length; i++)
                if (tags[i].Key == tagKey) tag = tags[i].Value?.ToString();
            lock (observed) observed.Add((instrument.Name, value, tag));
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return observed;
    }

    [Fact]
    public async Task WorkItemActiveGauge_ReportsCountPerState()
    {
        var store = new SqliteWorkItemStore(_dbPath);
        await store.CreateAsync(Item(WorkItemState.Queued));
        await store.CreateAsync(Item(WorkItemState.Queued));
        await store.CreateAsync(Item(WorkItemState.Working));

        using var svc = new CodeyBoxObservableMetrics(
            store,
            new InertSandboxProvider(),
            new OrchestratorOptions { MaxConcurrentWorkers = 8 },
            NullLogger<CodeyBoxObservableMetrics>.Instance,
            workerPool: new FakeWorkerPool(3),
            quotaSnapshot: null,
            refreshInterval: TimeSpan.FromMinutes(10));
        await svc.StartAsync(CancellationToken.None);

        var observed = CollectLong(svc, "state",
            "codeybox.work_item.active", "codeybox.workers.in_use", "codeybox.workers.max");

        Assert.Contains(observed, m => m.Instrument == "codeybox.work_item.active" && m.Tag == "Queued" && m.Value == 2);
        Assert.Contains(observed, m => m.Instrument == "codeybox.work_item.active" && m.Tag == "Working" && m.Value == 1);
        // Worker occupancy comes from the worker-pool abstraction (pool total),
        // not from summing per-agent routing reservations.
        Assert.Contains(observed, m => m.Instrument == "codeybox.workers.in_use" && m.Value == 3);
        Assert.Contains(observed, m => m.Instrument == "codeybox.workers.max" && m.Value == 8);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SandboxActiveGauge_ReportsSuspendableCount()
    {
        var store = new SqliteWorkItemStore(_dbPath);
        using var svc = new CodeyBoxObservableMetrics(
            store,
            new FakeSuspendingProvider(2),
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<CodeyBoxObservableMetrics>.Instance,
            workerPool: new FakeWorkerPool(0),
            quotaSnapshot: null,
            refreshInterval: TimeSpan.FromMinutes(10));
        await svc.StartAsync(CancellationToken.None);

        var observed = CollectLong(svc, "provider", "codeybox.sandbox.active");

        Assert.Contains(observed, m => m.Instrument == "codeybox.sandbox.active" && m.Value == 2 && m.Tag == "fake-vm");

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QuotaAvailableGauge_RegisteredWhenSnapshotPresent_ReportsPerAgentPct()
    {
        var store = new SqliteWorkItemStore(_dbPath);
        var snapshot = new FakeQuotaSnapshot(
        [
            (new AgentKind("claude"), "claude-opus-4-8", 72.5),
            (new AgentKind("codex"), null, -1),
        ]);
        using var svc = new CodeyBoxObservableMetrics(
            store,
            new InertSandboxProvider(),
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<CodeyBoxObservableMetrics>.Instance,
            workerPool: new FakeWorkerPool(0),
            quotaSnapshot: snapshot,
            refreshInterval: TimeSpan.FromMinutes(10));
        await svc.StartAsync(CancellationToken.None);

        var observed = new List<(double Value, string? Agent, string? Model)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "CodeyBox.Pipeline" && instrument.Name == "codeybox.agent.quota.available_pct")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string? agent = null, model = null;
            for (var i = 0; i < tags.Length; i++)
            {
                if (tags[i].Key == "agent.kind") agent = tags[i].Value?.ToString();
                if (tags[i].Key == "model") model = tags[i].Value?.ToString();
            }
            lock (observed) observed.Add((value, agent, model));
        });
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Contains(observed, m => m.Agent == "claude" && m.Model == "claude-opus-4-8" && Math.Abs(m.Value - 72.5) < 1e-9);
        Assert.Contains(observed, m => m.Agent == "codex" && m.Model == "(default)" && m.Value == -1);

        await svc.StopAsync(CancellationToken.None);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeWorkerPool(int total) : IWorkerPoolOccupancy
    {
        public int CurrentlyRunningTotal => total;
    }

    private sealed class FakeQuotaSnapshot(IReadOnlyList<(AgentKind, string?, double)> rows)
        : IAgentQuotaAvailabilitySnapshot
    {
        public IReadOnlyList<(AgentKind Agent, string? ModelId, double AvailablePct)> SnapshotQuotaAvailability()
            => rows.Select(r => (r.Item1, r.Item2, r.Item3)).ToList();
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

    private sealed class FakeSuspendingProvider(int active) : ISandboxProvider, ISuspendingSandboxProvider
    {
        public string Name => "fake-vm";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public IReadOnlyList<(WorkItemId WorkItemId, ISuspendableSandbox Sandbox)> SnapshotSuspendableActive() =>
            Enumerable.Range(0, active)
                .Select(_ => (new WorkItemId(Guid.NewGuid()), (ISuspendableSandbox)new FakeSuspendable()))
                .ToList();

        public Task ResumeSandboxAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeSuspendable : ISuspendableSandbox
    {
        public string Id => "fake";
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SuspendAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
