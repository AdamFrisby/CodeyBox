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

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level coverage for <c>GET /agent-pricing</c>. Exercises the full
/// response shape — <c>meta</c> (lastUpdated, sources, notes, counts),
/// <c>rates</c>, <c>defaultRates</c> — so a regression in the endpoint's JSON
/// surface (e.g., counts nested in the wrong block, operator-only data
/// returned without the bundled merge, sources/notes swapped) gets caught.
/// The bundled <see cref="BundledAgentPricing"/> is replaced with a known
/// fixture so assertions don't drift with the shipped <c>agent-pricing-defaults.json</c>.
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

        // _meta block has the fixture's lastUpdated, sources, notes, sourcePath.
        var meta = body.GetProperty("meta");
        Assert.Equal("2099-01-01", meta.GetProperty("lastUpdated").GetString());
        Assert.Equal(
            "https://example.invalid/claude-pricing",
            meta.GetProperty("sources").GetProperty("claude").GetString());
        Assert.Equal(
            "fixture note for claude",
            meta.GetProperty("notes").GetProperty("claude").GetString());
        Assert.EndsWith(AgentPricingDefaults.FileName, meta.GetProperty("sourcePath").GetString());

        // counts: bundled=2, operator-overrides=2 (one overlap, one new agent), overlap=1, total=3.
        var counts = meta.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("bundled").GetInt32());
        Assert.Equal(2, counts.GetProperty("operatorOverrides").GetInt32());
        Assert.Equal(1, counts.GetProperty("overlap").GetInt32());
        Assert.Equal(3, counts.GetProperty("total").GetInt32());

        // rates merged: claude-opus-4-7 operator-overridden, claude-haiku-4-5 from
        // bundled, opencode bucket from operator.
        var rates = body.GetProperty("rates");
        var opus = rates.GetProperty("claude").GetProperty("claude-opus-4-7");
        Assert.Equal(99.0, opus.GetProperty("inputPerMillion").GetDouble());
        Assert.Equal(990.0, opus.GetProperty("outputPerMillion").GetDouble());
        var haiku = rates.GetProperty("claude").GetProperty("claude-haiku-4-5");
        Assert.Equal(1.0, haiku.GetProperty("inputPerMillion").GetDouble());
        var deepseek = rates.GetProperty("opencode").GetProperty("deepseek-v4-pro");
        Assert.Equal(0.27, deepseek.GetProperty("inputPerMillion").GetDouble());

        // operator DefaultRates passes through unchanged.
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
            // Drop the appsettings.json source so its operator-side
            // AgentPricing block doesn't leak into the count assertions.
            // RetainingOptionsMonitorCache pre-populates from the raw
            // IConfiguration bind, bypassing Configure/PostConfigure, so this
            // is the only reliable way to control the operator snapshot.
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

                // Operator-side AgentPricing: one entry overlaps the bundled
                // fixture (claude-opus-4-7), one new agent bucket (opencode),
                // plus a DefaultRates entry so the endpoint's full response
                // shape is exercised.
                ["CodeyBox:AgentPricing:Rates:claude:claude-opus-4-7:inputPerMillion"] = "99.0",
                ["CodeyBox:AgentPricing:Rates:claude:claude-opus-4-7:cachedInputPerMillion"] = "9.9",
                ["CodeyBox:AgentPricing:Rates:claude:claude-opus-4-7:outputPerMillion"] = "990.0",
                ["CodeyBox:AgentPricing:Rates:opencode:deepseek-v4-pro:inputPerMillion"] = "0.27",
                ["CodeyBox:AgentPricing:Rates:opencode:deepseek-v4-pro:cachedInputPerMillion"] = "0.07",
                ["CodeyBox:AgentPricing:Rates:opencode:deepseek-v4-pro:outputPerMillion"] = "1.10",
                ["CodeyBox:AgentPricing:DefaultRates:codex:inputPerMillion"] = "5.0",
                ["CodeyBox:AgentPricing:DefaultRates:codex:cachedInputPerMillion"] = "0.5",
                ["CodeyBox:AgentPricing:DefaultRates:codex:outputPerMillion"] = "30.0",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            // Replace the bundled defaults with a deterministic fixture so the
            // shipped agent-pricing-defaults.json isn't load-bearing for these
            // assertions (the file's price values drift independently).
            services.RemoveAll<BundledAgentPricing>();
            services.AddSingleton(new BundledAgentPricing
            {
                Meta = new BundledAgentPricingMeta
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
                SourcePath = Path.Combine(
                    Path.GetTempPath(),
                    $"fixture-{Guid.NewGuid():N}",
                    AgentPricingDefaults.FileName),
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
