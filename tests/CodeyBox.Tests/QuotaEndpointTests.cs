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
}
