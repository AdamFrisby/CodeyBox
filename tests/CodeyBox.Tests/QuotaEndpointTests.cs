using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
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
}
