using System.Net;
using System.Text.Json;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Tests;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.QuotaAndRouting;

/// <summary>
/// UAT coverage for <c>Agent quota probes - Reads per-agent and per-model availability snapshots</c>.
/// Plan anchor: docs/uat/00-plan.md#agent-quota-probes---reads-per-agent-and-per-model-availability-snapshots
/// </summary>
public sealed class AgentQuotaProbesTests
{
    private static readonly AgentMembership ClaudeOpusMember = new()
    {
        Agent = AgentKind.Claude,
        Billing = AgentBilling.Subscription,
        ModelId = "claude-opus-4-7",
        QualityScore = 100,
    };

    [Fact]
    public async Task OperatorCallsQuota_ReturnsAvailabilityForRegisteredProbes()
    {
        using var factory = new WorkItemApiFactory();
        var probe = new StaticQuotaProbe(AgentKind.Claude, new AgentQuotaSnapshot
        {
            AvailablePct = 70,
            PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude-opus-4-7"] = new() { AvailablePct = 25, Window = "weekly" },
            },
        });

        var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentQuotaProbe>();
                services.AddSingleton<IAgentQuotaProbe>(probe);
            });
        }).CreateClient();

        using var response = await client.GetAsync("/quota");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var root = document.RootElement;
        Assert.Equal("UseObservedFailures", root.GetProperty("unknownPolicy").GetString());
        var apiProbe = root.GetProperty("probes").EnumerateArray().Single();
        Assert.Equal("claude", apiProbe.GetProperty("agent").GetString());
        Assert.Equal(70, apiProbe.GetProperty("latestSnapshot").GetProperty("availablePct").GetDouble());
        Assert.True(apiProbe.GetProperty("wouldAllow").GetBoolean());
        Assert.True(apiProbe.GetProperty("perModelWouldAllow").GetProperty("claude-opus-4-7").GetBoolean());
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public void ClaudeOauthUsageShape_PopulatesOverallAndModelAvailability()
    {
        var snapshot = ClaudeQuotaProbe.ParseResponse("""
        {
          "five_hour": { "utilization": 20, "resets_at": "2026-05-13T12:00:00Z" },
          "seven_day": { "utilization": 40, "resets_at": "2026-05-14T12:00:00Z" },
          "seven_day_opus": { "utilization": 100, "resets_at": "2026-05-15T12:00:00Z" },
          "seven_day_sonnet": { "utilization": 25, "resets_at": "2026-05-16T12:00:00Z" }
        }
        """);

        Assert.Equal(60, snapshot.AvailablePct);
        Assert.Equal(0, snapshot.PerModel["claude-opus-4-7"].AvailablePct);
        Assert.Equal(60, snapshot.PerModel["claude-sonnet-4-6"].AvailablePct);
        Assert.NotNull(snapshot.ResetAt);
    }

    [Fact]
    public void CodexWhamUsageShape_MapsOverallAndDefaultRoutedModel()
    {
        var snapshot = CodexQuotaProbe.ParseResponse("""
        {
          "rate_limit": { "primary_window": { "used_percent": 30 } },
          "additional_rate_limits": [
            {
              "limit_name": "GPT-5.3-Codex-Spark",
              "rate_limit": { "primary_window": { "used_percent": 100 } }
            }
          ]
        }
        """);

        Assert.Equal(70, snapshot.AvailablePct);
        Assert.Equal(0, snapshot.PerModel["GPT-5.3-Codex-Spark"].AvailablePct);
        Assert.Equal(0, snapshot.PerModel[CodexQuotaProbe.DefaultRoutedModelId].AvailablePct);
    }

    [Fact]
    public void GeminiCloudCodeUsageShape_MapsPerModelBucketsToSnapshot()
    {
        var snapshot = GeminiQuotaProbe.ParseResponse("""
        {
          "buckets": [
            { "modelId": "gemini-2.5-flash", "remainingFraction": 0.8, "resetTime": "2026-05-13T13:00:00Z", "tokenType": "REQUESTS" },
            { "modelId": "gemini-2.5-pro", "remainingFraction": 0.1, "resetTime": "2026-05-13T14:00:00Z", "tokenType": "REQUESTS" }
          ]
        }
        """);

        Assert.Equal(10, snapshot.AvailablePct);
        Assert.Equal(80, snapshot.PerModel["gemini-2.5-flash"].AvailablePct);
        Assert.Equal(10, snapshot.PerModel["gemini-2.5-pro"].AvailablePct);
        Assert.NotNull(snapshot.ResetAt);
    }

    [Fact]
    public async Task ProbeCredentialMissing_ReturnsUnknownWithDiagnosticNotes()
    {
        var calls = 0;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK, """{"rate_limit":{"primary_window":{"used_percent":0}}}""", _ => calls++);
        var probe = new ClaudeQuotaProbe(
            new QuotaFakeHttpClientFactory("agent-quota", handler),
            token: "",
            cacheTtl: TimeSpan.FromMinutes(1),
            NullLogger<ClaudeQuotaProbe>.Instance);

        var snapshot = await probe.GetAvailabilityAsync(ClaudeOpusMember, CancellationToken.None);

        Assert.Equal(-1, snapshot.AvailablePct);
        Assert.Contains("no token", snapshot.Notes ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void EndpointShapeChanges_ReturnUnknownInsteadOfThrowing()
    {
        var claude = ClaudeQuotaProbe.ParseResponse("""{"unexpected":true}""");
        var codex = CodexQuotaProbe.ParseResponse("""{"unexpected":true}""");
        var gemini = GeminiQuotaProbe.ParseResponse("""{"unexpected":true}""");

        Assert.All([claude, codex, gemini], snapshot =>
        {
            Assert.True(snapshot.AvailablePct < 0);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Notes));
        });
    }

    [Fact]
    public async Task ModelIdConfigured_RouterEvaluatesMatchingPerModelQuotaBeforeOverallQuota()
    {
        var router = BuildRouter(
            members:
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
                    ModelId = "gpt-5.5",
                    QualityScore = 99,
                },
            ],
            probes:
            [
                new StaticQuotaProbe(AgentKind.Claude, new AgentQuotaSnapshot
                {
                    AvailablePct = 80,
                    PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["claude-opus-4-7"] = new() { AvailablePct = 0, Window = "weekly" },
                    },
                }),
                new StaticQuotaProbe(AgentKind.Codex, new AgentQuotaSnapshot { AvailablePct = 60 }),
            ],
            options: new QuotaRouterOptions { MinQuotaPct = 10 });

        var decision = await router.ResolveAsync(Item("frontier"), null, CancellationToken.None);

        Assert.Equal(AgentKind.Codex, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task ProbeHttpRequestTimesOut_RouterFollowsUnknownPolicy()
    {
        var probe = new ClaudeQuotaProbe(
            new QuotaFakeHttpClientFactory("agent-quota", new QuotaThrowingHandler(new TimeoutException("quota timeout"))),
            token: "test-token",
            cacheTtl: TimeSpan.Zero,
            NullLogger<ClaudeQuotaProbe>.Instance);
        var router = BuildRouter(
            members:
            [
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                },
            ],
            probes: [probe],
            options: new QuotaRouterOptions
            {
                MinQuotaPct = 10,
                UnknownPolicy = QuotaUnknownPolicy.FailCautious,
            });

        var decision = await router.ResolveAsync(Item("frontier"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.Contains("below", decision.Reason);
    }

    [Fact]
    public async Task ProbeReportsMalformedAvailability_GateTreatsItAsUnknown()
    {
        var router = BuildRouter(
            members:
            [
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                },
            ],
            probes: [new StaticQuotaProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = -42, Notes = "negative availability" })],
            options: new QuotaRouterOptions
            {
                MinQuotaPct = 10,
                UnknownPolicy = QuotaUnknownPolicy.FailOpen,
            });

        var decision = await router.ResolveAsync(Item("frontier"), null, CancellationToken.None);

        Assert.Equal(AgentKind.Claude, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
        Assert.Contains("-42.0% available", decision.Reason);
    }

    [Fact]
    public async Task ProbeCacheTtl_ReusesSnapshotWithinTtl()
    {
        var calls = 0;
        var handler = new QuotaCapturingHandler(
            HttpStatusCode.OK,
            """{"rate_limit":{"primary_window":{"used_percent":25}}}""",
            _ => calls++);
        var probe = new ClaudeQuotaProbe(
            new QuotaFakeHttpClientFactory("agent-quota", handler),
            token: "test-token",
            cacheTtl: TimeSpan.FromMinutes(5),
            NullLogger<ClaudeQuotaProbe>.Instance);

        await probe.GetAvailabilityAsync(ClaudeOpusMember, CancellationToken.None);
        await probe.GetAvailabilityAsync(ClaudeOpusMember, CancellationToken.None);

        Assert.Equal(1, calls);
    }

    private static CodeyBox.Orchestrator.AgentClassRouter BuildRouter(
        AgentMembership[] members,
        IAgentQuotaProbe[] probes,
        QuotaRouterOptions options)
    {
        var agentClass = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = members,
        };

        return new CodeyBox.Orchestrator.AgentClassRouter(
            [agentClass],
            probes,
            options,
            NullLogger<CodeyBox.Orchestrator.AgentClassRouter>.Instance);
    }

    private static WorkItem Item(string agentClassId) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "Quota probe UAT",
        Prompt = "route this",
        AgentClassId = agentClassId,
    };

    private sealed class StaticQuotaProbe : IAgentQuotaProbe
    {
        private readonly AgentQuotaSnapshot _snapshot;

        public StaticQuotaProbe(AgentKind kind, AgentQuotaSnapshot snapshot)
        {
            Kind = kind;
            _snapshot = snapshot;
        }

        public AgentKind Kind { get; }
        public int CallCount { get; private set; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_snapshot);
        }
    }
}
