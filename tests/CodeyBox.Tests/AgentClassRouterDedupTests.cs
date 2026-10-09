using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Serilog;

namespace CodeyBox.Tests;

/// <summary>
/// A routing evaluation that reaches the same decision as the previous one for
/// an item must not repeat it at Information: the first sight (and any change)
/// logs at Information, identical repeats drop to Debug. Quota probes follow
/// the same rule (Debug unless the reading changed or the probe errored).
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AgentClassRouterDedupTests : IDisposable
{
    private readonly TestSink _sink = new();

    public AgentClassRouterDedupTests()
    {
        // MinimumLevel.Debug: repeats are demoted to Debug, so the sink must
        // capture Debug to assert the demotion (production keeps a higher
        // floor, which drops them entirely).
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(_sink).CreateLogger();
    }

    public void Dispose() => Log.CloseAndFlush();

    [Fact]
    public async Task IdenticalRoutingDecisionRepeated_LogsInformationOnce()
    {
        var log = new CapturingLogger<AgentClassRouter>();
        var router = new AgentClassRouter(
            [new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members = [new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                }],
            }],
            [new FakeProbe(AgentKind.Claude, 50.0)],
            new QuotaRouterOptions
            {
                MinQuotaPct = 10.0,
                QuotaRecheckInterval = TimeSpan.FromMinutes(5),
            },
            log);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier",
        };
        var project = new Project
        {
            Id = new ProjectId("proj"),
            DisplayName = "Test",
            RepositoryUrl = "https://git.example.com/repo",
            DefaultAgentClass = "frontier",
        };

        for (var i = 0; i < 3; i++)
        {
            var decision = await router.ResolveAsync(item, project, CancellationToken.None);
            Assert.NotNull(decision.Chosen);
        }

        var routed = log.Entries.Where(e => e.Message.Contains("routed to", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, routed.Count);
        Assert.Single(routed, e => e.Level == LogLevel.Information);
        Assert.Equal(2, routed.Count(e => e.Level == LogLevel.Debug));

        var scored = _sink.Events.Where(e =>
            e.Properties.TryGetValue("EventName", out var name)
            && name.ToString() == "\"quota_router.scored\"").ToList();
        Assert.Equal(3, scored.Count);
        Assert.Single(scored, e => e.Level == Serilog.Events.LogEventLevel.Information);
        Assert.Equal(2, scored.Count(e => e.Level == Serilog.Events.LogEventLevel.Debug));

        var probed = _sink.Events.Where(e =>
            e.Properties.TryGetValue("EventName", out var name)
            && name.ToString() == "\"quota_router.probed\"").ToList();
        Assert.Equal(3, probed.Count);
        Assert.Single(probed, e => e.Level == Serilog.Events.LogEventLevel.Information);
        Assert.Equal(2, probed.Count(e => e.Level == Serilog.Events.LogEventLevel.Debug));
    }

    [Fact]
    public async Task ChangedRoutingDecision_LogsInformationAgain()
    {
        var log = new CapturingLogger<AgentClassRouter>();
        var probe = new MutableProbe(AgentKind.Claude, 50.0);
        var router = new AgentClassRouter(
            [new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members = [new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                }],
            }],
            [probe],
            new QuotaRouterOptions
            {
                MinQuotaPct = 10.0,
                QuotaRecheckInterval = TimeSpan.FromMinutes(5),
            },
            log);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier",
        };
        var project = new Project
        {
            Id = new ProjectId("proj"),
            DisplayName = "Test",
            RepositoryUrl = "https://git.example.com/repo",
            DefaultAgentClass = "frontier",
        };

        Assert.NotNull((await router.ResolveAsync(item, project, CancellationToken.None)).Chosen);
        Assert.NotNull((await router.ResolveAsync(item, project, CancellationToken.None)).Chosen);

        probe.AvailablePct = 75.0;

        Assert.NotNull((await router.ResolveAsync(item, project, CancellationToken.None)).Chosen);

        // First sight + value change log at Information; the identical middle
        // evaluation drops to Debug.
        var routed = log.Entries.Where(e => e.Message.Contains("routed to", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, routed.Count);
        Assert.Equal(2, routed.Count(e => e.Level == LogLevel.Information));
        Assert.Single(routed, e => e.Level == LogLevel.Debug);
    }

    private sealed class MutableProbe(AgentKind kind, double availablePct) : IAgentQuotaProbe
    {
        public AgentKind Kind { get; } = kind;

        public double AvailablePct { get; set; } = availablePct;

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct) =>
            Task.FromResult(new AgentQuotaSnapshot { AvailablePct = AvailablePct });
    }
}
