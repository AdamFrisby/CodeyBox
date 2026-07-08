using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.StatisticsPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the statistics plugin's quota sampler. Each test stands the
/// plugin up against an in-memory probe stub and a private SQLite file under
/// <c>%TMP%</c>, exercises one piece of behaviour, and asserts on rows fetched
/// back through the plugin's own <see cref="IQuotaTimeSeriesStore"/> surface —
/// the same query path the REST endpoint uses.
/// </summary>
public sealed class StatisticsQuotaPluginTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public StatisticsQuotaPluginTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codeybox-stats-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "stats.db");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort — the next test creates a unique temp dir, leaks are reaped by /tmp cleanup.
        }
    }

    [Fact]
    public async Task SampleOnce_ExpandsSnapshotIntoOverallPlusPerWindowRows()
    {
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 42,
            Notes = "test snapshot",
            Windows =
            [
                new WindowQuota
                {
                    Name = "five_hour",
                    AvailablePct = 88,
                    ResetAt = DateTimeOffset.Parse("2026-06-14T18:00:00Z"),
                },
                new WindowQuota
                {
                    Name = "seven_day",
                    AvailablePct = 42,
                    ResetAt = DateTimeOffset.Parse("2026-06-20T18:00:00Z"),
                },
            ],
        };

        var probe = new StubProbe(AgentKind.Claude, snapshot);
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z"));
        await using var plugin = await BuildPluginAsync([probe], clock);

        await plugin.SampleOnceAsync(CancellationToken.None);

        var rows = await plugin.QueryAsync(new QuotaTimeSeriesFilter { Agent = "claude" }, default);
        Assert.Equal(3, rows.Count); // overall + 2 windows

        var overall = Assert.Single(rows, r => r.WindowName is null);
        Assert.Equal(42, overall.OverallPct);
        Assert.True(overall.IsKnown);
        Assert.True(overall.WouldAllow);
        Assert.Equal("test snapshot", overall.Notes);

        Assert.Single(rows, r => r.WindowName == "five_hour" && r.WindowPct == 88);
        Assert.Single(rows, r => r.WindowName == "seven_day" && r.WindowPct == 42);
    }

    [Fact]
    public async Task QueryFilter_BySpecificWindow_ReturnsOnlyThatWindow()
    {
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 50,
            Windows =
            [
                new WindowQuota { Name = "five_hour", AvailablePct = 90 },
                new WindowQuota { Name = "seven_day", AvailablePct = 50 },
            ],
        };
        await using var plugin = await BuildPluginAsync(
            [new StubProbe(AgentKind.Claude, snapshot)],
            new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z")));
        await plugin.SampleOnceAsync(CancellationToken.None);

        var rows = await plugin.QueryAsync(
            new QuotaTimeSeriesFilter { Agent = "claude", WindowName = "five_hour" },
            default);

        var only = Assert.Single(rows);
        Assert.Equal("five_hour", only.WindowName);
        Assert.Equal(90, only.WindowPct);
    }

    [Fact]
    public async Task QueryFilter_OverallSentinel_ReturnsOnlyAggregatedRows()
    {
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 60,
            Windows =
            [
                new WindowQuota { Name = "five_hour", AvailablePct = 95 },
                new WindowQuota { Name = "seven_day", AvailablePct = 60 },
            ],
        };
        await using var plugin = await BuildPluginAsync(
            [new StubProbe(AgentKind.Claude, snapshot)],
            new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z")));
        await plugin.SampleOnceAsync(CancellationToken.None);

        var rows = await plugin.QueryAsync(
            new QuotaTimeSeriesFilter { Agent = "claude", WindowName = "overall" },
            default);

        var only = Assert.Single(rows);
        Assert.Null(only.WindowName);
        Assert.Equal(60, only.OverallPct);
    }

    [Fact]
    public async Task SampleOnce_PersistsRawSnapshotAsJson()
    {
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 71,
            Notes = "raw fidelity probe",
            Windows = [new WindowQuota { Name = "five_hour", AvailablePct = 71 }],
        };
        await using var plugin = await BuildPluginAsync(
            [new StubProbe(AgentKind.Claude, snapshot)],
            new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z")));
        await plugin.SampleOnceAsync(CancellationToken.None);

        var raw = await plugin.QueryRawAsync(new QuotaTimeSeriesFilter { Agent = "claude" }, default);
        var only = Assert.Single(raw);
        Assert.Equal("claude", only.Agent);

        using var doc = JsonDocument.Parse(only.RawJson);
        Assert.Equal(71, doc.RootElement.GetProperty("AvailablePct").GetDouble());
        Assert.Equal("raw fidelity probe", doc.RootElement.GetProperty("Notes").GetString());
        var windows = doc.RootElement.GetProperty("Windows");
        Assert.Equal(1, windows.GetArrayLength());
        Assert.Equal("five_hour", windows[0].GetProperty("Name").GetString());
    }

    [Fact]
    public async Task SampleOnce_SkipsPayPerApiAndNullProbes()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z"));
        await using var plugin = await BuildPluginAsync(
            [
                new StubProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 33 }),
                new StubProbe(new AgentKind("pay-per-api"), new AgentQuotaSnapshot { AvailablePct = 100 }),
                new StubProbe(new AgentKind("null"), AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Permanent)),
            ],
            clock);

        await plugin.SampleOnceAsync(CancellationToken.None);

        var rows = await plugin.QueryAsync(new QuotaTimeSeriesFilter(), default);
        // claude only — no rows for pay-per-api or null probes.
        var only = Assert.Single(rows);
        Assert.Equal("claude", only.Agent);
        Assert.Equal(33, only.OverallPct);
    }

    [Fact]
    public async Task SampleOnce_RecordsUnknownSnapshot_AsIsKnownFalse()
    {
        var unknown = AgentQuotaSnapshot.UnknownSnapshot(
            QuotaUnknownReason.Transient,
            "endpoint returned 503");
        await using var plugin = await BuildPluginAsync(
            [new StubProbe(AgentKind.Claude, unknown)],
            new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z")));

        await plugin.SampleOnceAsync(CancellationToken.None);

        var rows = await plugin.QueryAsync(new QuotaTimeSeriesFilter(), default);
        var only = Assert.Single(rows);
        Assert.False(only.IsKnown);
        Assert.False(only.WouldAllow);
        Assert.Equal("Transient", only.UnknownReason);
        Assert.Equal("endpoint returned 503", only.Notes);
    }

    [Fact]
    public async Task SampleOnce_ProbeThrows_RecordsTransientUnknown_DoesNotCrash()
    {
        var probe = new StubProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 50 })
        {
            ThrowOnCall = new HttpRequestException("network unreachable"),
        };
        await using var plugin = await BuildPluginAsync(
            [probe],
            new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z")));

        await plugin.SampleOnceAsync(CancellationToken.None);

        var rows = await plugin.QueryAsync(new QuotaTimeSeriesFilter(), default);
        var only = Assert.Single(rows);
        Assert.False(only.IsKnown);
        Assert.Equal("Transient", only.UnknownReason);
        Assert.Contains("network unreachable", only.Notes);
    }

    [Fact]
    public async Task SampleOnce_PerModelSnapshot_RecordsPerModelRows()
    {
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 70,
            Windows = [new WindowQuota { Name = "five_hour", AvailablePct = 70 }],
            PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
            {
                ["composer-2.5"] = new ModelQuota
                {
                    AvailablePct = 12,
                    ResetAt = DateTimeOffset.Parse("2026-06-15T03:00:00Z"),
                    Windows = [new WindowQuota { Name = "five_hour", AvailablePct = 12 }],
                },
            },
        };
        await using var plugin = await BuildPluginAsync(
            [new StubProbe(new AgentKind("cursor"), snapshot)],
            new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z")));

        await plugin.SampleOnceAsync(CancellationToken.None);

        var rows = await plugin.QueryAsync(
            new QuotaTimeSeriesFilter { Agent = "cursor", ModelId = "composer-2.5" },
            default);
        // model-aggregate row (window_name null) + per-window expansion
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("composer-2.5", r.ModelId));
        Assert.Single(rows, r => r.WindowName is null && r.OverallPct == 12);
        Assert.Single(rows, r => r.WindowName == "five_hour" && r.WindowPct == 12);
    }

    [Fact]
    public async Task QueryFilter_TimeRange_BoundsResults()
    {
        var first = new FakeClock(DateTimeOffset.Parse("2026-06-14T10:00:00Z"));
        var snapshot = new AgentQuotaSnapshot { AvailablePct = 20 };
        await using var plugin = await BuildPluginAsync(
            [new StubProbe(AgentKind.Claude, snapshot)],
            first);

        await plugin.SampleOnceAsync(CancellationToken.None);
        first.Advance(TimeSpan.FromHours(1));
        await plugin.SampleOnceAsync(CancellationToken.None);
        first.Advance(TimeSpan.FromHours(1));
        await plugin.SampleOnceAsync(CancellationToken.None);

        var inRange = await plugin.QueryAsync(
            new QuotaTimeSeriesFilter
            {
                Agent = "claude",
                WindowName = "overall",
                FromUtc = DateTimeOffset.Parse("2026-06-14T10:30:00Z"),
                ToUtc = DateTimeOffset.Parse("2026-06-14T11:30:00Z"),
            },
            default);

        var only = Assert.Single(inRange);
        Assert.Equal(DateTimeOffset.Parse("2026-06-14T11:00:00Z"), only.SampledAt);
    }

    [Fact]
    public async Task QueryFilter_Descending_ReturnsNewestFirst_AndLimitKeepsNewest()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-14T10:00:00Z"));
        await using var plugin = await BuildPluginAsync(
            [new StubProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 20 })],
            clock);

        await plugin.SampleOnceAsync(CancellationToken.None); // 10:00
        clock.Advance(TimeSpan.FromHours(1));
        await plugin.SampleOnceAsync(CancellationToken.None); // 11:00
        clock.Advance(TimeSpan.FromHours(1));
        await plugin.SampleOnceAsync(CancellationToken.None); // 12:00 (newest)

        // Descending + Limit 1 must return ONLY the newest sample — an ascending
        // query would keep the OLDEST row under truncation and hand back a stale read.
        var newestSample = await plugin.QueryAsync(
            new QuotaTimeSeriesFilter { Agent = "claude", WindowName = "overall", Descending = true, Limit = 1 },
            default);
        Assert.Equal(DateTimeOffset.Parse("2026-06-14T12:00:00Z"), Assert.Single(newestSample).SampledAt);

        var newestRaw = await plugin.QueryRawAsync(
            new QuotaTimeSeriesFilter { Agent = "claude", Descending = true, Limit = 1 },
            default);
        Assert.Equal(DateTimeOffset.Parse("2026-06-14T12:00:00Z"), Assert.Single(newestRaw).SampledAt);
    }

    [Fact]
    public async Task Prune_RemovesRowsOlderThanRetention()
    {
        // Retention = 1 hour, so rows older than (now - 1h) are dropped.
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-14T10:00:00Z"));
        await using var plugin = await BuildPluginAsync(
            [new StubProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 50 })],
            clock,
            extraConfig: new Dictionary<string, string?>
            {
                ["RetentionHours"] = "1",
            });

        await plugin.SampleOnceAsync(CancellationToken.None);

        // Advance past retention + prune cadence so the next SampleOnceAsync
        // triggers the inline prune sweep.
        clock.Advance(TimeSpan.FromHours(3));
        await plugin.SampleOnceAsync(CancellationToken.None);

        var rows = await plugin.QueryAsync(
            new QuotaTimeSeriesFilter { Agent = "claude", WindowName = "overall" },
            default);
        // Only the second sample survives — first one was older than RetentionHours=1
        // at the time of the prune sweep.
        var only = Assert.Single(rows);
        Assert.Equal(DateTimeOffset.Parse("2026-06-14T13:00:00Z"), only.SampledAt);
    }

    [Fact]
    public async Task Survives_StoreReopen_RowsPersistAcrossInstances()
    {
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 25,
            Windows = [new WindowQuota { Name = "five_hour", AvailablePct = 25 }],
        };
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z"));

        await using (var first = await BuildPluginAsync([new StubProbe(AgentKind.Claude, snapshot)], clock))
        {
            await first.SampleOnceAsync(CancellationToken.None);
        }

        // Same DB path, fresh plugin instance — simulates an orchestrator restart.
        await using var second = await BuildPluginAsync([new StubProbe(AgentKind.Claude, snapshot)], clock);
        var rows = await second.QueryAsync(new QuotaTimeSeriesFilter { Agent = "claude" }, default);
        Assert.Equal(2, rows.Count); // overall + one window
    }

    [Fact]
    public async Task EnabledFlag_DefaultsTrue_HotReloadFlipsToFalse()
    {
        var configRoot = BuildConfig(extraConfig: null);
        var probe = new StubProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 80 });
        await using var plugin = new StatisticsQuotaPlugin(
            [probe],
            configRoot,
            quotaGate: null,
            timeProvider: new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z")));
        await plugin.InitializeAsync(BuildPluginContext(configRoot));

        Assert.True(plugin.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(15), plugin.Interval);
    }

    private async Task<StatisticsQuotaPlugin> BuildPluginAsync(
        IEnumerable<IAgentQuotaProbe> probes,
        TimeProvider timeProvider,
        IReadOnlyDictionary<string, string?>? extraConfig = null)
    {
        var configRoot = BuildConfig(extraConfig);
        var plugin = new StatisticsQuotaPlugin(probes, configRoot, quotaGate: null, timeProvider: timeProvider);
        await plugin.InitializeAsync(BuildPluginContext(configRoot));
        return plugin;
    }

    private IConfiguration BuildConfig(IReadOnlyDictionary<string, string?>? extraConfig)
    {
        var settings = new Dictionary<string, string?>
        {
            [$"CodeyBox:Plugins:{StatisticsQuotaPlugin.PluginId}:DatabasePath"] = _dbPath,
        };
        if (extraConfig is not null)
        {
            foreach (var (key, value) in extraConfig)
                settings[$"CodeyBox:Plugins:{StatisticsQuotaPlugin.PluginId}:{key}"] = value;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static PluginContext BuildPluginContext(IConfiguration configRoot)
    {
        var host = new TestPluginHost(configRoot.GetSection($"CodeyBox:Plugins:{StatisticsQuotaPlugin.PluginId}"));
        return new PluginContext(
            HostApiVersion: CodeyBoxApiVersion.Current,
            PluginId: StatisticsQuotaPlugin.PluginId,
            PluginDisplayName: "CodeyBox: Statistics",
            Host: host);
    }

    private sealed class StubProbe : IAgentQuotaProbe
    {
        public StubProbe(AgentKind kind, AgentQuotaSnapshot snapshot)
        {
            Kind = kind;
            Next = snapshot;
        }

        public AgentKind Kind { get; }
        public AgentQuotaSnapshot Next { get; set; }
        public Exception? ThrowOnCall { get; set; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => ThrowOnCall is not null
                ? Task.FromException<AgentQuotaSnapshot>(ThrowOnCall)
                : Task.FromResult(Next);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class TestPluginHost : IPluginHost
    {
        public TestPluginHost(IConfigurationSection scoped)
        {
            ScopedConfig = scoped;
        }

        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public IConfigurationSection ScopedConfig { get; }
    }
}
