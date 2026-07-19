using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Tests;

public sealed class SqliteWorkItemAttachmentStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-attachment-store-test-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _rawConn;
    private readonly SqliteWorkItemAttachmentStore _store;

    public SqliteWorkItemAttachmentStoreTests()
    {
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

        _store = new SqliteWorkItemAttachmentStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        _rawConn.Dispose();
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    private static WorkItemId NewId() => new(Guid.NewGuid());

    private void SeedWorkItem(WorkItemId id, WorkItemState state = WorkItemState.Queued, DateTimeOffset? updatedAt = null)
    {
        using var cmd = _rawConn.CreateCommand();
        cmd.CommandText = "INSERT INTO work_items (id, state, updated_at) VALUES ($id, $state, $now)";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$state", (int)state);
        cmd.Parameters.AddWithValue("$now", (updatedAt ?? DateTimeOffset.UtcNow).ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static WorkItemAttachmentRecord MakeRecord(WorkItemId workItemId, string suffix = "", string? sha256 = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        WorkItemId = workItemId,
        FileName = $"file{suffix}.txt",
        ContentType = "text/plain",
        SizeBytes = 100 + suffix.Length,
        Sha256 = sha256 ?? Guid.NewGuid().ToString("N"),
        Caption = $"Test caption {suffix}",
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task RoundTrip_CreateAndGet_StoresCorrectly()
    {
        var wi = NewId();
        SeedWorkItem(wi);
        var rec = MakeRecord(wi);

        await _store.CreateAsync(rec);
        var fetched = await _store.GetAsync(rec.Id);

        Assert.NotNull(fetched);
        Assert.Equal(rec.Id, fetched.Id);
        Assert.Equal(rec.WorkItemId, fetched.WorkItemId);
        Assert.Equal(rec.FileName, fetched.FileName);
        Assert.Equal(rec.ContentType, fetched.ContentType);
        Assert.Equal(rec.SizeBytes, fetched.SizeBytes);
        Assert.Equal(rec.Sha256, fetched.Sha256);
        Assert.Equal(rec.Caption, fetched.Caption);
        Assert.Equal(rec.CreatedAt.ToString("O"), fetched.CreatedAt.ToString("O"));
    }

    [Fact]
    public async Task ListForWorkItemAsync_ReturnsRowsOrderedByCreatedAndId()
    {
        var wi1 = NewId();
        var wi2 = NewId();
        SeedWorkItem(wi1);
        SeedWorkItem(wi2);

        var r1 = MakeRecord(wi1, "1") with { CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        var r2 = MakeRecord(wi1, "2") with { CreatedAt = DateTimeOffset.UtcNow };
        var r3 = MakeRecord(wi2, "3");

        await _store.CreateAsync(r1);
        await _store.CreateAsync(r2);
        await _store.CreateAsync(r3);

        var list = await _store.ListForWorkItemAsync(wi1);
        Assert.Equal(2, list.Count);
        Assert.Equal(r1.Id, list[0].Id);
        Assert.Equal(r2.Id, list[1].Id);
    }

    [Fact]
    public async Task AggregateForWorkItemAsync_ReturnsCorrectCountAndBytes()
    {
        var wi = NewId();
        SeedWorkItem(wi);

        var r1 = MakeRecord(wi, "1");
        var r2 = MakeRecord(wi, "2");

        await _store.CreateAsync(r1);
        await _store.CreateAsync(r2);

        var agg = await _store.AggregateForWorkItemAsync(wi);
        Assert.Equal(2, agg.Count);
        Assert.Equal(r1.SizeBytes + r2.SizeBytes, agg.TotalBytes);
    }

    [Fact]
    public async Task AggregateForWorkItemAsync_WithNoAttachments_ReturnsZeroes()
    {
        var wi = NewId();
        SeedWorkItem(wi);

        var agg = await _store.AggregateForWorkItemAsync(wi);
        Assert.Equal(0, agg.Count);
        Assert.Equal(0L, agg.TotalBytes);
    }

    [Fact]
    public async Task DeleteAsync_RemovesRecordAndReturnsIt()
    {
        var wi = NewId();
        SeedWorkItem(wi);
        var rec = MakeRecord(wi);

        await _store.CreateAsync(rec);
        var deleted = await _store.DeleteAsync(rec.Id);

        Assert.NotNull(deleted);
        Assert.Equal(rec.Id, deleted.Id);

        var fetched = await _store.GetAsync(rec.Id);
        Assert.Null(fetched);
    }

    [Fact]
    public async Task CountReferencesAsync_ReturnsCorrectCount()
    {
        var wi1 = NewId();
        var wi2 = NewId();
        SeedWorkItem(wi1);
        SeedWorkItem(wi2);

        var sha = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        var r1 = MakeRecord(wi1, "1", sha);
        var r2 = MakeRecord(wi2, "2", sha);
        var r3 = MakeRecord(wi1, "3"); // Different hash

        await _store.CreateAsync(r1);
        await _store.CreateAsync(r2);
        await _store.CreateAsync(r3);

        var count = await _store.CountReferencesAsync(sha);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CreateBatchIfUnderCapAsync_ReturnsFalseAndInsertsNoRows_WhenCapWouldBeExceeded()
    {
        var wi = NewId();
        SeedWorkItem(wi);
        var r1 = MakeRecord(wi, "1");
        var r2 = MakeRecord(wi, "2");

        var committed = await _store.CreateBatchIfUnderCapAsync(
            [r1, r2],
            maxCount: 1,
            maxTotalBytes: 1_000);

        Assert.False(committed);
        Assert.Empty(await _store.ListForWorkItemAsync(wi));
    }

    [Fact]
    public async Task CreateBatchIfUnderCapAsync_RollsBackWholeBatch_WhenSecondInsertFails()
    {
        var wi = NewId();
        SeedWorkItem(wi);
        var id = Guid.NewGuid().ToString("N");
        var r1 = MakeRecord(wi, "1") with { Id = id };
        var r2 = MakeRecord(wi, "2") with { Id = id };

        await Assert.ThrowsAsync<SqliteException>(() =>
            _store.CreateBatchIfUnderCapAsync([r1, r2], maxCount: 10, maxTotalBytes: 1_000));

        Assert.Empty(await _store.ListForWorkItemAsync(wi));
    }

    [Fact]
    public async Task CreateBatchForQueuedWorkItemIfUnderCapAsync_RejectsWhenWorkItemRacedOutOfQueued()
    {
        var wi = NewId();
        SeedWorkItem(wi, WorkItemState.Working);
        var record = MakeRecord(wi);

        var result = await _store.CreateBatchForQueuedWorkItemIfUnderCapAsync(
            [record],
            maxCount: 10,
            maxTotalBytes: 1_000);

        Assert.Equal(AttachmentMutationOutcome.StateMismatch, result.Outcome);
        Assert.Equal(WorkItemState.Working, result.CurrentState);
        Assert.Empty(await _store.ListForWorkItemAsync(wi));
    }

    [Fact]
    public async Task DeleteIfWorkItemQueuedAsync_RejectsWhenWorkItemRacedOutOfQueued()
    {
        var wi = NewId();
        SeedWorkItem(wi, WorkItemState.Queued);
        var record = MakeRecord(wi);
        await _store.CreateAsync(record);
        using (var cmd = _rawConn.CreateCommand())
        {
            cmd.CommandText = "UPDATE work_items SET state = $state WHERE id = $id;";
            cmd.Parameters.AddWithValue("$state", (int)WorkItemState.Working);
            cmd.Parameters.AddWithValue("$id", wi.ToString());
            cmd.ExecuteNonQuery();
        }

        var result = await _store.DeleteIfWorkItemQueuedAsync(record.Id, wi);

        Assert.Equal(AttachmentMutationOutcome.StateMismatch, result.Outcome);
        Assert.Equal(WorkItemState.Working, result.CurrentState);
        Assert.NotNull(await _store.GetAsync(record.Id));
    }

    [Fact]
    public async Task ListReferencedHashesAsync_ReturnsDistinctHashes()
    {
        var wi = NewId();
        SeedWorkItem(wi);

        var sha1 = "1111111111111111111111111111111111111111111111111111111111111111";
        var sha2 = "2222222222222222222222222222222222222222222222222222222222222222";

        var r1 = MakeRecord(wi, "1", sha1);
        var r2 = MakeRecord(wi, "2", sha1);
        var r3 = MakeRecord(wi, "3", sha2);

        await _store.CreateAsync(r1);
        await _store.CreateAsync(r2);
        await _store.CreateAsync(r3);

        var hashes = await _store.ListReferencedHashesAsync();
        Assert.Equal(2, hashes.Count);
        Assert.Contains(sha1, hashes);
        Assert.Contains(sha2, hashes);
    }

    [Fact]
    public async Task ListTerminalWithAttachmentsAsync_OnlyReturnsOlderTerminalItems()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);

        // WI 1: Done, updated 10 mins ago -> should be returned
        var wi1 = NewId();
        SeedWorkItem(wi1, WorkItemState.Done, DateTimeOffset.UtcNow.AddMinutes(-10));
        await _store.CreateAsync(MakeRecord(wi1));

        // WI 2: Done, updated just now -> should NOT be returned (not older than cutoff)
        var wi2 = NewId();
        SeedWorkItem(wi2, WorkItemState.Done, DateTimeOffset.UtcNow);
        await _store.CreateAsync(MakeRecord(wi2));

        // WI 3: Queued (not terminal), updated 10 mins ago -> should NOT be returned
        var wi3 = NewId();
        SeedWorkItem(wi3, WorkItemState.Queued, DateTimeOffset.UtcNow.AddMinutes(-10));
        await _store.CreateAsync(MakeRecord(wi3));

        // WI 4: Failed, updated 10 mins ago -> should be returned
        var wi4 = NewId();
        SeedWorkItem(wi4, WorkItemState.Failed, DateTimeOffset.UtcNow.AddMinutes(-10));
        await _store.CreateAsync(MakeRecord(wi4));

        var results = new List<WorkItemId>();
        await foreach (var item in _store.ListTerminalWithAttachmentsAsync(cutoff))
        {
            results.Add(item);
        }

        Assert.Equal(2, results.Count);
        Assert.Contains(wi1, results);
        Assert.Contains(wi4, results);
        Assert.DoesNotContain(wi2, results);
        Assert.DoesNotContain(wi3, results);
    }

    [Fact]
    public async Task ListCleanupCandidatesWithAttachmentsAsync_ReturnsTerminalOrOlderThanTtlItems()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);

        var terminalNew = NewId();
        SeedWorkItem(terminalNew, WorkItemState.Cancelled, DateTimeOffset.UtcNow);
        await _store.CreateAsync(MakeRecord(terminalNew, "terminal"));

        var nonTerminalOld = NewId();
        SeedWorkItem(nonTerminalOld, WorkItemState.Working, DateTimeOffset.UtcNow.AddMinutes(-10));
        await _store.CreateAsync(MakeRecord(nonTerminalOld, "old"));

        var nonTerminalNew = NewId();
        SeedWorkItem(nonTerminalNew, WorkItemState.Queued, DateTimeOffset.UtcNow);
        await _store.CreateAsync(MakeRecord(nonTerminalNew, "new"));

        var results = new List<WorkItemId>();
        await foreach (var item in _store.ListCleanupCandidatesWithAttachmentsAsync(cutoff))
            results.Add(item);

        Assert.Contains(terminalNew, results);
        Assert.Contains(nonTerminalOld, results);
        Assert.DoesNotContain(nonTerminalNew, results);
    }

    [Fact]
    public async Task DeleteAllForWorkItemAsync_RemovesAllAndReturnsThem()
    {
        var wi1 = NewId();
        var wi2 = NewId();
        SeedWorkItem(wi1);
        SeedWorkItem(wi2);

        var r1 = MakeRecord(wi1, "1");
        var r2 = MakeRecord(wi1, "2");
        var r3 = MakeRecord(wi2, "3");

        await _store.CreateAsync(r1);
        await _store.CreateAsync(r2);
        await _store.CreateAsync(r3);

        var deleted = await _store.DeleteAllForWorkItemAsync(wi1);
        Assert.Equal(2, deleted.Count);
        Assert.Contains(deleted, r => r.Id == r1.Id);
        Assert.Contains(deleted, r => r.Id == r2.Id);

        var list1 = await _store.ListForWorkItemAsync(wi1);
        Assert.Empty(list1);

        var list2 = await _store.ListForWorkItemAsync(wi2);
        Assert.Single(list2);
    }
}
