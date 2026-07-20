using System.Net;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

public sealed class QuotaRouterProgramWiringTests
{
    private static readonly AgentMembership ClaudeMember = new()
    {
        Agent = AgentKind.Claude,
        Billing = AgentBilling.Subscription,
        QualityScore = 100,
    };

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
        Assert.Equal(17, options.MaxQuotaRecoveryProbeEligibilityScan);
        Assert.True(options.ExpectedResets.TryGetValue("CODEX", out var codexReset));
        Assert.Equal(TimeSpan.FromDays(7), codexReset.Cadence);
        Assert.Equal(
            new DateTimeOffset(2026, 6, 1, 0, 20, 0, TimeSpan.Zero),
            codexReset.CadenceAnchor);
        Assert.Contains(
            new DateTimeOffset(2026, 6, 1, 0, 20, 0, TimeSpan.Zero),
            codexReset.Timestamps);
        Assert.Equal(TimeSpan.FromMinutes(30), options.PausedQuotaCacheTtl);
        Assert.Equal(TimeSpan.FromMinutes(45), options.PausedProbeMaxStaleness);
        Assert.Equal(256, options.PausedQuotaMaxCacheEntries);
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

    [Fact]
    public void Mapper_BindsPausedProbeCadence()
    {
        var config = new QuotaRouterConfig
        {
            PausedQuotaCacheTtlSeconds = 1800,
            PausedProbeMaxStalenessSeconds = 2700,
            PausedQuotaMaxCacheEntries = 128,
        };

        var options = QuotaRouterConfigMapper.ToOptions(config);

        Assert.Equal(TimeSpan.FromMinutes(30), options.PausedQuotaCacheTtl);
        Assert.Equal(TimeSpan.FromMinutes(45), options.PausedProbeMaxStaleness);
        Assert.Equal(128, options.PausedQuotaMaxCacheEntries);

        QuotaRouterConfigMapper.ApplyHotReload(options, new QuotaRouterConfig
        {
            PausedQuotaCacheTtlSeconds = 3600,
            PausedProbeMaxStalenessSeconds = 5400,
            PausedQuotaMaxCacheEntries = 256,
        });

        Assert.Equal(TimeSpan.FromHours(1), options.PausedQuotaCacheTtl);
        Assert.Equal(TimeSpan.FromMinutes(90), options.PausedProbeMaxStaleness);
        Assert.Equal(256, options.PausedQuotaMaxCacheEntries);
    }

    [Fact]
    public void Mapper_DefaultPausedProbeCadence_IsHourlyWithLongerStaleness()
    {
        var options = QuotaRouterConfigMapper.ToOptions(new QuotaRouterConfig());

        Assert.Equal(TimeSpan.FromHours(1), options.PausedQuotaCacheTtl);
        Assert.Equal(TimeSpan.FromMinutes(90), options.PausedProbeMaxStaleness);
        Assert.Equal(1024, options.PausedQuotaMaxCacheEntries);
    }

    [Theory]
    [InlineData(0, 5400, 1024)]
    [InlineData(3600, 0, 1024)]
    [InlineData(3600, 3000, 1024)]
    [InlineData(3600, 5400, 0)]
    public void Mapper_RejectsInvalidPausedProbeCadence(
        int pausedTtlSeconds,
        int pausedMaxStalenessSeconds,
        int pausedMaxCacheEntries)
    {
        var config = new QuotaRouterConfig
        {
            PausedQuotaCacheTtlSeconds = pausedTtlSeconds,
            PausedProbeMaxStalenessSeconds = pausedMaxStalenessSeconds,
            PausedQuotaMaxCacheEntries = pausedMaxCacheEntries,
        };

        Assert.ThrowsAny<ArgumentException>(() => QuotaRouterConfigMapper.ToOptions(config));
    }

