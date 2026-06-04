using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="AgentBurnEstimator"/>. Exercises every branch of
/// <c>ComputeAsync</c> and the cache TTL behaviour using a fake cost store.
/// </summary>
public sealed class AgentBurnEstimatorTests
{
    private static readonly AgentKind Codex = AgentKind.Codex;
    private static readonly AgentKind Claude = AgentKind.Claude;

    private static AgentBurnEstimator BuildEstimator(
        FakeCostStore costs,
        AgentBurnEstimatorOptions? opts = null,
        TimeProvider? time = null) =>
        new AgentBurnEstimator(
            costs,
            opts ?? new AgentBurnEstimatorOptions(),
            NullLogger<AgentBurnEstimator>.Instance,
            time);

    [Fact]
    public async Task NoSamples_ReturnsConfiguredDefaultPctWithSampleCountZero()
    {
        // The contract: a "no historical data" call surfaces SampleCount=0 so the
        // router knows to apply the cold-start fit fallback, even when a default
        // pct is configured.
        var costs = new FakeCostStore();
        var est = BuildEstimator(costs);

        var result = await est.GetEstimateAsync(Codex);

        Assert.Equal(0, result.SampleCount);
        Assert.Equal(90.0, result.AvgBurnPctPerItem);
        Assert.Equal(AgentBurnEstimateStatus.NoHistory, result.Status);
    }

    [Fact]
    public async Task NoSamples_UnknownAgent_ReturnsNegativeAvg()
    {
        var costs = new FakeCostStore();
        var est = BuildEstimator(costs);

        var result = await est.GetEstimateAsync(new AgentKind("madeupkind"));

        Assert.Equal(0, result.SampleCount);
        Assert.True(result.AvgBurnPctPerItem < 0);
        Assert.Equal(AgentBurnEstimateStatus.NoHistory, result.Status);
    }

    [Fact]
    public async Task HistoricalSamples_AndBudgetConfigured_ComputesPctFromTokens()
    {
        // 113M tokens / 120M budget = 94.17% — codex's observed real-world burn.
        var costs = new FakeCostStore { TokensByAgent = { ["codex"] = (113_000_000L, 5) } };
        var opts = new AgentBurnEstimatorOptions
        {
            WindowTokenBudget = { ["codex"] = 120_000_000L }
        };
        var est = BuildEstimator(costs, opts);

        var result = await est.GetEstimateAsync(Codex);

        Assert.Equal(5, result.SampleCount);
        Assert.Equal(94.17, Math.Round(result.AvgBurnPctPerItem, 2));
        Assert.Equal(AgentBurnEstimateStatus.Measured, result.Status);
    }

