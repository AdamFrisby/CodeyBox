using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class AgentClassRouterAuditTests : IDisposable
{
    private readonly TestSink _sink = new();

    public AgentClassRouterAuditTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose() => Log.CloseAndFlush();

    [Fact]
    public async Task InsufficientHeadroom_EmitsQuotaDispatchRefusedAuditEvent()
    {
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                },
            ],
        };
        var opts = new QuotaRouterOptions { MinQuotaPct = 10.0 };
        var probes = new IAgentQuotaProbe[] { new FakeProbe(AgentKind.Claude, 15.0) };
        var manager = new InProcessQuotaHeadroomManager(
            new FixedHeadroomEstimator(10.0),
            probes,
            opts,
            NullLogger<InProcessQuotaHeadroomManager>.Instance);
        var router = new AgentClassRouter(
            [cls],
            probes,
            opts,
            NullLogger<AgentClassRouter>.Instance,
            headroomManager: manager);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj-audit"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier",
        };

        var decision = await router.ResolveAsync(item, null, CancellationToken.None);

        Assert.True(decision.ShouldWait);
        var evt = _sink.Events.Single(e => GetScalar<string>(e, "EventName") == "quota_dispatch_refused");
        Assert.Equal(LogEventLevel.Warning, evt.Level);
        Assert.Equal("claude", GetScalar<string>(evt, "Agent"));
        Assert.Equal("proj-audit", GetScalar<string>(evt, "ProjectId"));
        Assert.Equal(15.0, GetScalar<double>(evt, "AvailablePct"));
        Assert.Equal(10.0, GetScalar<double>(evt, "EstimatedCost"));
        Assert.Equal("insufficient headroom", GetScalar<string>(evt, "Reason"));
    }

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        Assert.True(evt.Properties.TryGetValue(key, out var value), $"missing property {key}");
        var scalar = Assert.IsType<ScalarValue>(value);
        return (T?)scalar.Value;
    }
}
