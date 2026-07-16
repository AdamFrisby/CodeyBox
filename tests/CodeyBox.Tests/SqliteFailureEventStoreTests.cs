using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Tests;

public sealed class SqliteFailureEventStoreTests : IDisposable
{
    private readonly List<string> _dbPaths = [];

    private string NewDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codeybox-failure-test-{Guid.NewGuid():N}.db");
        _dbPaths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _dbPaths)
        {
            try { File.Delete(path); } catch { /* best-effort */ }
            try { File.Delete(path + "-wal"); } catch { /* best-effort */ }
            try { File.Delete(path + "-shm"); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Creates a minimal parent work_items row so the failure_events FK is
    /// satisfiable. Must run before the store enables foreign_keys.
    /// </summary>
    private static void SeedWorkItemRow(string dbPath, WorkItemId id)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS work_items (id TEXT PRIMARY KEY);
            INSERT OR IGNORE INTO work_items (id) VALUES ($id);
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    private static FailureEventRecord Rec(WorkItemId id, string kind, DateTimeOffset at) => new()
    {
        WorkItemId = id,
        Agent = "claude",
        Phase = "Failed",
        FailureKind = kind,
        ErrorMessage = "err",
        OccurredAt = at,
    };

    [Fact]
    public async Task AppendThenQuery_RoundTripsAllFields_AndTruncatesErrorAt2000()
    {
        var dbPath = NewDbPath();
        var id = new WorkItemId(Guid.NewGuid());
        SeedWorkItemRow(dbPath, id);
        using var store = new SqliteFailureEventStore(dbPath);

        var longError = new string('x', 2500);
        var occurredAt = DateTimeOffset.UtcNow;
        var record = new FailureEventRecord
        {
            WorkItemId = id,
            Agent = "claude",
            Phase = "Failed",
            Iteration = 3,
            FailureKind = "agent",
            ErrorMessage = longError,
            SandboxName = "vm-42",
            Provider = "incus",
            OccurredAt = occurredAt,
        };
        await store.AppendAsync(record);

        var rows = await store.QueryAsync(since: null, kind: null, limit: 200);

        Assert.Single(rows);
        var r = rows[0];
        Assert.Equal(record.Id, r.Id);
        Assert.Equal(id, r.WorkItemId);
        Assert.Equal("claude", r.Agent);
        Assert.Equal("Failed", r.Phase);
        Assert.Equal(3, r.Iteration);
        Assert.Equal("agent", r.FailureKind);
        Assert.Equal(SqliteFailureEventStore.MaxErrorMessageLength, r.ErrorMessage!.Length);
        Assert.Equal(new string('x', SqliteFailureEventStore.MaxErrorMessageLength), r.ErrorMessage);
        Assert.Equal("vm-42", r.SandboxName);
        Assert.Equal("incus", r.Provider);
        Assert.Equal(occurredAt.ToUniversalTime(), r.OccurredAt);
    }

    [Fact]
    public async Task Append_NullableFields_StoreAndReadBackAsNull()
    {
        var dbPath = NewDbPath();
        var id = new WorkItemId(Guid.NewGuid());
        SeedWorkItemRow(dbPath, id);
        using var store = new SqliteFailureEventStore(dbPath);

        await store.AppendAsync(new FailureEventRecord
        {
            WorkItemId = id,
            Agent = null,
            Phase = "WaitingForQuotaReset",
            Iteration = null,
            FailureKind = null,
            ErrorMessage = null,
            SandboxName = null,
            Provider = null,
            OccurredAt = DateTimeOffset.UtcNow,
        });

        var r = Assert.Single(await store.QueryAsync(null, null, 200));
        Assert.Null(r.Agent);
        Assert.Null(r.Iteration);
        Assert.Null(r.FailureKind);
        Assert.Null(r.ErrorMessage);
        Assert.Null(r.SandboxName);
        Assert.Null(r.Provider);
        Assert.Equal("WaitingForQuotaReset", r.Phase);
    }

    [Fact]
    public async Task Query_FiltersBySinceAndKind_OrderedByOccurredAtDesc()
    {
        var dbPath = NewDbPath();
        var id = new WorkItemId(Guid.NewGuid());
        SeedWorkItemRow(dbPath, id);
        using var store = new SqliteFailureEventStore(dbPath);

        var t0 = DateTimeOffset.UtcNow;
        await store.AppendAsync(Rec(id, "agent", t0.AddMinutes(-10)));
        await store.AppendAsync(Rec(id, "quota", t0.AddMinutes(-1)));
        await store.AppendAsync(Rec(id, "agent", t0.AddSeconds(-1)));

        // since filter excludes the 10-minutes-ago row.
        var recent = await store.QueryAsync(since: t0.AddMinutes(-5), kind: null, limit: 200);
        Assert.Equal(2, recent.Count);
        Assert.True(recent[0].OccurredAt >= recent[1].OccurredAt, "results must be ordered occurred_at desc");

        // kind filter returns only the two "agent" rows.
        var agentOnly = await store.QueryAsync(since: null, kind: "agent", limit: 200);
        Assert.Equal(2, agentOnly.Count);
        Assert.All(agentOnly, r => Assert.Equal("agent", r.FailureKind));

        // combined filter: recent AND agent → the single most-recent row.
        var recentAgent = await store.QueryAsync(since: t0.AddMinutes(-5), kind: "agent", limit: 200);
        var only = Assert.Single(recentAgent);
        Assert.Equal("agent", only.FailureKind);
    }

    [Fact]
    public async Task Query_LimitBoundsRowCount()
    {
        var dbPath = NewDbPath();
        var id = new WorkItemId(Guid.NewGuid());
        SeedWorkItemRow(dbPath, id);
        using var store = new SqliteFailureEventStore(dbPath);

        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            await store.AppendAsync(Rec(id, "agent", t0.AddSeconds(-i)));

        var limited = await store.QueryAsync(since: null, kind: null, limit: 2);
        Assert.Equal(2, limited.Count);
    }

    [Fact]
    public async Task Transition_IntoFailure_EmitsOnce_Deduplicates_AndEmitsOnKindChange()
    {
        var dbPath = NewDbPath();
        SqliteFailureEventStore? failureStore = null;
        using var workStore = new SqliteWorkItemStore(dbPath, failureEventStore: () => failureStore);
        using var fStore = new SqliteFailureEventStore(dbPath);
        failureStore = fStore;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            Agent = AgentKind.Claude,
            SuspendedVmName = "vm-7",
        };
        await workStore.CreateAsync(item);

        // Creating a (non-failure) Queued item emits nothing.
        Assert.Empty(await fStore.QueryAsync(null, null, 200));

        // Queued → Failed: exactly one row.
        var failed = item.With(WorkItemState.Failed, "boom", failureKind: "agent");
        await workStore.UpdateAsync(failed);
        Assert.Single(await fStore.QueryAsync(null, null, 200));

        // Failed → Failed with identical state + kind + error: no additional row.
        await workStore.UpdateAsync(failed);
        Assert.Single(await fStore.QueryAsync(null, null, 200));

        // Failed → Failed with a changed FailureKind: one more row.
        var failedTimeout = item.With(WorkItemState.Failed, "boom", failureKind: "timeout");
        await workStore.UpdateAsync(failedTimeout);

        var rows = await fStore.QueryAsync(null, null, 200);
        Assert.Equal(2, rows.Count);
        Assert.Equal("timeout", rows[0].FailureKind); // most recent first
        Assert.Equal("agent", rows[1].FailureKind);
        Assert.All(rows, r => Assert.Equal(item.Id, r.WorkItemId));
        Assert.Equal("claude", rows[0].Agent);
        Assert.Equal(WorkItemState.Failed.ToString(), rows[0].Phase);
        Assert.Equal("vm-7", rows[0].SandboxName);
        Assert.Equal("boom", rows[0].ErrorMessage);
    }

    [Fact]
    public async Task Create_DirectlyIntoFailureState_EmitsOneRow()
    {
        var dbPath = NewDbPath();
        SqliteFailureEventStore? failureStore = null;
        using var workStore = new SqliteWorkItemStore(dbPath, failureEventStore: () => failureStore);
        using var fStore = new SqliteFailureEventStore(dbPath);
        failureStore = fStore;

        // A replay/import can persist a brand-new row already in a failure state.
        // That is an entry into failure with no prior row and must be logged.
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            Agent = AgentKind.Claude,
            SuspendedVmName = "vm-9",
        }.With(WorkItemState.Failed, "boom", failureKind: "agent");

        await workStore.CreateAsync(item);

        var row = Assert.Single(await fStore.QueryAsync(null, null, 200));
        Assert.Equal(item.Id, row.WorkItemId);
        Assert.Equal("agent", row.FailureKind);
        Assert.Equal("boom", row.ErrorMessage);
        Assert.Equal(WorkItemState.Failed.ToString(), row.Phase);
        Assert.Equal("vm-9", row.SandboxName);
    }

    [Fact]
    public async Task Transition_NeedsOperatorInput_IsNotRecordedAsFailure()
    {
        var dbPath = NewDbPath();
        SqliteFailureEventStore? failureStore = null;
        using var workStore = new SqliteWorkItemStore(dbPath, failureEventStore: () => failureStore);
        using var fStore = new SqliteFailureEventStore(dbPath);
        failureStore = fStore;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
        };
        await workStore.CreateAsync(item);

        // Operator park is deliberately excluded from failure history.
        await workStore.UpdateAsync(item.With(WorkItemState.NeedsOperatorInput));
        Assert.Empty(await fStore.QueryAsync(null, null, 200));
    }

    [Fact]
    public async Task Transition_ConditionalUpdate_ThatDoesNotApply_EmitsNothing()
    {
        var dbPath = NewDbPath();
        SqliteFailureEventStore? failureStore = null;
        using var workStore = new SqliteWorkItemStore(dbPath, failureEventStore: () => failureStore);
        using var fStore = new SqliteFailureEventStore(dbPath);
        failureStore = fStore;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
        };
        await workStore.CreateAsync(item); // persisted state = Queued

        // CAS guard expects Working; the row is Queued, so the write does not
        // apply and no failure event must be emitted.
        var failed = item.With(WorkItemState.Failed, "boom", failureKind: "agent");
        var applied = await workStore.TryUpdateIfStateAsync(failed, WorkItemState.Working);

        Assert.False(applied);
        Assert.Empty(await fStore.QueryAsync(null, null, 200));
    }

    [Fact]
    public async Task Transition_ConditionalUpdate_ThatApplies_IntoFailure_EmitsOneRow()
    {
        var dbPath = NewDbPath();
        SqliteFailureEventStore? failureStore = null;
        using var workStore = new SqliteWorkItemStore(dbPath, failureEventStore: () => failureStore);
        using var fStore = new SqliteFailureEventStore(dbPath);
        failureStore = fStore;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            Agent = AgentKind.Claude,
            SuspendedVmName = "vm-7",
        };
        await workStore.CreateAsync(item); // persisted state = Queued
        Assert.Empty(await fStore.QueryAsync(null, null, 200));

        // The state-only CAS is the third hooked persist method. A successful
        // guarded transition into a failure state must emit exactly one row —
        // the positive counterpart to the non-applying case above.
        var failed = item.With(WorkItemState.Failed, "boom", failureKind: "agent");
        var applied = await workStore.TryUpdateIfStateAsync(failed, WorkItemState.Queued);

        Assert.True(applied);
        var row = Assert.Single(await fStore.QueryAsync(null, null, 200));
        Assert.Equal(item.Id, row.WorkItemId);
        Assert.Equal("agent", row.FailureKind);
        Assert.Equal("boom", row.ErrorMessage);
        Assert.Equal(WorkItemState.Failed.ToString(), row.Phase);
        Assert.Equal("vm-7", row.SandboxName);

        // A repeated apply that leaves state + kind + error unchanged must not
        // duplicate the row (dedup on the conditional-update path).
        var reapplied = await workStore.TryUpdateIfStateAsync(failed, WorkItemState.Failed);
        Assert.True(reapplied);
        Assert.Single(await fStore.QueryAsync(null, null, 200));
    }

    [Fact]
    public async Task Transition_StampedConditionalUpdate_IntoFailure_EmitsOneRow()
    {
        var dbPath = NewDbPath();
        SqliteFailureEventStore? failureStore = null;
        using var workStore = new SqliteWorkItemStore(dbPath, failureEventStore: () => failureStore);
        using var fStore = new SqliteFailureEventStore(dbPath);
        failureStore = fStore;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            Agent = AgentKind.Claude,
            SuspendedVmName = "vm-11",
        };
        await workStore.CreateAsync(item); // persisted state = Queued
        var persisted = await workStore.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Empty(await fStore.QueryAsync(null, null, 200));

        // The recovery/quota path persists via the (state, updated_at)-stamped CAS.
        // A successful stamped transition into a failure state must emit exactly one
        // row, exercising the fourth hooked persist method end-to-end.
        var failed = persisted! with
        {
            State = WorkItemState.Failed,
            LastError = "boom",
            FailureKind = "agent",
            UpdatedAt = persisted.UpdatedAt.AddSeconds(1),
        };
        var applied = await workStore.TryUpdateIfStateAndUpdatedAtAsync(
            failed,
            WorkItemState.Queued,
            persisted.UpdatedAt);

        Assert.True(applied);
        var row = Assert.Single(await fStore.QueryAsync(null, null, 200));
        Assert.Equal(item.Id, row.WorkItemId);
        Assert.Equal("agent", row.FailureKind);
        Assert.Equal("boom", row.ErrorMessage);
        Assert.Equal(WorkItemState.Failed.ToString(), row.Phase);
        Assert.Equal("vm-11", row.SandboxName);
    }

    [Fact]
    public async Task Transition_AgentRestoreRetryClaim_IntoFailure_EmitsOneRow()
    {
        var dbPath = NewDbPath();
        SqliteFailureEventStore? failureStore = null;
        using var workStore = new SqliteWorkItemStore(dbPath, failureEventStore: () => failureStore);
        using var fStore = new SqliteFailureEventStore(dbPath);
        failureStore = fStore;

        // The agent-restore retry path persists via the fifth hooked method,
        // TryUpdateIfStateAndUpdatedAtWithAgentRestoreRetryClaimAsync. It requires
        // the current row to be Failed (its CAS guard and stale-claim check), so
        // the item is created already Failed — that INSERT logs one entry row.
        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            Agent = AgentKind.Claude,
            SuspendedVmName = "vm-restore",
            State = WorkItemState.Failed,
            FailureKind = "agent",
            LastError = "agent down",
            UpdatedAt = outageStart.AddMinutes(2),
        };
        await workStore.CreateAsync(item);
        Assert.Single(await fStore.QueryAsync(null, null, 200));

        var restoredAt = DateTimeOffset.UtcNow;
        Assert.True(await workStore.TryClaimAgentRestoreRetryAsync(
            item.Id, AgentKind.Claude, outageStart, restoredAt));

        // Retry the claimed item straight INTO another failure/park state. The
        // real retrier moves items OUT of failure, but a future caller must not be
        // able to slip a failure entry past the log through this persist path, so
        // an applied transition into WaitingForQuotaReset must emit exactly one
        // more row carrying the new kind/error.
        var requeuedIntoFailure = item with
        {
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            LastError = "still throttled",
            UpdatedAt = item.UpdatedAt.AddSeconds(1),
        };
        var applied = await workStore.TryUpdateIfStateAndUpdatedAtWithAgentRestoreRetryClaimAsync(
            requeuedIntoFailure,
            WorkItemState.Failed,
            item.UpdatedAt,
            AgentKind.Claude,
            outageStart,
            restoredAt);

        Assert.True(applied);
        var rows = await fStore.QueryAsync(null, null, 200);
        Assert.Equal(2, rows.Count);
        var latest = rows[0]; // occurred_at desc — the requeue entry is newest
        Assert.Equal(item.Id, latest.WorkItemId);
        Assert.Equal(WorkItemState.WaitingForQuotaReset.ToString(), latest.Phase);
        Assert.Equal("quota", latest.FailureKind);
        Assert.Equal("still throttled", latest.ErrorMessage);
        Assert.Equal("vm-restore", latest.SandboxName);
    }
}
