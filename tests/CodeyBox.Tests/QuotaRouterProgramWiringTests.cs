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
        Assert.Equal(1.75, options.DrainAggressiveness);
        Assert.Equal(TimeSpan.FromSeconds(2), options.QuotaRecoveryProbeInterval);
        Assert.True(options.ExpectedResets.TryGetValue("CODEX", out var codexReset));
        Assert.Equal(TimeSpan.FromDays(7), codexReset.Cadence);
        Assert.Equal(
            new DateTimeOffset(2026, 6, 1, 0, 20, 0, TimeSpan.Zero),
            codexReset.CadenceAnchor);
        Assert.Contains(
            new DateTimeOffset(2026, 6, 1, 0, 20, 0, TimeSpan.Zero),
            codexReset.Timestamps);
    }

    [Fact]
    public void Mapper_DropsInvalidFloorByAgentEntriesAndFields()
    {
        var config = new QuotaRouterConfig
        {
            FloorByAgent = new(StringComparer.OrdinalIgnoreCase)
            {
                ["valid"] = new QuotaRouterFloorConfig
                {
                    MinQuotaPct = 1.0,
                    StartFloorPct = 2.0,
                    EndFloorPct = 0.0,
                    RampWindowSeconds = 60,
                },
                [" "] = new QuotaRouterFloorConfig { MinQuotaPct = 2.0 },
                ["null-entry"] = null!,
                ["empty"] = new QuotaRouterFloorConfig(),
                ["negative-only"] = new QuotaRouterFloorConfig
                {
                    MinQuotaPct = -1.0,
                    StartFloorPct = -2.0,
                    EndFloorPct = -3.0,
                },
                ["zero-window-only"] = new QuotaRouterFloorConfig { RampWindowSeconds = 0 },
                ["negative-window-only"] = new QuotaRouterFloorConfig { RampWindowSeconds = -60 },
                ["mixed"] = new QuotaRouterFloorConfig
                {
                    MinQuotaPct = -1.0,
                    StartFloorPct = 4.0,
                    RampWindowSeconds = 0,
                },
            },
        };

        var options = QuotaRouterConfigMapper.ToOptions(config);

        Assert.True(options.FloorByAgent.TryGetValue("valid", out var valid));
        Assert.Equal(1.0, valid.MinQuotaPct);
        Assert.Equal(2.0, valid.StartFloorPct);
        Assert.Equal(0.0, valid.EndFloorPct);
        Assert.Equal(TimeSpan.FromSeconds(60), valid.RampWindow);

        Assert.True(options.FloorByAgent.TryGetValue("mixed", out var mixed));
        Assert.Null(mixed.MinQuotaPct);
        Assert.Equal(4.0, mixed.StartFloorPct);
        Assert.Null(mixed.RampWindow);

        Assert.DoesNotContain(" ", options.FloorByAgent.Keys);
        Assert.DoesNotContain("null-entry", options.FloorByAgent.Keys);
        Assert.DoesNotContain("empty", options.FloorByAgent.Keys);
        Assert.DoesNotContain("negative-only", options.FloorByAgent.Keys);
        Assert.DoesNotContain("zero-window-only", options.FloorByAgent.Keys);
        Assert.DoesNotContain("negative-window-only", options.FloorByAgent.Keys);
    }

    [Fact]
    public void HotReloadMapper_DropsInvalidFloorByAgentEntries()
    {
        var options = new QuotaRouterOptions
        {
            FloorByAgent = new(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new QuotaFloorOverrideOptions { MinQuotaPct = 1.0 },
            },
        };
        var config = new QuotaRouterConfig
        {
            FloorByAgent = new(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new QuotaRouterFloorConfig(),
                ["claude"] = new QuotaRouterFloorConfig { MinQuotaPct = -1.0 },
                ["opencode"] = new QuotaRouterFloorConfig { RampWindowSeconds = 0 },
                ["gemini"] = new QuotaRouterFloorConfig { EndFloorPct = 0.0 },
            },
        };

        QuotaRouterConfigMapper.ApplyHotReload(options, config);

        var gemini = Assert.Single(options.FloorByAgent);
        Assert.Equal("gemini", gemini.Key);
        Assert.Equal(0.0, gemini.Value.EndFloorPct);
        Assert.Null(gemini.Value.MinQuotaPct);
        Assert.Null(gemini.Value.RampWindow);
    }

    [Fact]
    public void Mapper_DropsInvalidExpectedResetEntriesAndFields()
    {
        var timestamp = new DateTimeOffset(2026, 6, 1, 0, 20, 0, TimeSpan.Zero);
        var config = new QuotaRouterConfig
        {
            ExpectedResets = new(StringComparer.OrdinalIgnoreCase)
            {
                ["valid"] = new QuotaRouterExpectedResetConfig
                {
                    Timestamps = [timestamp],
                    CadenceSeconds = 3600,
                    CadenceAnchor = timestamp,
                },
                ["timestamps-only"] = new QuotaRouterExpectedResetConfig
                {
                    Timestamps = [timestamp.AddDays(1)],
                },
                ["cadence-without-anchor"] = new QuotaRouterExpectedResetConfig
                {
                    CadenceSeconds = 3600,
                },
                ["anchor-without-cadence"] = new QuotaRouterExpectedResetConfig
                {
                    CadenceAnchor = timestamp,
                },
                ["bad-cadence"] = new QuotaRouterExpectedResetConfig
                {
                    CadenceSeconds = 0,
                    CadenceAnchor = timestamp,
                },
                [" "] = new QuotaRouterExpectedResetConfig { Timestamps = [timestamp] },
                ["null-entry"] = null!,
            },
        };

        var options = QuotaRouterConfigMapper.ToOptions(config);

        Assert.True(options.ExpectedResets.TryGetValue("valid", out var valid));
        Assert.Equal(TimeSpan.FromHours(1), valid.Cadence);
        Assert.Equal(timestamp, valid.CadenceAnchor);
        Assert.Equal([timestamp], valid.Timestamps);
        Assert.True(options.ExpectedResets.TryGetValue("timestamps-only", out var timestampsOnly));
        Assert.Null(timestampsOnly.Cadence);
        Assert.Single(timestampsOnly.Timestamps);
        Assert.DoesNotContain("cadence-without-anchor", options.ExpectedResets.Keys);
        Assert.DoesNotContain("anchor-without-cadence", options.ExpectedResets.Keys);
        Assert.DoesNotContain("bad-cadence", options.ExpectedResets.Keys);
        Assert.DoesNotContain(" ", options.ExpectedResets.Keys);
        Assert.DoesNotContain("null-entry", options.ExpectedResets.Keys);
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
                    ["CodeyBox:QuotaRouter:QuotaRecoveryProbeIntervalSeconds"] = "2",
                    ["CodeyBox:QuotaRouter:DrainAggressiveness"] = "1.75",
                    ["CodeyBox:QuotaRouter:ExpectedResets:codex:Timestamps:0"] = "2026-06-01T00:20:00Z",
                    ["CodeyBox:QuotaRouter:ExpectedResets:codex:CadenceSeconds"] = "604800",
                    ["CodeyBox:QuotaRouter:ExpectedResets:codex:CadenceAnchor"] = "2026-06-01T00:20:00Z",
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
