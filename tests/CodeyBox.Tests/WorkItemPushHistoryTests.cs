using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Regression for the stale-record lease failure (d9951691): every
/// successful work-branch push must be recorded as the item's last-pushed
/// sha plus bounded history, so the next attempt proves the remote tip is
/// CodeyBox's own instead of misreporting it as third-party work.
/// Covers the pure history bookkeeping and its sqlite persistence
/// (round-trip, legacy NULL rows, corrupt payloads).
/// </summary>
public sealed class WorkItemPushHistoryTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-push-history-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "history test",
        Prompt = "do the thing",
        BaseBranch = "main",
        WorkBranch = "codeybox/history-test",
        PushUpstream = true,
    };

    private static string Sha(char c) => new(c, 40);

    [Fact]
    public void RecordPush_SetsLatestAndAppendsHistory()
    {
        var item = NewItem();

        var updated = WorkItemPushHistory.RecordPush(item, Sha('a'));

        Assert.Equal(Sha('a'), updated.LastPushedWorkBranchSha);
        Assert.Equal([Sha('a')], updated.PushedWorkBranchShaHistory);
        // Pure: the input is untouched.
        Assert.Null(item.LastPushedWorkBranchSha);
        Assert.Empty(item.PushedWorkBranchShaHistory);
    }

    [Fact]
    public void RecordPush_MovesRepeatToEndWithoutDuplicating()
    {
        var item = WorkItemPushHistory.RecordPush(NewItem(), Sha('a'));
        item = WorkItemPushHistory.RecordPush(item, Sha('b'));

        var updated = WorkItemPushHistory.RecordPush(item, Sha('a'));

        Assert.Equal(Sha('a'), updated.LastPushedWorkBranchSha);
        Assert.Equal([Sha('b'), Sha('a')], updated.PushedWorkBranchShaHistory);
    }

    [Fact]
    public void RecordPush_BoundsHistoryOldestDropsFirst()
    {
        var item = NewItem();
        item = WorkItemPushHistory.RecordPush(item, Sha('a'), maxEntries: 2);
        item = WorkItemPushHistory.RecordPush(item, Sha('b'), maxEntries: 2);

        var updated = WorkItemPushHistory.RecordPush(item, Sha('c'), maxEntries: 2);

        Assert.Equal(Sha('c'), updated.LastPushedWorkBranchSha);
        Assert.Equal([Sha('b'), Sha('c')], updated.PushedWorkBranchShaHistory);
    }

    [Fact]
    public void RecordPush_RejectsBlankAndImplausibleShas()
    {
        var item = NewItem();

        Assert.Same(item, WorkItemPushHistory.RecordPush(item, null));
        Assert.Same(item, WorkItemPushHistory.RecordPush(item, "   "));
        // Not hex / wrong length: untrusted forge output must never enter
        // the history lease decisions branch on.
        Assert.Same(item, WorkItemPushHistory.RecordPush(item, "not-a-sha"));
        Assert.Same(item, WorkItemPushHistory.RecordPush(item, new string('z', 40)));
        Assert.Same(item, WorkItemPushHistory.RecordPush(item, Sha('a')[..39]));
    }

    [Fact]
    public void Contains_MatchesLatestOrHistoryCaseInsensitively()
    {
        var item = WorkItemPushHistory.RecordPush(NewItem(), Sha('a'));
        item = WorkItemPushHistory.RecordPush(item, Sha('b'));

        Assert.True(WorkItemPushHistory.Contains(item, Sha('b')));
        Assert.True(WorkItemPushHistory.Contains(item, Sha('a')));
        Assert.True(WorkItemPushHistory.Contains(item, Sha('a').ToUpperInvariant()));
        Assert.False(WorkItemPushHistory.Contains(item, Sha('c')));
        Assert.False(WorkItemPushHistory.Contains(item, null));
    }

    [Fact]
    public async Task Store_RoundTripsPushedHistory()
    {
        var dbPath = Path.Combine(_workspace, "history.db");
        using var store = new SqliteWorkItemStore(dbPath);
        var item = WorkItemPushHistory.RecordPush(NewItem(), Sha('a'));
        item = WorkItemPushHistory.RecordPush(item, Sha('b'));
        await store.CreateAsync(item);

        var stored = (await store.GetAsync(item.Id))!;

        Assert.Equal(Sha('b'), stored.LastPushedWorkBranchSha);
        Assert.Equal([Sha('a'), Sha('b')], stored.PushedWorkBranchShaHistory);

        // Updates persist the history too (the push-record path writes via
        // UpdateAsync, not just CreateAsync).
        await store.UpdateAsync(WorkItemPushHistory.RecordPush(stored, Sha('c')));
        var reread = (await store.GetAsync(item.Id))!;
        Assert.Equal(Sha('c'), reread.LastPushedWorkBranchSha);
        Assert.Equal([Sha('a'), Sha('b'), Sha('c')], reread.PushedWorkBranchShaHistory);
    }

    [Fact]
    public async Task Store_LegacyNullHistoryReadsAsEmpty()
    {
        var dbPath = Path.Combine(_workspace, "legacy.db");
        using var store = new SqliteWorkItemStore(dbPath);
        var item = NewItem() with { LastPushedWorkBranchSha = Sha('a') };
        await store.CreateAsync(item);

        // Simulate a row predating the history column: NULL history with a
        // recorded latest sha still leases on the latest sha alone.
        await store.UpdateAsync(item with { PushedWorkBranchShaHistory = [] });
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE work_items SET pushed_work_branch_sha_history = NULL WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", item.Id.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        var stored = (await store.GetAsync(item.Id))!;

        Assert.Equal(Sha('a'), stored.LastPushedWorkBranchSha);
        Assert.Empty(stored.PushedWorkBranchShaHistory);
        Assert.True(WorkItemPushHistory.Contains(stored, Sha('a')));
    }
}
