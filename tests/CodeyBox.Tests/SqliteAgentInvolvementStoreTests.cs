using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Round-trip coverage for the SQLite-backed agent-involvement store plus the
/// per-phase progression that the orchestrator records as a work item moves
/// through Work → Audit → Rework → Merge. Other tests use the in-memory
/// variant, so these guard against column-ordinal, nullable, and
/// DateTimeOffset-format regressions that would only surface in production.
/// </summary>
public sealed class SqliteAgentInvolvementStoreTests : IDisposable
{
    private readonly string _workspace;

    public SqliteAgentInvolvementStoreTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-involvementdb-").FullName;

    public void Dispose() { CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace); }

    [Fact]
    public async Task RecordStartAndList_PopulatedRecord_RoundTripsAllFields()
    {
        using var store = NewStore();

        var workItemId = WorkItemId.New();
        var startedAt = new DateTimeOffset(2026, 4, 1, 12, 30, 45, 123, TimeSpan.Zero);
        var entry = new AgentInvolvement(
            Id: Guid.NewGuid(),
            WorkItemId: workItemId,
            AgentKind: AgentKind.Cursor,
            ModelId: "composer-2.5",
            Phase: "work",
            StartedAt: startedAt,
            EndedAt: null,
            Iteration: 1,
            Outcome: null);

        await store.RecordStartAsync(entry);
        var rows = await store.ListByWorkItemAsync(workItemId);

        var got = Assert.Single(rows);
        Assert.Equal(entry.Id, got.Id);
        Assert.Equal(workItemId, got.WorkItemId);
        Assert.Equal(AgentKind.Cursor, got.AgentKind);
        Assert.Equal("composer-2.5", got.ModelId);
        Assert.Equal("work", got.Phase);
        Assert.Equal(startedAt.ToUniversalTime(), got.StartedAt.ToUniversalTime());
        Assert.Null(got.EndedAt);
        Assert.Equal(1, got.Iteration);
        Assert.Null(got.Outcome);
    }

    [Fact]
    public async Task Finalize_StampsEndedAtAndOutcome()
    {
        using var store = NewStore();
        var workItemId = WorkItemId.New();
        var id = Guid.NewGuid();
        await store.RecordStartAsync(InProgress(id, workItemId, AgentKind.Claude, "audit", 2));

        var endedAt = new DateTimeOffset(2026, 4, 1, 13, 0, 0, TimeSpan.Zero);
        await store.FinalizeAsync(id, endedAt, "success");

        var got = Assert.Single(await store.ListByWorkItemAsync(workItemId));
        Assert.Equal(endedAt.ToUniversalTime(), got.EndedAt!.Value.ToUniversalTime());
        Assert.Equal("success", got.Outcome);
    }

    [Fact]
    public async Task Finalize_IsOneTime_DoesNotRewriteClosedEntry()
    {
        // The immutable-identity invariant: once an entry is finalized, a second
        // finalize (e.g. a racing/duplicate stamp) must not overwrite it.
        using var store = NewStore();
        var workItemId = WorkItemId.New();
        var id = Guid.NewGuid();
        await store.RecordStartAsync(InProgress(id, workItemId, AgentKind.Claude, "work", 1));

        await store.FinalizeAsync(id, DateTimeOffset.UtcNow, "success");
        await store.FinalizeAsync(id, DateTimeOffset.UtcNow.AddHours(1), "failure:quota");

        var got = Assert.Single(await store.ListByWorkItemAsync(workItemId));
        Assert.Equal("success", got.Outcome);
    }

    [Fact]
    public async Task Finalize_UnknownId_IsNoOp()
    {
        using var store = NewStore();
        // Must not throw when the id was never recorded (best-effort finalize).
        await store.FinalizeAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, "success");
        Assert.Empty(await store.ListByWorkItemAsync(WorkItemId.New()));
    }

    [Fact]
    public async Task ListByWorkItem_FiltersByIdAndOrdersByStartedAt()
    {
        using var store = NewStore();
        var ours = WorkItemId.New();
        var theirs = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);

        // Insert out of chronological order to expose any reliance on insert order.
        await store.RecordStartAsync(At(ours, t0.AddMinutes(5), "second"));
        await store.RecordStartAsync(At(theirs, t0.AddMinutes(2), "other"));
        await store.RecordStartAsync(At(ours, t0, "first"));

        var rows = await store.ListByWorkItemAsync(ours);
        Assert.Equal(2, rows.Count);
        Assert.Equal("first", rows[0].ModelId);
        Assert.Equal("second", rows[1].ModelId);
    }

    [Fact]
    public async Task PersistsAcrossReopen()
    {
        var dbPath = Path.Combine(_workspace, "inv-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var workItemId = WorkItemId.New();
        var id = Guid.NewGuid();

        using (var first = new SqliteAgentInvolvementStore(dbPath))
        {
            await first.RecordStartAsync(InProgress(id, workItemId, AgentKind.Opencode, "work", 1));
            await first.FinalizeAsync(id, DateTimeOffset.UtcNow, "success");
        }

        using var second = new SqliteAgentInvolvementStore(dbPath);
        var got = Assert.Single(await second.ListByWorkItemAsync(workItemId));
        Assert.Equal(AgentKind.Opencode, got.AgentKind);
        Assert.Equal("success", got.Outcome);
    }

    [Fact]
    public async Task ListByWorkItem_UnknownWorkItem_ReturnsEmpty()
    {
        using var store = NewStore();
        Assert.Empty(await store.ListByWorkItemAsync(WorkItemId.New()));
    }

    [Fact]
    public async Task FullProgression_RoundTripsOrderingMultiRowPerIterationAndFinalize()
    {
        // STORE-CONTRACT test only: pins SQLite ordering, multi-row-per-iteration,
        // and one-time finalize for a full Work → Audit → Rework → Audit → Merge
        // progression. It does NOT exercise PipelineRunner and is NOT the
        // acceptance-#5 guard — that lives in the REAL-pipeline tests
        // PipelineRunnerQuotaFallbackTests.Ac5_WorkAuditReworkAuditMerge_RecordsExactlySevenRowAgentHistory
        // (7 rows, 2 auditors) and
        // ThreeAuditorProgression_…_RecordsNineRowPerAuditorTrail (9 rows, 3 auditors).
        //
        // The shape modelled here matches production: the audit loop re-runs the
        // FULL auditor list on every iteration, so three LLM auditors produce
        // three "audit:{name}" rows in BOTH audit iterations (1 + 3 + 1 + 3 + 1 =
        // 9). Earlier this test seeded only a single audit row for iteration 2,
        // which implied a single-auditor re-check that the orchestrator never
        // performs; that fiction is removed here. Phase strings use the
        // production "audit:{name}" format ExecAuditorAsync emits, and work/merge
        // iteration is null to match InvokeAgentWithQuotaFallbackAsync.
        using var store = NewStore();
        var id = WorkItemId.New();
        var t = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

        async Task RecordPhase(AgentKind agent, string phase, int? iteration, string outcome)
        {
            var entryId = Guid.NewGuid();
            await store.RecordStartAsync(new AgentInvolvement(
                entryId, id, agent, ModelId: null, phase, t, EndedAt: null, iteration, Outcome: null));
            t = t.AddMinutes(5);
            await store.FinalizeAsync(entryId, t, outcome);
            t = t.AddMinutes(1);
        }

        // 1: Work phase, run by Cursor (iteration null, as production records it).
        await RecordPhase(AgentKind.Cursor, "work", null, "success");
        // 2-4: Audit iteration 1 — the full three-auditor list.
        await RecordPhase(AgentKind.Claude, "audit:security", 1, "success");
        await RecordPhase(AgentKind.Cursor, "audit:quality", 1, "success");
        await RecordPhase(AgentKind.Gemini, "audit:completeness", 1, "success");
        // 5: Rework, back to the original work agent. Production dispatches the
        // rework that follows audit iteration N as iteration N+1
        // (reworkIterationNumber = iteration + 1 in PipelineRunner), so the rework
        // after audit iteration 1 is iteration 2 — matching the real-pipeline
        // progression tests.
        await RecordPhase(AgentKind.Cursor, "rework", 2, "success");
        // 6-8: Audit iteration 2 — the SAME full auditor list re-runs (production
        // re-runs all auditors every iteration; it is not a single re-check).
        await RecordPhase(AgentKind.Claude, "audit:security", 2, "success");
        await RecordPhase(AgentKind.Cursor, "audit:quality", 2, "success");
        await RecordPhase(AgentKind.Gemini, "audit:completeness", 2, "success");
        // 9: Merge phase.
        await RecordPhase(AgentKind.Cursor, "merge", null, "success");

        var rows = await store.ListByWorkItemAsync(id);
        Assert.Equal(9, rows.Count);

        // Phase/agent sequence maps 1:1 to the orchestrator's phase transitions.
        Assert.Collection(rows,
            r => AssertRow(r, AgentKind.Cursor, "work", null),
            r => AssertRow(r, AgentKind.Claude, "audit:security", 1),
            r => AssertRow(r, AgentKind.Cursor, "audit:quality", 1),
            r => AssertRow(r, AgentKind.Gemini, "audit:completeness", 1),
            r => AssertRow(r, AgentKind.Cursor, "rework", 2),
            r => AssertRow(r, AgentKind.Claude, "audit:security", 2),
            r => AssertRow(r, AgentKind.Cursor, "audit:quality", 2),
            r => AssertRow(r, AgentKind.Gemini, "audit:completeness", 2),
            r => AssertRow(r, AgentKind.Cursor, "merge", null));

        // Every row is finalized; none is left dangling in-progress.
        Assert.All(rows, r =>
        {
            Assert.NotNull(r.EndedAt);
            Assert.Equal("success", r.Outcome);
        });

        // Three distinct agents touched the item across phases — the whole point
        // of the trail vs. the single, overwritten WorkItem.Agent field.
        Assert.Equal(3, rows.Select(r => r.AgentKind).Distinct().Count());
    }

    private static void AssertRow(AgentInvolvement r, AgentKind agent, string phase, int? iteration)
    {
        Assert.Equal(agent, r.AgentKind);
        Assert.Equal(phase, r.Phase);
        Assert.Equal(iteration, r.Iteration);
    }

    private SqliteAgentInvolvementStore NewStore() =>
        new(Path.Combine(_workspace, "inv-" + Guid.NewGuid().ToString("N")[..8] + ".db"));

    private static AgentInvolvement InProgress(Guid id, WorkItemId workItemId, AgentKind agent, string phase, int? iteration) =>
        new(id, workItemId, agent, ModelId: null, phase, DateTimeOffset.UtcNow, EndedAt: null, iteration, Outcome: null);

    private static AgentInvolvement At(WorkItemId workItemId, DateTimeOffset at, string modelTag) =>
        new(Guid.NewGuid(), workItemId, AgentKind.Cursor, ModelId: modelTag, Phase: "work", StartedAt: at, EndedAt: null, Iteration: 1, Outcome: null);
}
