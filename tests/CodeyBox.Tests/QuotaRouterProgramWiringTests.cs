using CodeyBox.Api;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

public sealed class QuotaRouterProgramWiringTests
{
    [Fact]
    public void StartupBindsQuotaRouterFloorByAgentFromConfiguration()
    {
        using var factory = new QuotaRouterWiringFactory();

        var options = factory.Services.GetRequiredService<QuotaRouterOptions>();

        Assert.True(options.FloorByAgent.TryGetValue("CODEX", out var codexFloor));
        Assert.NotNull(codexFloor);
        Assert.Equal(1.0, codexFloor.MinQuotaPct);
        Assert.Equal(1.0, codexFloor.StartFloorPct);
        Assert.Equal(0.0, codexFloor.EndFloorPct);
        Assert.Equal(TimeSpan.FromDays(1), codexFloor.RampWindow);
    }

    private sealed class QuotaRouterWiringFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-quota-router-wiring-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Path.GetTempPath();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                    ["CodeyBox:QuotaRouter:FloorByAgent:codex:MinQuotaPct"] = "1",
                    ["CodeyBox:QuotaRouter:FloorByAgent:codex:StartFloorPct"] = "1",
                    ["CodeyBox:QuotaRouter:FloorByAgent:codex:EndFloorPct"] = "0",
                    ["CodeyBox:QuotaRouter:FloorByAgent:codex:RampWindowSeconds"] = "86400",
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
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }
}
