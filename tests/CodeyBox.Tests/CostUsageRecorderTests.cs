using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Xunit;

namespace CodeyBox.Tests;

public sealed class CostUsageRecorderTests
{
    private sealed class StubExtractor(AgentCostSnapshot? snapshot, Exception? toThrow = null) : IAgentCostExtractor
    {
        public AgentKind Kind => AgentKind.Claude;
        public ModelRateConfig? DefaultPricing => null;

        public AgentCostSnapshot? TryExtract(string? stdout, string? stderr)
        {
            if (toThrow is not null) throw toThrow;
            return snapshot;
        }
    }

    private sealed class SummarisingCostStore(WorkItemUsageSummary? summary, Exception? toThrow = null) : IWorkItemCostStore
    {
        public List<WorkItemCost> Recorded { get; } = [];

        public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default)
        {
            if (toThrow is not null) throw toThrow;
            Recorded.Add(cost);
            return Task.CompletedTask;
        }

        public Task<WorkItemUsageSummary?> SummariseAsync(string workItemId, CancellationToken ct = default)
        {
            if (toThrow is not null) throw toThrow;
            return Task.FromResult(summary);
        }

        public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItemCost>>(Recorded);

        public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItemCost>>([]);

        public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, double)>>([]);

        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<decimal> SumEstimatedUsdAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult(0m);
    }

    [Fact]
    public async Task TryRecordCostAsync_WhenStoresNull_ReturnsGracefully()
    {
        var recorder = new CostUsageRecorder(
            costStore: null,
            usageStore: null,
            costCalculator: null,
            costExtractors: null,
            NullLogger.Instance);

        // Should return without throwing
        await recorder.TryRecordCostAsync(
            stdout: "output",
            stderr: null,
            agentKind: AgentKind.Claude,
            agentInstanceId: "inst-1",
            workItemId: new WorkItemId(Guid.NewGuid()),
            phase: "work",
            iteration: 1,
            startedAt: DateTimeOffset.UtcNow.AddSeconds(-10),
            endedAt: DateTimeOffset.UtcNow,
            dispatchModelId: "claude-model");
    }

    [Fact]
    public async Task TryRecordCostAsync_WithExtractor_RecordsCostAndUsage()
    {
        var costStore = new SummarisingCostStore(null);
        var usageStore = new PipelineRunnerCostCaptureTests.RecordingUsageStore();
        var pricingOpts = new AgentPricingOptions
        {
            DefaultRates = new()
            {
                [AgentKind.Claude.Value] = new ModelRateConfig { InputPerMillion = 3.0, OutputPerMillion = 15.0 },
            }
        };
        var calc = new AgentCostCalculator(pricingOpts);

        var extractors = new Dictionary<AgentKind, IAgentCostExtractor>
        {
            [AgentKind.Claude] = new StubExtractor(new AgentCostSnapshot(1000, 200, 500, "test-model")),
        };

        var recorder = new CostUsageRecorder(costStore, usageStore, calc, extractors, NullLogger.Instance);

        var itemId = new WorkItemId(Guid.NewGuid());
        var started = DateTimeOffset.UtcNow.AddSeconds(-5);
        var ended = DateTimeOffset.UtcNow;

        await recorder.TryRecordCostAsync(
            "out", "err", AgentKind.Claude, "inst-42", itemId, "work", 1, started, ended, "test-model");

        Assert.Single(costStore.Recorded);
        var cost = costStore.Recorded[0];
        Assert.Equal(itemId.ToString(), cost.WorkItemId);
        Assert.Equal("work", cost.Phase);
        Assert.Equal(1, cost.Iteration);
        Assert.Equal(AgentKind.Claude.Value, cost.AgentKind);
        Assert.Equal("inst-42", cost.AgentInstanceId);
        Assert.Equal("test-model", cost.ModelId);
        Assert.Equal(1000, cost.InputTokens);
        Assert.Equal(200, cost.CachedInputTokens);
        Assert.Equal(500, cost.OutputTokens);
        Assert.True(cost.HasExtractedTokenUsage);
        Assert.True(cost.EstimatedUsd > 0);

        Assert.Single(usageStore.Recorded);
        var usage = usageStore.Recorded[0];
        Assert.Equal(itemId.ToString(), usage.WorkItemId);
        Assert.Equal("test-model", usage.ModelId);
        Assert.Equal(1000, usage.InputTokens);
        Assert.Equal(200, usage.CachedInputTokens);
        Assert.Equal(500, usage.OutputTokens);
        Assert.True(usage.CostMicroCents > 0);
    }

    [Fact]
    public async Task TryRecordCostAsync_WhenExtractorThrows_RecordsElapsedFallback()
    {
        var costStore = new SummarisingCostStore(null);
        var usageStore = new PipelineRunnerCostCaptureTests.RecordingUsageStore();
        var extractors = new Dictionary<AgentKind, IAgentCostExtractor>
        {
            [AgentKind.Claude] = new StubExtractor(null, new InvalidOperationException("boom")),
        };

        var recorder = new CostUsageRecorder(costStore, usageStore, null, extractors, NullLogger.Instance);
        var itemId = new WorkItemId(Guid.NewGuid());

        await recorder.TryRecordCostAsync(
            "out", "err", AgentKind.Claude, null, itemId, "audit", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "dispatch-model");

        Assert.Single(costStore.Recorded);
        var cost = costStore.Recorded[0];
        Assert.False(cost.HasExtractedTokenUsage);
        Assert.Contains(CostUsageRecorder.ElapsedFallbackMetadataSource, cost.RawMetadataJson);
        Assert.Equal(0, cost.InputTokens);
        Assert.Equal(0, cost.OutputTokens);

        Assert.Single(usageStore.Recorded);
        var usage = usageStore.Recorded[0];
        Assert.Equal(0, usage.InputTokens);
        Assert.Equal("dispatch-model", usage.ModelId);
    }

    [Fact]
    public async Task TryRecordCostAsync_WhenStoreThrows_SwallowsException()
    {
        var costStore = new SummarisingCostStore(null, new InvalidOperationException("store db failure"));
        var recorder = new CostUsageRecorder(costStore, null, null, null, NullLogger.Instance);

        // Does not throw
        await recorder.TryRecordCostAsync(
            "out", "err", AgentKind.Claude, null, new WorkItemId(Guid.NewGuid()), "work", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
    }

    [Fact]
    public async Task TryRecordCompletionCostAsync_RecordsCostAndUsage()
    {
        var costStore = new SummarisingCostStore(null);
        var usageStore = new PipelineRunnerCostCaptureTests.RecordingUsageStore();
        var pricingOpts = new AgentPricingOptions
        {
            DefaultRates = new()
            {
                [AgentKind.Codex.Value] = new ModelRateConfig { InputPerMillion = 2.5, OutputPerMillion = 10.0 },
            }
        };
        var calc = new AgentCostCalculator(pricingOpts);

        var recorder = new CostUsageRecorder(costStore, usageStore, calc, null, NullLogger.Instance);

        var item = new WorkItem
        {
            Id = new WorkItemId(Guid.NewGuid()),
            ProjectId = new ProjectId("test-proj"),
            Title = "Test work item",
            Prompt = "Run completion",
            AgentInstanceId = "inst-completion",
        };

        var completionResult = new CheckAndActCompletionResult(
            Provider: "openai",
            AgentKind: AgentKind.Codex,
            ModelId: "gpt-4o",
            Output: "Completed successfully",
            Usage: new CheckAndActCompletionUsage(
                InputTokens: 500,
                CachedInputTokens: 50,
                OutputTokens: 120,
                CacheHit: true));

        var started = DateTimeOffset.UtcNow.AddSeconds(-2);
        var ended = DateTimeOffset.UtcNow;

        await recorder.TryRecordCompletionCostAsync(completionResult, item, "work", 1, started, ended);

        Assert.Single(costStore.Recorded);
        var cost = costStore.Recorded[0];
        Assert.Equal(item.Id.ToString(), cost.WorkItemId);
        Assert.Equal("work", cost.Phase);
        Assert.Equal("inst-completion", cost.AgentInstanceId);
        Assert.Equal("gpt-4o", cost.ModelId);
        Assert.Equal(500, cost.InputTokens);
        Assert.Equal(50, cost.CachedInputTokens);
        Assert.Equal(120, cost.OutputTokens);
        Assert.True(cost.HasExtractedTokenUsage);
        Assert.Contains("check_and_act_completion", cost.RawMetadataJson);

        Assert.Single(usageStore.Recorded);
        var usage = usageStore.Recorded[0];
        Assert.Equal(item.Id.ToString(), usage.WorkItemId);
        Assert.Equal("gpt-4o", usage.ModelId);
        Assert.Equal(500, usage.InputTokens);
    }

    [Fact]
    public async Task TryGetUsageSummaryAsync_WhenStoreNull_ReturnsNull()
    {
        var recorder = new CostUsageRecorder(null, null, null, null, NullLogger.Instance);
        var res = await recorder.TryGetUsageSummaryAsync(new WorkItemId(Guid.NewGuid()));
        Assert.Null(res);
    }

    [Fact]
    public async Task TryGetUsageSummaryAsync_WhenStoreHasSummary_ReturnsSummary()
    {
        var summary = new WorkItemUsageSummary(
            new WorkItemIterationUsage(1, 1000, 200, 0, 100, 0.05, 1000),
            new WorkItemUsageTotal(1000, 200, 0, 100, 0.05, 1000));
        var costStore = new SummarisingCostStore(summary);
        var recorder = new CostUsageRecorder(costStore, null, null, null, NullLogger.Instance);

        var res = await recorder.TryGetUsageSummaryAsync(new WorkItemId(Guid.NewGuid()));
        Assert.NotNull(res);
        Assert.Equal(1000, res.Total.TokensInput);
        Assert.Equal(0.05, res.Total.CostUsd);
    }

    [Fact]
    public async Task TryGetUsageSummaryAsync_WhenStoreThrows_ReturnsNull()
    {
        var costStore = new SummarisingCostStore(null, new InvalidOperationException("sql error"));
        var recorder = new CostUsageRecorder(costStore, null, null, null, NullLogger.Instance);

        var res = await recorder.TryGetUsageSummaryAsync(new WorkItemId(Guid.NewGuid()));
        Assert.Null(res);
    }

    [Fact]
    public void BuildUsageEvent_ClampsNegativeValues()
    {
        var snapshot = new AgentCostSnapshot(-10, -5, -20, "parsed-id");
        var ev = CostUsageRecorder.BuildUsageEvent(
            AgentKind.Claude,
            "instance-1",
            "dispatch-id",
            snapshot,
            usd: -10m,
            new WorkItemId(Guid.NewGuid()),
            endedAt: DateTimeOffset.UtcNow,
            phase: "work",
            startedAt: DateTimeOffset.UtcNow.AddSeconds(5)); // ended before started -> negative elapsed

        Assert.Equal(0, ev.InputTokens);
        Assert.Equal(0, ev.CachedInputTokens);
        Assert.Equal(0, ev.OutputTokens);
        Assert.Equal(0L, ev.CostMicroCents);
        Assert.Equal(0, ev.ElapsedMs);
        Assert.Equal("dispatch-id", ev.ModelId);
    }

    [Fact]
    public void ClampCostSnapshot_NormalizesAndClamps()
    {
        var snapshot = new AgentCostSnapshot(-10, -2, -30, " ");
        var clamped = CostUsageRecorder.ClampCostSnapshot(snapshot, "fallback-model");

        Assert.Equal(0, clamped.InputTokens);
        Assert.Equal(0, clamped.CachedInputTokens);
        Assert.Equal(0, clamped.OutputTokens);
        Assert.Equal("fallback-model", clamped.ModelId);
    }

    [Fact]
    public void PipelineRunner_Forwarders_DelegateCorrectly()
    {
        var snapshot = new AgentCostSnapshot(100, 20, 30, "parsed");
        var ev = PipelineRunner.BuildUsageEvent(
            AgentKind.Codex,
            "dispatch",
            snapshot,
            1.0m,
            new WorkItemId(Guid.NewGuid()),
            DateTimeOffset.UtcNow);

        Assert.Equal(100, ev.InputTokens);
        Assert.Equal("dispatch", ev.ModelId);

        var clamped = PipelineRunner.ClampCostSnapshot(new AgentCostSnapshot(-5, 10, -2, "model"));
        Assert.Equal(0, clamped.InputTokens);
        Assert.Equal(10, clamped.CachedInputTokens);
        Assert.Equal(0, clamped.OutputTokens);

        Assert.Equal("extracted", PipelineRunner.ResolveCostRowModelId("extracted", "dispatch"));
        Assert.Equal("dispatch", PipelineRunner.ResolveCostRowModelId("", "dispatch"));
        Assert.Null(PipelineRunner.ResolveCostRowModelId(null, null));
    }
}
