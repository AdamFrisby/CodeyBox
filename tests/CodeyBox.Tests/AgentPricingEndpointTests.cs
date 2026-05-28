using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using CodeyBox.Agents;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level coverage for <c>GET /agent-pricing</c>. Exercises the full
/// response shape — <c>meta</c> (lastUpdated, sources, notes, counts),
/// <c>rates</c>, <c>defaultRates</c> — so a regression in the endpoint's JSON
/// surface (e.g., counts nested in the wrong block, operator-only data
/// returned without the bundled merge, sources/notes swapped) gets caught.
/// The bundled <see cref="AgentPricingDefaultsSnapshot"/> is replaced with a
/// known fixture so assertions don't drift with the shipped
/// <c>agent-pricing-defaults.json</c>.
/// </summary>
public sealed class AgentPricingEndpointTests : IClassFixture<AgentPricingApiFactory>
{
    private readonly AgentPricingApiFactory _factory;

    public AgentPricingEndpointTests(AgentPricingApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_ReturnsMergedRates_BundledCarriedThroughWithOperatorOverride()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/agent-pricing");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var meta = body.GetProperty("meta");
        Assert.Equal("2099-01-01", meta.GetProperty("lastUpdated").GetString());
        Assert.Equal(
            "https://example.invalid/claude-pricing",
            meta.GetProperty("sources").GetProperty("claude").GetString());
        Assert.Equal(
            "fixture note for claude",
            meta.GetProperty("notes").GetProperty("claude").GetString());
        Assert.Equal(AgentPricingDefaults.FileName, meta.GetProperty("bundledFile").GetString());
        Assert.Contains("agent-pricing-defaults.json", meta.GetProperty("sourcePath").GetString());

        var counts = meta.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("bundled").GetInt32());
        Assert.Equal(2, counts.GetProperty("operatorOverrides").GetInt32());
        Assert.Equal(1, counts.GetProperty("overlap").GetInt32());
        Assert.Equal(3, counts.GetProperty("total").GetInt32());

        var rates = body.GetProperty("rates");
        var opus = rates.GetProperty("claude").GetProperty("claude-opus-4-7");
        Assert.Equal(99.0, opus.GetProperty("inputPerMillion").GetDouble());
        Assert.Equal(990.0, opus.GetProperty("outputPerMillion").GetDouble());
        var haiku = rates.GetProperty("claude").GetProperty("claude-haiku-4-5");
        Assert.Equal(1.0, haiku.GetProperty("inputPerMillion").GetDouble());
        var deepseek = rates.GetProperty("opencode").GetProperty("opencode-go/deepseek-v4-pro");
        Assert.Equal(0.27, deepseek.GetProperty("inputPerMillion").GetDouble());

        var defaults = body.GetProperty("defaultRates");
        Assert.Equal(5.0, defaults.GetProperty("codex").GetProperty("inputPerMillion").GetDouble());
    }
}

public sealed class AgentPricingApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-pricing-httptest-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var jsonSources = cfg.Sources
                .OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>()
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

                ["CodeyBox:AgentPricing:Rates:claude:claude-opus-4-7:inputPerMillion"] = "99.0",
                ["CodeyBox:AgentPricing:Rates:claude:claude-opus-4-7:cachedInputPerMillion"] = "9.9",
                ["CodeyBox:AgentPricing:Rates:claude:claude-opus-4-7:outputPerMillion"] = "990.0",
                ["CodeyBox:AgentPricing:Rates:opencode:opencode-go/deepseek-v4-pro:inputPerMillion"] = "0.27",
                ["CodeyBox:AgentPricing:Rates:opencode:opencode-go/deepseek-v4-pro:cachedInputPerMillion"] = "0.07",
                ["CodeyBox:AgentPricing:Rates:opencode:opencode-go/deepseek-v4-pro:outputPerMillion"] = "1.10",
                ["CodeyBox:AgentPricing:DefaultRates:codex:inputPerMillion"] = "5.0",
                ["CodeyBox:AgentPricing:DefaultRates:codex:cachedInputPerMillion"] = "0.5",
                ["CodeyBox:AgentPricing:DefaultRates:codex:outputPerMillion"] = "30.0",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            var baseline = new AgentPricingOptions
            {
                Rates = new Dictionary<string, Dictionary<string, ModelRateConfig>>(StringComparer.Ordinal)
                {
                    ["claude"] = new(StringComparer.Ordinal)
                    {
                        ["claude-opus-4-7"] = new()
                        {
                            InputPerMillion = 5.0,
                            CachedInputPerMillion = 0.5,
                            OutputPerMillion = 25.0,
                        },
                        ["claude-haiku-4-5"] = new()
                        {
                            InputPerMillion = 1.0,
                            CachedInputPerMillion = 0.1,
                            OutputPerMillion = 5.0,
                        },
                    },
                },
            };
            var defaultsSnapshot = new AgentPricingDefaultsSnapshot
            {
                Meta = new AgentPricingDefaultsMeta
                {
                    LastUpdated = "2099-01-01",
                    Sources = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["claude"] = "https://example.invalid/claude-pricing",
                    },
                    Notes = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["claude"] = "fixture note for claude",
                    },
                },
                SourcePath = Path.Combine(Path.GetTempPath(), AgentPricingDefaults.FileName),
                Baseline = baseline,
            };

            services.RemoveAll<AgentPricingDefaultsSnapshot>();
            services.RemoveAll<AgentPricingState>();
            services.RemoveAll<AgentCostCalculator>();
            services.AddSingleton(defaultsSnapshot);
            services.AddSingleton(sp =>
            {
                var snapshot = sp.GetRequiredService<AgentPricingDefaultsSnapshot>();
                var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CodeyBoxOptions>>().Value;
                var merged = AgentPricingMerge.Merge(snapshot.Baseline, opts.AgentPricing);
                return new AgentPricingState(snapshot, merged);
            });
            services.AddSingleton(sp =>
            {
                var state = sp.GetRequiredService<AgentPricingState>();
                var extractors = sp.GetRequiredService<IReadOnlyDictionary<AgentKind, IAgentCostExtractor>>();
                return new AgentCostCalculator(state.LastMerge.Options, extractors);
            });
        });
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
