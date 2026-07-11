using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.StatisticsPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage of the statistics plugin's
/// <see cref="IResetCreditExpiryEstimator"/> surface: sample real snapshots
/// carrying <c>ResetCreditsAvailable</c> into the store, then derive banked
/// credit expiries back out through the same query path the REST endpoint uses.
/// The pure algorithm is covered by <see cref="ResetCreditExpiryTrackerTests"/>;
/// this file pins the store→JSON→observation→derivation wiring and the
/// config-bound expiry period, safety buffer, and seeds.
/// </summary>
public sealed class ResetCreditExpiryEstimatorTests : IDisposable
{
    private readonly TestTempDirectory _temp;
    private readonly string _dbPath;

    public ResetCreditExpiryEstimatorTests()
    {
        _temp = TestTempDirectory.Create("codeybox-resetcredit-tests-");
        _dbPath = _temp.NewDatabasePath("stats");
    }

    public void Dispose()
        => TestTempArtifacts.CleanupAll(
            () => TestTempArtifacts.DeleteSqliteDatabase(_dbPath),
            _temp.Dispose);

    [Fact]
    public async Task Estimate_DerivesObservedGrant_PinnedToLastSampleAtLowerCount()
    {
        var probe = new MutableProbe(AgentKind.Codex);
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-01T00:00:00Z"));
        await using var plugin = await BuildPluginAsync([probe], clock);

        probe.ResetCreditsAvailable = 0;
        await plugin.SampleOnceAsync(CancellationToken.None); // t+0,  count 0
        clock.Advance(TimeSpan.FromMinutes(15));
        await plugin.SampleOnceAsync(CancellationToken.None); // t+15, count 0 (last at 0)
        clock.Advance(TimeSpan.FromMinutes(15));
        probe.ResetCreditsAvailable = 1;
        await plugin.SampleOnceAsync(CancellationToken.None); // t+30, count 1 (grant)

        var report = await plugin.EstimateAsync(new ResetCreditExpiryQuery());

        var credit = Assert.Single(report.Credits);
        Assert.False(credit.IsEstimated);
        Assert.Equal(DateTimeOffset.Parse("2026-06-01T00:15:00Z"), credit.GrantedAt);
        Assert.Equal(credit.GrantedAt + TimeSpan.FromDays(30), credit.ExpiresAt);
        Assert.Equal(credit.ExpiresAt - TimeSpan.FromHours(24), report.NextCreditExpiresAt);
        Assert.Equal(1, report.LatestObservedCount);
    }

    [Fact]
    public async Task Estimate_SkipsSamplesWithNoResetCreditField_AsGaps()
    {
        // A snapshot with a null ResetCreditsAvailable (older provider / probe
        // failure) is a gap, not a decrement to zero. The grant that follows
        // must still pin to the last KNOWN sample at the lower count.
        var probe = new MutableProbe(AgentKind.Codex);
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-01T00:00:00Z"));
        await using var plugin = await BuildPluginAsync([probe], clock);

        probe.ResetCreditsAvailable = 0;
        await plugin.SampleOnceAsync(CancellationToken.None); // t+0,  count 0
        clock.Advance(TimeSpan.FromMinutes(15));
        await plugin.SampleOnceAsync(CancellationToken.None); // t+15, count 0 (last KNOWN at 0)
        clock.Advance(TimeSpan.FromMinutes(15));
        probe.ResetCreditsAvailable = null;
        await plugin.SampleOnceAsync(CancellationToken.None); // t+30, field absent → gap
        clock.Advance(TimeSpan.FromMinutes(15));
        probe.ResetCreditsAvailable = 1;
        await plugin.SampleOnceAsync(CancellationToken.None); // t+45, count 1 (grant)

        var report = await plugin.EstimateAsync(new ResetCreditExpiryQuery());

        var credit = Assert.Single(report.Credits);
        Assert.Equal(DateTimeOffset.Parse("2026-06-01T00:15:00Z"), credit.GrantedAt);
    }

    [Fact]
    public async Task Estimate_AppliesSeededCreditsFromConfig_FlaggedEstimated()
    {
        var probe = new MutableProbe(AgentKind.Codex) { ResetCreditsAvailable = 0 };
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-01T00:00:00Z"));
        await using var plugin = await BuildPluginAsync(
            [probe],
            clock,
            extraConfig: new Dictionary<string, string?>
            {
                ["ResetCreditExpiry:Seeds:0:EstimatedExpiresAt"] = "2026-06-16T00:00:00Z",
                ["ResetCreditExpiry:Seeds:0:Label"] = "credit A",
            });

        await plugin.SampleOnceAsync(CancellationToken.None); // single reading; no observed grant

        var report = await plugin.EstimateAsync(new ResetCreditExpiryQuery());

        var seed = Assert.Single(report.Credits);
        Assert.True(seed.IsEstimated);
        Assert.Equal(DateTimeOffset.Parse("2026-06-16T00:00:00Z"), seed.ExpiresAt);
        Assert.Equal("credit A", seed.Label);
        Assert.Equal(
            DateTimeOffset.Parse("2026-06-16T00:00:00Z") - TimeSpan.FromHours(24),
            report.NextCreditExpiresAt);
    }

