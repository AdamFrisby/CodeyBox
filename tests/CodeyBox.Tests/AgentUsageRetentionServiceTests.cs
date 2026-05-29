using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the hourly retention sweep prunes with a cutoff derived from the
/// live RetentionDays, and skips entirely when retention is disabled
/// (RetentionDays &lt;= 0).
/// </summary>
public sealed class AgentUsageRetentionServiceTests
{
    private static AgentBudgetOptions Opts(int retentionDays, params AgentBudgetWindowOptions[] windows)
    {
        var opts = new AgentBudgetOptions { RetentionDays = retentionDays };
        if (windows.Length > 0)
        {
            opts.Members["opencode"] = new AgentBudgetMemberOptions
            {
                Models =
                {
                    ["m"] = new AgentBudgetModelOptions { Windows = windows.ToList() },
                },
            };
        }
        return opts;
    }

    [Fact]
    public async Task RunSweep_PrunesWithCutoffDerivedFromRetentionDays()
    {
        var now = new DateTimeOffset(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);
        var store = new RecordingPruneStore();
        var service = new AgentUsageRetentionService(
            store, () => Opts(90), NullLogger<AgentUsageRetentionService>.Instance,
            interval: TimeSpan.FromHours(1), time: new FixedTime(now));

        await service.RunSweepAsync(CancellationToken.None);

        var cutoff = Assert.Single(store.PruneCutoffs);
        Assert.Equal(now.AddDays(-90), cutoff);
    }

    [Fact]
    public async Task RunSweep_RetentionDisabled_DoesNotPrune()
    {
        var store = new RecordingPruneStore();
        var service = new AgentUsageRetentionService(
            store, () => Opts(0), NullLogger<AgentUsageRetentionService>.Instance,
            time: new FixedTime(DateTimeOffset.UtcNow));

        await service.RunSweepAsync(CancellationToken.None);

        Assert.Empty(store.PruneCutoffs);
    }

    [Fact]
    public async Task RunSweep_ShortRetention_DoesNotPruneInsideActiveMonthlyWindow()
    {
        // Day 20 of the month with 7-day retention: the naive cutoff (now-7d) would
        // delete events from days 1–13, which the active Monthly budget window still
        // needs to SUM. The cutoff must clamp back to the 1st of the month so no
        // in-window spend is dropped (which would fail-open the cap).
        var now = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var store = new RecordingPruneStore();
        var service = new AgentUsageRetentionService(
            store, () => Opts(7, new AgentBudgetWindowOptions { Kind = BudgetWindowKind.Monthly, LimitCents = 100 }),
            NullLogger<AgentUsageRetentionService>.Instance,
            time: new FixedTime(now));

        await service.RunSweepAsync(CancellationToken.None);

        var cutoff = Assert.Single(store.PruneCutoffs);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), cutoff);
    }

    [Fact]
    public async Task RunSweep_ShortRetention_DoesNotPruneInsideActiveWeeklyWindow()
    {
        // Friday 2026-05-29 with 2-day retention: the naive cutoff (now-2d =
        // 2026-05-27) would delete events from Monday/Tuesday that the active
        // Weekly budget window still needs to SUM. The cutoff must clamp back to
        // the ISO-week Monday (2026-05-25 00:00 UTC). A wrong Monday-boundary
        // calculation would prune in-window spend and fail-open the cap.
        var now = new DateTimeOffset(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);
        var store = new RecordingPruneStore();
        var service = new AgentUsageRetentionService(
            store, () => Opts(2, new AgentBudgetWindowOptions { Kind = BudgetWindowKind.Weekly, LimitCents = 100 }),
            NullLogger<AgentUsageRetentionService>.Instance,
            time: new FixedTime(now));

        await service.RunSweepAsync(CancellationToken.None);

        var cutoff = Assert.Single(store.PruneCutoffs);
        Assert.Equal(new DateTimeOffset(2026, 5, 25, 0, 0, 0, TimeSpan.Zero), cutoff);
    }

    [Fact]
    public async Task RunSweep_ShortRetention_DoesNotPruneInsideActiveRollingWindow()
    {
        // 10-day (240h) Rolling window with 7-day retention: the naive cutoff
        // (now-7d) would delete events 7–10 days old that the active Rolling
        // window still needs to SUM. The cutoff must clamp back to now-240h. A
        // wrong now-Hours calculation would prune in-window spend and fail-open
        // the cap.
        var now = new DateTimeOffset(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);
        var store = new RecordingPruneStore();
        var service = new AgentUsageRetentionService(
            store, () => Opts(7, new AgentBudgetWindowOptions { Kind = BudgetWindowKind.Rolling, Hours = 240, LimitCents = 100 }),
            NullLogger<AgentUsageRetentionService>.Instance,
            time: new FixedTime(now));

        await service.RunSweepAsync(CancellationToken.None);

        var cutoff = Assert.Single(store.PruneCutoffs);
        Assert.Equal(now.AddHours(-240), cutoff);
    }

    [Fact]
    public async Task RunSweep_LongRetention_DoesNotExtendCutoffBeyondRetention()
    {
        // Retention (90d) is older than the active window start, so the window floor
        // does not loosen retention — the regular retention cutoff stands.
        var now = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var store = new RecordingPruneStore();
        var service = new AgentUsageRetentionService(
            store, () => Opts(90, new AgentBudgetWindowOptions { Kind = BudgetWindowKind.Monthly, LimitCents = 100 }),
            NullLogger<AgentUsageRetentionService>.Instance,
            time: new FixedTime(now));

        await service.RunSweepAsync(CancellationToken.None);

        var cutoff = Assert.Single(store.PruneCutoffs);
        Assert.Equal(now.AddDays(-90), cutoff);
    }

    [Fact]
    public async Task RunSweep_StoreThrows_DoesNotBubble_ButAttemptsPruneAndLogs()
    {
        var store = new ThrowingPruneStore();
        var logger = new CapturingLogger<AgentUsageRetentionService>();
        var service = new AgentUsageRetentionService(
            store, () => Opts(90), logger,
            time: new FixedTime(DateTimeOffset.UtcNow));

        // Sweep must swallow store failures so the background loop survives...
        await service.RunSweepAsync(CancellationToken.None);

        // ...but it must actually have attempted the prune (so the swallow is of a
        // real failure, not a no-op) and surfaced it as a warning for operators.
        Assert.True(store.PruneAttempted);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    private sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTime(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class RecordingPruneStore : IAgentUsageStore
    {
        public List<DateTimeOffset> PruneCutoffs { get; } = new();

        public Task RecordAsync(AgentUsageEvent usage, CancellationToken ct = default) => Task.CompletedTask;

        public Task<AgentUsageWindowAggregate> SumWindowAsync(
            string agentKind, string? modelId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
            => Task.FromResult(new AgentUsageWindowAggregate(0, null, 0));

        public Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default)
        {
            PruneCutoffs.Add(cutoffUtc);
            return Task.FromResult(0);
        }
    }

    private sealed class ThrowingPruneStore : IAgentUsageStore
    {
        public bool PruneAttempted { get; private set; }

        public Task RecordAsync(AgentUsageEvent usage, CancellationToken ct = default) => Task.CompletedTask;

        public Task<AgentUsageWindowAggregate> SumWindowAsync(
            string agentKind, string? modelId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
            => Task.FromResult(new AgentUsageWindowAggregate(0, null, 0));

        public Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default)
        {
            PruneAttempted = true;
            throw new InvalidOperationException("injected prune failure");
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
