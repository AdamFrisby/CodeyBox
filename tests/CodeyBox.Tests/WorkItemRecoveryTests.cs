using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for OrchestratorService startup recovery logic.
/// Exercises ReplayPendingAsync directly so we can verify store state without
/// racing against the worker loop. Tests that mid-flight work items are reset to
/// the correct recoverable state, and that the recovery_attempts cap is enforced.
/// </summary>
public sealed class WorkItemRecoveryTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-recovery-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public WorkItemRecoveryTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem Item(WorkItemState state, int recoveryAttempts = 0) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = state,
        RecoveryAttempts = recoveryAttempts,
        StartedAt = state != WorkItemState.Queued ? DateTimeOffset.UtcNow : null,
        WorkBranch = state == WorkItemState.Working ? "codeybox/in-flight" : null,
    };

    private OrchestratorService BuildOrchestrator(int maxRecovery = 3)
    {
        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var pipeline = new FakePipelineRunner(_store);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1, MaxRecoveryAttempts = maxRecovery };
        return new OrchestratorService(
            queue, _store, pipeline, registry, opts,
            NullLogger<OrchestratorService>.Instance);
    }

    // ── State reset mapping ───────────────────────────────────────────────────

    [Fact]
    public async Task WorkingWithoutPreempt_TransitionsToFailed()
    {
        var item = Item(WorkItemState.Working);
        await _store.CreateAsync(item);

        var svc = BuildOrchestrator();
        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        var recovered = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, recovered!.State);
        Assert.Equal(1, recovered.RecoveryAttempts);
        Assert.Null(recovered.StartedAt);
        Assert.Equal("codeybox/in-flight", recovered.WorkBranch);
        Assert.Contains("without a preempt checkpoint", recovered.LastError);
    }

    [Fact]
    public async Task CheckAndActWorkingWithoutPreempt_RequeuesForFreshCheck()
    {
        var item = Item(WorkItemState.Working) with
        {
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is action needed?",
                OnYes = new OnYesActionSpec
                {
                    Title = "Act",
                    Prompt = "Act on the check.",
                },
            },
        };
        await _store.CreateAsync(item);

        var svc = BuildOrchestrator();
        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        var recovered = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, recovered!.State);
        Assert.Equal(1, recovered.RecoveryAttempts);
        Assert.Null(recovered.StartedAt);
        Assert.Null(recovered.PreemptCheckpoint);
        Assert.Null(recovered.LastError);
    }

    [Fact]
    public async Task CheckAndActWorkingWithPersistedVerdict_CompletesAndEnqueuesExistingFollowup()
    {
        var check = Item(WorkItemState.Working) with
        {
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is action needed?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec
                {
                    Title = "Act",
                    Prompt = "Act on the check.",
                },
            },
            Verdict = new CheckVerdict
            {
                Answer = true,
                Evidence = "actionable",
            },
            LastError = "stale worker exit",
        };
        var followup = Item(WorkItemState.Queued) with
        {
            JobType = JobType.Normal,
            OriginCheckWorkItemId = check.Id,
            // The startup state map is built before the check is recovered from
            // Working to Done, so the follow-up's own Queued replay path will
            // still see this dependency as unsatisfied. The enqueue below must
            // come from the recovered.State == Done branch.
            DependsOn = [check.Id],
        };
        await _store.CreateAsync(check);
        await _store.CreateAsync(followup);

        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(queue, _store, new FakePipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        var recovered = await _store.GetAsync(check.Id);
        Assert.Equal(WorkItemState.Done, recovered!.State);
        Assert.Equal(0, recovered.RecoveryAttempts);
        Assert.Null(recovered.StartedAt);
        Assert.Null(recovered.PreemptCheckpoint);
        Assert.Null(recovered.LastError);

        Assert.Equal(1, queue.Count);
        Assert.Equal(followup.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CheckAndActWorkingWithoutPreempt_AtRecoveryCap_TransitionsToAbandoned()
    {
        var item = Item(WorkItemState.Working, recoveryAttempts: 2) with
        {
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is action needed?",
                OnYes = new OnYesActionSpec
                {
                    Title = "Act",
                    Prompt = "Act on the check.",
                },
            },
        };
        await _store.CreateAsync(item);

        var svc = BuildOrchestrator(maxRecovery: 2);
        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        var recovered = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, recovered!.State);
        Assert.Contains("2 recovery attempts", recovered.LastError);
        Assert.Equal(3, recovered.RecoveryAttempts);
        Assert.Null(recovered.StartedAt);
        Assert.Null(recovered.PreemptCheckpoint);
    }

    [Fact]
    public async Task PreemptedWorking_ReenqueuesWithoutRecoveryReset()
    {
        var item = Item(WorkItemState.Working) with
        {
            PreemptedAt = DateTimeOffset.UtcNow,
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{Guid.NewGuid()}",
        };
        await _store.CreateAsync(item);

        var svc = BuildOrchestrator();
        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        var recovered = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, recovered!.State);
        Assert.Equal(0, recovered.RecoveryAttempts);
        Assert.Null(recovered.StartedAt);
        Assert.Equal(item.PreemptCheckpoint, recovered.PreemptCheckpoint);
    }

    [Fact]
    public async Task Auditing_ResetsTo_WorkComplete()
    {
        var item = Item(WorkItemState.Auditing);
        await _store.CreateAsync(item);

        var svc = BuildOrchestrator();
        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        var recovered = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, recovered!.State);
        Assert.Equal(1, recovered.RecoveryAttempts);
    }

    [Fact]
    public async Task Reworking_ResetsTo_WorkComplete()
    {
        var item = Item(WorkItemState.Reworking);
        await _store.CreateAsync(item);

        var svc = BuildOrchestrator();
        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        var recovered = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, recovered!.State);
        Assert.Equal(1, recovered.RecoveryAttempts);
    }

    [Fact]
    public async Task Merging_ResetsTo_AuditPassed()
    {
        var item = Item(WorkItemState.Merging);
        await _store.CreateAsync(item);

        var svc = BuildOrchestrator();
        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        var recovered = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditPassed, recovered!.State);
        Assert.Equal(1, recovered.RecoveryAttempts);
    }

    [Fact]
    public async Task UpstreamPushing_IsReenqueued_AsMerged()
    {
        var item = Item(WorkItemState.UpstreamPushing);
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var pipeline = new FakePipelineRunner(_store);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        var svc = new OrchestratorService(queue, _store, pipeline, registry, opts,
            NullLogger<OrchestratorService>.Instance);

        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        // UpstreamPushing is recovered as Merged so PipelineRunner's skip flags
        // route it directly to RunUpstreamPushPhaseAsync instead of replaying
        // the full work+audit+merge pipeline. This IS interrupted in-flight work,
        // so RecoveryAttempts must be incremented.
        var recovered = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Merged, recovered!.State);
        Assert.Equal(1, recovered.RecoveryAttempts);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task Cancelled_IsNotRecovered()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Cancelled,
            CancellationReason = WorkItemCancellationReason.OperatorRequested,
        };
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(queue, _store, new FakePipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        var readBack = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Cancelled, readBack!.State);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Done_IsNotRecovered()
    {
        var item = Item(WorkItemState.Done);
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(queue, _store, new FakePipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        Assert.Equal(0, queue.Count);
    }

    // ── RecoveryAttempts cap ──────────────────────────────────────────────────

    [Fact]
    public async Task AtMaxRecoveryAttempts_TransitionsToAbandoned()
    {
        // Item already at max (3); next recovery should abandon it.
        var item = Item(WorkItemState.Auditing, recoveryAttempts: 3);
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(queue, _store, new FakePipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1, MaxRecoveryAttempts = 3 },
            NullLogger<OrchestratorService>.Instance);

        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        var readBack = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, readBack!.State);
        Assert.Contains("3 recovery attempts", readBack.LastError);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task BelowMaxRecoveryAttempts_StillRecovered()
    {
        var item = Item(WorkItemState.Auditing, recoveryAttempts: 2);
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(queue, _store, new FakePipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1, MaxRecoveryAttempts = 3 },
            NullLogger<OrchestratorService>.Instance);

        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        var readBack = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, readBack!.State);
        Assert.Equal(3, readBack.RecoveryAttempts);
        Assert.Equal(1, queue.Count);
    }

    // ── WorkComplete / AuditPassed / Merged: re-enqueued as recovery handoffs ─

    [Theory]
    [InlineData(WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.Merged)]
    public async Task MidFlightPassThroughStates_ReenqueuedAndIncrementAttempts(WorkItemState state)
    {
        // Same-state redispatches still consume the recovery budget. Without this,
        // a WorkComplete -> Auditing -> WorkComplete livelock can repeatedly return
        // to a durable boundary and never reach MaxRecoveryAttempts.
        var item = Item(state, recoveryAttempts: 1);
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(queue, _store, new FakePipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        var recovered = await _store.GetAsync(item.Id);
        Assert.Equal(state, recovered!.State);
        Assert.Equal(2, recovered.RecoveryAttempts);
        Assert.Equal(1, queue.Count);
    }

    // ── Legacy ambiguous cancelled items: logged but not auto-recovered ───────

    [Fact]
    public async Task LegacyCancelledItem_NotAutoRecovered_OnlyLogged()
    {
        // Simulate a pre-fix item: Cancelled, no reason, last_error = "cancelled"
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Cancelled,
            LastError = "cancelled",
            CancellationReason = null,
        };
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(queue, _store, new FakePipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        // Must NOT be re-queued automatically
        var readBack = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Cancelled, readBack!.State);
        Assert.Equal(0, queue.Count);
    }
}
