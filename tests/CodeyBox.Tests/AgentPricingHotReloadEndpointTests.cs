using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using CodeyBox.Agents;
using CodeyBox.Api;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage for <c>GET /agent-pricing</c> after the host applies a
/// new merged pricing snapshot (the same <see cref="AgentPricingState.ApplySuccessfulMerge"/>
/// path <see cref="AgentConfigHotReload"/> uses on hot-reload).
/// </summary>
public sealed class AgentPricingHotReloadEndpointTests : IClassFixture<AgentPricingHotReloadApiFactory>
{
    private readonly AgentPricingHotReloadApiFactory _factory;

    public AgentPricingHotReloadEndpointTests(AgentPricingHotReloadApiFactory factory) =>
        _factory = factory;

    [Fact]
    public async Task Get_AfterSuccessfulMerge_ReflectsUpdatedCountsAndRates()
    {
        var client = _factory.CreateClient();

        var before = await client.GetFromJsonAsync<JsonElement>("/agent-pricing");
        var beforeCounts = before.GetProperty("meta").GetProperty("counts");
        Assert.Equal(0, beforeCounts.GetProperty("operatorOverrides").GetInt32());
        Assert.Equal(0, beforeCounts.GetProperty("overlap").GetInt32());
        Assert.True(beforeCounts.GetProperty("bundled").GetInt32() > 0);

        using (var scope = _factory.Services.CreateScope())
        {
            var state = scope.ServiceProvider.GetRequiredService<AgentPricingState>();
            var calculator = scope.ServiceProvider.GetRequiredService<AgentCostCalculator>();
            var operatorOpts = new AgentPricingOptions
            {
                Rates = new()
                {
                    ["claude"] = new()
                    {
                        ["claude-opus-4-7"] = new ModelRateConfig
                        {
                            InputPerMillion = 99.0,
                            CachedInputPerMillion = 9.9,
                            OutputPerMillion = 990.0,
                        },
                    },
                },
            };
            var merged = AgentPricingMerge.Merge(state.Defaults.Baseline, operatorOpts);
            state.ApplySuccessfulMerge(merged, calculator);
        }

        var after = await client.GetFromJsonAsync<JsonElement>("/agent-pricing");
        var afterCounts = after.GetProperty("meta").GetProperty("counts");
        Assert.Equal(1, afterCounts.GetProperty("operatorOverrides").GetInt32());
        Assert.Equal(1, afterCounts.GetProperty("overlap").GetInt32());
        Assert.Equal(beforeCounts.GetProperty("bundled").GetInt32(), afterCounts.GetProperty("bundled").GetInt32());

        var opus = after.GetProperty("rates").GetProperty("claude").GetProperty("claude-opus-4-7");
        Assert.Equal(99.0, opus.GetProperty("inputPerMillion").GetDouble());
        Assert.Equal(990.0, opus.GetProperty("outputPerMillion").GetDouble());

        var haiku = after.GetProperty("rates").GetProperty("claude").GetProperty("claude-haiku-4-5");
        Assert.Equal(1.0, haiku.GetProperty("inputPerMillion").GetDouble());
    }
}

public sealed class AgentPricingHotReloadApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-pricing-hotreload-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var jsonSources = cfg.Sources
                .OfType<JsonConfigurationSource>()
                .Where(s => (s.Path ?? string.Empty).Contains("appsettings", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var s in jsonSources) cfg.Sources.Remove(s);

            var tmp = Path.GetTempPath();
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
            });
        });
        builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}
