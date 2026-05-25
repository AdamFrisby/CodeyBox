using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for the safety nets that keep WaitingForQuotaReset items isolated
/// from the regular dispatch / recovery / in-flight code paths until the
/// quota retry scheduler decides to re-enqueue them. These cases all guard
/// silent regressions where a small one-line change (deleting a branch or
/// flipping a literal) would cause a parked item to be re-dispatched and
/// immediately re-fail with the same quota error.
/// </summary>
public sealed class WaitingForQuotaResetTests : IDisposable
{
    private readonly string _workspace;

    public WaitingForQuotaResetTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-waiting-").FullName;

    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public void WorkItemWith_TransitionToWaitingForQuotaReset_PreservesQuotaFields()
    {
        // The retry scheduler re-arms targeted timers across host restart from
        // QuotaResetAt + NextQuotaRetryAt; both must survive the .With() call
        // when the next state is also a quota-shaped state.
        var resetAt = DateTimeOffset.UtcNow.AddHours(1);
        var nextRetryAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var initial = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Working,
            QuotaResetAt = resetAt,
            NextQuotaRetryAt = nextRetryAt,
            FailureKind = null,
        };

        var transitioned = initial.With(
            WorkItemState.WaitingForQuotaReset, "all members exhausted",
            failureKind: "quota", quotaResetAt: resetAt);

