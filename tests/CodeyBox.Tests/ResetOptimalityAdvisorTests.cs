using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.StatisticsPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage of the statistics plugin's
/// <see cref="IResetOptimalityAdvisor"/> surface: sample real snapshots into the
/// store, then compose the latest quota reading + derived credit expiry +
/// cadence config into advice through the same path the REST endpoint uses.
/// The pure decision is covered by <see cref="ResetOptimalityEvaluatorTests"/>;
/// this file pins the store→snapshot→credits→config→evaluate wiring, including
/// the self-calibration of the cadence anchor from the logged weekly series.
/// </summary>
public sealed class ResetOptimalityAdvisorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-06-01T00:00:00Z");

    private readonly string _tempDir;
    private readonly string _dbPath;

    public ResetOptimalityAdvisorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codeybox-resetadvice-tests-" + Guid.NewGuid().ToString("n"));
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
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public async Task Advise_UsableQuotaAboveDust_ReportsBurnFirst()
    {
        var probe = new MutableProbe(AgentKind.Codex) { AvailablePct = 50, ResetCreditsAvailable = 0 };
        var clock = new FakeClock(T0);
        await using var plugin = await BuildPluginAsync([probe], clock, new()
        {
            ["ResetOptimality:CadenceAnchor"] = "2026-06-01T00:00:00Z",
            ["ResetOptimality:RefineAnchorFromLogger"] = "false",
        });

        await plugin.SampleOnceAsync(CancellationToken.None); // t+0, count 0
        clock.Advance(TimeSpan.FromMinutes(15));
        probe.ResetCreditsAvailable = 1;
        await plugin.SampleOnceAsync(CancellationToken.None); // t+15, count 1 (a real banked credit)

        // Even with a spendable credit, 50% usable quota means burn-first wins.
        var advice = await plugin.AdviseAsync(new ResetAdviceRequest());

        Assert.False(advice.ShouldSpend);
        Assert.Equal(ResetAdviceReason.BurnFirst, advice.Reason);
        Assert.Equal(50, advice.UsableQuotaPct);
    }

    [Fact]
    public async Task Advise_ExhaustedWithBankedCreditAndNaturalResetSoon_HoldsForFreeReset()
    {
        // Quota exhausted, a banked credit exists, no plan end. The natural
        // weekly reset lands well before the credit's 30-day spend-by, so the
        // advisor holds — spending would destroy the free reset.
        var probe = new MutableProbe(AgentKind.Codex) { AvailablePct = 0, ResetCreditsAvailable = 0 };
        var clock = new FakeClock(T0);
        await using var plugin = await BuildPluginAsync([probe], clock, new()
        {
            ["ResetOptimality:CadenceAnchor"] = "2026-06-01T00:00:00Z",
            ["ResetOptimality:RefineAnchorFromLogger"] = "false",
        });

        await plugin.SampleOnceAsync(CancellationToken.None); // t+0, count 0
        clock.Advance(TimeSpan.FromMinutes(15));
        probe.ResetCreditsAvailable = 1;
        await plugin.SampleOnceAsync(CancellationToken.None); // t+15, count 1 (grant)

        var advice = await plugin.AdviseAsync(new ResetAdviceRequest());

        Assert.False(advice.ShouldSpend);
        Assert.Equal(ResetAdviceReason.NaturalResetArrivesInTime, advice.Reason);
        Assert.Equal(T0 + TimeSpan.FromDays(7), advice.PredictedNaturalReset);
    }

    [Fact]
    public async Task Advise_ExhaustedAndPlanEndsBeforeNaturalReset_AdvisesSpend()
    {
        // Quota exhausted, a banked credit exists, but the plan ends in 2 days —
        // before the weekly natural reset. Quota after the plan end is worthless,
        // so spend the credit within the window that closes at the plan end.
        var probe = new MutableProbe(AgentKind.Codex) { AvailablePct = 0, ResetCreditsAvailable = 0 };
        var clock = new FakeClock(T0);
        var planEnd = T0 + TimeSpan.FromDays(2);
        await using var plugin = await BuildPluginAsync([probe], clock, new()
        {
            ["ResetOptimality:CadenceAnchor"] = "2026-06-01T00:00:00Z",
            ["ResetOptimality:RefineAnchorFromLogger"] = "false",
            ["ResetOptimality:PlanEndsAt"] = "2026-06-03T00:00:00Z",
        });

        await plugin.SampleOnceAsync(CancellationToken.None); // t+0, count 0
        clock.Advance(TimeSpan.FromMinutes(15));
        probe.ResetCreditsAvailable = 1;
        await plugin.SampleOnceAsync(CancellationToken.None); // t+15, count 1 (grant)

        var advice = await plugin.AdviseAsync(new ResetAdviceRequest());

        Assert.True(advice.ShouldSpend);
        Assert.Equal(ResetAdviceReason.SpendBeforeDeadline, advice.Reason);
        Assert.Equal(planEnd, advice.DecisionDeadline);
        Assert.NotNull(advice.OptimalWindow);
        Assert.Equal(planEnd, advice.OptimalWindow!.Value.ClosesAt);
    }

    [Fact]
    public async Task Advise_SelfCalibratesCadenceAnchorFromLoggedWeeklyReset()
    {
        // The logged weekly window shows a reset (spent → fresh) 10 hours after
        // the configured anchor. With refinement ON and a 6-hour tolerance, the
        // 10-hour drift is applied, shifting the predicted natural reset by 10h.
        var probe = new MutableProbe(AgentKind.Codex)
        {
            AvailablePct = 0,
            ResetCreditsAvailable = 0,
            WeeklyPct = 10, // spent
        };
        var clock = new FakeClock(T0);
        await using var plugin = await BuildPluginAsync([probe], clock, new()
        {
            ["ResetOptimality:CadenceAnchor"] = "2026-06-01T00:00:00Z",
            ["ResetOptimality:RefineAnchorFromLogger"] = "true",
            ["ResetOptimality:TimeToleranceHours"] = "6",
        });

        await plugin.SampleOnceAsync(CancellationToken.None); // t+0: weekly spent (10%)
        clock.Advance(TimeSpan.FromHours(10));
        probe.WeeklyPct = 90;              // weekly window refilled → reset observed
        probe.ResetCreditsAvailable = 1;   // and a credit is granted here
        await plugin.SampleOnceAsync(CancellationToken.None); // t+10h: reset detected

        // Time passes and the fresh weekly window is spent back down. At advice
        // time the reset-target (weekly) window is exhausted, so burn-first is
        // satisfied and the decision reaches the re-anchor branch. (A still-fresh
        // weekly window would correctly be burn-first territory instead.)
        clock.Advance(TimeSpan.FromHours(1));
        probe.WeeklyPct = 0;
        await plugin.SampleOnceAsync(CancellationToken.None); // t+11h: weekly spent again

        var advice = await plugin.AdviseAsync(new ResetAdviceRequest());

        // Refined anchor = T0 + 10h; next reset strictly after now (T0+11h) is
        // one period later → T0 + 7d + 10h (vs T0 + 7d without refinement).
        Assert.Equal(ResetAdviceReason.NaturalResetArrivesInTime, advice.Reason);
        Assert.Equal(T0 + TimeSpan.FromDays(7) + TimeSpan.FromHours(10), advice.PredictedNaturalReset);
    }

    [Fact]
    public async Task Advise_SelfCalibratesFromConfiguredResetTargetWindow()
    {
        // The weekly and seven_day windows refill at different phases. With
        // ResetTargetWindow=seven_day, refinement must learn the seven_day phase,
        // not the default weekly one.
        var probe = new MutableProbe(AgentKind.Codex)
        {
            AvailablePct = 0,
            ResetCreditsAvailable = 0,
            WeeklyPct = 10,
            SevenDayPct = 10,
        };
        var clock = new FakeClock(T0);
        await using var plugin = await BuildPluginAsync([probe], clock, new()
        {
            ["ResetOptimality:CadenceAnchor"] = "2026-06-01T00:00:00Z",
            ["ResetOptimality:ResetTargetWindow"] = "seven_day",
            ["ResetOptimality:RefineAnchorFromLogger"] = "true",
            ["ResetOptimality:AnchorRefineToleranceHours"] = "6",
        });

        await plugin.SampleOnceAsync(CancellationToken.None); // t+0: both windows spent
        clock.Advance(TimeSpan.FromHours(2));
        probe.WeeklyPct = 90; // weekly refills at +2h
        await plugin.SampleOnceAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromHours(8));
        probe.SevenDayPct = 90;             // configured target refills at +10h
        probe.ResetCreditsAvailable = 1;    // and a credit is granted here
        await plugin.SampleOnceAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromHours(1));
        probe.WeeklyPct = 0;
        probe.SevenDayPct = 0;
        await plugin.SampleOnceAsync(CancellationToken.None); // latest target window spent

        var advice = await plugin.AdviseAsync(new ResetAdviceRequest());

        Assert.Equal(ResetAdviceReason.NaturalResetArrivesInTime, advice.Reason);
        Assert.Equal(T0 + TimeSpan.FromDays(7) + TimeSpan.FromHours(10), advice.PredictedNaturalReset);
        Assert.Equal(0.0, advice.UsableQuotaPct);
    }

    [Fact]
    public async Task Advise_NoLoggedSnapshot_ReportsQuotaReadingUnavailable()
    {
        // Nothing sampled into the store → ReadLatestSnapshotAsync must translate
        // the empty series into an unknown snapshot so the evaluator holds, rather
        // than fabricating a 0% reading that would silently flip burn-first.
        var probe = new MutableProbe(AgentKind.Codex);
        var clock = new FakeClock(T0);
        await using var plugin = await BuildPluginAsync([probe], clock, new()
        {
            ["ResetOptimality:CadenceAnchor"] = "2026-06-01T00:00:00Z",
            ["ResetOptimality:RefineAnchorFromLogger"] = "false",
        });

        var advice = await plugin.AdviseAsync(new ResetAdviceRequest());

        Assert.False(advice.ShouldSpend);
        Assert.Equal(ResetAdviceReason.QuotaReadingUnavailable, advice.Reason);
        Assert.Null(advice.UsableQuotaPct);
    }

    [Fact]
    public async Task Advise_HotReloadedResetOptimalityOptionsAffectSubsequentAdvice()
    {
        var probe = new MutableProbe(AgentKind.Codex) { AvailablePct = 0, ResetCreditsAvailable = 0 };
        var clock = new FakeClock(T0);
        var source = new ReloadableMemorySource
        {
            Data = BuildSettings(new()
            {
                ["ResetOptimality:CadenceAnchor"] = "2026-06-01T00:00:00Z",
                ["ResetOptimality:RefineAnchorFromLogger"] = "false",
                ["ResetOptimality:PlanEndsAt"] = "2026-06-03T00:00:00Z",
            }),
        };
        var configRoot = new ConfigurationBuilder().Add(source).Build();
        await using var plugin = new StatisticsQuotaPlugin([probe], configRoot, quotaGate: null, timeProvider: clock);
        await plugin.InitializeAsync(BuildPluginContext(configRoot));

        await plugin.SampleOnceAsync(CancellationToken.None); // t+0, count 0
        clock.Advance(TimeSpan.FromMinutes(15));
        probe.ResetCreditsAvailable = 1;
        await plugin.SampleOnceAsync(CancellationToken.None); // t+15, count 1 (grant)

        var beforeReload = await plugin.AdviseAsync(new ResetAdviceRequest());
        Assert.True(beforeReload.ShouldSpend);
        Assert.Equal(ResetAdviceReason.SpendBeforeDeadline, beforeReload.Reason);

        source.TriggerReload(BuildSettings(new()
        {
            ["ResetOptimality:CadenceAnchor"] = "2026-06-01T00:00:00Z",
            ["ResetOptimality:RefineAnchorFromLogger"] = "false",
            ["ResetOptimality:PlanEndsAt"] = "2026-07-01T00:00:00Z",
        }));

        var afterReload = await plugin.AdviseAsync(new ResetAdviceRequest());
        Assert.False(afterReload.ShouldSpend);
        Assert.Equal(ResetAdviceReason.NaturalResetArrivesInTime, afterReload.Reason);
        Assert.Equal(DateTimeOffset.Parse("2026-07-01T00:00:00Z"), afterReload.PlanEndsAt);
    }

    private async Task<StatisticsQuotaPlugin> BuildPluginAsync(
        IEnumerable<IAgentQuotaProbe> probes,
        TimeProvider timeProvider,
        Dictionary<string, string?> extraConfig)
    {
        var configRoot = new ConfigurationBuilder().AddInMemoryCollection(BuildSettings(extraConfig)).Build();
        var plugin = new StatisticsQuotaPlugin(probes, configRoot, quotaGate: null, timeProvider: timeProvider);
        await plugin.InitializeAsync(BuildPluginContext(configRoot));
        return plugin;
    }

    private Dictionary<string, string?> BuildSettings(Dictionary<string, string?> extraConfig)
    {
        var settings = new Dictionary<string, string?>
        {
            [$"CodeyBox:Plugins:{StatisticsQuotaPlugin.PluginId}:DatabasePath"] = _dbPath,
        };
        foreach (var (key, value) in extraConfig)
            settings[$"CodeyBox:Plugins:{StatisticsQuotaPlugin.PluginId}:{key}"] = value;
        return settings;
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

    private sealed class MutableProbe : IAgentQuotaProbe
    {
        public MutableProbe(AgentKind kind) => Kind = kind;

        public AgentKind Kind { get; }
        public double AvailablePct { get; set; } = 80;
        public int? ResetCreditsAvailable { get; set; }

        /// <summary>Weekly-window usable %. Null omits the weekly window entirely.</summary>
        public double? WeeklyPct { get; set; }
        public double? SevenDayPct { get; set; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(new AgentQuotaSnapshot
            {
                AvailablePct = AvailablePct,
                ResetCreditsAvailable = ResetCreditsAvailable,
                Windows = BuildWindows(),
            });

        private WindowQuota[] BuildWindows()
        {
            var windows = new List<WindowQuota>(capacity: 2);
            if (WeeklyPct is { } weekly)
                windows.Add(new WindowQuota { Name = "weekly", AvailablePct = weekly });
            if (SevenDayPct is { } sevenDay)
                windows.Add(new WindowQuota { Name = "seven_day", AvailablePct = sevenDay });
            return windows.ToArray();
        }
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

    private sealed class ReloadableMemorySource : IConfigurationSource
    {
        public Dictionary<string, string?> Data { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public ReloadableMemoryProvider? Provider { get; private set; }

        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            Provider = new ReloadableMemoryProvider(this);
            return Provider;
        }

        public void TriggerReload(Dictionary<string, string?> next)
        {
            Data = new Dictionary<string, string?>(next, StringComparer.OrdinalIgnoreCase);
            Provider!.ReloadFromSource();
        }
    }

    private sealed class ReloadableMemoryProvider : ConfigurationProvider
    {
        private readonly ReloadableMemorySource _source;

        public ReloadableMemoryProvider(ReloadableMemorySource source)
        {
            _source = source;
            ReloadFromSource();
        }

        public override void Load() { }

        public void ReloadFromSource()
        {
            Data = new Dictionary<string, string?>(_source.Data, StringComparer.OrdinalIgnoreCase);
            OnReload();
        }
    }
}
