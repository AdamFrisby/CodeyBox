using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class QuotaEndpointTests
{
    [Fact]
    public async Task GetQuota_ReturnsSnapshotsPerModelFailuresAndWouldAllow()
    {
        using var factory = new WorkItemApiFactory();
        var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentQuotaProbe>();
                services.AddSingleton<IAgentQuotaProbe>(new FakeProbe(AgentKind.Claude, new AgentQuotaSnapshot
                {
                    AvailablePct = 60,
                    PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["claude-opus-4-7"] = new() { AvailablePct = 0, Window = "weekly" },
                    },
                }));
            });
        }).CreateClient();

        var response = await client.GetAsync("/quota");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var root = doc.RootElement;
        Assert.Equal("UseObservedFailures", root.GetProperty("unknownPolicy").GetString());
        var probe = root.GetProperty("probes")[0];
        Assert.Equal("claude", probe.GetProperty("agent").GetString());
        Assert.True(probe.GetProperty("wouldAllow").GetBoolean());
        Assert.False(probe.GetProperty("perModelWouldAllow").GetProperty("claude-opus-4-7").GetBoolean());
        Assert.True(probe.TryGetProperty("observedFailuresLast60m", out _));
    }

    [Fact]
    public async Task GetQuota_ExposesResetCreditsAndRawPerWindowFields()
    {
        var reset = DateTimeOffset.FromUnixTimeSeconds(1778091218);
        using var factory = new WorkItemApiFactory();
        var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentQuotaProbe>();
                services.AddSingleton<IAgentQuotaProbe>(new FakeProbe(AgentKind.Codex, new AgentQuotaSnapshot
                {
                    AvailablePct = 63,
                    ResetCreditsAvailable = 3,
                    Windows =
                    [
                        new WindowQuota
                        {
                            Name = "5h-rolling",
                            AvailablePct = 66,
                            ResetAt = reset,
                            UsedPercent = 34,
                            ResetAtEpochSeconds = 1778091218,
                        },
                    ],
                }));
            });
        }).CreateClient();

        var response = await client.GetAsync("/quota");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var snapshot = doc.RootElement.GetProperty("probes")[0].GetProperty("latestSnapshot");
        Assert.Equal(3, snapshot.GetProperty("resetCreditsAvailable").GetInt32());
        var window = snapshot.GetProperty("windows")[0];
        Assert.Equal(34, window.GetProperty("usedPercent").GetDouble());
        Assert.Equal(1778091218, window.GetProperty("resetAtEpochSeconds").GetInt64());
    }

    [Fact]
    public async Task GetQuota_ModelSpecificObservedFailureAffectsWouldAllowEvenWhenProbeOmitsModel()
    {
        using var factory = new WorkItemApiFactory();
        var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentQuotaProbe>();
                services.AddSingleton<IAgentQuotaProbe>(new FakeProbe(AgentKind.Claude, new AgentQuotaSnapshot
                {
                    AvailablePct = 60,
                }));
            });
        });
        var client = configuredFactory.CreateClient();
        var failures = configuredFactory.Services.GetRequiredService<IQuotaFailureStore>();
        await failures.RecordForProjectAsync(
            AgentKind.Claude,
            "claude-opus-4-7",
            new ProjectId("test-project"),
            QuotaFailureKind.LimitReached,
            DateTimeOffset.UtcNow);

        var response = await client.GetAsync("/quota");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var probe = doc.RootElement.GetProperty("probes")[0];
        Assert.False(probe.GetProperty("wouldAllow").GetBoolean());
        Assert.True(probe.GetProperty("defaultModelWouldAllow").GetBoolean());
        Assert.False(probe.GetProperty("perModelWouldAllow").GetProperty("claude-opus-4-7").GetBoolean());
    }

    [Fact]
    public async Task GetQuota_WouldAllowUsesPerAgentFloorPolicy()
    {
        var reset = DateTimeOffset.UtcNow + TimeSpan.FromDays(7);
        var options = new QuotaRouterOptions
        {
            MinQuotaPct = 10.0,
            StartFloorPct = 25.0,
            EndFloorPct = 3.0,
            RampWindow = TimeSpan.FromDays(7),
        };
        options.FloorByAgent[AgentKind.Codex.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 1.0,
            StartFloorPct = 1.0,
            EndFloorPct = 0.0,
        };

        using var factory = new WorkItemApiFactory();
        var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentQuotaProbe>();
                services.AddSingleton<IAgentQuotaProbe>(new FakeProbe(AgentKind.Codex, new AgentQuotaSnapshot
                {
                    AvailablePct = 5.0,
                    ResetAt = reset,
                }));
                services.AddSingleton<IAgentQuotaProbe>(new FakeProbe(AgentKind.Claude, new AgentQuotaSnapshot
                {
                    AvailablePct = 20.0,
                    ResetAt = reset,
                }));
                services.RemoveAll<QuotaRouterOptions>();
                services.AddSingleton(options);
                services.RemoveAll<QuotaGatePolicy>();
                services.AddSingleton(new QuotaGatePolicy(options));
            });
        }).CreateClient();

        var response = await client.GetAsync("/quota");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var probes = doc.RootElement.GetProperty("probes").EnumerateArray().ToList();
        Assert.All(
            probes.Where(p => string.Equals(p.GetProperty("agent").GetString(), "codex", StringComparison.OrdinalIgnoreCase)),
            p => Assert.True(p.GetProperty("wouldAllow").GetBoolean()));
        Assert.All(
            probes.Where(p => string.Equals(p.GetProperty("agent").GetString(), "claude", StringComparison.OrdinalIgnoreCase)),
            p => Assert.False(p.GetProperty("wouldAllow").GetBoolean()));
    }

    [Fact]
    public async Task GetQuota_IncludesBudgetsArray_WhenProviderConfigured()
    {
        using var factory = new WorkItemApiFactory();
        var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentBudgetProvider>();
                services.AddSingleton<IAgentBudgetProvider>(new FakeBudgetProvider());
            });
        }).CreateClient();

        var response = await client.GetAsync("/quota");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var root = doc.RootElement;
        Assert.False(root.GetProperty("budgetsError").GetBoolean());
        var budget = root.GetProperty("budgets")[0];
        Assert.Equal("opencode", budget.GetProperty("agent").GetString());
        Assert.Equal("opencode-go/deepseek-v4-pro", budget.GetProperty("model").GetString());
        var window = budget.GetProperty("windows")[0];
        Assert.Equal("Rolling", window.GetProperty("kind").GetString());
        Assert.Equal(5, window.GetProperty("hours").GetInt32());
        Assert.Equal(16, window.GetProperty("usedCents").GetInt64());
        Assert.Equal(200, window.GetProperty("limitCents").GetInt64());
        Assert.Equal(92, window.GetProperty("percentRemaining").GetDouble());
    }

    [Fact]
    public async Task GetQuota_BudgetProviderThrows_SetsBudgetsError()
    {
        using var factory = new WorkItemApiFactory();
        var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentBudgetProvider>();
                services.AddSingleton<IAgentBudgetProvider>(new ThrowingBudgetProvider());
            });
        }).CreateClient();

        var response = await client.GetAsync("/quota");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var root = doc.RootElement;
        // Failures are surfaced, not masqueraded as "no budgets configured".
        Assert.True(root.GetProperty("budgetsError").GetBoolean());
        Assert.Empty(root.GetProperty("budgets").EnumerateArray());
    }

    [Fact]
    public async Task GetQuota_IncludesConfiguredInstancesSeparatelyAndKindAggregate()
    {
        using var factory = new WorkItemApiFactory();
        var resetA = DateTimeOffset.UtcNow.AddMinutes(45);
        var resetB = DateTimeOffset.UtcNow.AddMinutes(90);
        var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:QuotaRouter:IntraKindRoutingPolicy"] = "RoundRobin",
                    ["CodeyBox:AgentClasses:0:Id"] = "frontier",
                    ["CodeyBox:AgentClasses:0:DisplayName"] = "Frontier",
                    ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
                    ["CodeyBox:AgentClasses:0:Members:0:InstanceId"] = "acct-a",
                    ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
                    ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
                    ["CodeyBox:AgentClasses:0:Members:1:Agent"] = "claude",
                    ["CodeyBox:AgentClasses:0:Members:1:InstanceId"] = "acct-b",
                    ["CodeyBox:AgentClasses:0:Members:1:Billing"] = "Subscription",
                    ["CodeyBox:AgentClasses:0:Members:1:QualityScore"] = "99",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentQuotaProbe>();
                services.AddSingleton<IAgentQuotaProbe>(new InstanceSnapshotProbe(
                    AgentKind.Claude,
                    new Dictionary<string, AgentQuotaSnapshot>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["claude/acct-a"] = new()
                        {
                            AvailablePct = 20,
                            ResetAt = resetA,
                            Windows = [new WindowQuota { Name = "five_hour", AvailablePct = 20, ResetAt = resetA }],
                        },
                        ["claude/acct-b"] = new()
                        {
                            AvailablePct = 80,
                            ResetAt = resetB,
                            Windows = [new WindowQuota { Name = "five_hour", AvailablePct = 80, ResetAt = resetB }],
                        },
                    }));
            });
        }).CreateClient();

        var response = await client.GetAsync("/quota");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var root = doc.RootElement;
        Assert.Equal("RoundRobin", root.GetProperty("intraKindRoutingPolicy").GetString());

        var claudeRows = root.GetProperty("probes")
            .EnumerateArray()
            .Where(p => p.GetProperty("agent").GetString() == "claude")
            .ToList();
        Assert.Equal(2, claudeRows.Count);

        var acctA = Assert.Single(claudeRows, p => p.GetProperty("agentInstanceId").GetString() == "claude/acct-a");
        Assert.Equal("acct-a", acctA.GetProperty("instanceId").GetString());
        Assert.Equal("frontier", acctA.GetProperty("classId").GetString());
        Assert.Equal(20, acctA.GetProperty("latestSnapshot").GetProperty("availablePct").GetDouble());
        Assert.Equal("five_hour", acctA.GetProperty("latestSnapshot").GetProperty("windows")[0].GetProperty("name").GetString());

        var acctB = Assert.Single(claudeRows, p => p.GetProperty("agentInstanceId").GetString() == "claude/acct-b");
        Assert.Equal("acct-b", acctB.GetProperty("instanceId").GetString());
        Assert.Equal(80, acctB.GetProperty("latestSnapshot").GetProperty("availablePct").GetDouble());

        var aggregate = Assert.Single(root.GetProperty("kindAggregates").EnumerateArray(), a =>
            a.GetProperty("agent").GetString() == "claude");
        Assert.Equal(2, aggregate.GetProperty("instances").GetInt32());
    }

    private sealed class FakeBudgetProvider : IAgentBudgetProvider
    {
        public Task<AgentQuotaSnapshot?> GetBudgetSnapshotAsync(AgentKind agent, string? modelId, CancellationToken ct = default)
            => Task.FromResult<AgentQuotaSnapshot?>(null);

        public Task<IReadOnlyList<AgentBudgetUsageView>> SummariseAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentBudgetUsageView>>(
            [
                new AgentBudgetUsageView("opencode", "opencode-go/deepseek-v4-pro",
                [
                    new BudgetWindowUsage("Rolling", 5, UsedCents: 16, LimitCents: 200, PercentRemaining: 92, ResetAt: null),
                ]),
            ]);
    }

    private sealed class ThrowingBudgetProvider : IAgentBudgetProvider
    {
        public Task<AgentQuotaSnapshot?> GetBudgetSnapshotAsync(AgentKind agent, string? modelId, CancellationToken ct = default)
            => Task.FromResult<AgentQuotaSnapshot?>(null);

        public Task<IReadOnlyList<AgentBudgetUsageView>> SummariseAllAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("budget summarisation failed");
    }

    private sealed class InstanceSnapshotProbe : IAgentQuotaProbe
    {
        private readonly IReadOnlyDictionary<string, AgentQuotaSnapshot> _snapshotsByRoute;

        public InstanceSnapshotProbe(
            AgentKind kind,
            IReadOnlyDictionary<string, AgentQuotaSnapshot> snapshotsByRoute)
        {
            Kind = kind;
            _snapshotsByRoute = snapshotsByRoute;
        }

        public AgentKind Kind { get; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(_snapshotsByRoute.TryGetValue(member.RouteKey, out var snapshot)
                ? snapshot
                : AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient));
    }
}
