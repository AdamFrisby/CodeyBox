using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Durability tests for <see cref="SqliteAuditCheckPublicationStore"/> through
/// real SQLite files: round-trips, restart recovery, concurrent writers,
/// lost-response convergence (no duplicate identity), the stale-completion
/// guard, no-regress upserts, and bounded retry listing.
/// </summary>
public sealed class SqliteAuditCheckPublicationStoreTests : IDisposable
{
    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-checkpub-store-");
    private string DbPath => _scratch.DbPath("checkpub-store.db");
    private readonly SqliteAuditCheckPublicationStore _store;

    public SqliteAuditCheckPublicationStoreTests()
    {
        _store = new SqliteAuditCheckPublicationStore(DbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(DbPath); } catch { }
        TestScratchDirectory.DeleteSqliteCompanions(DbPath);
        _scratch.Dispose();
    }

    private static AuditCheckPublicationRecord Make(
        int iteration = 1,
        int attempt = 1,
        string scope = "aggregate",
        AuditCheckPublicationState state = AuditCheckPublicationState.Pending) => new()
        {
            Repository = "myorg/myrepo",
            HeadSha = "0123456789abcdef0123456789abcdef01234567",
            WorkItemId = "wi-1",
            Target = "code",
            Iteration = iteration,
            Attempt = attempt,
            Scope = scope,
            CheckName = "codeybox-audit",
            ExternalId = $"codeybox/wi-1/code/{iteration}/{attempt}/{scope}/sha",
            State = state,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

    private Task<AuditCheckPublicationRecord?> Get(
        int iteration = 1, int attempt = 1, string scope = "aggregate") =>
        _store.GetAsync("myorg/myrepo", "0123456789abcdef0123456789abcdef01234567",
            "wi-1", "code", iteration, attempt, scope);

    [Fact]
    public async Task RoundTrip_PreservesAllFields()
    {
        var record = Make() with
        {
            CheckRunId = 4242,
            Status = "completed",
            Conclusion = "failure",
            AnnotationsPublished = 12,
            BatchesSent = 1,
            LastBatchUncertain = true,
            BlockedReason = "denied",
            LastError = "boom",
            TransportAttempts = 3,
            NextRetryUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        await _store.UpsertAsync(record);

        var loaded = await Get();
        Assert.NotNull(loaded);
        Assert.Equal(4242, loaded.CheckRunId);
        Assert.Equal("failure", loaded.Conclusion);
        Assert.Equal(12, loaded.AnnotationsPublished);
        Assert.True(loaded.LastBatchUncertain);
        Assert.Equal("denied", loaded.BlockedReason);
        Assert.Equal(3, loaded.TransportAttempts);
        Assert.NotNull(loaded.NextRetryUtc);
        Assert.Equal(AuditCheckPublicationState.Pending, loaded.State);
    }

    [Fact]
    public async Task Restart_ReopensDurableRows()
    {
        await _store.UpsertAsync(Make(iteration: 2) with
        {
            State = AuditCheckPublicationState.Completed,
            CheckRunId = 777,
        });
        _store.Dispose();

        using var reopened = new SqliteAuditCheckPublicationStore(DbPath);
        var loaded = await reopened.GetAsync("myorg/myrepo",
            "0123456789abcdef0123456789abcdef01234567", "wi-1", "code", 2, 1, "aggregate");
        Assert.NotNull(loaded);
        Assert.Equal(AuditCheckPublicationState.Completed, loaded.State);
        Assert.Equal(777, loaded.CheckRunId);
    }

    [Fact]
    public async Task ConcurrentWriters_DoNotCorruptOrLoseRows()
    {
        var tasks = Enumerable.Range(0, 20).Select(i =>
            _store.UpsertAsync(Make(iteration: 1, attempt: 1, scope: $"auditor-{i % 5}") with
            {
                TransportAttempts = i,
            }));
        await Task.WhenAll(tasks);

        for (var i = 0; i < 5; i++)
        {
            var loaded = await Get(scope: $"auditor-{i}");
            Assert.NotNull(loaded);
        }
    }

    [Fact]
    public async Task ConcurrentSameKeyUpserts_ConvergeOnStoredIdentity()
    {
        // Lost create responses converge: every racing writer carries the same
        // forge check-run id, and the row keeps it (never nulled, never lost).
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            _store.UpsertAsync(Make() with { CheckRunId = 999, State = AuditCheckPublicationState.Pending })));
        var loaded = await Get();
        Assert.NotNull(loaded);
        Assert.Equal(999, loaded.CheckRunId);
    }

