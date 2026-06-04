using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Router-driven audit coverage for the rate-aware gate. This mutates Serilog's
/// global logger, so it shares the serialized collection used by other audit
/// tests.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AgentClassRouterRateAwareAuditTests : IDisposable
{
    private static readonly AgentKind Codex = AgentKind.Codex;

    private readonly TestSink _sink = new();

    public AgentClassRouterRateAwareAuditTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose() => Log.CloseAndFlush();

    [Fact]
    public async Task RateAwareGate_AuditLogCarriesBurnEstimateStatus()
    {
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = Codex,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                    ModelId = "gpt-5.5",
                },
            ],
        };
        var counters = new FakeCounters();
        counters.Increment(Codex);
        counters.Increment(Codex);
        var estimator = new FakeBurnEstimator
        {
            EstimatesByAgent =
            {
                [Codex] = new AgentBurnEstimate
                {
                    AvgBurnPctPerItem = 90.0,
                    SampleCount = 0,
                    Status = AgentBurnEstimateStatus.NoHistory,
                },
            },
        };
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Codex, 81.0)],
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance,
            TimeProvider.System,
            todModifiers: null,
            quotaFailures: null,
            burnEstimator: estimator,
            runningCounters: counters);

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        var evt = Assert.Single(_sink.Events, e =>
            string.Equals(ScalarText(e, "EventName"), "concurrency.gated_rate_aware", StringComparison.Ordinal));
        Assert.Equal("NoHistory", ScalarText(evt, "Status"));
        Assert.Contains("status=NoHistory", evt.RenderMessage(), StringComparison.Ordinal);
    }

    private static WorkItem MakeItem(string classId) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = classId,
    };

    private static string? ScalarText(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return null;
        return sv.Value switch
        {
            null => null,
            AgentBurnEstimateStatus status => status.ToString(),
            string s => s,
            _ => sv.Value.ToString(),
        };
    }
}
