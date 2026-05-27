using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Unit coverage for the two R8-core helpers in <see cref="PipelineRunner"/>
/// that compute the in-VM agent log path and persist it on the work item
/// before the agent runs. The persisted value is the contract the
/// suspend-on-shutdown handler reads back; corrupting it silently breaks
/// re-tail across a restart.
/// </summary>
public sealed class AgentLogPathHelpersTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-logpath-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public AgentLogPathHelpersTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem MakeItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Working,
        StartedAt = DateTimeOffset.UtcNow,
    };

    // ── BuildAgentLogPath ────────────────────────────────────────────────────

    [Fact]
    public void Build_HappyPath_HasWorkItemIdPhaseAndIteration()
    {
        var id = WorkItemId.New();
        var path = PipelineRunner.BuildAgentLogPath(id, "work", iteration: 0);

        Assert.StartsWith(SandboxConventions.AgentLogDir + "/", path);
        Assert.Contains(id.ToString(), path);
        Assert.Contains("-work-i0.log", path);
    }

    [Fact]
    public void Build_NullIteration_DropsIterationSuffix()
    {
        // Merge / conflict-rework invocations have no audit-loop counter — the
        // suffix must be elided rather than written as "-i0" which would
        // collide with the iteration-0 work-phase log on the same item.
        var id = WorkItemId.New();
        var path = PipelineRunner.BuildAgentLogPath(id, "merge", iteration: null);

        Assert.EndsWith("-merge.log", path);
        Assert.DoesNotContain("-i", path[(SandboxConventions.AgentLogDir.Length + 1 + id.ToString().Length)..]);
    }

    [Fact]
    public void Build_EmptyPhase_FallsBackToAgentLiteral()
    {
        // The literal "agent" fallback keeps the filename grammatically
        // intact when the caller has no semantic phase tag (defensive — every
        // production call site passes a non-empty phase).
        var id = WorkItemId.New();
        var path = PipelineRunner.BuildAgentLogPath(id, "", iteration: 0);
        Assert.Contains("-agent-i0.log", path);

        var path2 = PipelineRunner.BuildAgentLogPath(id, "", iteration: null);
        Assert.EndsWith("-agent.log", path2);
    }

    [Fact]
    public void Build_DifferentInputs_ProduceDifferentPaths()
    {
        var id = WorkItemId.New();
        var work0 = PipelineRunner.BuildAgentLogPath(id, "work", 0);
        var work1 = PipelineRunner.BuildAgentLogPath(id, "work", 1);
        var audit0 = PipelineRunner.BuildAgentLogPath(id, "audit", 0);

        Assert.NotEqual(work0, work1);
        Assert.NotEqual(work0, audit0);
    }

    // ── PersistAgentLogPathAsync ─────────────────────────────────────────────

    [Fact]
    public async Task Persist_WritesAgentLogPath_OnFirstCall()
    {
        var item = MakeItem();
        await _store.CreateAsync(item);

        var wrote = await PipelineRunner.PersistAgentLogPathAsync(
            _store, NullLogger.Instance, item.Id,
            "/work/.codeybox/agent-logs/abc.log", CancellationToken.None);

        Assert.True(wrote);
        var after = await _store.GetAsync(item.Id);
        Assert.Equal("/work/.codeybox/agent-logs/abc.log", after!.AgentLogPath);
    }

    [Fact]
    public async Task Persist_IsIdempotent_WhenPathAlreadyMatches()
    {
        // The dedup short-circuit avoids burning a row-write for every
        // sub-invocation that lands on the same path (e.g. a retry on the
        // same phase/iteration). A regression that dropped it would 2-3x
        // store traffic per audit loop iteration.
        var item = MakeItem();
        await _store.CreateAsync(item with { AgentLogPath = "/work/.codeybox/agent-logs/same.log" });

        var wrote = await PipelineRunner.PersistAgentLogPathAsync(
            _store, NullLogger.Instance, item.Id,
            "/work/.codeybox/agent-logs/same.log", CancellationToken.None);

        Assert.False(wrote);
    }

    [Fact]
    public async Task Persist_ReturnsFalse_WhenItemMissing()
    {
        var wrote = await PipelineRunner.PersistAgentLogPathAsync(
            _store, NullLogger.Instance, WorkItemId.New(),
            "/work/.codeybox/agent-logs/x.log", CancellationToken.None);

        Assert.False(wrote);
    }

    [Fact]
    public async Task Persist_PropagatesOperationCanceledException()
    {
        // Cancellation must NOT be swallowed: the orchestrator's pipeline ct
        // is the only signal a worker has that the work item was cancelled,
        // and a swallowed token would let the agent invocation start anyway.
        var item = MakeItem();
        await _store.CreateAsync(item);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PipelineRunner.PersistAgentLogPathAsync(
                _store, NullLogger.Instance, item.Id,
                "/work/.codeybox/agent-logs/x.log", cts.Token));
    }

    [Fact]
    public async Task Persist_SwallowsNonCancellationStoreFailure()
    {
        // Any non-cancellation store exception must NOT bubble out so a
        // transient SQLite hiccup cannot block the agent invocation. The
        // worst-case outcome is the suspend-on-shutdown handler missing the
        // path and the item recovering through the stranded-item sweep.
        var throwingStore = new ThrowOnUpdateStore();

        var wrote = await PipelineRunner.PersistAgentLogPathAsync(
            throwingStore, NullLogger.Instance,
            WorkItemId.New(), "/work/.codeybox/agent-logs/x.log",
            CancellationToken.None);

        Assert.False(wrote);
    }

    private sealed class ThrowOnUpdateStore : IWorkItemStore
    {
        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) =>
            Task.FromResult<WorkItem?>(new WorkItem
            {
                Id = id,
                ProjectId = new ProjectId("test"),
                Title = "t",
                Prompt = "p",
                State = WorkItemState.Working,
            });
        public Task UpdateAsync(WorkItem item, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated store hiccup");

        // ── unused ──
        public Task CreateAsync(WorkItem item, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) => Task.FromResult(true);
        public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default)
            => Task.FromResult(new PriorityUpdateResult(PriorityUpdateOutcome.NotFound, null, null));
        public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => Empty();
        public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => Empty();
        public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => Task.FromResult(0);
        public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
        public IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) => Empty();
        public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) => Task.FromResult<WorkItem?>(null);
        public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) => Task.FromResult<WorkItem?>(null);
        public Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) => Task.FromResult<WorkItem?>(null);
        public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, int, int, string)>>([]);
        public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, int)>>([]);
        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>());
        public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) => Empty();
        public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => Empty();
        public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => Task.CompletedTask;
        public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => Empty();
        public Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default)
            => Task.FromResult(new PromptReplaceResult(PromptReplaceOutcome.NotFound, null));
        public Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItemIteration>>([]);

        private static async IAsyncEnumerable<WorkItem> Empty([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
