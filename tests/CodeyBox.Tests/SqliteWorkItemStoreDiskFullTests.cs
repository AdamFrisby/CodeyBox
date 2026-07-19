using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Events;

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
///
/// Also asserts the audit-log emission: <c>AuditLog.StoreDiskFull</c> is the
/// CRIT signal operators alert on. Without that assertion a refactor that
/// moves the throw before the audit call (or drops the audit call entirely)
/// would silently break the operator's only "host out of state-store disk"
/// alarm while leaving the exception type assertion green.
/// </summary>
public sealed class SqliteWorkItemStoreDiskFullTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-diskfull-{Guid.NewGuid():N}.db");
    private readonly TestSink _sink = new();

    private Serilog.ILogger ConfigureLogger()
        => new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();

    public void Dispose()
    {
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    [Fact]
    public async Task CreateAsync_TranslatesSqliteFull_ToTypedException()
    {
        var logger = ConfigureLogger();
        _sink.Clear();
        using var store = new SqliteWorkItemStore(_dbPath, logger);

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
        await AssertAuditEmittedAsync("CreateAsync");
    }

    [Fact]
    public async Task UpdateAsync_TranslatesSqliteFull_ToTypedException()
    {
        var logger = ConfigureLogger();
        _sink.Clear();
        using var store = new SqliteWorkItemStore(_dbPath, logger);

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

        // UpdateAsync no longer writes the prompt column (see IWorkItemStore
        // docstring — TryReplacePromptAsync is the dedicated path). Force the
        // page-count cap via a large LastError instead, which is still part
        // of the full-row UPDATE.
        var bigError = item with { LastError = new string('z', 256 * 1024) };
        var ex = await Assert.ThrowsAsync<WorkItemStoreDiskFullException>(() => store.UpdateAsync(bigError));
        Assert.Equal("UpdateAsync", ex.Operation);
        Assert.IsType<SqliteException>(ex.InnerException);
        await AssertAuditEmittedAsync("UpdateAsync");
    }

    /// <summary>
    /// TryUpdateIfStateAsync is the orchestrator's primary persistence call for
    /// state transitions (every WorkItemRetrier.RetryAsync goes through it),
    /// so the SQLITE_FULL translation must hold here too. Without this test a
    /// refactor that reorders the catches or drops the error-code comparison
    /// would not be caught.
    /// </summary>
    [Fact]
    public async Task TryUpdateIfStateAsync_TranslatesSqliteFull_ToTypedException()
    {
        var logger = ConfigureLogger();
        _sink.Clear();
        using var store = new SqliteWorkItemStore(_dbPath, logger);

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

        // Force a write large enough that SQLite has to grow the file and
        // cannot satisfy that growth under max_page_count=1. TryUpdateIfStateAsync
        // no longer writes the prompt column; LastError is the convenient
        // unbounded text field that still flows through the full-row UPDATE.
        var bigError = item with { LastError = new string('z', 256 * 1024) };
        var ex = await Assert.ThrowsAsync<WorkItemStoreDiskFullException>(
            () => store.TryUpdateIfStateAsync(bigError, item.State));
        Assert.Equal("TryUpdateIfStateAsync", ex.Operation);
        Assert.IsType<SqliteException>(ex.InnerException);
        await AssertAuditEmittedAsync("TryUpdateIfStateAsync");
    }

    [Fact]
    public async Task TryReplaceKnobsIfStateAndUpdatedAtAsync_TranslatesSqliteFull_ToTypedException()
    {
        var logger = ConfigureLogger();
        _sink.Clear();
        using var store = new SqliteWorkItemStore(_dbPath, logger);

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

        var knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["largeKnob"] = new string('k', 256 * 1024),
        };
        var ex = await Assert.ThrowsAsync<WorkItemStoreDiskFullException>(
            () => store.TryReplaceKnobsIfStateAndUpdatedAtAsync(
                item.Id,
                knobs,
                item.UpdatedAt.AddSeconds(1),
                item.State,
                item.UpdatedAt));
        Assert.Equal("TryReplaceKnobsIfStateAndUpdatedAtAsync", ex.Operation);
        Assert.IsType<SqliteException>(ex.InnerException);
        await AssertAuditEmittedAsync("TryReplaceKnobsIfStateAndUpdatedAtAsync");
    }

    [Fact]
    public async Task TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync_TranslatesSqliteFull_ToTypedException()
    {
        var logger = ConfigureLogger();
        _sink.Clear();
        using var store = new SqliteWorkItemStore(_dbPath, logger);

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

        var updated = item with
        {
            UpdatedAt = item.UpdatedAt.AddSeconds(1),
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["largeKnob"] = new string('k', 256 * 1024),
            },
        };
        var ex = await Assert.ThrowsAsync<WorkItemStoreDiskFullException>(
            () => store.TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync(
                updated,
                item.State,
                item.UpdatedAt));
        Assert.Equal("TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync", ex.Operation);
        Assert.IsType<SqliteException>(ex.InnerException);
        await AssertAuditEmittedAsync("TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync");
    }

    private async Task AssertAuditEmittedAsync(string expectedOperation)
    {
        LogEvent? evt = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (evt is null && DateTimeOffset.UtcNow < deadline)
        {
            evt = _sink.Events.FirstOrDefault(e =>
                e.Properties.TryGetValue("EventName", out var name)
                && name is ScalarValue sv
                && (string?)sv.Value == "store.disk_full");
            if (evt is null)
                await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.NotNull(evt);
        Assert.Equal(LogEventLevel.Fatal, evt!.Level);
        Assert.True(evt.Properties.TryGetValue("Audit", out var auditProp));
        Assert.True(auditProp is ScalarValue audit && audit.Value is bool b && b,
            "store.disk_full event must be tagged Audit=true so the audit sink picks it up");
        Assert.True(evt.Properties.TryGetValue("Operation", out var opProp));
        Assert.Equal(expectedOperation,
            (opProp as ScalarValue)?.Value as string);
    }
}
