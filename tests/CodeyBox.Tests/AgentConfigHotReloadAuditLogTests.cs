using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Direct coverage of acceptance criterion #4: every successful per-block
/// hot-reload emits exactly one structured <c>config_reloaded</c> audit
/// entry with the correct <c>Block</c> name, and a no-op OnChange (no
/// blocks mutated against the last-applied serialised form) emits none.
/// Wires a Serilog sink into the global <see cref="Log.Logger"/> so the
/// entries from <see cref="AuditLog.ConfigReloaded"/> are observable;
/// shares the <see cref="GlobalSerilogCollection"/> with the other classes
/// that mutate the static logger so the sink can't be swapped mid-test.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AgentConfigHotReloadAuditLogTests : IDisposable
{
    private static readonly AgentKind Claude = AgentKind.Claude;

    private readonly TestSink _sink = new();

    public AgentConfigHotReloadAuditLogTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose() => Log.CloseAndFlush();

    [Fact]
    public async Task EachChangedBlock_EmitsOneConfigReloadedEntryWithCorrectBlockName()
    {
        var initial = BuildOptions(
            claudeCap: 1,
            classMembers: [new() { Agent = "claude", Billing = "Subscription", QualityScore = 100 }],
            burnDefault: 4.0);
        var coordinator = await StartCoordinatorAsync(initial);

        // Fire an OnChange that mutates all three blocks.
        coordinator.Monitor.Fire(BuildOptions(
            claudeCap: 3,
            classMembers: [new() { Agent = "codex", Billing = "Subscription", QualityScore = 100 }],
            burnDefault: 9.0));

        var reloadEvents = _sink.Events
            .Where(e => e.Properties.TryGetValue("EventName", out var ev)
                && ev is ScalarValue sv
                && (string?)sv.Value == "config_reloaded")
            .ToList();

        Assert.Equal(3, reloadEvents.Count);
        var blockNames = reloadEvents
            .Select(e => (string?)((ScalarValue)e.Properties["Block"]).Value)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { "AgentBurnEstimator", "AgentClasses", "AgentConcurrency" },
            blockNames);

        // Each entry carries OldValue + NewValue properties so an operator
        // grepping by Block name gets the full transition.
        foreach (var evt in reloadEvents)
        {
            Assert.True(evt.Properties.ContainsKey("OldValue"));
            Assert.True(evt.Properties.ContainsKey("NewValue"));
            var oldVal = (string?)((ScalarValue)evt.Properties["OldValue"]).Value;
            var newVal = (string?)((ScalarValue)evt.Properties["NewValue"]).Value;
            Assert.NotEqual(oldVal, newVal);
        }

        await coordinator.Coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    [Fact]
    public async Task OnlyMutatedBlock_EmitsConfigReloaded_AndUnchangedBlocksStaySilent()
    {
        var initial = BuildOptions(
            claudeCap: 1,
            classMembers: [new() { Agent = "claude", Billing = "Subscription", QualityScore = 100 }],
            burnDefault: 4.0);
        var coordinator = await StartCoordinatorAsync(initial);

        // Mutate ONLY AgentConcurrency; the other two blocks are byte-identical.
        coordinator.Monitor.Fire(BuildOptions(
            claudeCap: 5,
            classMembers: [new() { Agent = "claude", Billing = "Subscription", QualityScore = 100 }],
            burnDefault: 4.0));

        var reloadEvents = _sink.Events
            .Where(e => e.Properties.TryGetValue("EventName", out var ev)
                && ev is ScalarValue sv
                && (string?)sv.Value == "config_reloaded")
            .ToList();

        var single = Assert.Single(reloadEvents);
        Assert.Equal(
            "AgentConcurrency",
            (string?)((ScalarValue)single.Properties["Block"]).Value);

        await coordinator.Coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    [Fact]
    public async Task NoOpOnChange_EmitsNoConfigReloadedEntries()
    {
        var initial = BuildOptions(
            claudeCap: 1,
            classMembers: [new() { Agent = "claude", Billing = "Subscription", QualityScore = 100 }],
            burnDefault: 4.0);
        var coordinator = await StartCoordinatorAsync(initial);

        // Fire OnChange with a byte-identical payload.
        coordinator.Monitor.Fire(BuildOptions(
            claudeCap: 1,
            classMembers: [new() { Agent = "claude", Billing = "Subscription", QualityScore = 100 }],
            burnDefault: 4.0));

        Assert.DoesNotContain(_sink.Events, e =>
            e.Properties.TryGetValue("EventName", out var ev)
            && ev is ScalarValue sv
            && (string?)sv.Value == "config_reloaded");

        await coordinator.Coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static CodeyBoxOptions BuildOptions(
        int claudeCap,
        List<AgentMembershipOptions> classMembers,
        double burnDefault) => new()
        {
            AgentConcurrency = new AgentConcurrencyOptions
            {
                Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = claudeCap } },
            },
            AgentBurnEstimator = new AgentBurnEstimatorOptions
            {
                DefaultBurnPercentPerItem = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["claude"] = burnDefault,
                },
            },
            AgentClasses =
        [
            new AgentClassOptions
            {
                Id = "frontier",
                Members = classMembers,
            },
        ],
        };

    private static async Task<CoordinatorContext> StartCoordinatorAsync(CodeyBoxOptions initial)
    {
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var router = new AgentClassRouter(
            AgentClassesConfigBuilder.Build(initial.AgentClasses, NullLogger<AgentClassRouter>.Instance),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        var dbPath = Path.Combine(Path.GetTempPath(), $"cb-audit-{Guid.NewGuid():N}.db");
        var store = new SqliteWorkItemStore(dbPath);
        var orch = new OrchestratorService(
            new InMemoryTaskQueue(),
            store,
            new NoopPipelineRunnerForAudit(),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStoreForAudit(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);
        var coordinator = new AgentConfigHotReload(
            monitor, orch, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance);
        await coordinator.StartAsync(CancellationToken.None);
        return new CoordinatorContext(coordinator, monitor, store, dbPath);
    }

    private sealed class CoordinatorContext : IDisposable
    {
        public AgentConfigHotReload Coordinator { get; }
        public ManualOptionsMonitor<CodeyBoxOptions> Monitor { get; }
        private readonly SqliteWorkItemStore _store;
        private readonly string _dbPath;

        public CoordinatorContext(
            AgentConfigHotReload coordinator,
            ManualOptionsMonitor<CodeyBoxOptions> monitor,
            SqliteWorkItemStore store,
            string dbPath)
        {
            Coordinator = coordinator;
            Monitor = monitor;
            _store = store;
            _dbPath = dbPath;
        }

        public void Dispose()
        {
            _store.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }
    }

    private sealed class ManualOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _value;
        private readonly List<Action<T, string?>> _listeners = new();
        private readonly Lock _gate = new();

        public ManualOptionsMonitor(T initial) { _value = initial; }

        public T CurrentValue => _value;
        public T Get(string? name) => _value;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            lock (_gate) _listeners.Add(listener);
            return new Subscription(() => { lock (_gate) _listeners.Remove(listener); });
        }

        public void Fire(T next)
        {
            _value = next;
            Action<T, string?>[] snapshot;
            lock (_gate) snapshot = _listeners.ToArray();
            foreach (var l in snapshot) l(next, null);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly Action _onDispose;
            public Subscription(Action onDispose) { _onDispose = onDispose; }
            public void Dispose() => _onDispose();
        }
    }

    private sealed class NoopPipelineRunnerForAudit : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken phaseCt, CancellationToken hostCt) =>
            Task.CompletedTask;
    }

    private sealed class InertCostStoreForAudit : IWorkItemCostStore, IRecentCostsByAgentQueryable
    {
        public Task<(long AvgTokens, int Samples)> GetAvgTokensPerItemAsync(
            string agentKind, int limit, CancellationToken ct = default) =>
            Task.FromResult<(long, int)>((0L, 0));

        public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItemCost>>(Array.Empty<WorkItemCost>());
        public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItemCost>>(Array.Empty<WorkItemCost>());
        public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string, double)>>(Array.Empty<(string, double)>());
        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<decimal> SumEstimatedUsdAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult(0m);
    }
}
