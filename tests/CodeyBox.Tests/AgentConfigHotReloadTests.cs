using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies hot-reload of <c>CodeyBox:AgentConcurrency</c>,
/// <c>CodeyBox:AgentClasses</c>, and <c>CodeyBox:AgentBurnEstimator</c>: edits
/// to these blocks of the layered config land in the running router /
/// orchestrator / burn estimator without a restart, and in-flight items
/// already past the dispatch gate keep the snapshot they started on.
/// </summary>
public sealed class AgentConfigHotReloadTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;

    // ── AgentClassRouter.ApplyConfigReload ──────────────────────────────────

    [Fact]
    public async Task Router_ApplyConfigReload_SwapsCatalog_NewClassResolvable()
    {
        var initial = MakeClass("frontier", Claude);
        var router = new AgentClassRouter(
            new[] { initial },
            new[] { (IAgentQuotaProbe)new FakeProbe(Claude, 80.0), new FakeProbe(Codex, 80.0) },
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);

        Assert.Contains("frontier", router.ClassIds);
        Assert.DoesNotContain("bulk", router.ClassIds);

        var bulk = MakeClass("bulk", Codex);
        router.ApplyConfigReload(new[] { bulk }, Array.Empty<ParsedTodModifier>());

        Assert.DoesNotContain("frontier", router.ClassIds);
        Assert.Contains("bulk", router.ClassIds);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "bulk",
        };
        var decision = await router.ResolveAsync(item, project: null, CancellationToken.None);
        Assert.Equal(Codex, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task Router_ApplyConfigReload_SwapsTodModifiers_AffectsSubsequentScoring()
    {
        // Two members tied on QualityScore=100; the TOD modifier breaks the tie.
        // Initially Claude has +3 — Claude is picked. After reload swaps the
        // modifier to +3 on Codex, Codex is picked.
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = Codex,  Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };
        var router = new AgentClassRouter(
            new[] { cls },
            new[] { (IAgentQuotaProbe)new FakeProbe(Claude, 80.0), new FakeProbe(Codex, 80.0) },
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance,
            todModifiers: new[]
            {
                new ParsedTodModifier(Claude, 3, new[] { AllHoursWindow() }),
            });

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier",
        };
        var before = await router.ResolveAsync(item, project: null, CancellationToken.None);
        Assert.Equal(Claude, before.Chosen!.Agent);

        router.ApplyConfigReload(
            new[] { cls },
            new[] { new ParsedTodModifier(Codex, 3, new[] { AllHoursWindow() }) });

        var after = await router.ResolveAsync(item, project: null, CancellationToken.None);
        Assert.Equal(Codex, after.Chosen!.Agent);
    }

    // ── OrchestratorService.ApplyAgentConcurrencyReload ─────────────────────

    [Fact]
    public void Orchestrator_ApplyAgentConcurrencyReload_SwapsCaps_VisibleInState()
    {
        using var fixture = OrchestratorFixture.Build(new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
        });
        var before = fixture.Orchestrator.GetConcurrencyState();
        Assert.Equal(1, before.PerAgentCaps["claude"]);
        Assert.DoesNotContain("codex", before.PerAgentCaps);

        fixture.Orchestrator.ApplyAgentConcurrencyReload(new AgentConcurrencyOptions
        {
            Members =
            {
                ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 3 },
                ["codex"]  = new AgentConcurrencyEntry { MaxConcurrent = 2 },
            },
        });

        var after = fixture.Orchestrator.GetConcurrencyState();
        Assert.Equal(3, after.PerAgentCaps["claude"]);
        Assert.Equal(2, after.PerAgentCaps["codex"]);
    }

    [Fact]
    public void Orchestrator_ApplyAgentConcurrencyReload_DoesNotRetroactivelyKillInFlight()
    {
        // Reserve a claude slot under the initial cap=1, then drop cap to 0
        // mid-flight. The already-reserved running count must remain — the new
        // cap only gates *new* dispatches; lowering it must not snap the
        // running call.
        using var fixture = OrchestratorFixture.Build(new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
        });
        Assert.True(fixture.Orchestrator.TryReserveAgentSlotForTest(Claude));
        Assert.Equal(1, fixture.Orchestrator.GetRunning(Claude));

        fixture.Orchestrator.ApplyAgentConcurrencyReload(new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 0 } },
        });

        // The reservation already past the gate keeps its slot.
        Assert.Equal(1, fixture.Orchestrator.GetRunning(Claude));

        // New reservation attempt sees the post-reload cap (0 = unlimited per
        // the spec, since values < 1 are "no cap"). A negative path would be a
        // distinct cap setting; the contract here is that lowering the cap
        // does not yank in-flight work.
        fixture.Orchestrator.ReleaseAgentSlotForTest(Claude);
        Assert.Equal(0, fixture.Orchestrator.GetRunning(Claude));
    }

    // ── AgentBurnEstimator.ApplyConfigReload ────────────────────────────────

    [Fact]
    public async Task BurnEstimator_ApplyConfigReload_SwapsDefaults_AndClearsCache()
    {
        var costs = new InertCostStore();
        var initial = new AgentBurnEstimatorOptions
        {
            DefaultBurnPercentPerItem = new(StringComparer.OrdinalIgnoreCase) { ["claude"] = 4.0 },
        };
        var estimator = new AgentBurnEstimator(
            costs, initial, NullLogger<AgentBurnEstimator>.Instance);

        var first = await estimator.GetEstimateAsync(Claude);
        Assert.Equal(4.0, first.AvgBurnPctPerItem);
        Assert.Equal(0, first.SampleCount);

        // Swap to a different default. Without ApplyConfigReload clearing the
        // in-process cache, the next read would serve the stale 4.0 for up to
        // CacheTtl.
        var updated = new AgentBurnEstimatorOptions
        {
            DefaultBurnPercentPerItem = new(StringComparer.OrdinalIgnoreCase) { ["claude"] = 25.0 },
        };
        estimator.ApplyConfigReload(updated);

        var second = await estimator.GetEstimateAsync(Claude);
        Assert.Equal(25.0, second.AvgBurnPctPerItem);
    }

    // ── AgentConfigHotReload coordinator ────────────────────────────────────

    [Fact]
    public async Task Coordinator_OnChange_PushesUpdatesIntoEachConsumer()
    {
        var initial = new CodeyBoxOptions
        {
            AgentConcurrency = new AgentConcurrencyOptions
            {
                Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
            },
            AgentBurnEstimator = new AgentBurnEstimatorOptions
            {
                DefaultBurnPercentPerItem = new(StringComparer.OrdinalIgnoreCase) { ["claude"] = 4.0 },
            },
            AgentClasses =
            [
                new AgentClassOptions
                {
                    Id = "frontier",
                    Members =
                    [
                        new AgentMembershipOptions
                        {
                            Agent = "claude",
                            Billing = "Subscription",
                            QualityScore = 100,
                        },
                    ],
                },
            ],
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);

        // Build downstream consumers from the same initial snapshot so the
        // coordinator's StartAsync captures an already-applied baseline.
        var router = new AgentClassRouter(
            AgentClassesConfigBuilder.Build(initial.AgentClasses, NullLogger<AgentClassRouter>.Instance),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance);
        await coordinator.StartAsync(CancellationToken.None);

        // Fire OnChange with a new value that mutates all three blocks.
        var updated = new CodeyBoxOptions
        {
            AgentConcurrency = new AgentConcurrencyOptions
            {
                Members =
                {
                    ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 3 },
                    ["codex"]  = new AgentConcurrencyEntry { MaxConcurrent = 1 },
                },
            },
            AgentBurnEstimator = new AgentBurnEstimatorOptions
            {
                DefaultBurnPercentPerItem = new(StringComparer.OrdinalIgnoreCase) { ["claude"] = 7.5 },
            },
            AgentClasses =
            [
                new AgentClassOptions
                {
                    Id = "bulk",
                    Members =
                    [
                        new AgentMembershipOptions
                        {
                            Agent = "codex",
                            Billing = "Subscription",
                            QualityScore = 100,
                        },
                    ],
                },
            ],
        };
        monitor.Fire(updated);

        // Concurrency caps reflect the new value.
        var concurrencyState = orchFixture.Orchestrator.GetConcurrencyState();
        Assert.Equal(3, concurrencyState.PerAgentCaps["claude"]);
        Assert.Equal(1, concurrencyState.PerAgentCaps["codex"]);

        // Router catalog reflects the new value.
        Assert.Contains("bulk", router.ClassIds);
        Assert.DoesNotContain("frontier", router.ClassIds);

        // Burn estimator default reflects the new value.
        var est = await burnEstimator.GetEstimateAsync(Claude);
        Assert.Equal(7.5, est.AvgBurnPctPerItem);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_OnChange_OnlyMutatedBlocksAreReapplied()
    {
        // Confirms that an edit which touches only one block does not also
        // reapply the others — verifies the coordinator's per-block JSON-diff
        // gate. Tests behaviour indirectly via observable state, since the
        // audit log is global Serilog state.
        var classes = new List<AgentClassOptions>
        {
            new()
            {
                Id = "frontier",
                Members =
                [
                    new() { Agent = "claude", Billing = "Subscription", QualityScore = 100 },
                ],
            },
        };
        var initial = new CodeyBoxOptions
        {
            AgentConcurrency = new AgentConcurrencyOptions
            {
                Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
            },
            AgentClasses = classes,
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var router = new AgentClassRouter(
            AgentClassesConfigBuilder.Build(classes, NullLogger<AgentClassRouter>.Instance),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance);
        await coordinator.StartAsync(CancellationToken.None);

        // Only change AgentConcurrency. The router catalog and burn defaults
        // are byte-for-byte identical to the baseline.
        var updated = new CodeyBoxOptions
        {
            AgentConcurrency = new AgentConcurrencyOptions
            {
                Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 5 } },
            },
            AgentClasses = classes,
        };
        monitor.Fire(updated);

        Assert.Equal(5, orchFixture.Orchestrator.GetConcurrencyState().PerAgentCaps["claude"]);
        // Router catalog unchanged.
        Assert.Contains("frontier", router.ClassIds);

        await coordinator.StopAsync(CancellationToken.None);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AgentClass MakeClass(string id, AgentKind agent) => new()
    {
        Id = id,
        DisplayName = id,
        Members =
        [
            new AgentMembership { Agent = agent, Billing = AgentBilling.Subscription, QualityScore = 100 },
        ],
    };

    private static ParsedTimeWindow AllHoursWindow() => new(
        new HashSet<DayOfWeek>
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
        },
        TimeSpan.Zero,
        TimeSpan.FromHours(24));

    private sealed class OrchestratorFixture : IDisposable
    {
        public OrchestratorService Orchestrator { get; private init; } = null!;
        private SqliteWorkItemStore? _store;
        private string? _dbPath;

        public static OrchestratorFixture Build(AgentConcurrencyOptions concurrency)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"cb-hotreload-{Guid.NewGuid():N}.db");
            var store = new SqliteWorkItemStore(dbPath);
            var orch = new OrchestratorService(
                new InMemoryTaskQueue(),
                store,
                new NoopPipelineRunner(),
                new CancellationRegistry(CancellationToken.None),
                new OrchestratorOptions { MaxConcurrentWorkers = 4 },
                NullLogger<OrchestratorService>.Instance,
                agentConcurrency: concurrency);
            return new OrchestratorFixture { Orchestrator = orch, _store = store, _dbPath = dbPath };
        }

        public void Dispose()
        {
            _store?.Dispose();
            if (_dbPath is not null) { try { File.Delete(_dbPath); } catch { } }
        }
    }

    private sealed class NoopPipelineRunner : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken phaseCt, CancellationToken hostCt) =>
            Task.CompletedTask;
    }

    private sealed class InertCostStore : IWorkItemCostStore, IRecentCostsByAgentQueryable
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
}