        Assert.Equal(WorkItemState.WaitingForQuotaReset, transitioned.State);
        Assert.Equal("quota", transitioned.FailureKind);
        Assert.Equal(resetAt, transitioned.QuotaResetAt);
        // Critical: NextQuotaRetryAt must NOT be cleared. The retry scheduler
        // uses this field on restart to decide whether to re-arm a targeted
        // timer or rely on the periodic sweep.
        Assert.Equal(nextRetryAt, transitioned.NextQuotaRetryAt);
    }

    [Fact]
    public async Task QuotaWaitParker_PreservesPreemptCheckpointForParkedWorkingItem()
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var resetAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var preemptedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var id = WorkItemId.New();
        var checkpoint = $"refs/heads/codeybox/preempt/{id}";
        var item = new WorkItem
        {
            Id = id,
            ProjectId = new ProjectId("p1"),
            Title = "preempted",
            Prompt = "p",
            State = WorkItemState.Working,
            PreemptedAt = preemptedAt,
            PreemptCheckpoint = checkpoint,
        };
        await store.CreateAsync(item);

        var parker = new QuotaWaitParker(store, timeProvider: new FixedClock(DateTimeOffset.UtcNow));
        await parker.ParkAsync(new QuotaWaitParkRequest(
            item,
            "insufficient headroom",
            "work",
            resetAt));

        var parked = await store.GetAsync(item.Id);
        Assert.NotNull(parked);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, parked!.State);
        Assert.Equal(preemptedAt, parked.PreemptedAt);
        Assert.Equal(checkpoint, parked.PreemptCheckpoint);
        Assert.Equal("work", parked.QuotaRetryFrom);
    }

    [Theory]
    [InlineData("work", WorkItemState.Working)]
    [InlineData("audit", WorkItemState.Reworking)]
    public async Task WorkItemRetrier_WaitingForQuotaResetWithPreemptCheckpoint_ResumesCheckpointState(
        string retryFrom,
        WorkItemState expectedState)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var queue = new InMemoryTaskQueue();
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var id = WorkItemId.New();
        var checkpoint = $"refs/heads/codeybox/preempt/{id}";
        var item = new WorkItem
        {
            Id = id,
            ProjectId = new ProjectId("p1"),
            Title = "preempted",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            WorkBranch = "codeybox/test",
            PreemptedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            PreemptCheckpoint = checkpoint,
            QuotaRetryFrom = retryFrom,
        };
        await store.CreateAsync(item);
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await TestSupport.RunGit(gitHost.GetRepoPath(repoId), "update-ref", checkpoint, "refs/heads/main");
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);

        var result = await retrier.RetryAsync(item, retryFrom, trigger: "periodic");

        Assert.True(result.Success, result.Error);
        Assert.Equal(expectedState, result.ResumeState);
        Assert.Equal(retryFrom, result.ActualFrom);
        var stored = await store.GetAsync(item.Id);
        Assert.Equal(expectedState, stored!.State);
        Assert.Equal(checkpoint, stored.PreemptCheckpoint);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public void WorkItemWith_TransitionToNonQuotaState_ClearsQuotaFields()
    {
        // Symmetric guard: transitioning back to Queued for a retry must clear
        // FailureKind / QuotaResetAt / NextQuotaRetryAt so the scheduler
        // doesn't see a stale "still parked" record on the next pickup.
        var initial = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            QuotaResetAt = DateTimeOffset.UtcNow,
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddMinutes(15),
            FailureKind = "quota",
        };

        var transitioned = initial.With(WorkItemState.Queued);

        Assert.Equal(WorkItemState.Queued, transitioned.State);
        Assert.Null(transitioned.FailureKind);
        Assert.Null(transitioned.QuotaResetAt);
        Assert.Null(transitioned.NextQuotaRetryAt);
    }

    [Fact]
    public async Task SqliteWorkItemStore_CountInFlight_ExcludesWaitingForQuotaReset()
    {
        // A WaitingForQuotaReset item must not count against the project's
        // concurrent in-flight cap — otherwise the cap could be permanently
        // saturated by items waiting on a quota window.
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var pid = new ProjectId("p1");

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "parked",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaResetAt = DateTimeOffset.UtcNow.AddHours(1),
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddMinutes(10),
            StartedAt = DateTimeOffset.UtcNow,
        };
        await store.CreateAsync(parked);

        var inflight = await store.CountInFlightAsync(pid);
        Assert.Equal(0, inflight);

        // Sanity contrast: a Working item with the same StartedAt does count.
        var working = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "working",
            Prompt = "p",
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await store.CreateAsync(working);

        Assert.Equal(1, await store.CountInFlightAsync(pid));
    }

    [Fact]
    public async Task QuotaWaitParker_DoesNotOverwriteConcurrentCancel()
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var queuedSnapshot = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await store.CreateAsync(queuedSnapshot);

        await store.UpdateAsync(
            queuedSnapshot.With(
                WorkItemState.Cancelled,
                "cancelled via API",
                cancellationReason: WorkItemCancellationReason.OperatorRequested));

        var parker = new QuotaWaitParker(store, timeProvider: new FixedClock(DateTimeOffset.UtcNow));
        await parker.ParkAsync(new QuotaWaitParkRequest(
            queuedSnapshot,
            "insufficient headroom",
            "work",
            DateTimeOffset.UtcNow.AddHours(1)));

        var refetched = await store.GetAsync(queuedSnapshot.Id);
        Assert.NotNull(refetched);
        Assert.Equal(WorkItemState.Cancelled, refetched!.State);
        Assert.Equal(WorkItemCancellationReason.OperatorRequested, refetched.CancellationReason);
        Assert.Null(refetched.QuotaResetAt);
        Assert.Null(refetched.NextQuotaRetryAt);
    }

    [Fact]
    public async Task QuotaWaitParker_ConditionalUpdateRaceReturnsWithoutNotification()
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var innerStore = new SqliteWorkItemStore(stateDb);
        var store = new ConcurrentChangeOnTryUpdateStore(innerStore);

        var queuedSnapshot = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await store.CreateAsync(queuedSnapshot);

        var notifier = new CapturingQuotaRetryNotifier();
        var webhooks = new CapturingWebhookDispatcher();
        var parker = new QuotaWaitParker(
            store,
            webhooks,
            notifier,
            timeProvider: new FixedClock(DateTimeOffset.UtcNow));

        await parker.ParkAsync(new QuotaWaitParkRequest(
            queuedSnapshot,
            "insufficient headroom",
            "work",
            DateTimeOffset.UtcNow.AddHours(1)));

        var refetched = await store.GetAsync(queuedSnapshot.Id);
        Assert.NotNull(refetched);
        Assert.Equal(WorkItemState.Cancelled, refetched!.State);
        Assert.Equal("cancelled during conditional update", refetched.LastError);
        Assert.Null(refetched.QuotaResetAt);
        Assert.Null(refetched.NextQuotaRetryAt);
        Assert.Empty(notifier.Notifications);
        Assert.Empty(webhooks.Events);
    }

    [Fact]
    public async Task QuotaWaitParker_DoesNotOverwriteTerminalItem()
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var terminal = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Done,
        };
        await store.CreateAsync(terminal);

        var notifier = new CapturingQuotaRetryNotifier();
        var parker = new QuotaWaitParker(
            store,
            retryNotifier: notifier,
            timeProvider: new FixedClock(DateTimeOffset.UtcNow));

        await parker.ParkAsync(new QuotaWaitParkRequest(
            terminal,
            "insufficient headroom",
            "work",
            DateTimeOffset.UtcNow.AddHours(1)));

        var refetched = await store.GetAsync(terminal.Id);
        Assert.NotNull(refetched);
        Assert.Equal(WorkItemState.Done, refetched!.State);
        Assert.Null(refetched.FailureKind);
        Assert.Null(refetched.QuotaResetAt);
        Assert.Null(refetched.NextQuotaRetryAt);
        Assert.Empty(notifier.Notifications);
    }

    [Fact]
    public async Task QuotaWaitParker_DeletedStoreRowReturnsWithoutNotification()
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var deletedSnapshot = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await store.CreateAsync(deletedSnapshot);

        await using (var deleteConn = new SqliteConnection($"Data Source={stateDb}"))
        {
            await deleteConn.OpenAsync();
            await using var deleteCmd = deleteConn.CreateCommand();
            deleteCmd.CommandText = "DELETE FROM work_items WHERE id = $id";
            deleteCmd.Parameters.AddWithValue("$id", deletedSnapshot.Id.ToString());
            await deleteCmd.ExecuteNonQueryAsync();
        }

        var notifier = new CapturingQuotaRetryNotifier();
        var parker = new QuotaWaitParker(
            store,
            retryNotifier: notifier,
            timeProvider: new FixedClock(DateTimeOffset.UtcNow));

        await parker.ParkAsync(new QuotaWaitParkRequest(
            deletedSnapshot,
            "insufficient headroom",
            "work",
            DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Null(await store.GetAsync(deletedSnapshot.Id));
        Assert.Empty(notifier.Notifications);
    }

    [Fact]
    public async Task QuotaWaitParker_NotifiesRetryNotifierAfterSuccessfulPark()
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var resetAt = DateTimeOffset.UtcNow.AddHours(1);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await store.CreateAsync(item);

        var notifier = new CapturingQuotaRetryNotifier();
        var parker = new QuotaWaitParker(
            store,
            retryNotifier: notifier,
            timeProvider: new FixedClock(DateTimeOffset.UtcNow));

        await parker.ParkAsync(new QuotaWaitParkRequest(
            item,
            "insufficient headroom",
            "work",
            resetAt));

        var notified = Assert.Single(notifier.Notifications);
        Assert.Equal(item.Id, notified.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, notified.State);
        Assert.Equal("quota", notified.FailureKind);
        Assert.Equal(resetAt, notified.QuotaResetAt);
        Assert.Equal(resetAt, notified.NextQuotaRetryAt);
    }

    [Fact]
    public async Task QuotaWaitParker_NullResetUsesProjectClassEarliestExhaustedReset()
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var now = DateTimeOffset.UtcNow;
        var time = new FixedClock(now);
        var pid = new ProjectId("p1");
        var resetAt = now.AddMinutes(1);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = pid,
            DisplayName = "p1",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Claude,
            DefaultAgentClass = "test-class",
        });
        var classes = new List<AgentClass>
        {
            new AgentClass
            {
                Id = "test-class",
                DisplayName = "Test",
                Members =
                [
                    new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                ],
            },
        };
        var router = new AgentClassRouter(
            classes,
            [new FakeProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = resetAt })],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance,
            time);
        var parker = new QuotaWaitParker(
            store,
            projects: projects,
            classRouter: router,
            timeProvider: time);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await store.CreateAsync(item);

        await parker.ParkAsync(new QuotaWaitParkRequest(
            item,
            "insufficient headroom",
            "work",
            QuotaResetAt: null,
            Project: null));

        var parked = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, parked!.State);
        Assert.Equal(resetAt, parked.QuotaResetAt);
        Assert.Equal(resetAt, parked.NextQuotaRetryAt);
    }

    [Fact]
    public async Task QuotaWaitParker_ResetFallbackFailureUsesDefaultPause()
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var now = DateTimeOffset.UtcNow;
        var time = new FixedClock(now);
        var router = new AgentClassRouter(
            [],
            [],
            new QuotaRouterOptions(),
            NullLogger<AgentClassRouter>.Instance,
            time);
        var parker = new QuotaWaitParker(
            store,
            projects: new ThrowingProjectRepository(),
            classRouter: router,
            timeProvider: time);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await store.CreateAsync(item);

        await parker.ParkAsync(new QuotaWaitParkRequest(
            item,
            "insufficient headroom",
            "work",
            QuotaResetAt: null,
            Project: null));

        var parked = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, parked!.State);
        Assert.Equal(now.AddMinutes(5), parked.QuotaResetAt);
        Assert.Equal(now.AddMinutes(5), parked.NextQuotaRetryAt);
    }

    [Theory]
    [InlineData(WorkItemState.Queued, "work")]
    [InlineData(WorkItemState.Working, "work")]
    [InlineData(WorkItemState.WorkComplete, "audit")]
    [InlineData(WorkItemState.Auditing, "audit")]
    [InlineData(WorkItemState.Reworking, "audit")]
    [InlineData(WorkItemState.AuditPassed, "merge")]
    [InlineData(WorkItemState.Merging, "merge")]
    [InlineData(WorkItemState.Merged, "upstream")]
    [InlineData(WorkItemState.UpstreamPushing, "upstream")]
    public async Task OrchestratorService_QuotaDeferral_PreservesDispatchResumePhase(
        WorkItemState state,
        string expectedRetryFrom)
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var now = DateTimeOffset.UtcNow;
        var time = new FixedClock(now);
        var pid = new ProjectId("p1");
        var resetAt = now.AddMinutes(30);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = pid,
            DisplayName = "p1",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Claude,
            DefaultAgentClass = "test-class",
        });
        var classes = new List<AgentClass>
        {
            new AgentClass
            {
                Id = "test-class",
                DisplayName = "Test",
                Members =
                [
                    new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                ],
            },
        };
        var router = new AgentClassRouter(
            classes,
            [new FakeProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 5, ResetAt = resetAt })],
            new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromMinutes(5) },
            NullLogger<AgentClassRouter>.Instance,
            time);

        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var pipeline = new FakePipelineRunner(store);
        var svc = new OrchestratorService(
            queue,
            store,
            pipeline,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router,
            projects);

        var itemId = WorkItemId.New();
        var item = new WorkItem
        {
            Id = itemId,
            ProjectId = pid,
            Title = "phase resume",
            Prompt = "p",
            State = state,
            WorkBranch = "codeybox/test",
            PreemptedAt = state == WorkItemState.Working ? now.AddMinutes(-5) : null,
            PreemptCheckpoint = state == WorkItemState.Working
                ? $"refs/heads/codeybox/preempt/{itemId}"
                : null,
        };
        await store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await svc.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            WorkItem? parked = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                parked = await store.GetAsync(item.Id);
                if (parked?.State == WorkItemState.WaitingForQuotaReset)
                    break;
                await Task.Delay(20);
            }

            Assert.NotNull(parked);
            Assert.Equal(WorkItemState.WaitingForQuotaReset, parked!.State);
            Assert.Equal(expectedRetryFrom, parked.QuotaRetryFrom);
            Assert.Equal(resetAt, parked.QuotaResetAt);
            Assert.DoesNotContain(item.Id, pipeline.Executed);
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DispatchParkedQueuedWork_SchedulerRetriesFromWorkAndPipelineRuns()
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var now = DateTimeOffset.UtcNow;
        var time = new FixedClock(now);
        var pid = new ProjectId("p1");
        var resetAt = now.AddMinutes(30);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = pid,
            DisplayName = "p1",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Claude,
            DefaultAgentClass = "test-class",
        });
        var classes = new List<AgentClass>
        {
            new AgentClass
            {
                Id = "test-class",
                DisplayName = "Test",
                Members =
                [
                    new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                ],
            },
        };

        var exhaustedRouter = new AgentClassRouter(
            classes,
            [new FakeProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 5, ResetAt = resetAt })],
            new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromMinutes(5) },
            NullLogger<AgentClassRouter>.Instance,
            time);
        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var pipeline = new FakePipelineRunner(store);
        var service = new OrchestratorService(
            queue,
            store,
            pipeline,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            exhaustedRouter,
            projects);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "dispatch retry",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await service.StartAsync(CancellationToken.None);
        WorkItem? parked;
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            parked = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                parked = await store.GetAsync(item.Id);
                if (parked?.State == WorkItemState.WaitingForQuotaReset)
                    break;
                await Task.Delay(20);
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.NotNull(parked);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, parked!.State);
        Assert.Equal("work", parked.QuotaRetryFrom);
        Assert.DoesNotContain(item.Id, pipeline.Executed);

        var restoredRouter = new AgentClassRouter(
            classes,
            [new FakeProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 100, ResetAt = resetAt })],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance,
            time);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(1),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            restoredRouter,
            projects,
            timeProvider: time);

        var sweep = typeof(QuotaRetryScheduler).GetMethod(
            "RunPeriodicSweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)sweep.Invoke(scheduler, [CancellationToken.None])!;

        var retried = await store.GetAsync(item.Id);
        Assert.NotNull(retried);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);

        var resumeService = new OrchestratorService(
            queue,
            store,
            pipeline,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            restoredRouter,
            projects);

        await resumeService.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline && !pipeline.Executed.Contains(item.Id))
                await Task.Delay(20);
        }
        finally
        {
            await resumeService.StopAsync(CancellationToken.None);
        }

        Assert.Contains(item.Id, pipeline.Executed);
        var final = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task QuotaRetryScheduler_PeriodicSweep_ReEnqueuesWaitingForQuotaResetItem()
    {
        // The other half of the spec test case "all members exhausted → item
        // moves to WaitingForQuotaReset; periodic probe re-enqueues": once a
        // class member becomes available again, the periodic sweep must lift
        // the parked item back to Queued so a worker can pick it up.
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var time = new FixedClock(DateTimeOffset.UtcNow);
        var pid = new ProjectId("p1");
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = pid,
            DisplayName = "p1",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Claude,
            DefaultAgentClass = "test-class",
        });

        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var webhooks = new CapturingWebhookDispatcher();

        var classes = new List<AgentClass>
        {
            new AgentClass
            {
                Id = "test-class",
                DisplayName = "Test",
                Members =
                [
                    new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.PayPerApi, QualityScore = 100 },
                ],
            },
        };
        var router = new AgentClassRouter(classes, [new PayPerApiQuotaProbe()],
            new QuotaRouterOptions(), NullLogger<AgentClassRouter>.Instance, time);
        var opts = new OrchestratorOptions
        {
            AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
            {
                Enabled = true,
                PeriodicCheckInterval = TimeSpan.FromHours(1),
                MaxAutoRetriesPerWorkItem = 3,
            },
        };
        var scheduler = new QuotaRetryScheduler(store, retrier, opts,
            NullLogger<QuotaRetryScheduler>.Instance, router, projects, null, webhooks, time);

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "parked",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            AgentClassId = "test-class",
            QuotaResetAt = time.GetUtcNow().AddMinutes(-5), // already due
            NextQuotaRetryAt = time.GetUtcNow().AddMinutes(-1),
        };
        await store.CreateAsync(parked);

        // Trigger the periodic sweep via the same reflection hook the existing
        // tests use; the real loop calls this every PeriodicCheckInterval.
        var sweep = typeof(QuotaRetryScheduler).GetMethod(
            "RunPeriodicSweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)sweep.Invoke(scheduler, [CancellationToken.None])!;

        var refetched = await store.GetAsync(parked.Id);
        Assert.NotNull(refetched);
        Assert.Equal(WorkItemState.Queued, refetched!.State);
        Assert.Equal(1, refetched.QuotaRetryAttempts);
        Assert.Contains(webhooks.Events, e => e.Event == "work_item.auto_retry");
    }

    [Fact]
    public async Task QuotaRetryScheduler_PeriodicSweep_ReEnqueuesWaitingForQuotaResetItemWhenSubscriptionQuotaRestored()
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var time = new FixedClock(DateTimeOffset.UtcNow);
        var pid = new ProjectId("p1");
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = pid,
            DisplayName = "p1",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Claude,
            DefaultAgentClass = "test-class",
        });

        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);

        var classes = new List<AgentClass>
        {
            new AgentClass
            {
                Id = "test-class",
                DisplayName = "Test",
                Members =
                [
                    new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                ],
            },
        };
        var router = new AgentClassRouter(
            classes,
            [new FakeProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 100 })],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance,
            time);
        var opts = new OrchestratorOptions
        {
            AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
            {
                Enabled = true,
                PeriodicCheckInterval = TimeSpan.FromHours(1),
                MaxAutoRetriesPerWorkItem = 3,
            },
        };
        var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            opts,
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            timeProvider: time);

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "parked",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            AgentClassId = "test-class",
            QuotaResetAt = time.GetUtcNow().AddMinutes(-5),
            NextQuotaRetryAt = time.GetUtcNow().AddMinutes(-1),
        };
        await store.CreateAsync(parked);

        var sweep = typeof(QuotaRetryScheduler).GetMethod(
            "RunPeriodicSweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)sweep.Invoke(scheduler, [CancellationToken.None])!;

        var refetched = await store.GetAsync(parked.Id);
        Assert.NotNull(refetched);
        Assert.Equal(WorkItemState.Queued, refetched!.State);
        Assert.Equal(1, refetched.QuotaRetryAttempts);
    }

    [Fact]
    public void OrchestratorService_StartupRecovery_DoesNotRecoverWaitingForQuotaReset()
    {
        // WaitingForQuotaReset must be a resting point on startup recovery —
        // the QuotaRetryScheduler is the sole owner. If TryBuildRecoveredState
        // returned non-null for this state, the recovery loop would burn a
        // RecoveryAttempt credit and re-enqueue the item, which would then
        // re-fail with the same quota error inside the pipeline.
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var pipeline = new FakePipelineRunner(store);
        var svc = new OrchestratorService(queue, store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaResetAt = DateTimeOffset.UtcNow.AddHours(1),
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddMinutes(30),
            RecoveryAttempts = 0,
        };

        var recovered = svc.TryBuildRecoveredStateForTest(item);
        Assert.Null(recovered);
    }

    [Fact]
    public async Task OrchestratorService_Dispatch_SkipsWaitingForQuotaResetItem()
    {
        // Even if an over-eager test (or external caller) directly enqueues a
        // WaitingForQuotaReset item, the dispatch path must reject it without
        // running the pipeline. Without this gate, an enqueued item would be
        // re-run and immediately re-fail with the same quota error.
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var pipeline = new FakePipelineRunner(store);
        var svc = new OrchestratorService(queue, store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaResetAt = DateTimeOffset.UtcNow.AddHours(1),
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddMinutes(30),
        };
        await store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await svc.StopAsync(CancellationToken.None);

        // Pipeline must not have executed it.
        Assert.DoesNotContain(item.Id, pipeline.Executed);
        // State unchanged — the worker logged "skipping" and returned without
        // touching the row.
        var refetched = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, refetched!.State);
    }

    [Fact]
    public async Task OrchestratorService_WaitingForQuotaResetRace_ReleasesActiveSlot()
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var pipeline = new FakePipelineRunner(store);
        var resetAt = DateTimeOffset.UtcNow.AddHours(1);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await store.CreateAsync(item);

        var spawnCount = 0;
        var opts = new OrchestratorOptions
        {
            MaxConcurrentWorkers = 1,
            OnWorkerSpawned = () =>
            {
                if (Interlocked.Increment(ref spawnCount) != 1)
                    return;

                var current = store.GetAsync(item.Id).GetAwaiter().GetResult();
                store.UpdateAsync(current!.With(
                    WorkItemState.WaitingForQuotaReset,
                    "parked after pickup",
                    failureKind: "quota",
                    quotaResetAt: resetAt) with
                {
                    NextQuotaRetryAt = resetAt,
                }).GetAwaiter().GetResult();
            },
        };

        var svc = new OrchestratorService(queue, store, pipeline, registry, opts,
            NullLogger<OrchestratorService>.Instance);

        await queue.EnqueueAsync(item.Id);
        await svc.StartAsync(CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        WorkItem? parked = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            parked = await store.GetAsync(item.Id);
            if (parked?.State == WorkItemState.WaitingForQuotaReset)
                break;
            await Task.Delay(20);
        }

        Assert.NotNull(parked);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, parked!.State);
        Assert.DoesNotContain(item.Id, pipeline.Executed);

        await store.UpdateAsync(parked.With(WorkItemState.Queued));
        await queue.EnqueueAsync(item.Id);

        deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && !pipeline.Executed.Contains(item.Id))
            await Task.Delay(20);

        await svc.StopAsync(CancellationToken.None);

        Assert.Contains(item.Id, pipeline.Executed);
        var final = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task QuotaRetryScheduler_TargetedTimer_FiresForWaitingForQuotaReset()
    {
        // Sanity that the timer path (not just the periodic sweep) treats
        // WaitingForQuotaReset as eligible. NotifyQuotaFailureAsync schedules
        // a timer; when it fires we expect the parked item to be retried.
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var time = new FixedClock(DateTimeOffset.UtcNow);
        var pid = new ProjectId("p1");
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = pid,
            DisplayName = "p1",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Claude,
            DefaultAgentClass = "test-class",
        });

        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var webhooks = new CapturingWebhookDispatcher();

        var classes = new List<AgentClass>
        {
            new AgentClass
            {
                Id = "test-class",
                DisplayName = "Test",
                Members =
                [
                    new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.PayPerApi, QualityScore = 100 },
                ],
            },
        };
        var router = new AgentClassRouter(classes, [new PayPerApiQuotaProbe()],
            new QuotaRouterOptions(), NullLogger<AgentClassRouter>.Instance, time);
        var opts = new OrchestratorOptions
        {
            AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
            {
                Enabled = true,
                PeriodicCheckInterval = TimeSpan.FromHours(1),
                MaxAutoRetriesPerWorkItem = 3,
            },
        };
        var scheduler = new QuotaRetryScheduler(store, retrier, opts,
            NullLogger<QuotaRetryScheduler>.Instance, router, projects, null, webhooks, time);

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "parked",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            AgentClassId = "test-class",
            QuotaResetAt = time.GetUtcNow().AddMinutes(-5),
            NextQuotaRetryAt = time.GetUtcNow().AddMinutes(-1),
        };
        await store.CreateAsync(parked);

        var fired = typeof(QuotaRetryScheduler).GetMethod(
            "OnTargetedTimerFired",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        fired.Invoke(scheduler, [parked.Id]);

        // Background task — give it a moment to complete the retry call.
        await Task.Delay(150);

        var refetched = await store.GetAsync(parked.Id);
        Assert.NotNull(refetched);
        Assert.Equal(WorkItemState.Queued, refetched!.State);
    }

    [Fact]
    public async Task QuotaRetryScheduler_WaitingForQuotaResetWakeupWorksWhenAutoRetryDisabled()
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var time = new FixedClock(DateTimeOffset.UtcNow);
        var pid = new ProjectId("p1");
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = pid,
            DisplayName = "p1",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Claude,
            DefaultAgentClass = "test-class",
        });

        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);

        var classes = new List<AgentClass>
        {
            new AgentClass
            {
                Id = "test-class",
                DisplayName = "Test",
                Members =
                [
                    new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.PayPerApi, QualityScore = 100 },
                ],
            },
        };
        var router = new AgentClassRouter(classes, [new PayPerApiQuotaProbe()],
            new QuotaRouterOptions(), NullLogger<AgentClassRouter>.Instance, time);
        var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions(),
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            timeProvider: time);

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "parked",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            AgentClassId = "test-class",
            QuotaResetAt = time.GetUtcNow().AddMinutes(-5),
            NextQuotaRetryAt = time.GetUtcNow().AddMinutes(-1),
        };
        await store.CreateAsync(parked);

        await scheduler.NotifyQuotaFailureAsync(parked);

        var timersField = typeof(QuotaRetryScheduler).GetField(
            "_targetedTimers",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var timers = (System.Collections.IDictionary)timersField!.GetValue(scheduler)!;
        Assert.True(timers.Contains(parked.Id));

        var fired = typeof(QuotaRetryScheduler).GetMethod(
            "OnTargetedTimerFired",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        fired.Invoke(scheduler, [parked.Id]);
        await Task.Delay(150);

        var refetched = await store.GetAsync(parked.Id);
        Assert.Equal(WorkItemState.Queued, refetched!.State);
    }

    [Fact]
    public async Task QuotaRetryScheduler_Start_RearmsWaitingForQuotaResetWhenAutoRetryDisabled()
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var time = new FixedClock(DateTimeOffset.UtcNow);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = false,
                    PeriodicCheckInterval = TimeSpan.FromHours(1),
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            timeProvider: time);

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "parked",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaResetAt = time.GetUtcNow().AddMinutes(10),
            NextQuotaRetryAt = time.GetUtcNow().AddMinutes(10),
        };
        await store.CreateAsync(parked);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var timersField = typeof(QuotaRetryScheduler).GetField(
                "_targetedTimers",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var timers = (System.Collections.IDictionary)timersField!.GetValue(scheduler)!;

            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline && !timers.Contains(parked.Id))
                await Task.Delay(20);

            Assert.True(timers.Contains(parked.Id));
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    private sealed class CapturingQuotaRetryNotifier : IQuotaRetryNotifier
    {
        public List<WorkItem> Notifications { get; } = [];

        public Task NotifyQuotaFailureAsync(WorkItem item)
        {
            Notifications.Add(item);
            return Task.CompletedTask;
        }
    }

    private sealed class ConcurrentChangeOnTryUpdateStore : IWorkItemStore
    {
        private readonly IWorkItemStore _inner;
        private int _changed;

        public ConcurrentChangeOnTryUpdateStore(IWorkItemStore inner) => _inner = inner;

        public Task CreateAsync(WorkItem item, CancellationToken ct = default) =>
            _inner.CreateAsync(item, ct);

        public Task UpdateAsync(WorkItem item, CancellationToken ct = default) =>
            _inner.UpdateAsync(item, ct);

        public async Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _changed, 1) == 0)
            {
                var current = await _inner.GetAsync(item.Id, ct);
                if (current is not null)
                {
                    await _inner.UpdateAsync(
                        current.With(WorkItemState.Cancelled, "cancelled during conditional update"),
                        ct);
                }
            }

            return await _inner.TryUpdateIfStateAsync(item, onlyIfState, ct);
        }

        public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdatePriorityAsync(id, priority, updatedAt, ct);

        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) =>
            _inner.GetAsync(id, ct);

        public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) =>
            _inner.ListAsync(ct);

        public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) =>
            _inner.ListByStateAsync(state, ct);

        public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) =>
            _inner.CountByStateAsync(state, ct);

        public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) =>
            _inner.ReorderAsync(orderedIds, ct);

        public IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) =>
            _inner.ListDispatchEligibleByPriorityAsync(skipIds, ct);

        public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) =>
            _inner.CountStartedInWindowAsync(projectId, since, ct);

        public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) =>
            _inner.CountInFlightAsync(projectId, ct);

        public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) =>
            _inner.GetByExternalIdAsync(projectId, externalId, ct);

        public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) =>
            _inner.GetFleetStateCountsAsync(ct);

        public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) =>
            _inner.GetFleetRecentOutcomesAsync(perProject, ct);

        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) =>
            _inner.GetFleetPauseStatesAsync(ct);

        public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) =>
            _inner.ListByReplaySourceAsync(sourceId, ct);

        public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) =>
            _inner.OrphanReplaysAsync(sourceId, ct);

        public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) =>
            _inner.ListByReleaseAsync(releaseId, ct);
    }

    private sealed class ThrowingProjectRepository : IProjectRepository
    {
        public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default)
            => throw new InvalidOperationException("project lookup failed");

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Project>>([]);
    }

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new NoopTimer();

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
