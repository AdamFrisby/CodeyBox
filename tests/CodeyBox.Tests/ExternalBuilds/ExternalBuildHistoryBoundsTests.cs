using CodeyBox.Core.ExternalBuilds;
using CodeyBox.Orchestrator.ExternalBuilds;

namespace CodeyBox.Tests.ExternalBuilds;

/// <summary>
/// Duration-history retention: the history table grows once per successful
/// build, so it must be pruned (stale-age plus oldest-first cap) and read
/// with a recency filter plus LIMIT — never as an unbounded full-table scan.
/// </summary>
public sealed class ExternalBuildHistoryBoundsTests
{
    private static readonly ExternalBuildTargetKey Target = new()
    {
        ProviderId = "fake-snapshot",
        TargetId = "fake-target",
        Configuration = "release",
    };

    private static ExternalBuildDurationSample SampleAt(DateTimeOffset at) =>
        new(Target, TimeSpan.FromMinutes(12), true, at, ColdCache: false);

    private static string NewDbPath(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "xb-hist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "state.db");
    }

    [Fact]
    public async Task Sqlite_PruneHistory_RemovesStale_AndTrimsOldestFirst()
    {
        var path = NewDbPath(out var dir);
        try
        {
            using var store = new SqliteExternalBuildStore(path);
            var now = DateTimeOffset.UtcNow;
            await store.RecordSampleAsync(SampleAt(now.AddDays(-60)));
            for (var i = 0; i < 5; i++)
                await store.RecordSampleAsync(SampleAt(now.AddHours(-i - 1)));

            var removed = await store.PruneHistoryAsync(now.AddDays(-30), maxRows: 3);

            Assert.Equal(3, removed);
            var remaining = await store.ListSamplesAsync();
            Assert.Equal(3, remaining.Count);
            Assert.Equal(now.AddHours(-1), remaining[0].CompletedAt);
            Assert.Equal(now.AddHours(-3), remaining[2].CompletedAt);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Sqlite_ListSamples_RespectsSinceAndLimit_NewestFirst()
    {
        var path = NewDbPath(out var dir);
        try
        {
            using var store = new SqliteExternalBuildStore(path);
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < 5; i++)
                await store.RecordSampleAsync(SampleAt(now.AddHours(-i - 1)));

            var page = await store.ListSamplesAsync(since: now.AddHours(-3), limit: 2);

            Assert.Equal(2, page.Count);
            Assert.Equal(now.AddHours(-1), page[0].CompletedAt);
            Assert.Equal(now.AddHours(-2), page[1].CompletedAt);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                store.ListSamplesAsync(limit: 0));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task InMemory_PruneHistory_MatchesSqliteSemantics()
    {
        var store = new InMemoryExternalBuildStore();
        var now = DateTimeOffset.UtcNow;
        await store.RecordSampleAsync(SampleAt(now.AddDays(-60)));
        for (var i = 0; i < 5; i++)
            await store.RecordSampleAsync(SampleAt(now.AddHours(-i - 1)));

        var removed = await store.PruneHistoryAsync(now.AddDays(-30), maxRows: 3);

        Assert.Equal(3, removed);
        var remaining = await store.ListSamplesAsync(since: now.AddDays(-30), limit: 1000);
        Assert.Equal(3, remaining.Count);
        Assert.Equal(now.AddHours(-1), remaining[0].CompletedAt);
    }

    [Fact]
    public void InMemoryHistory_Prune_DropsStaleAndExcess()
    {
        var history = new InMemoryExternalBuildHistory();
        var now = DateTimeOffset.UtcNow;
        history.Record(SampleAt(now.AddDays(-60)));
        for (var i = 0; i < 5; i++)
            history.Record(SampleAt(now.AddHours(-i - 1)));

        var removed = history.Prune(now.AddDays(-30), maxRows: 3);

        Assert.Equal(3, removed);
        Assert.Equal(3, history.Snapshot().Count);
    }

    [Fact]
    public async Task CleanupAsync_PrunesHistory_NotJustBuildRows()
    {
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var opts = ExternalBuildTestKit.Options(configure: o =>
        {
            o.HistoryRetentionDays = 30;
            o.MaxHistorySamples = 100;
        });
        var store = new InMemoryExternalBuildStore();
        var service = new ExternalBuildService(
            store, [new FakeSnapshotBuildProvider()], () => opts, clock);

        await store.RecordSampleAsync(SampleAt(clock.GetUtcNow().AddDays(-60)));
        for (var i = 0; i < 105; i++)
            await store.RecordSampleAsync(SampleAt(clock.GetUtcNow().AddHours(-i - 1)));

        await service.CleanupAsync();

        var remaining = await store.ListSamplesAsync();
        Assert.Equal(100, remaining.Count);
        Assert.All(remaining, s => Assert.True(s.CompletedAt >= clock.GetUtcNow().AddDays(-30)));
    }

    [Fact]
    public async Task DeliverCompletions_KeepsHistoryWithinConfiguredMax()
    {
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var opts = ExternalBuildTestKit.Options(configure: o => o.MaxHistorySamples = 100);
        var store = new InMemoryExternalBuildStore();
        var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 1 };
        var service = new ExternalBuildService(store, [provider], () => opts, clock);
        var history = new InMemoryExternalBuildHistory();
        var coordinator = new ExternalBuildParkCoordinator(service, store, history, () => opts, clock);

        for (var i = 0; i < 100; i++)
            await store.RecordSampleAsync(SampleAt(clock.GetUtcNow().AddMinutes(-i - 1)));

        var record = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        await service.ReconcileAsync(record.Id);
        var delivered = await coordinator.DeliverCompletionsAsync((r, kind, ct) => Task.CompletedTask);

        Assert.Single(delivered);
        Assert.True((await store.ListSamplesAsync()).Count <= 100);
        Assert.True(history.Snapshot().Count <= 100);
    }

    [Fact]
    public void HistoryOptions_InvalidWhenOutOfRange()
    {
        var tooSmall = ExternalBuildTestKit.Options(configure: o => o.MaxHistorySamples = 99);
        var tooLarge = ExternalBuildTestKit.Options(configure: o => o.MaxHistorySamples = 1_000_001);
        var badRetention = ExternalBuildTestKit.Options(configure: o => o.HistoryRetentionDays = 0);

        Assert.False(ExternalBuildOptions.IsValid(tooSmall));
        Assert.False(ExternalBuildOptions.IsValid(tooLarge));
        Assert.False(ExternalBuildOptions.IsValid(badRetention));
        Assert.True(ExternalBuildOptions.IsValid(ExternalBuildTestKit.Options()));
    }
}
