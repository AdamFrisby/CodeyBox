using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Round-trip coverage for the SQLite-backed fallback-history store. Every
/// other test uses the in-memory variant, so without these tests a regression
/// in column ordinals, nullable handling, or the DateTimeOffset format would
/// surface only in production as missing rows on the /workitems/{id} read.
/// </summary>
public sealed class SqliteAgentFallbackHistoryStoreTests : IDisposable
{
    private readonly string _workspace;

    public SqliteAgentFallbackHistoryStoreTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-fallbackdb-").FullName;

    public void Dispose() { CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace); }

    [Fact]
    public async Task RecordAndList_PopulatedRecord_RoundTripsAllFields()
    {
        // Asserts every field survives the SQLite write+read with the correct
        // column ordinal. A swap of two columns (e.g., to_agent and to_model)
        // would not be caught by a count-only test.
        using var store = NewStore();

        var workItemId = WorkItemId.New();
        var occurredAt = new DateTimeOffset(2026, 4, 1, 12, 30, 45, 123, TimeSpan.Zero);
        var record = new AgentFallbackRecord(
            Id: Guid.NewGuid(),
            WorkItemId: workItemId,
            Phase: "work",
            Iteration: 3,
            FromAgent: AgentKind.Codex,
            FromModel: "gpt-5",
            ToAgent: AgentKind.Claude,
            ToModel: "claude-sonnet-4",
            Reason: "rate_limit_exceeded",
            OccurredAt: occurredAt);

        await store.RecordAsync(record);
        var rows = await store.ListByWorkItemAsync(workItemId);

        var got = Assert.Single(rows);
        Assert.Equal(record.Id, got.Id);
        Assert.Equal(workItemId, got.WorkItemId);
        Assert.Equal("work", got.Phase);
        Assert.Equal(3, got.Iteration);
        Assert.Equal(AgentKind.Codex, got.FromAgent);
        Assert.Equal("gpt-5", got.FromModel);
        Assert.Equal(AgentKind.Claude, got.ToAgent);
        Assert.Equal("claude-sonnet-4", got.ToModel);
        Assert.Equal("rate_limit_exceeded", got.Reason);
        // OccurredAt must round-trip to the same UTC instant; a regression that
        // drops sub-second precision or applies a local-timezone shift would
        // show up here.
        Assert.Equal(occurredAt.ToUniversalTime(), got.OccurredAt.ToUniversalTime());
    }

    [Fact]
    public async Task RecordAndList_AllNullablesNull_RoundTripsAsNull()
    {
        // The "all members exhausted, parked in WaitingForQuotaReset" event
        // sets ToAgent / ToModel / Iteration / FromModel to null. If any
        // IsDBNull branch in ListByWorkItemAsync is wrong, the row will come
        // back with a fabricated AgentKind("") or an exception.
        using var store = NewStore();

        var workItemId = WorkItemId.New();
        var record = new AgentFallbackRecord(
            Id: Guid.NewGuid(),
            WorkItemId: workItemId,
            Phase: "rework",
            Iteration: null,
            FromAgent: AgentKind.Gemini,
            FromModel: null,
            ToAgent: null,
            ToModel: null,
            Reason: "all members exhausted",
            OccurredAt: DateTimeOffset.UtcNow);

        await store.RecordAsync(record);
        var rows = await store.ListByWorkItemAsync(workItemId);

        var got = Assert.Single(rows);
        Assert.Null(got.Iteration);
        Assert.Null(got.FromModel);
        Assert.Null(got.ToAgent);
        Assert.Null(got.ToModel);
        Assert.Equal(AgentKind.Gemini, got.FromAgent);
    }

    [Fact]
    public async Task ListByWorkItem_FiltersByIdAndOrdersByOccurredAt()
    {
        // Three rows: two for our work item written out of order, one for a
        // different work item. The query must return only the matching
        // work item's rows and order them by occurred_at ASC. A regression
        // that drops the index, the WHERE, or the ORDER BY would be caught.
        using var store = NewStore();

        var ours = WorkItemId.New();
        var theirs = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);

        var early = MakeRecord(ours, t0, "first");
        var late = MakeRecord(ours, t0.AddMinutes(5), "second");
        var other = MakeRecord(theirs, t0.AddMinutes(2), "other");

        // Insert in non-chronological order to expose any reliance on insertion order.
        await store.RecordAsync(late);
        await store.RecordAsync(other);
        await store.RecordAsync(early);

        var rows = await store.ListByWorkItemAsync(ours);
        Assert.Equal(2, rows.Count);
        Assert.Equal("first", rows[0].Reason);
        Assert.Equal("second", rows[1].Reason);
    }

    [Fact]
    public async Task RecordAsync_PersistsAcrossReopen()
    {
        // Open the store, write a row, close, reopen at the same path: the row
        // must still be there. A regression that accidentally uses an in-memory
        // (":memory:") connection or fails to flush WAL would only fail here.
        var dbPath = Path.Combine(_workspace, "history-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var workItemId = WorkItemId.New();
        var record = MakeRecord(workItemId, DateTimeOffset.UtcNow, "persist");

        using (var first = new SqliteAgentFallbackHistoryStore(dbPath))
            await first.RecordAsync(record);

        using var second = new SqliteAgentFallbackHistoryStore(dbPath);
        var rows = await second.ListByWorkItemAsync(workItemId);
        Assert.Single(rows);
        Assert.Equal("persist", rows[0].Reason);
    }

    [Fact]
    public async Task RecordAsync_PreservesAgentKindIdentityAcrossRoundTrip()
    {
        // AgentKind is reconstructed from the raw string column via
        // `new AgentKind(reader.GetString(...))`. A regression that introduces
        // case-mangling or trimming would silently produce an unequal
        // AgentKind that still serialises to a similar string.
        using var store = NewStore();
        var workItemId = WorkItemId.New();

        foreach (var kind in new[] { AgentKind.Codex, AgentKind.Claude, AgentKind.Gemini })
        {
            await store.RecordAsync(new AgentFallbackRecord(
                Id: Guid.NewGuid(),
                WorkItemId: workItemId,
                Phase: "work",
                Iteration: null,
                FromAgent: kind,
                FromModel: null,
                ToAgent: kind,
                ToModel: null,
                Reason: kind.Value,
                OccurredAt: DateTimeOffset.UtcNow.AddSeconds(kind.Value.GetHashCode())));
        }

        var rows = await store.ListByWorkItemAsync(workItemId);
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.FromAgent == AgentKind.Codex && r.ToAgent == AgentKind.Codex);
        Assert.Contains(rows, r => r.FromAgent == AgentKind.Claude && r.ToAgent == AgentKind.Claude);
        Assert.Contains(rows, r => r.FromAgent == AgentKind.Gemini && r.ToAgent == AgentKind.Gemini);
    }

    [Fact]
    public async Task RecordAsync_LocalTimeOccurredAt_RoundTripsAsSameInstant()
    {
        // The store writes OccurredAt.ToUniversalTime().ToString("O") and reads
        // back via DateTimeOffset.Parse. Feeding in a non-UTC offset must
        // round-trip to the same absolute instant (not the same wall-clock
        // string).
        using var store = NewStore();

        var workItemId = WorkItemId.New();
        var local = new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.FromHours(-5));
        var record = new AgentFallbackRecord(
            Id: Guid.NewGuid(),
            WorkItemId: workItemId,
            Phase: "merge",
            Iteration: 1,
            FromAgent: AgentKind.Codex,
            FromModel: null,
            ToAgent: AgentKind.Claude,
            ToModel: null,
            Reason: "tz",
            OccurredAt: local);

        await store.RecordAsync(record);
        var rows = await store.ListByWorkItemAsync(workItemId);
        var got = Assert.Single(rows);
        Assert.Equal(local.ToUniversalTime(), got.OccurredAt.ToUniversalTime());
    }

    [Fact]
    public async Task ListByWorkItem_UnknownWorkItem_ReturnsEmpty()
    {
        using var store = NewStore();
        var rows = await store.ListByWorkItemAsync(WorkItemId.New());
        Assert.Empty(rows);
    }

    private SqliteAgentFallbackHistoryStore NewStore() =>
        new(Path.Combine(_workspace, "history-" + Guid.NewGuid().ToString("N")[..8] + ".db"));

    private static AgentFallbackRecord MakeRecord(WorkItemId workItemId, DateTimeOffset at, string reason) =>
        new(
            Id: Guid.NewGuid(),
            WorkItemId: workItemId,
            Phase: "work",
            Iteration: 1,
            FromAgent: AgentKind.Codex,
            FromModel: "gpt-5",
            ToAgent: AgentKind.Claude,
            ToModel: "claude-sonnet-4",
            Reason: reason,
            OccurredAt: at);
}
