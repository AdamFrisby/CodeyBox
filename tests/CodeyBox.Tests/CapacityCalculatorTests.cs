using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.StatisticsPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="StatisticsQuotaPlugin"/>'s capacity analyser. Each
/// test stands the plugin up against an in-memory <see cref="IAgentQuotaProbe"/>
/// stub for the time-series side and an in-memory <see cref="IAgentUsageStore"/>
/// stub for the usage side, then exercises <see cref="ICapacityCalculator.ComputeAsync"/>
/// — the same surface the <c>/stats/capacity</c> endpoint hits.
/// </summary>
public sealed class CapacityCalculatorTests : IDisposable
{
    private readonly TestTempDirectory _temp;
    private readonly string _dbPath;

    public CapacityCalculatorTests()
    {
        _temp = TestTempDirectory.Create("codeybox-capacity-tests-");
        _dbPath = _temp.NewDatabasePath("stats");
    }

    public void Dispose()
        => TestTempArtifacts.CleanupAll(
            () => TestTempArtifacts.DeleteSqliteDatabase(_dbPath),
            _temp.Dispose);

    /// <summary>
    /// Core acceptance test from the task brief: given accumulated quota samples
    /// + usage events, the endpoint returns a per-agent/per-window
    /// token-and-request capacity estimate with the underlying burn-rate
    /// series; the test feeds synthetic samples + usage and asserts the
    /// computed capacity matches expectation.
    ///
    /// Synthetic input shape (claude / seven_day):
    ///   t=0h:   pct=100%, 0 events
    ///   t=1h:   pct=98%   (Δ 2%) — 2,000,000 input tokens, 4 events in [0,1)
    ///   t=2h:   pct=96%   (Δ 2%) — 2,000,000 input tokens, 4 events in [1,2)
    ///   t=3h:   pct=94%   (Δ 2%) — 2,000,000 input tokens, 4 events in [2,3)
    ///
    /// Expected: input tokens per 1% = 1,000,000; full-window cap = 100,000,000;
    /// requests per 1% = 2; full-window cap = 200.
    /// </summary>
    [Fact]
    public async Task ComputeAsync_KnownSamplesAndUsage_ProducesExpectedCapacity()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z"));
        var probe = new StubProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 100 });
        var usage = new FakeUsageStore();
        await using var plugin = await BuildPluginAsync([probe], usage, clock);

        // Sample 0: pct=100 at t=0
        probe.Next = MakeSnapshot(pct: 100, sevenDayPct: 100);
        await plugin.SampleOnceAsync(default);

        // Hour 1: 2,000,000 input tok + 600,000 output tok, 4 events
        AddEvents(usage, "claude", clock.GetUtcNow(), 1, inputPerEvent: 500_000, outputPerEvent: 150_000, count: 4);
        clock.Advance(TimeSpan.FromHours(1));
        probe.Next = MakeSnapshot(pct: 98, sevenDayPct: 98);
        await plugin.SampleOnceAsync(default);

        // Hour 2: same 4 events
        AddEvents(usage, "claude", clock.GetUtcNow(), 1, inputPerEvent: 500_000, outputPerEvent: 150_000, count: 4);
        clock.Advance(TimeSpan.FromHours(1));
        probe.Next = MakeSnapshot(pct: 96, sevenDayPct: 96);
        await plugin.SampleOnceAsync(default);

        // Hour 3: same 4 events
        AddEvents(usage, "claude", clock.GetUtcNow(), 1, inputPerEvent: 500_000, outputPerEvent: 150_000, count: 4);
        clock.Advance(TimeSpan.FromHours(1));
        probe.Next = MakeSnapshot(pct: 94, sevenDayPct: 94);
        await plugin.SampleOnceAsync(default);

        var report = await plugin.ComputeAsync(new CapacityFilter
        {
            Agent = "claude",
            WindowName = "seven_day",
        }, default);

        var entry = Assert.Single(report.Entries);
        Assert.Equal("claude", entry.Agent);
        Assert.Equal("seven_day", entry.WindowName);
        Assert.Equal(3, entry.SampleIntervals);
        Assert.Equal(6.0, entry.TotalDeltaPct, 3);
        Assert.Equal(6_000_000, entry.TotalInputTokens);
        Assert.Equal(1_800_000, entry.TotalOutputTokens);
        Assert.Equal(12, entry.TotalRequests);

        // tokens/% = 6,000,000 input / 6% drop = 1,000,000 input per 1%
        Assert.NotNull(entry.InputTokensPerPercent);
        Assert.Equal(1_000_000d, entry.InputTokensPerPercent!.Value, 0);
        Assert.NotNull(entry.OutputTokensPerPercent);
        Assert.Equal(300_000d, entry.OutputTokensPerPercent!.Value, 0);
        Assert.NotNull(entry.RequestsPerPercent);
        Assert.Equal(2d, entry.RequestsPerPercent!.Value, 3);

        // Implied full-window capacity = 100% × tokens/%
        Assert.NotNull(entry.EstimatedFullWindowInputTokens);
        Assert.Equal(100_000_000d, entry.EstimatedFullWindowInputTokens!.Value, 0);
        Assert.NotNull(entry.EstimatedFullWindowRequests);
        Assert.Equal(200d, entry.EstimatedFullWindowRequests!.Value, 3);

        // Latest sample is pct=94 at t=3h
        Assert.NotNull(entry.CurrentPct);
        Assert.Equal(94d, entry.CurrentPct!.Value, 3);

        // Burn-rate series carried back
        Assert.Equal(3, entry.Intervals.Count);
        Assert.All(entry.Intervals, iv => Assert.False(iv.IsWindowReset));
        Assert.All(entry.Intervals, iv => Assert.Equal(2.0, iv.DeltaPct, 3));
        Assert.All(entry.Intervals, iv => Assert.Equal(2_000_000, iv.InputTokens));

        // 3 counted intervals → Medium confidence (Low: 1-2, Medium: 3-9, High: 10+).
        Assert.Equal(CapacityConfidence.Medium, entry.Confidence);
    }

    /// <summary>
    /// Window reset (pct jumps UP between two samples) must be flagged and
    /// excluded from the burn-rate average, OR the implied capacity would
    /// be wildly wrong.
    /// </summary>
    [Fact]
    public async Task ComputeAsync_WindowReset_ExcludesIntervalFromAverage()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z"));
        var probe = new StubProbe(AgentKind.Claude, MakeSnapshot(pct: 50, sevenDayPct: 50));
        var usage = new FakeUsageStore();
        await using var plugin = await BuildPluginAsync([probe], usage, clock);

        // pct=50 → 40 over 1h, 1M input tokens consumed
        probe.Next = MakeSnapshot(pct: 50, sevenDayPct: 50);
        await plugin.SampleOnceAsync(default);
        AddEvents(usage, "claude", clock.GetUtcNow(), 1, inputPerEvent: 250_000, outputPerEvent: 50_000, count: 4);
        clock.Advance(TimeSpan.FromHours(1));
        probe.Next = MakeSnapshot(pct: 40, sevenDayPct: 40);
        await plugin.SampleOnceAsync(default);

        // pct=40 → 100 (window reset) over 1h, 0 tokens consumed (idle period before reset)
        clock.Advance(TimeSpan.FromHours(1));
        probe.Next = MakeSnapshot(pct: 100, sevenDayPct: 100);
        await plugin.SampleOnceAsync(default);

        // pct=100 → 90 over 1h, 1M tokens
        AddEvents(usage, "claude", clock.GetUtcNow(), 1, inputPerEvent: 250_000, outputPerEvent: 50_000, count: 4);
        clock.Advance(TimeSpan.FromHours(1));
        probe.Next = MakeSnapshot(pct: 90, sevenDayPct: 90);
        await plugin.SampleOnceAsync(default);

        var report = await plugin.ComputeAsync(new CapacityFilter
        {
            Agent = "claude",
            WindowName = "seven_day",
        }, default);

        var entry = Assert.Single(report.Entries);

        // Two counted intervals (both real consumption) — the reset boundary is NOT counted.
        Assert.Equal(2, entry.SampleIntervals);
        Assert.Equal(20d, entry.TotalDeltaPct, 3); // 10 + 10

        // The reset interval must be reflected in Intervals though, flagged.
        Assert.Equal(3, entry.Intervals.Count);
        Assert.Single(entry.Intervals, iv => iv.IsWindowReset);
        Assert.Equal(2, entry.Intervals.Count(iv => !iv.IsWindowReset));

        // Implied capacity = 2,000,000 tokens / 20% = 100,000/%, ×100 = 10,000,000
        Assert.NotNull(entry.EstimatedFullWindowInputTokens);
        Assert.Equal(10_000_000d, entry.EstimatedFullWindowInputTokens!.Value, 0);
    }

    /// <summary>
    /// Differences below the noise-floor (<see cref="CapacityFilter.MinDeltaPct"/>)
    /// must NOT be folded into the average — a 0.01% drop with a 1M-token call
    /// would otherwise produce a 10b-token "capacity".
    /// </summary>
    [Fact]
    public async Task ComputeAsync_DeltaBelowNoiseFloor_IsIgnored()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z"));
        var probe = new StubProbe(AgentKind.Claude, MakeSnapshot(pct: 50, sevenDayPct: 50));
        var usage = new FakeUsageStore();
        await using var plugin = await BuildPluginAsync([probe], usage, clock);

        // pct=50 → 49.99 (Δ=0.01, below default MinDeltaPct=0.25)
        await plugin.SampleOnceAsync(default);
        AddEvents(usage, "claude", clock.GetUtcNow(), 1, inputPerEvent: 1_000_000, outputPerEvent: 200_000, count: 1);
        clock.Advance(TimeSpan.FromHours(1));
        probe.Next = MakeSnapshot(pct: 49.99, sevenDayPct: 49.99);
        await plugin.SampleOnceAsync(default);

        // pct=49.99 → 45 (Δ=4.99, well above threshold) — 2M tokens
        AddEvents(usage, "claude", clock.GetUtcNow(), 1, inputPerEvent: 500_000, outputPerEvent: 100_000, count: 4);
        clock.Advance(TimeSpan.FromHours(1));
        probe.Next = MakeSnapshot(pct: 45, sevenDayPct: 45);
        await plugin.SampleOnceAsync(default);

        var report = await plugin.ComputeAsync(new CapacityFilter
        {
            Agent = "claude",
            WindowName = "seven_day",
        }, default);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(1, entry.SampleIntervals); // only the real drop counts
        Assert.Equal(4.99, entry.TotalDeltaPct, 3);
        Assert.Equal(2_000_000, entry.TotalInputTokens);

        // tokens/% computed off the 4.99% real drop, not the 5%-total
        Assert.NotNull(entry.InputTokensPerPercent);
        Assert.Equal(2_000_000 / 4.99, entry.InputTokensPerPercent!.Value, 0);
    }

    /// <summary>
    /// One sample is not enough to compute a capacity — the entry is still
    /// emitted (so the dashboard shows current pct + reset hint) but with
    /// <see cref="CapacityConfidence.None"/> and null estimates.
    /// </summary>
    [Fact]
    public async Task ComputeAsync_SingleSample_EmitsEntryWithoutEstimate()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z"));
        var probe = new StubProbe(AgentKind.Claude, MakeSnapshot(pct: 88, sevenDayPct: 88));
        var usage = new FakeUsageStore();
        await using var plugin = await BuildPluginAsync([probe], usage, clock);

        await plugin.SampleOnceAsync(default);

        var report = await plugin.ComputeAsync(new CapacityFilter
        {
            Agent = "claude",
            WindowName = "seven_day",
        }, default);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(0, entry.SampleIntervals);
        Assert.Null(entry.InputTokensPerPercent);
        Assert.Null(entry.EstimatedFullWindowInputTokens);
        Assert.Equal(88d, entry.CurrentPct!.Value, 3);
        Assert.Equal(CapacityConfidence.None, entry.Confidence);
        Assert.NotEmpty(entry.Notes);
    }

    /// <summary>
    /// When no usage events back the time-series (probe runs, usage store is
    /// empty), the calculator must NOT produce a "zero capacity" estimate —
    /// a provider-side drain with no captured local usage is an attribution
    /// gap, not real zero-token burn. The interval is still surfaced in the
    /// returned series for visibility, but it is excluded from the burn-rate
    /// denominator and a caveat is added to Notes.
    /// </summary>
    [Fact]
    public async Task ComputeAsync_NoUsageEvents_NoEstimateProduced()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z"));
        var probe = new StubProbe(AgentKind.Claude, MakeSnapshot(pct: 100, sevenDayPct: 100));
        var usage = new FakeUsageStore();
        await using var plugin = await BuildPluginAsync([probe], usage, clock);

        await plugin.SampleOnceAsync(default);
        clock.Advance(TimeSpan.FromHours(1));
        probe.Next = MakeSnapshot(pct: 97, sevenDayPct: 97);
        await plugin.SampleOnceAsync(default);

        var report = await plugin.ComputeAsync(new CapacityFilter
        {
            Agent = "claude",
            WindowName = "seven_day",
        }, default);

        var entry = Assert.Single(report.Entries);
        // The drop survived MinDeltaPct but no usage events were captured —
        // the interval is excluded from the burn-rate denominator and no
        // capacity estimate is produced. The interval still appears in the
        // returned series so the dashboard can show "drained, no attribution"
        // visibility, and Notes flags the attribution gap to the operator.
        Assert.Equal(0, entry.SampleIntervals);
        Assert.Null(entry.InputTokensPerPercent);
        Assert.Null(entry.EstimatedFullWindowInputTokens);
        Assert.Equal(CapacityConfidence.None, entry.Confidence);
        Assert.Single(entry.Intervals);
        Assert.Contains(entry.Notes, n => n.Contains("no matching", StringComparison.OrdinalIgnoreCase)
            || n.Contains("no local consumption", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The endpoint exposes per-agent overall capacity when WindowName is
    /// omitted — multiple agents produce multiple entries side by side.
    /// </summary>
    [Fact]
    public async Task ComputeAsync_MultipleAgents_OneEntryPerAgentWindow()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z"));
        var claudeProbe = new StubProbe(AgentKind.Claude, MakeSnapshot(pct: 100, sevenDayPct: 100));
        var codexProbe = new StubProbe(new AgentKind("codex"), MakeSnapshot(pct: 100, sevenDayPct: 100));
        var usage = new FakeUsageStore();
        await using var plugin = await BuildPluginAsync([claudeProbe, codexProbe], usage, clock);

        await plugin.SampleOnceAsync(default);
        AddEvents(usage, "claude", clock.GetUtcNow(), 1, inputPerEvent: 500_000, outputPerEvent: 100_000, count: 4);
        AddEvents(usage, "codex", clock.GetUtcNow(), 1, inputPerEvent: 200_000, outputPerEvent: 50_000, count: 2);
        clock.Advance(TimeSpan.FromHours(1));
        claudeProbe.Next = MakeSnapshot(pct: 98, sevenDayPct: 98);
        codexProbe.Next = MakeSnapshot(pct: 96, sevenDayPct: 96);
        await plugin.SampleOnceAsync(default);

        var report = await plugin.ComputeAsync(new CapacityFilter
        {
            WindowName = "seven_day",
        }, default);

        // One entry per agent for the seven_day window.
        Assert.Equal(2, report.Entries.Count);
        var claudeEntry = Assert.Single(report.Entries, e => e.Agent == "claude");
        var codexEntry = Assert.Single(report.Entries, e => e.Agent == "codex");
        Assert.Equal(1_000_000d, claudeEntry.InputTokensPerPercent!.Value, 0); // 2M / 2%
        Assert.Equal(100_000d, codexEntry.InputTokensPerPercent!.Value, 0); // 400k / 4%
    }

    /// <summary>
    /// IncludeIntervals=false suppresses the per-interval series in the
    /// response but MUST still return the aggregate totals AND the exhaustion
    /// projection. The auditor flagged that EstimatedExhaustionAt previously
    /// only fired when intervals were carried back — verify that the
    /// projection now works regardless of the IncludeIntervals payload knob.
    /// </summary>
    [Fact]
    public async Task ComputeAsync_IncludeIntervalsFalse_OmitsSeries_PreservesTotalsAndExhaustion()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z"));
        var probe = new StubProbe(AgentKind.Claude, MakeSnapshot(pct: 100, sevenDayPct: 100));
        var usage = new FakeUsageStore();
        await using var plugin = await BuildPluginAsync([probe], usage, clock);

        // 4 ticks at 1h spacing, dropping 5% each tick with usage events.
        probe.Next = MakeSnapshot(pct: 100, sevenDayPct: 100);
        await plugin.SampleOnceAsync(default);
        for (int i = 0; i < 3; i++)
        {
            AddEvents(usage, "claude", clock.GetUtcNow(), 1, inputPerEvent: 500_000, outputPerEvent: 100_000, count: 4);
            clock.Advance(TimeSpan.FromHours(1));
            probe.Next = MakeSnapshot(pct: 95 - i * 5, sevenDayPct: 95 - i * 5);
            await plugin.SampleOnceAsync(default);
        }

        var report = await plugin.ComputeAsync(new CapacityFilter
        {
            Agent = "claude",
            WindowName = "seven_day",
            IncludeIntervals = false,
        }, default);

        var entry = Assert.Single(report.Entries);
        Assert.Empty(entry.Intervals); // series suppressed
        Assert.Equal(3, entry.SampleIntervals);
        Assert.Equal(15d, entry.TotalDeltaPct, 3);
        Assert.NotNull(entry.InputTokensPerPercent);
        Assert.NotNull(entry.EstimatedFullWindowInputTokens);

        // Latest sample is pct=85 at t=3h; recent burn rate is 5% / hour,
        // so projected exhaustion ~17h from now.
        Assert.NotNull(entry.EstimatedExhaustionAt);
        var hours = (entry.EstimatedExhaustionAt!.Value - clock.GetUtcNow()).TotalHours;
        Assert.InRange(hours, 15, 20);
    }

    /// <summary>
    /// Model-scoped capacity: the calculator and SQL both narrow to the
    /// requested model_id. A model filter routes to the per-model probe rows
    /// AND to the per-model usage aggregate. A regression that mismatched the
    /// model id between the two sides — e.g. passing null to the usage store
    /// — would surface a wildly wrong tokens-per-percent here.
    /// </summary>
    [Fact]
    public async Task ComputeAsync_ModelFilter_NarrowsBothQuotaAndUsage()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-14T15:00:00Z"));
        var probe = new StubProbe(AgentKind.Claude, MakeSnapshotForModel("opus", pct: 100, sevenDayPct: 100));
        var usage = new FakeUsageStore();
        await using var plugin = await BuildPluginAsync([probe], usage, clock);

        // Cross-model events should NOT count when the filter narrows to opus.
        probe.Next = MakeSnapshotForModel("opus", pct: 100, sevenDayPct: 100);
        await plugin.SampleOnceAsync(default);

        // 2M opus tokens between snapshots; 500k haiku tokens at the same
        // timestamps must be excluded by the modelId filter.
        AddEvents(usage, "claude", clock.GetUtcNow(), 1, inputPerEvent: 500_000, outputPerEvent: 100_000, count: 4, model: "opus");
        AddEvents(usage, "claude", clock.GetUtcNow(), 1, inputPerEvent: 250_000, outputPerEvent: 50_000, count: 2, model: "haiku");

        clock.Advance(TimeSpan.FromHours(1));
        probe.Next = MakeSnapshotForModel("opus", pct: 98, sevenDayPct: 98);
        await plugin.SampleOnceAsync(default);

        var report = await plugin.ComputeAsync(new CapacityFilter
        {
            Agent = "claude",
            ModelId = "opus",
            WindowName = "seven_day",
        }, default);

        var entry = Assert.Single(report.Entries);
        Assert.Equal("opus", entry.ModelId);
        // Only the 2M opus tokens count (haiku is filtered out by ModelId).
        Assert.Equal(2_000_000, entry.TotalInputTokens);
        Assert.Equal(1_000_000d, entry.InputTokensPerPercent!.Value, 0); // 2M / 2%
        Assert.Equal(100_000_000d, entry.EstimatedFullWindowInputTokens!.Value, 0);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static AgentQuotaSnapshot MakeSnapshot(double pct, double sevenDayPct, double? fiveHourPct = null)
    {
        var windows = new List<WindowQuota>
        {
            new() { Name = "seven_day", AvailablePct = sevenDayPct },
        };
        if (fiveHourPct.HasValue)
            windows.Add(new WindowQuota { Name = "five_hour", AvailablePct = fiveHourPct.Value });
        return new AgentQuotaSnapshot
        {
            AvailablePct = pct,
            Windows = windows,
        };
    }

    /// <summary>Snapshot carrying per-model quota detail — drives the
    /// model-scoped time-series rows so model-filtered queries can pick them
    /// up. The probe still has an overall AvailablePct; the per-model bucket
    /// is what the calculator narrows to when ModelId is set on the filter.</summary>
    private static AgentQuotaSnapshot MakeSnapshotForModel(string modelId, double pct, double sevenDayPct)
        => new()
        {
            AvailablePct = pct,
            Windows = new List<WindowQuota>
            {
                new() { Name = "seven_day", AvailablePct = sevenDayPct },
            },
            PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
            {
                [modelId] = new ModelQuota
                {
                    AvailablePct = pct,
                    Windows = new List<WindowQuota>
                    {
                        new() { Name = "seven_day", AvailablePct = sevenDayPct },
                    },
                },
            },
        };

    private static void AddEvents(
        FakeUsageStore usage,
        string agent,
        DateTimeOffset baseTime,
        int totalHoursToFill,
        int inputPerEvent,
        int outputPerEvent,
        int count,
        string? model = null)
    {
        // Spread `count` events evenly across the interval.
        if (count <= 0) return;
        var dt = TimeSpan.FromHours((double)totalHoursToFill / Math.Max(1, count));
        for (int i = 0; i < count; i++)
        {
            usage.Add(new AgentUsageEvent
            {
                Id = Guid.NewGuid().ToString("n"),
                TimeUtc = baseTime + dt * i + TimeSpan.FromSeconds(1),
                AgentKind = agent,
                ModelId = model,
                InputTokens = inputPerEvent,
                OutputTokens = outputPerEvent,
                CostMicroCents = 0,
            });
        }
    }

    private async Task<StatisticsQuotaPlugin> BuildPluginAsync(
        IEnumerable<IAgentQuotaProbe> probes,
        IAgentUsageStore usage,
        TimeProvider timeProvider)
    {
        var configRoot = BuildConfig();
        var plugin = new StatisticsQuotaPlugin(
            probes, configRoot,
            quotaGate: null,
            usageStore: usage,
            timeProvider: timeProvider);
        await plugin.InitializeAsync(BuildPluginContext(configRoot));
        return plugin;
    }

    private IConfiguration BuildConfig()
    {
        var settings = new Dictionary<string, string?>
        {
            [$"CodeyBox:Plugins:{StatisticsQuotaPlugin.PluginId}:DatabasePath"] = _dbPath,
        };
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

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(Next);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class FakeUsageStore : IAgentUsageStore
    {
        private readonly List<AgentUsageEvent> _events = new();

        public void Add(AgentUsageEvent ev) => _events.Add(ev);

        public Task RecordAsync(AgentUsageEvent usage, CancellationToken ct = default)
        {
            _events.Add(usage);
            return Task.CompletedTask;
        }

        public Task<AgentUsageWindowAggregate> SumWindowAsync(
            string agentKind, string? modelId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
        {
            var matches = Matching(agentKind, modelId, fromUtc, toUtc).ToList();
            return Task.FromResult(new AgentUsageWindowAggregate(
                SumMicroCents: matches.Sum(e => e.CostMicroCents),
                EarliestUtc: matches.Count == 0 ? null : matches.Min(e => e.TimeUtc),
                Count: matches.Count));
        }

        public Task<AgentUsageWindowTokens> SumTokensWindowAsync(
            string agentKind, string? modelId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
        {
            var matches = Matching(agentKind, modelId, fromUtc, toUtc).ToList();
            if (matches.Count == 0) return Task.FromResult(AgentUsageWindowTokens.Empty);
            return Task.FromResult(new AgentUsageWindowTokens(
                InputTokens: matches.Sum(e => (long)e.InputTokens),
                CachedInputTokens: matches.Sum(e => (long)e.CachedInputTokens),
                OutputTokens: matches.Sum(e => (long)e.OutputTokens),
                SumMicroCents: matches.Sum(e => e.CostMicroCents),
                Count: matches.Count,
                EarliestUtc: matches.Min(e => e.TimeUtc)));
        }

        public Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default)
            => Task.FromResult(0);

        private IEnumerable<AgentUsageEvent> Matching(
            string agentKind, string? modelId, DateTimeOffset fromUtc, DateTimeOffset toUtc)
            => _events.Where(e =>
                string.Equals(e.AgentKind, agentKind, StringComparison.OrdinalIgnoreCase)
                && (modelId is null || string.Equals(e.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                && e.TimeUtc >= fromUtc && e.TimeUtc < toUtc);
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