    [Fact]
    public async Task HistoricalSamples_ButNoBudget_ReportsSamplesAndUnknownBurn()
    {
        // When the store returns samples but the operator has not configured a
        // WindowTokenBudget for that agent, the estimator must not present the
        // hardcoded default as a measured burn. It preserves the real sample
        // count and marks the burn as unknown so the router can fail open.
        var costs = new FakeCostStore { TokensByAgent = { ["codex"] = (50_000_000L, 7) } };
        var opts = new AgentBurnEstimatorOptions
        {
            WindowTokenBudget = { ["claude"] = 1L }, // no WindowTokenBudget for codex
        };
        var logger = new CapturingLogger<AgentBurnEstimator>();
        var est = new AgentBurnEstimator(costs, opts, logger);

        var result = await est.GetEstimateAsync(Codex);

        Assert.Equal(7, result.SampleCount);
        Assert.True(result.AvgBurnPctPerItem < 0);
        Assert.Equal(AgentBurnEstimateStatus.NoWindowBudget, result.Status);

        var warning = Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("token samples but no positive WindowTokenBudget", StringComparison.Ordinal));
        Assert.Equal("codex", warning.Properties["Agent"]);
        Assert.Equal(7, warning.Properties["Samples"]);
    }

    [Fact]
    public async Task BurnPctClampedAt100_EvenIfTokensExceedBudget()
    {
        // Item burned 1.5x the configured budget — the gate should still report
        // 100%, not 150%, because availability cannot go negative beyond zero.
        var costs = new FakeCostStore { TokensByAgent = { ["codex"] = (150L, 3) } };
        var opts = new AgentBurnEstimatorOptions
        {
            WindowTokenBudget = { ["codex"] = 100L }
        };
        var est = BuildEstimator(costs, opts);

        var result = await est.GetEstimateAsync(Codex);

        Assert.Equal(100.0, result.AvgBurnPctPerItem);
        Assert.Equal(AgentBurnEstimateStatus.Measured, result.Status);
    }

    [Fact]
    public async Task ZeroBudget_ReportsNoWindowBudgetWithoutDivideByZero()
    {
        // A configured budget of 0 would divide by zero; treat it as unusable
        // and fail open instead of throttling on a guessed default.
        var costs = new FakeCostStore { TokensByAgent = { ["codex"] = (100L, 2) } };
        var opts = new AgentBurnEstimatorOptions
        {
            WindowTokenBudget = { ["codex"] = 0L }
        };
        var est = BuildEstimator(costs, opts);

        var result = await est.GetEstimateAsync(Codex);

        Assert.Equal(2, result.SampleCount);
        Assert.True(result.AvgBurnPctPerItem < 0);
        Assert.Equal(AgentBurnEstimateStatus.NoWindowBudget, result.Status);
    }

    [Fact]
    public void Constructor_NoPositiveWindowBudgets_EmitsStartupWarning()
    {
        var logger = new CapturingLogger<AgentBurnEstimator>();

        _ = new AgentBurnEstimator(
            new FakeCostStore(),
            new AgentBurnEstimatorOptions(),
            logger);

        var warning = Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("no positive WindowTokenBudget", StringComparison.Ordinal));
        Assert.Equal("startup", warning.Properties["Reason"]);
    }

    [Fact]
    public void Constructor_PositiveWindowBudget_SuppressesStartupWarning()
    {
        var logger = new CapturingLogger<AgentBurnEstimator>();
        var opts = new AgentBurnEstimatorOptions
        {
            WindowTokenBudget = { ["codex"] = 1L },
        };

        _ = new AgentBurnEstimator(new FakeCostStore(), opts, logger);

        Assert.DoesNotContain(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("no positive WindowTokenBudget", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyConfigReload_NoPositiveWindowBudgets_EmitsReloadWarning()
    {
        var logger = new CapturingLogger<AgentBurnEstimator>();
        var initial = new AgentBurnEstimatorOptions
        {
            WindowTokenBudget = { ["codex"] = 1L },
        };
        var est = new AgentBurnEstimator(new FakeCostStore(), initial, logger);

        est.ApplyConfigReload(new AgentBurnEstimatorOptions());

        var warning = Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("no positive WindowTokenBudget", StringComparison.Ordinal));
        Assert.Equal("config reload", warning.Properties["Reason"]);
    }

    [Fact]
    public async Task CostStoreThrows_ReturnsDefaultWithSampleCountZero_NoThrow()
    {
        // Spec: implementations MUST surface "no data" rather than throwing,
        // so the dispatch hot path is never blocked on a transient store fault.
        var costs = new FakeCostStore { ThrowOnQuery = true };
        var est = BuildEstimator(costs);

        var result = await est.GetEstimateAsync(Codex);

        Assert.Equal(0, result.SampleCount);
        Assert.Equal(90.0, result.AvgBurnPctPerItem);
        Assert.Equal(AgentBurnEstimateStatus.SampleSourceUnavailable, result.Status);
    }

    [Fact]
    public async Task DeferredResolverThrows_ReturnsDefaultWithSampleCountZero_NoThrow()
    {
        // The deferred-resolution constructor: container build-time errors
        // surfaced on the first GetEstimateAsync must not crash the dispatcher.
        var opts = new AgentBurnEstimatorOptions();
        var est = new AgentBurnEstimator(
            () => throw new InvalidOperationException("DI not ready"),
            opts,
            NullLogger<AgentBurnEstimator>.Instance);

        var result = await est.GetEstimateAsync(Codex);

        Assert.Equal(0, result.SampleCount);
        Assert.Equal(90.0, result.AvgBurnPctPerItem);
        Assert.Equal(AgentBurnEstimateStatus.SampleSourceUnavailable, result.Status);
    }

    [Fact]
    public async Task LegacyStore_NotIRecentCostsByAgentQueryable_FallsBackToDefault()
    {
        // An older IWorkItemCostStore without the capability interface must
        // not blow up; estimator treats the empirical sample source as unavailable.
        var legacy = new LegacyCostStore();
        var est = new AgentBurnEstimator(
            () => legacy,
            new AgentBurnEstimatorOptions(),
            NullLogger<AgentBurnEstimator>.Instance);

        var result = await est.GetEstimateAsync(Codex);

        Assert.Equal(0, result.SampleCount);
        Assert.Equal(90.0, result.AvgBurnPctPerItem);
        Assert.Equal(AgentBurnEstimateStatus.SampleSourceUnavailable, result.Status);
    }

    [Fact]
    public async Task CacheHit_AvoidsReQueryingTheCostStore_WithinTtl()
    {
        var costs = new FakeCostStore { TokensByAgent = { ["codex"] = (10L, 1) } };
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var opts = new AgentBurnEstimatorOptions
        {
            WindowTokenBudget = { ["codex"] = 100L },
            CacheTtl = TimeSpan.FromSeconds(60),
        };
        var est = BuildEstimator(costs, opts, time);

        await est.GetEstimateAsync(Codex);
        await est.GetEstimateAsync(Codex);
        await est.GetEstimateAsync(Codex);

        Assert.Equal(1, costs.QueryCount("codex"));
    }

    [Fact]
    public async Task CacheExpires_ReQueriesOnceTtlElapses()
    {
        var costs = new FakeCostStore { TokensByAgent = { ["codex"] = (10L, 1) } };
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var opts = new AgentBurnEstimatorOptions
        {
            WindowTokenBudget = { ["codex"] = 100L },
            CacheTtl = TimeSpan.FromSeconds(30),
        };
        var est = BuildEstimator(costs, opts, time);

        await est.GetEstimateAsync(Codex);
        time.Advance(TimeSpan.FromSeconds(31));
        await est.GetEstimateAsync(Codex);

        Assert.Equal(2, costs.QueryCount("codex"));
    }

    [Fact]
    public async Task CachePerAgent_DoesNotShareEntriesBetweenAgents()
    {
        var costs = new FakeCostStore
        {
            TokensByAgent =
            {
                ["codex"]  = (100L, 1),
                ["claude"] = (200L, 1),
            }
        };
        var opts = new AgentBurnEstimatorOptions
        {
            WindowTokenBudget =
            {
                ["codex"]  = 1000L,
                ["claude"] = 1000L,
            }
        };
        var est = BuildEstimator(costs, opts);

        var codexResult = await est.GetEstimateAsync(Codex);
        var claudeResult = await est.GetEstimateAsync(Claude);

        // 100/1000 = 10%; 200/1000 = 20% — distinct cache entries.
        Assert.Equal(10.0, codexResult.AvgBurnPctPerItem);
        Assert.Equal(20.0, claudeResult.AvgBurnPctPerItem);
    }

    [Fact]
    public async Task RollingSampleSize_PassedThroughToStoreQuery()
    {
        var costs = new FakeCostStore { TokensByAgent = { ["codex"] = (1L, 1) } };
        var opts = new AgentBurnEstimatorOptions { RollingSampleSize = 42 };
        var est = BuildEstimator(costs, opts);

        await est.GetEstimateAsync(Codex);

        Assert.Equal(42, costs.LastLimit);
    }

    private sealed class FakeCostStore : IWorkItemCostStore, IRecentCostsByAgentQueryable
    {
        public Dictionary<string, (long Tokens, int Samples)> TokensByAgent { get; } = new();
        public bool ThrowOnQuery { get; set; }
        public int LastLimit { get; private set; }

        private readonly Dictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);

        public int QueryCount(string agentKind) => _calls.GetValueOrDefault(agentKind);

        public Task<(long AvgTokens, int Samples)> GetAvgTokensPerItemAsync(
            string agentKind, int limit, CancellationToken ct = default)
        {
            _calls[agentKind] = _calls.GetValueOrDefault(agentKind) + 1;
            LastLimit = limit;
            if (ThrowOnQuery) throw new InvalidOperationException("cost store fault");
            if (TokensByAgent.TryGetValue(agentKind, out var t)) return Task.FromResult(t);
            return Task.FromResult<(long, int)>((0L, 0));
        }

        // Unused by the estimator; satisfy the interface.
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

    private sealed class LegacyCostStore : IWorkItemCostStore
    {
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

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) { _now = start; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan d) { _now = _now.Add(d); }
    }
}
