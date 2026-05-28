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
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the production host wires bundled defaults into
/// <see cref="AgentCostCalculator"/> at startup (not only via GET /agent-pricing).
/// </summary>
public sealed class AgentPricingStartupTests : IClassFixture<AgentPricingStartupFactory>
{
    private readonly AgentPricingStartupFactory _factory;

    public AgentPricingStartupTests(AgentPricingStartupFactory factory) => _factory = factory;

    [Fact]
    public void AgentCostCalculator_UsesBundledRates_WhenOperatorConfigEmpty()
    {
        using var scope = _factory.Services.CreateScope();
        var calculator = scope.ServiceProvider.GetRequiredService<AgentCostCalculator>();

        var snapshot = new AgentCostSnapshot(
            InputTokens: 1_000_000,
            CachedInputTokens: 0,
            OutputTokens: 0,
            ModelId: "claude-opus-4-7");

        var cost = calculator.Calculate(snapshot, AgentKind.Claude);

        // Bundled shipped rate: $5/M input — not the old appsettings $15/M shadow.
        Assert.Equal(5.0m, cost);
    }

    [Fact]
    public void AgentCostCalculator_UsesBundledCodexCliModelId()
    {
        using var scope = _factory.Services.CreateScope();
        var calculator = scope.ServiceProvider.GetRequiredService<AgentCostCalculator>();

        var snapshot = new AgentCostSnapshot(
            InputTokens: 1_000_000,
            CachedInputTokens: 0,
            OutputTokens: 0,
            ModelId: "codex-5.5");

        var cost = calculator.Calculate(snapshot, AgentKind.Codex);

        Assert.Equal(5.0m, cost);
    }
}

/// <summary>
/// Exercises the production composition-root merge (bundled defaults + operator
/// config) without substituting <see cref="AgentPricingState"/> registration.
/// </summary>
public sealed class AgentPricingStartupOperatorOverrideTests
{
    [Fact]
    public void AgentCostCalculator_UsesOperatorOverride_WhenOperatorConfigSetAtStartup()
    {
        using var factory = new AgentPricingStartupOperatorFactory();
        using var scope = factory.Services.CreateScope();
        var calculator = scope.ServiceProvider.GetRequiredService<AgentCostCalculator>();

        var snapshot = new AgentCostSnapshot(
            InputTokens: 1_000_000,
            CachedInputTokens: 0,
            OutputTokens: 0,
            ModelId: "claude-opus-4-7");

        var cost = calculator.Calculate(snapshot, AgentKind.Claude);

        // Bundled shipped rate is $5/M; operator override in factory is $99/M.
        Assert.Equal(99.0m, cost);
    }
}

public sealed class AgentPricingStartupFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-pricing-startup-{Guid.NewGuid():N}.db");

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
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
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

public sealed class AgentPricingStartupOperatorFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-pricing-startup-op-{Guid.NewGuid():N}.db");

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

                ["CodeyBox:AgentPricing:Rates:claude:claude-opus-4-7:inputPerMillion"] = "99.0",
                ["CodeyBox:AgentPricing:Rates:claude:claude-opus-4-7:cachedInputPerMillion"] = "9.9",
                ["CodeyBox:AgentPricing:Rates:claude:claude-opus-4-7:outputPerMillion"] = "990.0",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
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
