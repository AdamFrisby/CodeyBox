using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class AgentClassRouterPerModelTests
{
    [Fact]
    public async Task PerModelQuotaExhausted_SkipsThatModelEvenWhenOverallAvailable()
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
                    ModelId = "claude-opus-4-7",
                    QualityScore = 100,
                },
                new AgentMembership
                {
                    Agent = AgentKind.Codex,
                    Billing = AgentBilling.Subscription,
                    ModelId = "codex-5.5",
                    QualityScore = 99,
                },
            ],
        };

        var claudeSnapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 60,
            PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude-opus-4-7"] = new() { AvailablePct = 0, Window = "weekly" },
            },
        };

        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(AgentKind.Claude, claudeSnapshot), new FakeProbe(AgentKind.Codex, 80)],
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance);

        var decision = await router.ResolveAsync(new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier",
        }, null, CancellationToken.None);

        Assert.Equal(AgentKind.Codex, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task CodexDisplayBucketExhausted_SkipsDefaultRoutedCodexModel()
    {
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Codex,
                    Billing = AgentBilling.Subscription,
                    ModelId = "gpt-5.5",
                    QualityScore = 100,
                },
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    ModelId = "claude-opus-4-7",
                    QualityScore = 99,
                },
            ],
        };

        var codexSnapshot = CodeyBox.Agents.Codex.CodexQuotaProbe.ParseResponse("""
        {
          "rate_limit": { "primary_window": { "used_percent": 40 } },
          "additional_rate_limits": [
            {
              "limit_name": "GPT-5.3-Codex-Spark",
              "rate_limit": { "primary_window": { "used_percent": 100 } }
            }
          ]
        }
        """);

        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(AgentKind.Codex, codexSnapshot), new FakeProbe(AgentKind.Claude, 80)],
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance);

        var decision = await router.ResolveAsync(new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier",
        }, null, CancellationToken.None);

        Assert.Equal(AgentKind.Claude, decision.Chosen!.Agent);
    }
}