    [Fact]
    public async Task ProgramQuotaProbeStack_AppliesPausedCadenceButLeavesActiveCadence()
    {
        var time = new ManualTimeProvider();
        var handler = new CountingQuotaHandler(HttpStatusCode.Unauthorized);
        using var factory = new PausedQuotaCadenceWiringFactory(handler, time);
        var probe = factory.Services.GetServices<IAgentQuotaProbe>()
            .Single(p => p.Kind == AgentKind.Claude);
        var pauses = factory.Services.GetRequiredService<IAgentPauseController>();

        await pauses.PauseAsync(AgentKind.Claude, "reserve", "test");

        await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));
        await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);

        Assert.Equal(1, handler.CallCount);

        await pauses.ResumeAsync(AgentKind.Claude, "test");

        await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);
        await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);

        Assert.Equal(2, handler.CallCount);

        time.Advance(TimeSpan.FromSeconds(6));
        await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);

        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task ProgramQuotaProbeStack_ServesPausedRetainedReadingWithinPausedStaleness()
    {
        var time = new ManualTimeProvider();
        var handler = new RetryAfterSequenceHandler(
            new RetryAfterResponse(HttpStatusCode.OK, ClaudeRollup(25), null),
            new RetryAfterResponse(HttpStatusCode.TooManyRequests, "", null));
        using var factory = new PausedQuotaCadenceWiringFactory(handler, time);
        var probe = factory.Services.GetServices<IAgentQuotaProbe>()
            .Single(p => p.Kind == AgentKind.Claude);
        var pauses = factory.Services.GetRequiredService<IAgentPauseController>();

        await pauses.PauseAsync(AgentKind.Claude, "reserve", "test");

        var fresh = await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);
        Assert.Equal(75, fresh.AvailablePct, precision: 5);

        time.Advance(TimeSpan.FromMinutes(61));
        var stale = await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);

        Assert.Equal(75, stale.AvailablePct, precision: 5);
        Assert.Contains("stale", stale.Notes!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task ProgramQuotaProbeStack_HotReloadsPausedCadenceAndStalenessWithoutRestart()
    {
        var time = new ManualTimeProvider();
        var handler = new RetryAfterSequenceHandler(
            new RetryAfterResponse(HttpStatusCode.OK, ClaudeRollup(25), null),
            new RetryAfterResponse(HttpStatusCode.TooManyRequests, "", null),
            new RetryAfterResponse(HttpStatusCode.TooManyRequests, "", null));
        var monitor = new MutableOptionsMonitor<CodeyBoxOptions>(
            PausedCadenceOptions(pausedTtlSeconds: 3600, pausedMaxStalenessSeconds: 5400));
        using var factory = new PausedQuotaCadenceWiringFactory(handler, time, monitor);
        var hotReload = factory.Services.GetRequiredService<AgentConfigHotReload>();
        await hotReload.StartAsync(CancellationToken.None);
        try
        {
            var probe = factory.Services.GetServices<IAgentQuotaProbe>()
                .Single(p => p.Kind == AgentKind.Claude);
            var pauses = factory.Services.GetRequiredService<IAgentPauseController>();

            await pauses.PauseAsync(AgentKind.Claude, "reserve", "test");

            var fresh = await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);
            Assert.Equal(75, fresh.AvailablePct, precision: 5);
            Assert.Equal(1, handler.CallCount);

            monitor.Fire(PausedCadenceOptions(pausedTtlSeconds: 20, pausedMaxStalenessSeconds: 120));
            time.Advance(TimeSpan.FromSeconds(30));

            var stale = await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);
            Assert.Equal(75, stale.AvailablePct, precision: 5);
            Assert.Contains("stale", stale.Notes!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, handler.CallCount);

            monitor.Fire(PausedCadenceOptions(pausedTtlSeconds: 5, pausedMaxStalenessSeconds: 25));
            time.Advance(TimeSpan.FromSeconds(6));

            var expired = await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);
            Assert.False(expired.IsKnown);
            Assert.Equal(QuotaUnknownReason.Transient, expired.Unknown);
            Assert.Equal(3, handler.CallCount);
        }
        finally
        {
            await hotReload.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ProgramQuotaRouterHotReload_InvalidPausedCadenceKeepsPreviousLiveOptions()
    {
        var time = new ManualTimeProvider();
        var handler = new CountingQuotaHandler(HttpStatusCode.Unauthorized);
        var monitor = new MutableOptionsMonitor<CodeyBoxOptions>(
            PausedCadenceOptions(pausedTtlSeconds: 3600, pausedMaxStalenessSeconds: 5400));
        using var factory = new PausedQuotaCadenceWiringFactory(handler, time, monitor);
        var hotReload = factory.Services.GetRequiredService<AgentConfigHotReload>();
        await hotReload.StartAsync(CancellationToken.None);
        try
        {
            var options = factory.Services.GetRequiredService<QuotaRouterOptions>();
            Assert.Equal(TimeSpan.FromHours(1), options.PausedQuotaCacheTtl);
            Assert.Equal(TimeSpan.FromMinutes(90), options.PausedProbeMaxStaleness);

            var exception = Record.Exception(() =>
                monitor.Fire(PausedCadenceOptions(pausedTtlSeconds: 120, pausedMaxStalenessSeconds: 60)));

            Assert.Null(exception);
            Assert.Equal(TimeSpan.FromHours(1), options.PausedQuotaCacheTtl);
            Assert.Equal(TimeSpan.FromMinutes(90), options.PausedProbeMaxStaleness);

            monitor.Fire(PausedCadenceOptions(pausedTtlSeconds: 20, pausedMaxStalenessSeconds: 120));

            Assert.Equal(TimeSpan.FromSeconds(20), options.PausedQuotaCacheTtl);
            Assert.Equal(TimeSpan.FromSeconds(120), options.PausedProbeMaxStaleness);
        }
        finally
        {
            await hotReload.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ProgramClaudeQuotaProbe_RetryAfterCapReadsUpdatedQuotaRouterOptions()
    {
        var time = new CapturingDelayTimeProvider(DateTimeOffset.UtcNow);
        var handler = new RetryAfterSequenceHandler(
            new RetryAfterResponse(HttpStatusCode.TooManyRequests, "", TimeSpan.FromSeconds(10)),
            new RetryAfterResponse(HttpStatusCode.OK, ClaudeRollup(40), null),
            new RetryAfterResponse(HttpStatusCode.TooManyRequests, "", TimeSpan.FromSeconds(10)),
            new RetryAfterResponse(HttpStatusCode.OK, ClaudeRollup(30), null));
        var monitor = new MutableOptionsMonitor<CodeyBoxOptions>(ClaudeRetryOptions(maxRetryDelaySeconds: 1));
        using var factory = new ClaudeRetryCapWiringFactory(handler, time, monitor);
        var probe = factory.Services.GetServices<IAgentQuotaProbe>()
            .Single(p => p.Kind == AgentKind.Claude);

        var first = await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);
        Assert.Equal(60, first.AvailablePct, precision: 5);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(time.Delays));

        monitor.Set(ClaudeRetryOptions(maxRetryDelaySeconds: 3));

        var second = await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);
        Assert.Equal(70, second.AvailablePct, precision: 5);
        Assert.Equal(2, time.Delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), time.Delays[1]);
        Assert.Equal(4, handler.CallCount);
    }

    private static string ClaudeRollup(double usedPercent, string resetAt = "4102444800") =>
        $"{{\"rate_limit\":{{\"primary_window\":{{\"used_percent\":{usedPercent},\"reset_at\":{resetAt}}}}}}}";

    private static CodeyBoxOptions ClaudeRetryOptions(int maxRetryDelaySeconds) => new()
    {
        QuotaRouter = new QuotaRouterConfig
        {
            ProbeMaxRetries = 1,
            ProbeRetryInitialDelayMs = 0,
            ProbeRetryMaxDelaySeconds = maxRetryDelaySeconds,
            ProbeMaxConsecutiveFailures = 3,
            ProbeMaxStalenessSeconds = 300,
        },
    };

    private static CodeyBoxOptions PausedCadenceOptions(
        int pausedTtlSeconds,
        int pausedMaxStalenessSeconds) => new()
    {
        QuotaRouter = new QuotaRouterConfig
        {
            QuotaCacheTtlSeconds = 5,
            PausedQuotaCacheTtlSeconds = pausedTtlSeconds,
            PausedProbeMaxStalenessSeconds = pausedMaxStalenessSeconds,
            ProbeMaxRetries = 0,
            ProbeMaxStalenessSeconds = 300,
        },
    };

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
                    ["CodeyBox:QuotaRouter:MaxQuotaRecoveryProbeEligibilityScan"] = "17",
                    ["CodeyBox:QuotaRouter:DrainAggressiveness"] = "1.75",
                    ["CodeyBox:QuotaRouter:ExpectedResets:codex:Timestamps:0"] = "2026-06-01T00:20:00Z",
                    ["CodeyBox:QuotaRouter:ExpectedResets:codex:CadenceSeconds"] = "604800",
                    ["CodeyBox:QuotaRouter:ExpectedResets:codex:CadenceAnchor"] = "2026-06-01T00:20:00Z",
                    ["CodeyBox:QuotaRouter:PausedQuotaCacheTtlSeconds"] = "1800",
                    ["CodeyBox:QuotaRouter:PausedProbeMaxStalenessSeconds"] = "2700",
                    ["CodeyBox:QuotaRouter:PausedQuotaMaxCacheEntries"] = "256",
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

    private sealed class PausedQuotaCadenceWiringFactory : WebApplicationFactory<Program>
    {
        private readonly HttpMessageHandler _handler;
        private readonly TimeProvider _time;
        private readonly IOptionsMonitor<CodeyBoxOptions>? _optionsMonitor;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-paused-quota-wiring-{Guid.NewGuid():N}.db");

        public PausedQuotaCadenceWiringFactory(
            HttpMessageHandler handler,
            TimeProvider time,
            IOptionsMonitor<CodeyBoxOptions>? optionsMonitor = null)
        {
            _handler = handler;
            _time = time;
            _optionsMonitor = optionsMonitor;
        }

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
                    ["CodeyBox:QuotaRouter:QuotaCacheTtlSeconds"] = "5",
                    ["CodeyBox:QuotaRouter:PausedQuotaCacheTtlSeconds"] = "3600",
                    ["CodeyBox:QuotaRouter:PausedProbeMaxStalenessSeconds"] = "5400",
                    ["CodeyBox:QuotaRouter:ProbeMaxRetries"] = "0",
                    ["CodeyBox:QuotaRouter:ProbeMaxStalenessSeconds"] = "300",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(
                    new QuotaFakeHttpClientFactory("agent-quota", _handler));
                services.RemoveAll<IClaudeQuotaTokenSource>();
                services.AddSingleton<IClaudeQuotaTokenSource>(
                    new StaticClaudeQuotaTokenSource("claude-token"));
                if (_optionsMonitor is not null)
                {
                    services.RemoveAll<IOptionsMonitor<CodeyBoxOptions>>();
                    services.AddSingleton(_optionsMonitor);
                }
                services.AddSingleton(_time);
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }

    private sealed class ClaudeRetryCapWiringFactory : WebApplicationFactory<Program>
    {
        private readonly HttpMessageHandler _handler;
        private readonly TimeProvider _time;
        private readonly IOptionsMonitor<CodeyBoxOptions> _optionsMonitor;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-claude-retry-cap-{Guid.NewGuid():N}.db");

        public ClaudeRetryCapWiringFactory(
            HttpMessageHandler handler,
            TimeProvider time,
            IOptionsMonitor<CodeyBoxOptions> optionsMonitor)
        {
            _handler = handler;
            _time = time;
            _optionsMonitor = optionsMonitor;
        }

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
                    ["CodeyBox:QuotaRouter:QuotaCacheTtlSeconds"] = "0",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(
                    new QuotaFakeHttpClientFactory("agent-quota", _handler));
                services.RemoveAll<IClaudeQuotaTokenSource>();
                services.AddSingleton<IClaudeQuotaTokenSource>(
                    new StaticClaudeQuotaTokenSource("claude-token"));
                services.RemoveAll<IOptionsMonitor<CodeyBoxOptions>>();
                services.AddSingleton(_optionsMonitor);
                services.AddSingleton(_time);
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }

    private sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _currentValue;
        private readonly List<Action<T, string?>> _listeners = [];

        public MutableOptionsMonitor(T currentValue)
        {
            _currentValue = currentValue;
        }

        public T CurrentValue => _currentValue;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _listeners.Add(listener);
            return new Subscription(_listeners, listener);
        }

        public void Set(T value) => _currentValue = value;

        public void Fire(T value)
        {
            _currentValue = value;
            foreach (var listener in _listeners.ToArray())
                listener(value, Options.DefaultName);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly List<Action<T, string?>> _listeners;
            private readonly Action<T, string?> _listener;

            public Subscription(List<Action<T, string?>> listeners, Action<T, string?> listener)
            {
                _listeners = listeners;
                _listener = listener;
            }

            public void Dispose()
            {
                _listeners.Remove(_listener);
            }
        }
    }

    private sealed class StaticClaudeQuotaTokenSource : IClaudeQuotaTokenSource
    {
        private readonly string _token;

        public StaticClaudeQuotaTokenSource(string token)
        {
            _token = token;
        }

        public string FilePath => "test-claude-token-source";

        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>(_token);

        public void Dispose()
        {
        }
    }

    private sealed class CountingQuotaHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private int _callCount;

        public CountingQuotaHandler(HttpStatusCode status)
        {
            _status = status;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(""),
            });
        }
    }
}
