using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// The dispatch skip-id TEMP table used to be staged inside
/// <c>BeginTransaction()</c> (BEGIN IMMEDIATE), which takes the main
/// database's RESERVED lock even though TEMP writes need none of it. A
/// concurrent writer then surfaces as SQLITE_BUSY on the dispatch/health
/// read path. These tests pin the WAL-mode contract: a held IMMEDIATE
/// writer must not block listing, and skip-set filtering stays exact.
/// </summary>
public sealed class SqliteDispatchSkipTableLockTests : IDisposable
{
    private static readonly TimeSpan DispatchListTimeout = TimeSpan.FromSeconds(2);

    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-dispatch-skip-lock-");
    private readonly string _dbPath;
    private readonly SqliteWorkItemStore _store;

    public SqliteDispatchSkipTableLockTests()
    {
        _dbPath = _scratch.DbPath("dispatch-skip.db");
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
        _scratch.Dispose();
    }

    [Fact]
    public async Task ListDispatchEligibleWithSkipSet_CompletesWhileAnotherConnectionHoldsImmediateWriteLock()
    {
        var kept = Sample(priority: 10);
        var skipped = Sample(priority: 20);
        await _store.CreateAsync(kept);
        await _store.CreateAsync(skipped);

        await using var locker = await OpenExclusiveConnectionAsync(_dbPath);
        using var held = locker.BeginTransaction();
        AssertImmediateWriterBlocksOtherImmediate(_dbPath);

        var listed = await ListIncludingQuotaRetryAsync(
            new HashSet<WorkItemId> { skipped.Id },
            limit: 10);

        Assert.Contains(listed, item => item.Id == kept.Id);
        Assert.DoesNotContain(listed, item => item.Id == skipped.Id);
    }

    [Fact]
    public async Task ListDispatchEligible_EmptySkipSet_StillReturnsEligibleRowsUnderImmediateWriteLock()
    {
        var first = Sample(priority: 5);
        var second = Sample(priority: 1);
        await _store.CreateAsync(first);
        await _store.CreateAsync(second);

        await using var locker = await OpenExclusiveConnectionAsync(_dbPath);
        using var held = locker.BeginTransaction();
        AssertImmediateWriterBlocksOtherImmediate(_dbPath);

        var listed = await ListIncludingQuotaRetryAsync(
            new HashSet<WorkItemId>(),
            limit: 10);

        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, item => item.Id == first.Id);
        Assert.Contains(listed, item => item.Id == second.Id);
    }

    [Fact]
    public async Task ListDispatchEligible_SkipSetExcludesMatchingIdsAndKeepsOthers()
    {
        var high = Sample(priority: 100);
        var mid = Sample(priority: 50);
        var low = Sample(priority: 1);
        await _store.CreateAsync(high);
        await _store.CreateAsync(mid);
        await _store.CreateAsync(low);

        var skipped = await ListIncludingQuotaRetryAsync(
            new HashSet<WorkItemId> { high.Id, low.Id },
            limit: 10);
        Assert.Equal([mid.Id], skipped.Select(item => item.Id).ToArray());

        var emptySkip = await ListIncludingQuotaRetryAsync(
            new HashSet<WorkItemId>(),
            limit: 10);
        Assert.Equal([high.Id, mid.Id, low.Id], emptySkip.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task PickupSqliteBusy_LogsWarningAndRetriesOnNextTurnWithoutEscalating()
    {
        var busy = new SqliteBusyInjectingStore(_store);
        var log = new CapturingLogger<OrchestratorService>();
        using var svc = new OrchestratorService(
            new InMemoryTaskQueue(),
            busy,
            new CompletingPipeline(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                MaxConsecutiveDispatchGateTimeoutsBeforeEscalation = 1,
            },
            log);

        await _store.CreateAsync(Sample(priority: 1));

        Assert.Null(await svc.PickNextEligibleResilientAsync(CancellationToken.None));
        Assert.Null(await svc.PickNextEligibleResilientAsync(CancellationToken.None));

        Assert.Contains(
            log.Entries,
            e => e.Level == LogLevel.Warning
                 && e.Message.Contains("state database is locked", StringComparison.Ordinal)
                 && e.Exception is SqliteException sqlite
                 && sqlite.SqliteErrorCode == SqliteDefaults.SqliteBusy);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Critical);
    }

    private async Task<List<WorkItem>> ListIncludingQuotaRetryAsync(
        IReadOnlySet<WorkItemId> skipIds,
        int limit)
    {
        var sw = Stopwatch.StartNew();
        async Task<List<WorkItem>> CollectAsync()
        {
            var rows = new List<WorkItem>();
            await foreach (var item in _store.ListDispatchEligibleIncludingDueQuotaRetryByPriorityAsync(
                skipIds,
                DateTimeOffset.UtcNow,
                limit,
                DispatchCandidateOrdering.FinishingThenPriority))
            {
                rows.Add(item);
            }

            return rows;
        }

        var listed = await CollectAsync().WaitAsync(DispatchListTimeout);
        Assert.True(
            sw.Elapsed < DispatchListTimeout,
            $"dispatch list waited {sw.Elapsed} under a held BEGIN IMMEDIATE lock; the skip-table path must not take the main-database write lock.");
        return listed;
    }

    private static WorkItem Sample(int priority) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("dispatch-skip-lock"),
        Title = "t",
        Prompt = "p",
        Agent = AgentKind.Claude,
        State = WorkItemState.Queued,
        Priority = priority,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<SqliteConnection> OpenExclusiveConnectionAsync(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await conn.OpenAsync();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=0;";
        await pragma.ExecuteNonQueryAsync();

        using var mode = conn.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        var journal = Convert.ToString(await mode.ExecuteScalarAsync());
        Assert.Equal("wal", journal, ignoreCase: true);
        return conn;
    }

    private static void AssertImmediateWriterBlocksOtherImmediate(string dbPath)
    {
        using var probe = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        probe.Open();
        using (var pragma = probe.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=0;";
            pragma.ExecuteNonQuery();
        }

        var ex = Assert.Throws<SqliteException>(() => probe.BeginTransaction());
        Assert.Equal(SqliteDefaults.SqliteBusy, ex.SqliteErrorCode);
    }

    private sealed class CompletingPipeline(IWorkItemStore store) : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
            => store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }

    private sealed class SqliteBusyInjectingStore(SqliteWorkItemStore inner) : ForwardingWorkItemStore(inner)
    {
        public override IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(
            IReadOnlySet<WorkItemId> skipIds,
            DispatchCandidateOrdering ordering,
            CancellationToken ct = default)
            => throw new SqliteException(
                "SQLite Error 5: 'database is locked'.",
                errorCode: SqliteDefaults.SqliteBusy);
    }
}