    [Fact]
    public async Task TryComplete_StaleCompletionDoesNotOverwriteNewerRun()
    {
        // Iteration 2 is already being published when iteration 1's delayed
        // completion arrives: the stale write must not land.
        await _store.UpsertAsync(Make(iteration: 1) with { State = AuditCheckPublicationState.Pending });
        await _store.UpsertAsync(Make(iteration: 2) with { State = AuditCheckPublicationState.Pending });

        var stale = Make(iteration: 1) with
        {
            State = AuditCheckPublicationState.Completed,
            CheckRunId = 111,
            Status = "completed",
        };
        Assert.False(await _store.TryCompleteAsync(stale));

        var older = await Get(iteration: 1);
        Assert.NotNull(older);
        Assert.Equal(AuditCheckPublicationState.Pending, older.State);
        Assert.Null(older.CheckRunId);
    }

    [Fact]
    public async Task TryComplete_NewerAttempt_CompletesNormally()
    {
        await _store.UpsertAsync(Make(iteration: 2, attempt: 1));
        await _store.UpsertAsync(Make(iteration: 2, attempt: 2));
        var done = Make(iteration: 2, attempt: 2) with
        {
            State = AuditCheckPublicationState.Completed,
            CheckRunId = 222,
            Status = "completed",
            Conclusion = "success",
        };
        Assert.True(await _store.TryCompleteAsync(done));
        var loaded = await Get(iteration: 2, attempt: 2);
        Assert.NotNull(loaded);
        Assert.Equal(AuditCheckPublicationState.Completed, loaded.State);
        Assert.Equal(222, loaded.CheckRunId);
    }

    [Fact]
    public async Task Upsert_NeverRegressesCompletedRow()
    {
        await _store.UpsertAsync(Make() with
        {
            State = AuditCheckPublicationState.Completed,
            CheckRunId = 333,
        });
        await _store.UpsertAsync(Make() with { State = AuditCheckPublicationState.Pending });

        var loaded = await Get();
        Assert.NotNull(loaded);
        Assert.Equal(AuditCheckPublicationState.Completed, loaded.State);
        Assert.Equal(333, loaded.CheckRunId);
    }

    [Fact]
    public async Task Upsert_PreservesProvenCheckRunId()
    {
        await _store.UpsertAsync(Make() with { CheckRunId = 444 });
        await _store.UpsertAsync(Make() with
        {
            State = AuditCheckPublicationState.AwaitingRetry,
            NextRetryUtc = DateTimeOffset.UtcNow.AddMinutes(1),
        });

        var loaded = await Get();
        Assert.NotNull(loaded);
        Assert.Equal(444, loaded.CheckRunId);
        Assert.Equal(AuditCheckPublicationState.AwaitingRetry, loaded.State);
    }

    [Fact]
    public async Task ListDueForRetry_ReturnsOnlyDueRowsBounded()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertAsync(Make(scope: "due") with
        {
            State = AuditCheckPublicationState.AwaitingRetry,
            NextRetryUtc = now.AddMinutes(-1),
        });
        await _store.UpsertAsync(Make(scope: "future") with
        {
            State = AuditCheckPublicationState.AwaitingRetry,
            NextRetryUtc = now.AddHours(1),
        });
        await _store.UpsertAsync(Make(scope: "done") with
        {
            State = AuditCheckPublicationState.Completed,
        });

        var due = await _store.ListDueForRetryAsync(now, 10);
        Assert.Single(due);
        Assert.Equal("due", due[0].Scope);

        var bounded = await _store.ListDueForRetryAsync(now, 1);
        Assert.Single(bounded);
    }
}