    [Fact]
    public async Task Estimate_HonoursConfiguredExpiryPeriodAndSafetyBuffer()
    {
        var probe = new MutableProbe(AgentKind.Codex);
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-01T00:00:00Z"));
        await using var plugin = await BuildPluginAsync(
            [probe],
            clock,
            extraConfig: new Dictionary<string, string?>
            {
                ["ResetCreditExpiry:ExpiryPeriodDays"] = "7",
                ["ResetCreditExpiry:SafetyBufferHours"] = "12",
            });

        probe.ResetCreditsAvailable = 0;
        await plugin.SampleOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(15));
        probe.ResetCreditsAvailable = 1;
        await plugin.SampleOnceAsync(CancellationToken.None);

        var report = await plugin.EstimateAsync(new ResetCreditExpiryQuery());

        var credit = Assert.Single(report.Credits);
        Assert.Equal(credit.GrantedAt + TimeSpan.FromDays(7), credit.ExpiresAt);
        Assert.Equal(credit.ExpiresAt - TimeSpan.FromHours(12), credit.AdvisedSpendByAt);
        Assert.Equal(TimeSpan.FromDays(7), report.ExpiryPeriod);
        Assert.Equal(TimeSpan.FromHours(12), report.SafetyBuffer);
    }

    [Fact]
    public async Task Estimate_OnlyReadsTheConfiguredAgentSeries()
    {
        // The default agent is codex; a claude snapshot (no reset-credit
        // concept) in the same store must not pollute the codex derivation.
        var codex = new MutableProbe(AgentKind.Codex) { ResetCreditsAvailable = 0 };
        var claude = new MutableProbe(AgentKind.Claude) { ResetCreditsAvailable = null };
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-01T00:00:00Z"));
        await using var plugin = await BuildPluginAsync([codex, claude], clock);

        await plugin.SampleOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(15));
        codex.ResetCreditsAvailable = 1;
        await plugin.SampleOnceAsync(CancellationToken.None);

        var report = await plugin.EstimateAsync(new ResetCreditExpiryQuery());
        var credit = Assert.Single(report.Credits);
        Assert.Equal(DateTimeOffset.Parse("2026-06-01T00:00:00Z"), credit.GrantedAt);

        // Explicitly asking for claude finds no reset-credit series at all.
        var claudeReport = await plugin.EstimateAsync(new ResetCreditExpiryQuery { Agent = "claude" });
        Assert.Empty(claudeReport.Credits);
    }

    [Fact]
    public async Task Estimate_SeedsApplyOnlyToConfiguredAgent_NotOtherAgents()
    {
        // Seeds are pre-observation credits for the single configured agent
        // (codex). Querying an unrelated agent must NOT surface them, or the
        // estimator fabricates banked credits for an agent that has none.
        var codex = new MutableProbe(AgentKind.Codex) { ResetCreditsAvailable = 0 };
        var claude = new MutableProbe(AgentKind.Claude) { ResetCreditsAvailable = null };
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-01T00:00:00Z"));
        await using var plugin = await BuildPluginAsync(
            [codex, claude],
            clock,
            extraConfig: new Dictionary<string, string?>
            {
                ["ResetCreditExpiry:Seeds:0:EstimatedExpiresAt"] = "2026-06-16T00:00:00Z",
                ["ResetCreditExpiry:Seeds:0:Label"] = "credit A",
            });

        await plugin.SampleOnceAsync(CancellationToken.None);

        // Default query resolves to the configured agent (codex): seed present.
        var codexReport = await plugin.EstimateAsync(new ResetCreditExpiryQuery());
        var seed = Assert.Single(codexReport.Credits);
        Assert.True(seed.IsEstimated);
        Assert.True(codexReport.NextCreditIsEstimated);

        // Explicitly querying claude must NOT inherit codex's seed.
        var claudeReport = await plugin.EstimateAsync(new ResetCreditExpiryQuery { Agent = "claude" });
        Assert.Empty(claudeReport.Credits);
        Assert.Null(claudeReport.NextCreditExpiresAt);
        Assert.False(claudeReport.NextCreditIsEstimated);
    }

    private async Task<StatisticsQuotaPlugin> BuildPluginAsync(
        IEnumerable<IAgentQuotaProbe> probes,
        TimeProvider timeProvider,
        IReadOnlyDictionary<string, string?>? extraConfig = null)
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
        var configRoot = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var plugin = new StatisticsQuotaPlugin(probes, configRoot, quotaGate: null, timeProvider: timeProvider);
        var host = new TestPluginHost(configRoot.GetSection($"CodeyBox:Plugins:{StatisticsQuotaPlugin.PluginId}"));
        await plugin.InitializeAsync(new PluginContext(
            HostApiVersion: CodeyBoxApiVersion.Current,
            PluginId: StatisticsQuotaPlugin.PluginId,
            PluginDisplayName: "CodeyBox: Statistics",
            Host: host));
        return plugin;
    }

    private sealed class MutableProbe : IAgentQuotaProbe
    {
        public MutableProbe(AgentKind kind) => Kind = kind;

        public AgentKind Kind { get; }
        public int? ResetCreditsAvailable { get; set; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(new AgentQuotaSnapshot
            {
                AvailablePct = 80,
                ResetCreditsAvailable = ResetCreditsAvailable,
            });
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
        public TestPluginHost(IConfigurationSection scoped) => ScopedConfig = scoped;
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public IConfigurationSection ScopedConfig { get; }
    }
}
