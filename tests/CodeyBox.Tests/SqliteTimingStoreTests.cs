using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Tests;

public sealed class SqliteTimingStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-timing-test-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _rawConn;
    private readonly SqliteTimingStore _store;

    public SqliteTimingStoreTests()
    {
        // Create the work_items parent table before SqliteTimingStore enables foreign_keys.
        // SqliteTimingStore.work_item_timings has REFERENCES work_items(id) ON DELETE CASCADE.
        _rawConn = new SqliteConnection($"Data Source={_dbPath}");
        _rawConn.Open();
        using var setupCmd = _rawConn.CreateCommand();
        setupCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS work_items (
                id         TEXT PRIMARY KEY,
                state      INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL DEFAULT ''
            );
            """;
        setupCmd.ExecuteNonQuery();

        _store = new SqliteTimingStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        _rawConn.Dispose();
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    private static WorkItemId NewId() => new(Guid.NewGuid());

    private void SeedWorkItem(WorkItemId id, WorkItemState state = WorkItemState.Done, DateTimeOffset? updatedAt = null)
    {
        using var cmd = _rawConn.CreateCommand();
        cmd.CommandText = "INSERT INTO work_items (id, state, updated_at) VALUES ($id, $state, $now)";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$state", (int)state);
        cmd.Parameters.AddWithValue("$now", (updatedAt ?? DateTimeOffset.UtcNow).ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static TimingRecord MakeRecord(WorkItemId id, string phase, string step, int? iter = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        WorkItemId = id,
        Phase = phase,
        Iteration = iter,
        Step = step,
        StartedAt = DateTimeOffset.UtcNow,
        MetadataJson = "{}",
    };

    private static async Task<List<TimingRecord>> CollectAsync(IAsyncEnumerable<TimingRecord> source)
    {
        var result = new List<TimingRecord>();
        await foreach (var item in source)
            result.Add(item);
        return result;
    }

    [Fact]
    public async Task RoundTrip_BeginAndEnd_StoresCorrectly()
    {
        var id = NewId();
        SeedWorkItem(id);
        var rec = MakeRecord(id, "work", "agent.exec");
        await _store.BeginAsync(rec);

        var rows = await _store.GetByWorkItemAsync(id);
        Assert.Single(rows);
        Assert.Equal(rec.Id, rows[0].Id);
        Assert.Equal("work", rows[0].Phase);
        Assert.Equal("agent.exec", rows[0].Step);
        Assert.Null(rows[0].EndedAt);
        Assert.Null(rows[0].DurationMs);

        var endedAt = DateTimeOffset.UtcNow;
        await _store.EndAsync(rec.Id, endedAt, 1234);

        var updated = await _store.GetByWorkItemAsync(id);
        Assert.Single(updated);
        Assert.NotNull(updated[0].EndedAt);
        Assert.Equal(1234L, updated[0].DurationMs);
    }

    [Fact]
    public async Task GetByWorkItemAsync_OnlyReturnsRowsForThatItem()
    {
        var id1 = NewId();
        var id2 = NewId();
        SeedWorkItem(id1);
        SeedWorkItem(id2);

        await _store.BeginAsync(MakeRecord(id1, "work", "agent.exec"));
        await _store.BeginAsync(MakeRecord(id2, "work", "agent.exec"));

        var rows1 = await _store.GetByWorkItemAsync(id1);
        var rows2 = await _store.GetByWorkItemAsync(id2);
        Assert.Single(rows1);
        Assert.Single(rows2);
        Assert.Equal(id1, rows1[0].WorkItemId);
        Assert.Equal(id2, rows2[0].WorkItemId);
    }

    [Fact]
    public async Task DeleteByWorkItemAsync_RemovesAllRowsForItem()
    {
        var id = NewId();
        SeedWorkItem(id);

        await _store.BeginAsync(MakeRecord(id, "work", "agent.exec"));
        await _store.BeginAsync(MakeRecord(id, "work", "git.clone"));
        await _store.DeleteByWorkItemAsync(id);

        var rows = await _store.GetByWorkItemAsync(id);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task DeleteByWorkItemAsync_DoesNotAffectOtherItems()
    {
        var id1 = NewId();
        var id2 = NewId();
        SeedWorkItem(id1);
        SeedWorkItem(id2);

        await _store.BeginAsync(MakeRecord(id1, "work", "agent.exec"));
        await _store.BeginAsync(MakeRecord(id2, "work", "agent.exec"));

        await _store.DeleteByWorkItemAsync(id1);

        var rows2 = await _store.GetByWorkItemAsync(id2);
        Assert.Single(rows2);
    }

    [Fact]
    public async Task Begin_InFlightRow_LeftWithNullEnd_IsVisibleAsIncomplete()
    {
        var id = NewId();
        SeedWorkItem(id);
        var rec = MakeRecord(id, "work", "agent.exec");
        await _store.BeginAsync(rec);

        var rows = await _store.GetByWorkItemAsync(id);
        Assert.Single(rows);
        Assert.Null(rows[0].EndedAt);
        Assert.Null(rows[0].DurationMs);
    }

    [Fact]
    public async Task RowsOrderedByStartedAt()
    {
        var id = NewId();
        SeedWorkItem(id);
        var r1 = MakeRecord(id, "work", "git.clone") with { StartedAt = DateTimeOffset.UtcNow };
        await Task.Delay(5);
        var r2 = MakeRecord(id, "work", "agent.exec") with { StartedAt = DateTimeOffset.UtcNow };

        await _store.BeginAsync(r1);
        await _store.BeginAsync(r2);

        var rows = await _store.GetByWorkItemAsync(id);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].StartedAt <= rows[1].StartedAt);
    }

    // ── StreamCompletedAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task StreamCompletedAsync_OnlyReturnsDoneWorkItemRows()
    {
        var doneId = NewId();
        var workingId = NewId();
        SeedWorkItem(doneId, WorkItemState.Done);
        SeedWorkItem(workingId, WorkItemState.Working);

        var r1 = MakeRecord(doneId, "work", "agent.exec");
        var r2 = MakeRecord(workingId, "work", "agent.exec");
        await _store.BeginAsync(r1);
        await _store.BeginAsync(r2);
        await _store.EndAsync(r1.Id, DateTimeOffset.UtcNow, 1000);
        await _store.EndAsync(r2.Id, DateTimeOffset.UtcNow, 2000);

        var rows = await CollectAsync(_store.StreamCompletedAsync(10));

        Assert.Single(rows);
        Assert.Equal(doneId, rows[0].WorkItemId);
    }

    [Fact]
    public async Task StreamCompletedAsync_ExcludesRowsWithNullDurationMs()
    {
        var id = NewId();
        SeedWorkItem(id, WorkItemState.Done);

        var inflight = MakeRecord(id, "work", "agent.exec"); // never ended — null duration_ms
        var completed = MakeRecord(id, "work", "vm.clone");
        await _store.BeginAsync(inflight);
        await _store.BeginAsync(completed);
        await _store.EndAsync(completed.Id, DateTimeOffset.UtcNow, 500);

        var rows = await CollectAsync(_store.StreamCompletedAsync(10));

        Assert.Single(rows);
        Assert.Equal("vm.clone", rows[0].Step);
    }

    [Fact]
    public async Task StreamCompletedAsync_BoundedByWorkItemLimit()
    {
        var t = DateTimeOffset.UtcNow;
        var id1 = NewId();
        var id2 = NewId();
        var id3 = NewId();
        SeedWorkItem(id1, WorkItemState.Done, t.AddMinutes(-10));
        SeedWorkItem(id2, WorkItemState.Done, t.AddMinutes(-5));
        SeedWorkItem(id3, WorkItemState.Done, t);

        foreach (var id in new[] { id1, id2, id3 })
        {
            var r = MakeRecord(id, "work", "agent.exec");
            await _store.BeginAsync(r);
            await _store.EndAsync(r.Id, DateTimeOffset.UtcNow, 1000);
        }

        // Limit=2: only the 2 most-recently-updated Done items (id2 and id3).
        var rows = await CollectAsync(_store.StreamCompletedAsync(2));

        Assert.Equal(2, rows.Count);
        var returnedIds = rows.Select(r => r.WorkItemId).ToHashSet();
        Assert.Contains(id2, returnedIds);
        Assert.Contains(id3, returnedIds);
        Assert.DoesNotContain(id1, returnedIds);
    }
}
