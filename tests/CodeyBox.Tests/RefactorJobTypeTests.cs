using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the JobType.Refactor project-exclusive dispatch gate.
///
/// Acceptance criteria:
///   - a Refactor item only starts when its project has zero in-flight items;
///   - while a Refactor item is in flight for a project, no other item for the
///     same project may start (refactor holds an exclusive project lock);
///   - Refactor items are mutually exclusive per project (one at a time);
///   - the gate is strictly project-scoped: a Refactor in project X does not
///     block work in project Y.
///
/// Store-level tests pin the in-flight split query the orchestrator gate reads;
/// the orchestrator-level integration tests pin the dispatch-time behaviour.
/// </summary>
public sealed class RefactorJobTypeTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-refactor-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public RefactorJobTypeTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem MakeQueued(string projectId = "proj-a", JobType jobType = JobType.Normal)
        => new()
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(projectId),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
            JobType = jobType,
        };

    // ── Persistence ──────────────────────────────────────────────────────────

    [Fact]
    public async Task JobTypeRefactor_RoundTripsThroughStore()
    {
        var item = MakeQueued(jobType: JobType.Refactor);
        await _store.CreateAsync(item);
        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(JobType.Refactor, read!.JobType);
    }

    // ── CountInFlightSplitByRefactorAsync (store-level) ─────────────────────

    [Fact]
    public async Task CountInFlightSplit_NoItems_ReturnsZeroZero()
    {
        var (refactor, other) = await _store.CountInFlightSplitByRefactorAsync(
            new ProjectId("proj-a"));
        Assert.Equal(0, refactor);
        Assert.Equal(0, other);
    }

    [Fact]
    public async Task CountInFlightSplit_PartitionsByJobType()
    {
        var pid = new ProjectId("proj-mix");
        await _store.CreateAsync(MakeQueued("proj-mix") with
        {
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow,
            JobType = JobType.Refactor,
        });
        await _store.CreateAsync(MakeQueued("proj-mix") with
        {
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow,
            JobType = JobType.Normal,
        });
        await _store.CreateAsync(MakeQueued("proj-mix") with
        {
            State = WorkItemState.Auditing,
            StartedAt = DateTimeOffset.UtcNow,
            JobType = JobType.Normal,
        });

        var (refactor, other) = await _store.CountInFlightSplitByRefactorAsync(pid);
        Assert.Equal(1, refactor);
        Assert.Equal(2, other);
    }

    [Fact]
    public async Task CountInFlightSplit_TerminalAndQueuedExcluded()
    {
        var pid = new ProjectId("proj-x");
        // Terminal — excluded.
        await _store.CreateAsync(MakeQueued("proj-x") with
        {
            State = WorkItemState.Done,
            StartedAt = DateTimeOffset.UtcNow,
            JobType = JobType.Refactor,
        });
        // Queued (no StartedAt) — excluded.
        await _store.CreateAsync(MakeQueued("proj-x", JobType.Refactor));
        // Parked — excluded.
        await _store.CreateAsync(MakeQueued("proj-x") with
        {
            State = WorkItemState.WaitingForAgentResume,
            StartedAt = DateTimeOffset.UtcNow,
            JobType = JobType.Refactor,
        });

        var (refactor, other) = await _store.CountInFlightSplitByRefactorAsync(pid);
        Assert.Equal(0, refactor);
        Assert.Equal(0, other);
    }

    [Fact]
    public async Task CountInFlightSplit_OnlyCountsMatchingProject()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.CreateAsync(MakeQueued("proj-a") with
        {
            State = WorkItemState.Working,
            StartedAt = now,
            JobType = JobType.Refactor,
        });
        await _store.CreateAsync(MakeQueued("proj-b") with
        {
            State = WorkItemState.Working,
            StartedAt = now,
            JobType = JobType.Normal,
        });

        var a = await _store.CountInFlightSplitByRefactorAsync(new ProjectId("proj-a"));
        var b = await _store.CountInFlightSplitByRefactorAsync(new ProjectId("proj-b"));

        Assert.Equal((1, 0), a);
        Assert.Equal((0, 1), b);
    }

    // ── OrchestratorService integration ──────────────────────────────────────

    /// <summary>
    /// refactor-waits-for-drained-project: a queued Refactor item must defer
    /// while any other item for the same project is in flight. Once those
    /// items complete, the refactor is allowed to dispatch.
    /// </summary>
    [Fact]
    public async Task RefactorWaitsForDrainedProject()
    {
        var pid = "proj-refactor-waits";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Waits",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var pipelineGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ManualReleasePipelineRunner(_store, releaseGate: pipelineGate.Task);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 4 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMilliseconds(200),
        });
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        // 1. Queue a Normal item and let the orchestrator pick it up.
        var normal = MakeQueued(pid);
        await _store.CreateAsync(normal);
        await queue.EnqueueAsync(normal.Id);

        await svc.StartAsync(CancellationToken.None);

        await WaitForStartedAsync(_store, normal.Id);

        // 2. Queue a Refactor. While normal is in flight, the refactor must
        //    repeatedly defer instead of being picked up.
        var refactor = MakeQueued(pid, JobType.Refactor);
        await _store.CreateAsync(refactor);
        await queue.EnqueueAsync(refactor.Id);

        await WaitForDeferredAsync(svc, refactor.Id);
        Assert.True(svc.IsDeferredForTest(refactor.Id));

        // The refactor must not have StartedAt set yet.
        var refactorState = await _store.GetAsync(refactor.Id);
        Assert.NotNull(refactorState);
        Assert.Null(refactorState!.StartedAt);

        // 3. Release the normal item. After it finishes, the refactor should
        //    eventually be dispatched (StartedAt set, pipeline invoked).
        pipelineGate.TrySetResult();

        await WaitForStartedAsync(_store, refactor.Id);
        var refactorRunning = await _store.GetAsync(refactor.Id);
        Assert.NotNull(refactorRunning!.StartedAt);

        await svc.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// refactor-blocks-others: while a Refactor item is in flight for a
    /// project, queued Normal items for the same project must not start.
    /// </summary>
    [Fact]
    public async Task RefactorBlocksOthers()
    {
        var pid = "proj-refactor-blocks";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Blocks",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var pipelineGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ManualReleasePipelineRunner(_store, releaseGate: pipelineGate.Task);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 4 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMilliseconds(200),
        });
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        // 1. Refactor goes in first and is picked up.
        var refactor = MakeQueued(pid, JobType.Refactor);
        await _store.CreateAsync(refactor);
        await queue.EnqueueAsync(refactor.Id);

        await svc.StartAsync(CancellationToken.None);
        await WaitForStartedAsync(_store, refactor.Id);

        // 2. Queue two Normal follow-ups. Both must defer while the refactor
        //    is in flight.
        var normalA = MakeQueued(pid);
        var normalB = MakeQueued(pid);
        await _store.CreateAsync(normalA);
        await _store.CreateAsync(normalB);
        await queue.EnqueueAsync(normalA.Id);
        await queue.EnqueueAsync(normalB.Id);

        await WaitForDeferredAsync(svc, normalA.Id);
        await WaitForDeferredAsync(svc, normalB.Id);

        Assert.Null((await _store.GetAsync(normalA.Id))!.StartedAt);
        Assert.Null((await _store.GetAsync(normalB.Id))!.StartedAt);

        pipelineGate.TrySetResult();
        await svc.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// refactor-mutual-exclusion: only one Refactor item per project can be
    /// in flight at a time. The second refactor must defer until the first
    /// completes.
    /// </summary>
    [Fact]
    public async Task RefactorsAreMutuallyExclusivePerProject()
    {
        var pid = "proj-refactor-mutual";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Mutual",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var pipelineGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ManualReleasePipelineRunner(_store, releaseGate: pipelineGate.Task);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 4 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMilliseconds(200),
        });
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        var refactor1 = MakeQueued(pid, JobType.Refactor);
        var refactor2 = MakeQueued(pid, JobType.Refactor);
        await _store.CreateAsync(refactor1);
        await _store.CreateAsync(refactor2);
        await queue.EnqueueAsync(refactor1.Id);
        await queue.EnqueueAsync(refactor2.Id);

        await svc.StartAsync(CancellationToken.None);
        await WaitForStartedAsync(_store, refactor1.Id);

        await WaitForDeferredAsync(svc, refactor2.Id);
        Assert.Null((await _store.GetAsync(refactor2.Id))!.StartedAt);

        pipelineGate.TrySetResult();
        await svc.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// cross-project-independence: a Refactor in flight for project X does
    /// NOT block items in project Y.
    /// </summary>
    [Fact]
    public async Task RefactorGateIsProjectScoped()
    {
        var projectRepo = new InMemoryProjectRepository(
            new Project
            {
                Id = new ProjectId("proj-x"),
                DisplayName = "Proj X",
                RepositoryUrl = "https://github.com/test/x",
            },
            new Project
            {
                Id = new ProjectId("proj-y"),
                DisplayName = "Proj Y",
                RepositoryUrl = "https://github.com/test/y",
            });

        var pipelineGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ManualReleasePipelineRunner(_store, releaseGate: pipelineGate.Task);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 4 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMilliseconds(200),
        });
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        // Refactor in proj-x.
        var refactorX = MakeQueued("proj-x", JobType.Refactor);
        await _store.CreateAsync(refactorX);
        await queue.EnqueueAsync(refactorX.Id);

        await svc.StartAsync(CancellationToken.None);
        await WaitForStartedAsync(_store, refactorX.Id);

        // Normal item in proj-y. Must NOT be blocked by proj-x's refactor.
        var normalY = MakeQueued("proj-y");
        await _store.CreateAsync(normalY);
        await queue.EnqueueAsync(normalY.Id);

        await WaitForStartedAsync(_store, normalY.Id);

        // Neither item should have been deferred by the refactor gate.
        Assert.False(svc.IsDeferredForTest(normalY.Id));

        pipelineGate.TrySetResult();
        await svc.StopAsync(CancellationToken.None);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task WaitForStartedAsync(
        IWorkItemStore store, WorkItemId id, int timeoutSeconds = 10)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var item = await store.GetAsync(id);
            if (item?.StartedAt is not null) return;
            await Task.Delay(25);
        }
        var final = await store.GetAsync(id);
        throw new InvalidOperationException(
            $"Work item {id} did not transition to in-flight within {timeoutSeconds}s; state={final?.State}");
    }

    private static async Task WaitForDeferredAsync(
        OrchestratorService svc, WorkItemId id, int timeoutSeconds = 10)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (svc.IsDeferredForTest(id)) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException(
            $"Work item {id} was not deferred within {timeoutSeconds}s");
    }

    /// <summary>
    /// Pipeline stub that holds every dispatched item until the operator-
    /// supplied gate task completes, then marks it Done. Used so we can
    /// reliably observe items as "in flight" while still being able to drain.
    /// </summary>
    private sealed class ManualReleasePipelineRunner : IPipelineRunner
    {
        private readonly IWorkItemStore _store;
        private readonly Task _releaseGate;

        public ManualReleasePipelineRunner(IWorkItemStore store, Task releaseGate)
        {
            _store = store;
            _releaseGate = releaseGate;
        }

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            try { await _releaseGate.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
        }
    }
}
