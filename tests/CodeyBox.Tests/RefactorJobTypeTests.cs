using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

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
[Collection("Background service timing")]
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
    public async Task CountInFlightSplit_PreemptedRowsExcluded()
    {
        var pid = new ProjectId("proj-preempted-sqlite");
        await _store.CreateAsync(MakeQueued(pid.Value, JobType.Refactor) with
        {
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow,
            PreemptCheckpoint = "checkpoint",
        });
        await _store.CreateAsync(MakeQueued(pid.Value) with
        {
            State = WorkItemState.Reworking,
            StartedAt = DateTimeOffset.UtcNow,
            PreemptCheckpoint = "checkpoint",
        });

        var counts = await _store.CountInFlightSplitByRefactorAsync(pid);

        Assert.Equal((0, 0), counts);
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

    [Fact]
    public async Task CountInFlightSplit_CanExcludeCandidateRow()
    {
        var pid = new ProjectId("proj-exclude-current");
        var refactor = MakeQueued(pid.Value, JobType.Refactor) with
        {
            State = WorkItemState.WorkComplete,
            StartedAt = DateTimeOffset.UtcNow,
        };
        var normal = MakeQueued(pid.Value) with
        {
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await _store.CreateAsync(refactor);
        await _store.CreateAsync(normal);

        var excludingRefactor = await _store.CountInFlightSplitByRefactorAsync(
            pid,
            excludeId: refactor.Id);
        var excludingNormal = await _store.CountInFlightSplitByRefactorAsync(
            pid,
            excludeId: normal.Id);

        Assert.Equal((0, 1), excludingRefactor);
        Assert.Equal((1, 0), excludingNormal);
    }

    [Fact]
    public async Task DefaultCountInFlightSplit_PartitionsAndExcludesCandidate()
    {
        var pid = new ProjectId("proj-default-split");
        var refactor = MakeQueued(pid.Value, JobType.Refactor) with
        {
            State = WorkItemState.WorkComplete,
            StartedAt = DateTimeOffset.UtcNow,
        };
        var normal = MakeQueued(pid.Value) with
        {
            State = WorkItemState.Auditing,
            StartedAt = DateTimeOffset.UtcNow,
        };
        var preempted = MakeQueued(pid.Value, JobType.Refactor) with
        {
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow,
            PreemptCheckpoint = "checkpoint",
        };
        var transientParked = MakeQueued(pid.Value, JobType.Refactor) with
        {
            State = WorkItemState.WaitingForTransientRetry,
            FailureKind = "transient",
            StartedAt = DateTimeOffset.UtcNow,
        };
        var otherProject = MakeQueued("other-project", JobType.Refactor) with
        {
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow,
        };
        var store = new StubWorkItemStore();
        store.Items.AddRange([refactor, normal, preempted, transientParked, otherProject]);

        var counts = await ((IWorkItemStore)store).CountInFlightSplitByRefactorAsync(
            pid,
            excludeId: refactor.Id);

        Assert.Equal((0, 1), counts);
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
        await WaitForStartedAsync(_store, normalA.Id);
        await WaitForStartedAsync(_store, normalB.Id);

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
        var startedId = await WaitForStartedAnyAsync(_store, [refactor1.Id, refactor2.Id]);
        var blockedId = startedId == refactor1.Id ? refactor2.Id : refactor1.Id;

        await WaitForDeferredAsync(svc, blockedId);
        Assert.Null((await _store.GetAsync(blockedId))!.StartedAt);

        pipelineGate.TrySetResult();
        await WaitForStartedAsync(_store, blockedId);

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

    [Fact]
    public async Task RecoveredRefactorContinuationDoesNotBlockOnItself()
    {
        var pid = "proj-refactor-resume";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Resume",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<WorkItemId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ManualReleasePipelineRunner(_store, releaseGate.Task, entered);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 2 };
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

        var refactor = MakeQueued(pid, JobType.Refactor) with
        {
            State = WorkItemState.WorkComplete,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await _store.CreateAsync(refactor);
        await queue.EnqueueAsync(refactor.Id);

        await svc.StartAsync(CancellationToken.None);

        var enteredId = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(refactor.Id, enteredId);
        Assert.False(svc.IsDeferredForTest(refactor.Id));

        releaseGate.TrySetResult();
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RefactorGateDoesNotDependOnProjectLookup()
    {
        var pid = "proj-refactor-no-project-repo";

        var pipelineGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ManualReleasePipelineRunner(_store, pipelineGate.Task);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 2 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMilliseconds(200),
        });
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            projects: null,
            budgetDeferralRecheck: snapshot);

        var normal = MakeQueued(pid);
        await _store.CreateAsync(normal);
        await queue.EnqueueAsync(normal.Id);

        await svc.StartAsync(CancellationToken.None);
        await WaitForStartedAsync(_store, normal.Id);

        var refactor = MakeQueued(pid, JobType.Refactor);
        await _store.CreateAsync(refactor);
        await queue.EnqueueAsync(refactor.Id);

        await WaitForDeferredAsync(svc, refactor.Id);
        Assert.Null((await _store.GetAsync(refactor.Id))!.StartedAt);

        pipelineGate.TrySetResult();
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RefactorGateRunsBeforeReleaseBranchSideEffects()
    {
        var pid = "proj-refactor-before-release";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Before Release",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var releaseDbPath = Path.Combine(
            Path.GetTempPath(),
            $"codeybox-refactor-release-{Guid.NewGuid():N}.db");
        try
        {
            using var releases = new SqliteReleaseStore(releaseDbPath);
            var release = ReleaseTestHelper.SeedRelease(ReleaseState.Open, projectId: pid);
            await releases.CreateAsync(release);
            var gitHost = new CountingGitHost();
            var releaseService = ReleaseTestHelper.BuildService(
                releases,
                _store,
                projectRepo,
                new NullWebhookDispatcher(),
                gitHost: gitHost);

            var pipelineGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pipeline = new ManualReleasePipelineRunner(_store, pipelineGate.Task);
            var queue = new InMemoryTaskQueue();
            var opts = new OrchestratorOptions { MaxConcurrentWorkers = 2 };
            var reg = new CancellationRegistry(CancellationToken.None);
            var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
            {
                RefactorExclusivityRecheck = TimeSpan.FromMilliseconds(200),
            });
            var svc = new OrchestratorService(
                queue, _store, pipeline, reg, opts,
                NullLogger<OrchestratorService>.Instance,
                projects: projectRepo,
                releaseService: releaseService,
                budgetDeferralRecheck: snapshot);

            var refactor = MakeQueued(pid, JobType.Refactor);
            await _store.CreateAsync(refactor);
            await queue.EnqueueAsync(refactor.Id);

            await svc.StartAsync(CancellationToken.None);
            await WaitForStartedAsync(_store, refactor.Id);

            var blockedNormal = MakeQueued(pid) with { ReleaseId = release.Id };
            await _store.CreateAsync(blockedNormal);
            await queue.EnqueueAsync(blockedNormal.Id);

            await WaitForDeferredAsync(svc, blockedNormal.Id);
            Assert.Equal(0, gitHost.EnsureRepositoryCalls);
            var stored = await _store.GetAsync(blockedNormal.Id);
            Assert.NotNull(stored);
            Assert.Null(stored!.StartedAt);
            Assert.Null(stored.BaseBranch);

            pipelineGate.TrySetResult();
            await svc.StopAsync(CancellationToken.None);
        }
        finally
        {
            try { File.Delete(releaseDbPath); } catch { }
        }
    }

    [Fact]
    public async Task FinalRefactorGateDefersConcurrentPickupAfterEarlyGateRace()
    {
        var pid = "proj-refactor-final-race";
        var project = new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Final Race",
            RepositoryUrl = "https://github.com/test/repo",
        };
        var projectRepo = new BarrierProjectRepository(project, expectedBarrierCalls: 1);

        var pipelineGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ManualReleasePipelineRunner(_store, pipelineGate.Task);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 2 };
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

        var startedId = await WaitForStartedAnyAsync(_store, [refactor1.Id, refactor2.Id]);
        var blockedId = startedId == refactor1.Id ? refactor2.Id : refactor1.Id;

        await WaitForDeferredAsync(svc, blockedId);
        var first = await _store.GetAsync(refactor1.Id);
        var second = await _store.GetAsync(refactor2.Id);
        Assert.Equal(1, new[] { first, second }.Count(i => i!.StartedAt is not null));
        Assert.Null((await _store.GetAsync(blockedId))!.StartedAt);

        pipelineGate.TrySetResult();
        await WaitForStartedAsync(_store, blockedId);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RefactorGateRunsBeforeRouterSideEffects()
    {
        var pid = "proj-refactor-before-router";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Before Router",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "empty-class",
                    DisplayName = "Empty Class",
                    Members = [],
                },
            ],
            [],
            new QuotaRouterOptions(),
            NullLogger<AgentClassRouter>.Instance);
        var pipelineGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ManualReleasePipelineRunner(_store, pipelineGate.Task);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 2 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMinutes(5),
        });
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            router: router,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        var refactor = MakeQueued(pid, JobType.Refactor);
        await _store.CreateAsync(refactor);
        await queue.EnqueueAsync(refactor.Id);

        await svc.StartAsync(CancellationToken.None);

        await WaitForStartedAsync(_store, refactor.Id);

        var blockedNormal = MakeQueued(pid) with
        {
            AgentClassId = "empty-class",
        };
        await _store.CreateAsync(blockedNormal);
        await queue.EnqueueAsync(blockedNormal.Id);

        await WaitForDeferredAsync(svc, blockedNormal.Id);
        var stored = await _store.GetAsync(blockedNormal.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Queued, stored!.State);
        Assert.Null(stored.StartedAt);
        Assert.Null(stored.LastError);

        pipelineGate.TrySetResult();
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EligibleRefactorDrainsProjectBeforeLowerPriorityFreshNormalStarts()
    {
        var pid = "proj-refactor-drain";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Drain",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var pipeline = new PerItemReleasePipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 3 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMilliseconds(100),
        });
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        var inFlightNormal = MakeQueued(pid) with { Priority = 0 };
        await _store.CreateAsync(inFlightNormal);
        await queue.EnqueueAsync(inFlightNormal.Id);

        await svc.StartAsync(CancellationToken.None);
        await WaitForStartedAsync(_store, inFlightNormal.Id);

        var refactor = MakeQueued(pid, JobType.Refactor) with { Priority = 100 };
        var lowerPriorityNormal = MakeQueued(pid) with { Priority = 0 };
        await _store.CreateAsync(refactor);
        await _store.CreateAsync(lowerPriorityNormal);
        await queue.EnqueueAsync(lowerPriorityNormal.Id);
        await queue.EnqueueAsync(refactor.Id);

        await WaitForDeferredAsync(svc, refactor.Id);
        await WaitForDeferredAsync(svc, lowerPriorityNormal.Id);
        Assert.Null((await _store.GetAsync(lowerPriorityNormal.Id))!.StartedAt);

        var draining = Assert.Single(await svc.GetRefactorProjectGateStatusAsync());
        Assert.Equal("draining", draining.State);
        Assert.Equal(refactor.Id, draining.RefactorWorkItemId);
        Assert.Equal(1, draining.OtherInFlight);

        pipeline.Release(inFlightNormal.Id);
        await WaitForStartedAsync(_store, refactor.Id);
        Assert.Null((await _store.GetAsync(lowerPriorityNormal.Id))!.StartedAt);

        var locked = Assert.Single(await svc.GetRefactorProjectGateStatusAsync());
        Assert.Equal("locked", locked.State);
        Assert.Equal(refactor.Id, locked.RefactorWorkItemId);

        pipeline.Release(refactor.Id);
        await WaitForStartedAsync(_store, lowerPriorityNormal.Id);

        pipeline.Release(lowerPriorityNormal.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BusyProjectLowerPriorityRefactorDoesNotDrainBeforeHigherPriorityFreshNormal()
    {
        var pid = "proj-refactor-drain-priority-order";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Drain Priority Order",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var pipeline = new PerItemReleasePipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMinutes(5),
        });
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        var inFlightNormal = MakeQueued(pid);
        await _store.CreateAsync(inFlightNormal);
        await queue.EnqueueAsync(inFlightNormal.Id);

        await svc.StartAsync(CancellationToken.None);
        await WaitForStartedAsync(_store, inFlightNormal.Id);

        var refactorCreatedAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        var refactor = MakeQueued(pid, JobType.Refactor) with
        {
            Priority = 0,
            CreatedAt = refactorCreatedAt,
        };
        var higherPriorityNormal = MakeQueued(pid) with
        {
            Priority = 100,
            CreatedAt = refactorCreatedAt.AddSeconds(1),
        };
        await _store.CreateAsync(refactor);
        await _store.CreateAsync(higherPriorityNormal);
        await queue.EnqueueAsync(refactor.Id);
        await queue.EnqueueAsync(higherPriorityNormal.Id);

        await WaitForStartedAsync(_store, higherPriorityNormal.Id);
        Assert.False(svc.IsDeferredForTest(higherPriorityNormal.Id));
        Assert.Null((await _store.GetAsync(refactor.Id))!.StartedAt);

        pipeline.Release(higherPriorityNormal.Id);
        pipeline.Release(inFlightNormal.Id);
        await WaitForStartedAsync(_store, refactor.Id, timeoutSeconds: 30);

        pipeline.Release(refactor.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancelledQueuedRefactorClearsDrainClaimAndAllowsSameProjectNormal()
    {
        var pid = "proj-refactor-drain-cancelled";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Drain Cancelled",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var pipeline = new PerItemReleasePipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMinutes(5),
        });
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        var inFlightNormal = MakeQueued(pid);
        await _store.CreateAsync(inFlightNormal);
        await queue.EnqueueAsync(inFlightNormal.Id);

        await svc.StartAsync(CancellationToken.None);
        await WaitForStartedAsync(_store, inFlightNormal.Id);

        var refactor = MakeQueued(pid, JobType.Refactor) with { Priority = 100 };
        await _store.CreateAsync(refactor);
        await queue.EnqueueAsync(refactor.Id);

        await WaitForDeferredAsync(svc, refactor.Id);
        Assert.Single(await svc.GetRefactorProjectGateStatusAsync());

        var storedRefactor = await _store.GetAsync(refactor.Id);
        Assert.NotNull(storedRefactor);
        await _store.UpdateAsync(storedRefactor!.With(WorkItemState.Cancelled));

        Assert.Empty(await svc.GetRefactorProjectGateStatusAsync());

        var normalAfterCancel = MakeQueued(pid) with { Priority = 100 };
        await _store.CreateAsync(normalAfterCancel);
        await queue.EnqueueAsync(normalAfterCancel.Id);

        await WaitForStartedAsync(_store, normalAfterCancel.Id);
        Assert.False(svc.IsDeferredForTest(normalAfterCancel.Id));

        pipeline.Release(normalAfterCancel.Id);
        pipeline.Release(inFlightNormal.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RefactorReservationClearsWhenAgentCapDefersBeforeStartedAt()
    {
        var pid = "proj-refactor-cap-deferral";
        var pipeline = new PerItemReleasePipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMinutes(5),
        });
        var agentConcurrency = new AgentConcurrencyOptions
        {
            Members =
            {
                [AgentKind.Claude.Value] = new AgentConcurrencyEntry { MaxConcurrent = 1 },
            },
        };
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: agentConcurrency,
            budgetDeferralRecheck: snapshot);

        var otherProjectClaude = MakeQueued("proj-other") with
        {
            Agent = AgentKind.Claude,
        };
        await _store.CreateAsync(otherProjectClaude);
        await queue.EnqueueAsync(otherProjectClaude.Id);

        await svc.StartAsync(CancellationToken.None);
        await WaitForStartedAsync(_store, otherProjectClaude.Id);

        var refactor = MakeQueued(pid, JobType.Refactor) with
        {
            Agent = AgentKind.Claude,
            Priority = 100,
        };
        await _store.CreateAsync(refactor);
        await queue.EnqueueAsync(refactor.Id);

        await WaitForDeferredAsync(svc, refactor.Id);
        Assert.Empty(await svc.GetRefactorProjectGateStatusAsync());

        var sameProjectNormal = MakeQueued(pid);
        await _store.CreateAsync(sameProjectNormal);
        await queue.EnqueueAsync(sameProjectNormal.Id);

        await WaitForStartedAsync(_store, sameProjectNormal.Id);
        Assert.False(svc.IsDeferredForTest(sameProjectNormal.Id));

        pipeline.Release(sameProjectNormal.Id);
        pipeline.Release(otherProjectClaude.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReservedFreshNormalDefersWhenRefactorDrainClaimAppearsBeforeStartedAt()
    {
        var pid = "proj-refactor-reserved-normal";
        var projectRepo = new SwitchableBlockingProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Reserved Normal",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var pipeline = new PerItemReleasePipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMinutes(5),
        });
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 3 },
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        var inFlightNormal = MakeQueued(pid);
        await _store.CreateAsync(inFlightNormal);
        await queue.EnqueueAsync(inFlightNormal.Id);

        await svc.StartAsync(CancellationToken.None);
        await WaitForStartedAsync(_store, inFlightNormal.Id);

        projectRepo.EnableBlock();
        var reservedNormal = MakeQueued(pid) with { Priority = 100 };
        await _store.CreateAsync(reservedNormal);
        await queue.EnqueueAsync(reservedNormal.Id);
        await projectRepo.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null((await _store.GetAsync(reservedNormal.Id))!.StartedAt);

        var refactor = MakeQueued(pid, JobType.Refactor);
        await _store.CreateAsync(refactor);
        await queue.EnqueueAsync(refactor.Id);
        await WaitForDeferredAsync(svc, refactor.Id);

        projectRepo.Release();
        await WaitForDeferredAsync(svc, reservedNormal.Id);
        Assert.Null((await _store.GetAsync(reservedNormal.Id))!.StartedAt);

        pipeline.Release(inFlightNormal.Id);
        await WaitForStartedAsync(_store, refactor.Id, timeoutSeconds: 30);

        pipeline.Release(refactor.Id);
        await WaitForStartedAsync(_store, reservedNormal.Id, timeoutSeconds: 30);

        pipeline.Release(reservedNormal.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DrainedProjectStartsClaimedRefactorWithoutWaitingForRecheckTimer()
    {
        var pid = "proj-refactor-drain-wake";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Drain Wake",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var pipeline = new PerItemReleasePipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMinutes(5),
        });
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        var normal = MakeQueued(pid);
        await _store.CreateAsync(normal);
        await queue.EnqueueAsync(normal.Id);

        await svc.StartAsync(CancellationToken.None);
        await WaitForStartedAsync(_store, normal.Id);

        var refactor = MakeQueued(pid, JobType.Refactor) with { Priority = 100 };
        await _store.CreateAsync(refactor);
        await queue.EnqueueAsync(refactor.Id);
        await WaitForDeferredAsync(svc, refactor.Id);

        pipeline.Release(normal.Id);
        await WaitForStartedAsync(_store, refactor.Id, timeoutSeconds: 30);

        pipeline.Release(refactor.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PartialDrainReleaseKeepsClaimedRefactorDeferredUntilFinalNormalCompletes()
    {
        var pid = "proj-refactor-partial-drain-wake";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Partial Drain Wake",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var pipeline = new PerItemReleasePipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMinutes(5),
        });
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 3 },
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        var normal1 = MakeQueued(pid);
        var normal2 = MakeQueued(pid);
        await _store.CreateAsync(normal1);
        await _store.CreateAsync(normal2);
        await queue.EnqueueAsync(normal1.Id);
        await queue.EnqueueAsync(normal2.Id);

        await svc.StartAsync(CancellationToken.None);
        await WaitForStartedAsync(_store, normal1.Id);
        await WaitForStartedAsync(_store, normal2.Id);

        var refactor = MakeQueued(pid, JobType.Refactor) with { Priority = 100 };
        await _store.CreateAsync(refactor);
        await queue.EnqueueAsync(refactor.Id);
        await WaitForDeferredAsync(svc, refactor.Id);
        var initialGeneration = svc.DeferredGenerationForTest(refactor.Id);
        Assert.NotNull(initialGeneration);

        pipeline.Release(normal1.Id);
        await WaitForConditionAsync(
            () => svc.CurrentlyRunningTotal == 1,
            "first normal did not leave the worker pool");
        await Task.Delay(200);

        Assert.Equal(initialGeneration, svc.DeferredGenerationForTest(refactor.Id));
        Assert.Null((await _store.GetAsync(refactor.Id))!.StartedAt);

        pipeline.Release(normal2.Id);
        await WaitForStartedAsync(_store, refactor.Id, timeoutSeconds: 30);

        pipeline.Release(refactor.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RefactorCompletionWakesHeldNormalWithoutWaitingForRecheckTimer()
    {
        var pid = "proj-refactor-normal-wake";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Normal Wake",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var pipeline = new PerItemReleasePipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMinutes(5),
        });
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        var refactor = MakeQueued(pid, JobType.Refactor) with { Priority = 100 };
        var normal = MakeQueued(pid);
        await _store.CreateAsync(refactor);
        await _store.CreateAsync(normal);
        await queue.EnqueueAsync(refactor.Id);
        await queue.EnqueueAsync(normal.Id);

        await svc.StartAsync(CancellationToken.None);
        await WaitForStartedAsync(_store, refactor.Id);
        await WaitForDeferredAsync(svc, normal.Id);

        pipeline.Release(refactor.Id);
        await WaitForStartedAsync(_store, normal.Id, timeoutSeconds: 30);

        pipeline.Release(normal.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LowerPriorityRefactorDoesNotClaimDrainBeforeHigherPriorityFreshNormal()
    {
        var pid = "proj-refactor-priority";
        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(
            queue,
            _store,
            new ManualReleasePipelineRunner(_store, Task.CompletedTask),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        var refactor = MakeQueued(pid, JobType.Refactor) with { Priority = 0 };
        var higherPriorityNormal = MakeQueued(pid) with { Priority = 100 };
        await _store.CreateAsync(refactor);
        await _store.CreateAsync(higherPriorityNormal);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);

        Assert.Equal(higherPriorityNormal.Id, picked);
        Assert.False(svc.IsDeferredForTest(higherPriorityNormal.Id));
        Assert.Empty(await svc.GetRefactorProjectGateStatusAsync());
        svc.Dispose();
    }

    [Fact]
    public async Task SelectedRefactorReservationHoldsFreshNormalUntilStartedAtIsDurable()
    {
        var pid = "proj-refactor-reservation";
        var projectRepo = new BlockingProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Reservation",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var pipeline = new PerItemReleasePipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 2 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMilliseconds(100),
        });
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        var refactor = MakeQueued(pid, JobType.Refactor);
        await _store.CreateAsync(refactor);
        await queue.EnqueueAsync(refactor.Id);

        await svc.StartAsync(CancellationToken.None);
        await projectRepo.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null((await _store.GetAsync(refactor.Id))!.StartedAt);

        var normal = MakeQueued(pid) with { Priority = 100 };
        await _store.CreateAsync(normal);
        await queue.EnqueueAsync(normal.Id);

        await WaitForDeferredAsync(svc, normal.Id);
        Assert.Null((await _store.GetAsync(normal.Id))!.StartedAt);

        var gate = Assert.Single(await svc.GetRefactorProjectGateStatusAsync());
        Assert.Equal("draining", gate.State);
        Assert.Equal(refactor.Id, gate.RefactorWorkItemId);
        Assert.Equal(0, gate.RefactorInFlight);
        Assert.Equal(0, gate.OtherInFlight);

        projectRepo.Release.TrySetResult();
        await WaitForStartedAsync(_store, refactor.Id);

        pipeline.Release(refactor.Id);
        await WaitForStartedAsync(_store, normal.Id);
        pipeline.Release(normal.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DrainClaimUsesDispatchPriorityBeforeCreatedAtForRefactorOwner()
    {
        var pid = "proj-refactor-first-claim";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor First Claim",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var pipelineGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ManualReleasePipelineRunner(_store, pipelineGate.Task);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 3 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMilliseconds(100),
        });
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        var normal = MakeQueued(pid);
        await _store.CreateAsync(normal);
        await queue.EnqueueAsync(normal.Id);

        await svc.StartAsync(CancellationToken.None);
        await WaitForStartedAsync(_store, normal.Id);

        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        var firstRefactor = MakeQueued(pid, JobType.Refactor) with
        {
            Priority = 10,
            CreatedAt = createdAt,
        };
        var laterRefactor = MakeQueued(pid, JobType.Refactor) with
        {
            Priority = 100,
            CreatedAt = createdAt.AddSeconds(1),
        };
        await _store.CreateAsync(firstRefactor);
        await _store.CreateAsync(laterRefactor);
        await queue.EnqueueAsync(laterRefactor.Id);
        await queue.EnqueueAsync(firstRefactor.Id);

        await WaitForDeferredAsync(svc, firstRefactor.Id);
        await WaitForDeferredAsync(svc, laterRefactor.Id);

        var gate = Assert.Single(await svc.GetRefactorProjectGateStatusAsync());
        Assert.Equal("draining", gate.State);
        Assert.Equal(laterRefactor.Id, gate.RefactorWorkItemId);

        pipelineGate.TrySetResult();
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QueuedRefactorDrainDoesNotBlockOtherProjectStarts()
    {
        var projectRepo = new InMemoryProjectRepository(
            new Project
            {
                Id = new ProjectId("proj-drain-a"),
                DisplayName = "Drain A",
                RepositoryUrl = "https://github.com/test/a",
            },
            new Project
            {
                Id = new ProjectId("proj-drain-b"),
                DisplayName = "Drain B",
                RepositoryUrl = "https://github.com/test/b",
            });

        var pipeline = new PerItemReleasePipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 3 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMilliseconds(100),
        });
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        var inFlightA = MakeQueued("proj-drain-a");
        await _store.CreateAsync(inFlightA);
        await queue.EnqueueAsync(inFlightA.Id);

        await svc.StartAsync(CancellationToken.None);
        await WaitForStartedAsync(_store, inFlightA.Id);

        var refactorA = MakeQueued("proj-drain-a", JobType.Refactor);
        var normalB = MakeQueued("proj-drain-b") with { Priority = 100 };
        await _store.CreateAsync(refactorA);
        await _store.CreateAsync(normalB);
        await queue.EnqueueAsync(refactorA.Id);
        await queue.EnqueueAsync(normalB.Id);

        await WaitForDeferredAsync(svc, refactorA.Id);
        await WaitForStartedAsync(_store, normalB.Id);
        Assert.Null((await _store.GetAsync(refactorA.Id))!.StartedAt);
        Assert.False(svc.IsDeferredForTest(normalB.Id));

        pipeline.Release(inFlightA.Id);
        await WaitForStartedAsync(_store, refactorA.Id);
        pipeline.Release(refactorA.Id);
        pipeline.Release(normalB.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RefactorDrainAllowsStartedSameProjectContinuationToRun()
    {
        var pid = "proj-refactor-continuation";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Continuation",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<WorkItemId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ManualReleasePipelineRunner(_store, releaseGate.Task, entered);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 2 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMilliseconds(100),
        });
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        var continuation = MakeQueued(pid) with
        {
            State = WorkItemState.WorkComplete,
            StartedAt = DateTimeOffset.UtcNow,
        };
        var refactor = MakeQueued(pid, JobType.Refactor);
        await _store.CreateAsync(continuation);
        await _store.CreateAsync(refactor);
        await queue.EnqueueAsync(refactor.Id);
        await queue.EnqueueAsync(continuation.Id);

        await svc.StartAsync(CancellationToken.None);

        var enteredId = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(continuation.Id, enteredId);
        Assert.False(svc.IsDeferredForTest(continuation.Id));
        await WaitForDeferredAsync(svc, refactor.Id);

        releaseGate.TrySetResult();
        await WaitForStartedAsync(_store, refactor.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartedNonRefactorContinuationWaitsBehindActiveRefactor()
    {
        var pid = "proj-refactor-started-normal-lock";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Started Normal Lock",
            RepositoryUrl = "https://github.com/test/repo",
        });

        var pipeline = new PerItemReleasePipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            RefactorExclusivityRecheck = TimeSpan.FromMinutes(5),
        });
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        var refactor = MakeQueued(pid, JobType.Refactor);
        await _store.CreateAsync(refactor);
        await queue.EnqueueAsync(refactor.Id);

        await svc.StartAsync(CancellationToken.None);
        await pipeline.WaitForEnteredAsync(refactor.Id);

        var continuation = MakeQueued(pid) with
        {
            State = WorkItemState.WorkComplete,
            StartedAt = DateTimeOffset.UtcNow,
            Priority = 100,
        };
        await _store.CreateAsync(continuation);
        await queue.EnqueueAsync(continuation.Id);

        await WaitForDeferredAsync(svc, continuation.Id);
        Assert.False(pipeline.HasEntered(continuation.Id));

        pipeline.Release(refactor.Id);
        await pipeline.WaitForEnteredAsync(continuation.Id, timeoutSeconds: 30);

        pipeline.Release(continuation.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FreshRefactorDrainAllowsStartedRefactorContinuationToRun()
    {
        var pid = "proj-refactor-continuation-refactor";
        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(
            queue,
            _store,
            new ManualReleasePipelineRunner(_store, Task.CompletedTask),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        var continuation = MakeQueued(pid, JobType.Refactor) with
        {
            State = WorkItemState.WorkComplete,
            StartedAt = DateTimeOffset.UtcNow,
            Priority = 0,
        };
        var freshRefactor = MakeQueued(pid, JobType.Refactor) with { Priority = 100 };
        await _store.CreateAsync(continuation);
        await _store.CreateAsync(freshRefactor);

        using var cts = new CancellationTokenSource();
        var picked = await svc.PickNextEligibleForTestAsync(cts.Token);

        Assert.Equal(continuation.Id, picked);
        Assert.False(svc.IsDeferredForTest(continuation.Id));
        await WaitForDeferredAsync(svc, freshRefactor.Id);
        cts.Cancel();
        svc.Dispose();
    }

    [Fact]
    public async Task RefactorWithUnsatisfiedDependenciesDoesNotClaimDrain()
    {
        var pid = "proj-refactor-unsatisfied-dep";
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(pid),
            DisplayName = "Refactor Unsatisfied Dep",
            RepositoryUrl = "https://github.com/test/repo",
        });
        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(
            queue,
            _store,
            new ManualReleasePipelineRunner(_store, Task.CompletedTask),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo);

        var failedDependency = MakeQueued(pid) with { State = WorkItemState.Failed };
        var blockedRefactor = MakeQueued(pid, JobType.Refactor) with
        {
            Priority = 100,
            DependsOn = [failedDependency.Id],
        };
        var normal = MakeQueued(pid);
        await _store.CreateAsync(failedDependency);
        await _store.CreateAsync(blockedRefactor);
        await _store.CreateAsync(normal);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);

        Assert.Equal(normal.Id, picked);
        Assert.Empty(await svc.GetRefactorProjectGateStatusAsync());
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

    private static async Task<WorkItemId> WaitForStartedAnyAsync(
        IWorkItemStore store,
        IReadOnlyList<WorkItemId> ids,
        int timeoutSeconds = 10)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var id in ids)
            {
                var item = await store.GetAsync(id);
                if (item?.StartedAt is not null)
                    return id;
            }
            await Task.Delay(25);
        }

        var states = new List<string>();
        foreach (var id in ids)
        {
            var item = await store.GetAsync(id);
            states.Add($"{id}: state={item?.State}, startedAt={item?.StartedAt:O}");
        }
        throw new InvalidOperationException(
            $"No work item transitioned to in-flight within {timeoutSeconds}s: {string.Join("; ", states)}");
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

    private static async Task WaitForConditionAsync(
        Func<bool> predicate,
        string failureMessage,
        int timeoutSeconds = 10)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }

        throw new InvalidOperationException(failureMessage);
    }

    private sealed class BarrierProjectRepository : IProjectRepository
    {
        private readonly Project _project;
        private readonly int _expectedBarrierCalls;
        private readonly TaskCompletionSource _barrier =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public BarrierProjectRepository(Project project, int expectedBarrierCalls)
        {
            _project = project;
            _expectedBarrierCalls = expectedBarrierCalls;
        }

        public async Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default)
        {
            if (id == _project.Id)
            {
                var call = Interlocked.Increment(ref _calls);
                if (call <= _expectedBarrierCalls)
                {
                    if (call == _expectedBarrierCalls)
                        _barrier.TrySetResult();
                    await _barrier.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                }
            }

            return id == _project.Id ? _project : null;
        }

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Project>>([_project]);
    }

    private sealed class BlockingProjectRepository : IProjectRepository
    {
        private readonly Project _project;

        public BlockingProjectRepository(Project project) => _project = project;

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default)
        {
            if (id != _project.Id)
                return null;

            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return _project;
        }

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Project>>([_project]);
    }

    private sealed class SwitchableBlockingProjectRepository : IProjectRepository
    {
        private readonly Project _project;
        private readonly object _lock = new();
        private bool _blocked;
        private TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SwitchableBlockingProjectRepository(Project project) => _project = project;

        public Task Entered
        {
            get
            {
                lock (_lock)
                    return _entered.Task;
            }
        }

        public void EnableBlock()
        {
            lock (_lock)
            {
                _blocked = true;
                _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Release()
        {
            TaskCompletionSource release;
            lock (_lock)
            {
                _blocked = false;
                release = _release;
            }

            release.TrySetResult();
        }

        public async Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default)
        {
            if (id != _project.Id)
                return null;

            Task? waitForRelease = null;
            lock (_lock)
            {
                if (_blocked)
                {
                    _entered.TrySetResult();
                    waitForRelease = _release.Task;
                }
            }

            if (waitForRelease is not null)
                await waitForRelease.WaitAsync(TimeSpan.FromSeconds(10), ct);

            return _project;
        }

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Project>>([_project]);
    }

    private sealed class CountingGitHost : IGitHost
    {
        private int _ensureRepositoryCalls;
        public int EnsureRepositoryCalls => Volatile.Read(ref _ensureRepositoryCalls);

        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _ensureRepositoryCalls);
            throw new InvalidOperationException("release branch setup should not run while refactor gate blocks pickup");
        }

        public Task<string> EnsureRepositoryAsync(
            WorkItemId id,
            string? seedFromUrl,
            string? baseBranch,
            CancellationToken ct = default) =>
            EnsureRepositoryAsync(id, seedFromUrl, ct);

        public SandboxRepositoryAccess GetSandboxAccess(string repositoryId) =>
            throw new NotSupportedException();

        public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default) =>
            Task.FromResult("main");

        public Task PushToUpstreamAsync(
            string repositoryId,
            string upstreamUrl,
            string branch,
            IReadOnlyDictionary<string, string> upstreamEnv,
            UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
            string repositoryId,
            string baseBranch,
            string workBranch,
            CancellationToken ct = default) =>
            Task.FromResult(("", ""));
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
        private readonly TaskCompletionSource<WorkItemId>? _entered;

        public ManualReleasePipelineRunner(
            IWorkItemStore store,
            Task releaseGate,
            TaskCompletionSource<WorkItemId>? entered = null)
        {
            _store = store;
            _releaseGate = releaseGate;
            _entered = entered;
        }

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            _entered?.TrySetResult(item.Id);
            try { await _releaseGate.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
        }
    }

    private sealed class PerItemReleasePipelineRunner : IPipelineRunner
    {
        private readonly IWorkItemStore _store;
        private readonly Dictionary<WorkItemId, TaskCompletionSource> _releaseGates = [];
        private readonly Dictionary<WorkItemId, TaskCompletionSource> _entered = [];
        private readonly object _lock = new();

        public PerItemReleasePipelineRunner(IWorkItemStore store) => _store = store;

        public bool HasEntered(WorkItemId id)
        {
            lock (_lock)
            {
                return _entered.TryGetValue(id, out var entered) && entered.Task.IsCompleted;
            }
        }

        public Task WaitForEnteredAsync(WorkItemId id, int timeoutSeconds = 10)
        {
            TaskCompletionSource entered;
            lock (_lock)
            {
                if (!_entered.TryGetValue(id, out entered!))
                {
                    entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _entered[id] = entered;
                }
            }

            return entered.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
        }

        public void Release(WorkItemId id)
        {
            TaskCompletionSource gate;
            lock (_lock)
            {
                if (!_releaseGates.TryGetValue(id, out gate!))
                {
                    gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _releaseGates[id] = gate;
                }
            }
            gate.TrySetResult();
        }

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            TaskCompletionSource gate;
            TaskCompletionSource entered;
            lock (_lock)
            {
                if (!_entered.TryGetValue(item.Id, out entered!))
                {
                    entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _entered[item.Id] = entered;
                }

                if (!_releaseGates.TryGetValue(item.Id, out gate!))
                {
                    gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _releaseGates[item.Id] = gate;
                }
            }

            entered.TrySetResult();
            try { await gate.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
        }
    }
}
