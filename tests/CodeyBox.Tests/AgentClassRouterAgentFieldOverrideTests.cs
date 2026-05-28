using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Documents and locks in the behaviour that <see cref="WorkItem.Agent"/> on a
/// class-routed item is a preference, not a hard pin — the router may pick a
/// higher-scoring class member and the orchestrator rewrites <see cref="WorkItem.Agent"/>.
/// </summary>
public sealed class AgentClassRouterAgentFieldOverrideTests
{
    private static readonly AgentKind AgentA = AgentKind.Claude;   // QualityScore 100
    private static readonly AgentKind AgentB = AgentKind.Codex;    // QualityScore 90

    private static AgentClassRouter BuildRouter(AgentClass agentClass)
    {
        var opts = new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromMinutes(5) };
        return new AgentClassRouter(
            [agentClass],
            [new FakeProbe(AgentA, 50.0), new FakeProbe(AgentB, 50.0)],
            opts,
            NullLogger<AgentClassRouter>.Instance);
    }

    [Fact]
    public async Task ClassRoutedItem_WithAgentPreference_OutscoredMemberWinsAndRewritesAgent()
    {
        var agentClass = new AgentClass
        {
            Id = "test-class",
            DisplayName = "Test",
            Members =
            [
                new AgentMembership { Agent = AgentA, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentB, Billing = AgentBilling.Subscription, QualityScore = 90 },
            ],
        };
        var router = BuildRouter(agentClass);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            Agent = AgentB,
            AgentClassId = "test-class",
            MinModelScore = 80,
        };

        var decision = await router.ResolveAsync(item, project: null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(AgentA, decision.Chosen!.Agent);
        Assert.NotEqual(AgentB, decision.Chosen.Agent);

        // Mirrors OrchestratorService pickup: router choice rewrites WorkItem.Agent.
        var routed = item with { Agent = decision.Chosen.Agent };
        Assert.Equal(AgentA, routed.Agent);
    }
}
