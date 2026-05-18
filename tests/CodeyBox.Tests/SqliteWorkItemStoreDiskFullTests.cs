using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Tests;

/// <summary>
/// SQLite reports primary error code 13 (<c>SQLITE_FULL</c>) when the
/// database can't grow because the host disk is full. The store needs to
/// surface that as a typed <see cref="WorkItemStoreDiskFullException"/> so
/// the HTTP boundary returns a clean degraded-service response instead of
/// leaking a raw <see cref="SqliteException"/> stack trace, and so the
/// orchestrator's higher layers can refuse to accept further work.
///
/// We simulate SQLITE_FULL via the store's <c>ForceMaxPageCountForTesting</c>
/// hook — <c>PRAGMA max_page_count</c> is per-connection, so clamping it on
/// a separate connection wouldn't constrain the store's own writes.
/// </summary>
public sealed class SqliteWorkItemStoreDiskFullTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-diskfull-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task CreateAsync_TranslatesSqliteFull_ToTypedException()
    {
        using var store = new SqliteWorkItemStore(_dbPath);

        store.ForceMaxPageCountForTesting(1);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = new string('a', 64 * 1024),
            Agent = AgentKind.Claude,
        };

        var ex = await Assert.ThrowsAsync<WorkItemStoreDiskFullException>(() => store.CreateAsync(item));
        Assert.Equal("CreateAsync", ex.Operation);
        Assert.IsType<SqliteException>(ex.InnerException);
    }

    [Fact]
    public async Task UpdateAsync_TranslatesSqliteFull_ToTypedException()
    {
        using var store = new SqliteWorkItemStore(_dbPath);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            Agent = AgentKind.Claude,
        };
        await store.CreateAsync(item);

        store.ForceMaxPageCountForTesting(1);

        var bigPrompt = item with { Prompt = new string('z', 256 * 1024) };
        var ex = await Assert.ThrowsAsync<WorkItemStoreDiskFullException>(() => store.UpdateAsync(bigPrompt));
        Assert.Equal("UpdateAsync", ex.Operation);
    }
}
