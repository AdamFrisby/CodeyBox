using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Codex;
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

        // Drive the REAL codex probe so the config-sourced subscription-bucket
        // alias in ApplyMemberGate runs: the WHAM response reports the Codex
        // subscription's usage under the "GPT-5.3-Codex-Spark" display bucket
        // (exhausted, used_percent=100), not the model the CLI routes to. With
        // the routed default configured as gpt-5.5 (matching the class member),
        // the probe aliases that exhausted bucket onto gpt-5.5 — so codex is
        // skipped even though the account-wide overall (used_percent=40) has
        // headroom. No hardcoded routing id is involved; the alias target is
        // sourced from CodeyBox:AgentDefaults[codex].
        var codexBody = """
        {
          "rate_limit": { "primary_window": { "used_percent": 40 } },
          "additional_rate_limits": [
            {
              "limit_name": "GPT-5.3-Codex-Spark",
              "rate_limit": { "primary_window": { "used_percent": 100 } }
            }
          ]
        }
        """;
        var codexHandler = new QuotaCapturingHandler(HttpStatusCode.OK, codexBody, _ => { });
        var codexProbe = new CodexQuotaProbe(
            new QuotaFakeHttpClientFactory("agent-quota", codexHandler),
            (AgentMembership _) => new AgentQuotaCredentials("test-token"),
            cacheTtl: TimeSpan.FromMinutes(1),
            NullLogger<CodexQuotaProbe>.Instance,
            defaults: new AgentDefaultsSnapshot(
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["codex"] = "gpt-5.5" }));

        var router = new AgentClassRouter(
            [cls],
            [codexProbe, new FakeProbe(AgentKind.Claude, 80)],
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
