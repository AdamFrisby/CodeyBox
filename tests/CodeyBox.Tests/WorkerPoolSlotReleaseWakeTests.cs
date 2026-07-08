using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;

// Framework FakeTimeProvider (CreateTimer fires on Advance); aliased to avoid
// the namespace-local FakeTimeProvider in AgentClassRouterScoreTests.cs whose
// CreateTimer would stay on the system clock. See SandboxSuspendResumeTests.
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

[Collection("Background service timing")]
public sealed class WorkerPoolSlotReleaseWakeTests : IDisposable
{
    private static readonly TimeSpan DispatchWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NoDispatchQuietPeriod = TimeSpan.FromMilliseconds(500);
    // Backstop for event-driven positive waits (TaskCompletionSource-backed
    // WaitForEnteredAsync/WaitForDoneAsync) that must survive severe CPU
    // starvation under the 6-core capped full suite on a co-resident host —
    // never the mechanism that makes the assertion pass, only headroom so a
    // correct-but-slow dispatch is not misread as a failure.
    private static readonly TimeSpan StarvationBackstopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SpawnPacingBranchInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SpawnPacingEarlyExitTimeout = TimeSpan.FromSeconds(4);

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-slot-release-wake-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public WorkerPoolSlotReleaseWakeTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task SlotReleaseWake_RefillsPoolFromIndependentReadyBacklogWithoutExternalKick()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var first = MakeItem(createdAt: now);
        var second = MakeItem(createdAt: now.AddMilliseconds(1));
        var readyBacklog = MakeItem(createdAt: now.AddMilliseconds(2));
        await _store.CreateAsync(first);
        await _store.CreateAsync(second);
        await _store.CreateAsync(readyBacklog);

        await queue.EnqueueAsync(first.Id);
        await queue.EnqueueAsync(second.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(first.Id, DispatchWaitTimeout));
        Assert.True(await pipeline.WaitForEnteredAsync(second.Id, DispatchWaitTimeout));
        Assert.False(pipeline.HasEntered(readyBacklog.Id));

        pipeline.Release(first.Id);

        Assert.True(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, DispatchWaitTimeout),
            "Releasing a worker slot should wake the dispatcher to rescan independent ready backlog rows.");
        Assert.Equal(0, queue.EnqueueCount(readyBacklog.Id));

        pipeline.Release(second.Id);
        pipeline.Release(readyBacklog.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SlotReleaseWake_RefillsAllOpenSlotsFromIndependentReadyBacklogWithoutExternalKick()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        // Fill the whole 4-slot pool so the "backlog is held out" assertion is a
        // DETERMINISTIC consequence of zero free slots, not a race against the
        // refill loop. The dispatcher's contract is to refill EVERY open slot
        // from one wake (see DispatchWake_RefillsAllOpenSlotsFromReadyBacklog...),
        // so a single occupied slot in a 4-slot pool leaves three free slots that
        // the very first wake legitimately fills — checking HasEntered==false at
        // that instant only ever observed "not yet" by luck on a loaded host and
        // was guaranteed to fail when the dispatcher won the race. Occupying all
        // four slots makes the hold-out real: backlog cannot enter until a slot
        // is released.
        var now = DateTimeOffset.UtcNow;
        var occupants = new[]
        {
            MakeItem(createdAt: now),
            MakeItem(createdAt: now.AddMilliseconds(1)),
            MakeItem(createdAt: now.AddMilliseconds(2)),
            MakeItem(createdAt: now.AddMilliseconds(3)),
        };
        var readyBacklog = new[]
        {
            MakeItem(createdAt: now.AddMilliseconds(4)),
            MakeItem(createdAt: now.AddMilliseconds(5)),
            MakeItem(createdAt: now.AddMilliseconds(6)),
        };

        foreach (var item in occupants)
            await _store.CreateAsync(item);
        foreach (var item in readyBacklog)
            await _store.CreateAsync(item);

        // One kick is enough: the refill loop fills all four slots from the
        // store-backed pickup query without a per-item enqueue.
        await queue.EnqueueAsync(occupants[0].Id);
        foreach (var item in occupants)
            Assert.True(
                await pipeline.WaitForEnteredAsync(item.Id, DispatchWaitTimeout),
                "All four slots should fill from a single kick via the store-backed refill loop.");

        // Pool is now full (four entered occupants == four slots), so the ready
        // backlog is genuinely blocked. This is a real invariant, not a timing
        // snapshot.
        foreach (var item in readyBacklog)
            Assert.False(pipeline.HasEntered(item.Id));

        // Release the occupants one at a time. Each completion's slot-release
        // wake must refill exactly one open slot from the independent ready
        // backlog WITHOUT any per-item kick (EnqueueCount stays 0 for each
        // backlog id), proving the slot-release wake keeps refilling while free
        // slots and ready backlog remain.
        for (var i = 0; i < readyBacklog.Length; i++)
        {
            pipeline.Release(occupants[i].Id);
            Assert.True(await pipeline.WaitForDoneAsync(occupants[i].Id, DispatchWaitTimeout));

            Assert.True(
                await pipeline.WaitForEnteredAsync(readyBacklog[i].Id, DispatchWaitTimeout),
                "A slot-release wake should refill the freed slot from independent ready backlog without an external kick.");
            Assert.Equal(0, queue.EnqueueCount(readyBacklog[i].Id));
        }

        pipeline.Release(occupants[^1].Id);
        foreach (var item in readyBacklog)
            pipeline.Release(item.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DispatchWake_RefillsAllOpenSlotsFromReadyBacklogWithoutPerItemSignals()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 3 },
            NullLogger<OrchestratorService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var readyBacklog = new[]
        {
            MakeItem(createdAt: now),
            MakeItem(createdAt: now.AddMilliseconds(1)),
            MakeItem(createdAt: now.AddMilliseconds(2)),
        };

        foreach (var item in readyBacklog)
            await _store.CreateAsync(item);

        await queue.EnqueueDispatchWakeAsync();

        foreach (var item in readyBacklog)
        {
            Assert.True(
                await pipeline.WaitForEnteredAsync(item.Id, DispatchWaitTimeout),
                "One dispatch wake should keep refilling while free slots and ready backlog remain.");
            Assert.Equal(0, queue.EnqueueCount(item.Id));
        }

        Assert.Equal(1, queue.GenericWakeEnqueueCount);
        Assert.Equal(1, queue.TotalEnqueueCount);

        foreach (var item in readyBacklog)
            pipeline.Release(item.Id);
        foreach (var item in readyBacklog)
            Assert.True(await pipeline.WaitForDoneAsync(item.Id, DispatchWaitTimeout));

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DispatchWake_AdmitsDueQuotaWaitingItemByPriorityBeforeQueuedItem()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        var waitingHighPriority = MakeItem(createdAt: now) with
        {
            State = WorkItemState.WaitingForQuotaReset,
            Priority = 200,
            AgentClassId = "quota-class",
            QuotaRetryFrom = "work",
            NextQuotaRetryAt = now.AddHours(-1),
        };
        var queuedLowPriority = MakeItem(createdAt: now.AddMilliseconds(1)) with
        {
            Priority = -1000,
            AgentClassId = "quota-class",
        };

        await _store.CreateAsync(waitingHighPriority);
        await _store.CreateAsync(queuedLowPriority);

        var project = new Project
        {
            Id = new ProjectId("test"),
            DisplayName = "Test",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Codex,
            DefaultAgentClass = "quota-class",
        };
        var projects = new InMemoryProjectRepository(project);
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "quota-class",
                    DisplayName = "Quota Class",
                    Members =
                    [
                        new AgentMembership
                        {
                            Agent = AgentKind.Codex,
                            Billing = AgentBilling.Subscription,
                            QualityScore = 100,
                        },
                    ],
                },
            ],
            [new FakeProbe(AgentKind.Codex, 100.0)],
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);

        var gitRoot = Directory.CreateTempSubdirectory("codeybox-quota-dispatch-git-").FullName;
        try
        {
            var gitHost = new LocalGitHost(
                new LocalGitHostOptions { RootDirectory = gitRoot },
                NullLogger<LocalGitHost>.Instance);
            var retryOptions = new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(1),
                    ClockDriftSafetyMargin = TimeSpan.Zero,
                    MaxAutoRetriesPerWorkItem = 3,
                },
            };
            var retrier = new WorkItemRetrier(
                _store,
                queue,
                gitHost,
                NullLogger<WorkItemRetrier>.Instance);
            using var scheduler = new QuotaRetryScheduler(
                _store,
                retrier,
                retryOptions,
                NullLogger<QuotaRetryScheduler>.Instance,
                router,
                projects);
            using var svc = new OrchestratorService(
                queue, _store, pipeline, registry,
                retryOptions,
                NullLogger<OrchestratorService>.Instance,
                router: router,
                projects: projects,
                quotaRetryDispatchPromoter: scheduler);

            await svc.StartAsync(CancellationToken.None);
            await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

            await queue.EnqueueAsync(queuedLowPriority.Id);

            Assert.True(
                await pipeline.WaitForEnteredAsync(waitingHighPriority.Id, DispatchWaitTimeout),
                "The dispatcher should promote and run the overdue high-priority quota-waiting item before the low-priority queued item that supplied the wake.");
            Assert.False(
                pipeline.HasEntered(queuedLowPriority.Id),
                "The low-priority queued item must not consume the first available quota/worker slot.");

            var promoted = await _store.GetAsync(waitingHighPriority.Id);
            Assert.Equal(1, promoted!.QuotaRetryAttempts);

            pipeline.Release(waitingHighPriority.Id);
            Assert.True(await pipeline.WaitForDoneAsync(waitingHighPriority.Id, DispatchWaitTimeout));

            if (await pipeline.WaitForEnteredAsync(queuedLowPriority.Id, NoDispatchQuietPeriod))
                pipeline.Release(queuedLowPriority.Id);

            await svc.StopAsync(CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(gitRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DispatchWake_DoesNotLetLowerQueuedItemCatchIntermittentQuotaSliver()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        var waitingHighPriority = MakeQuotaWaitingItem(now, priority: 200);
        var queuedLowPriority = MakeItem(createdAt: now.AddMilliseconds(1)) with
        {
            Priority = -1000,
            AgentClassId = "quota-class",
        };

        var project = QuotaProject();
        var projects = new InMemoryProjectRepository(project);
        var probe = new SequenceProbe(
            AgentKind.Codex,
            new AgentQuotaSnapshot { AvailablePct = 0 },
            new AgentQuotaSnapshot { AvailablePct = 100 },
            new AgentQuotaSnapshot { AvailablePct = 100 });
        var router = BuildQuotaRouter(probe);

        var gitRoot = Directory.CreateTempSubdirectory("codeybox-quota-dispatch-git-").FullName;
        try
        {
            var gitHost = new LocalGitHost(
                new LocalGitHostOptions { RootDirectory = gitRoot },
                NullLogger<LocalGitHost>.Instance);
            var retryOptions = QuotaRetryOptions();
            var retrier = new WorkItemRetrier(
                _store,
                queue,
                gitHost,
                NullLogger<WorkItemRetrier>.Instance);
            using var scheduler = new QuotaRetryScheduler(
                _store,
                retrier,
                retryOptions,
                NullLogger<QuotaRetryScheduler>.Instance,
                router,
                projects);
            using var svc = new OrchestratorService(
                queue, _store, pipeline, registry,
                retryOptions,
                NullLogger<OrchestratorService>.Instance,
                router: router,
                projects: projects,
                quotaRetryDispatchPromoter: scheduler);

            await svc.StartAsync(CancellationToken.None);
            await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

            await _store.CreateAsync(waitingHighPriority);
            await _store.CreateAsync(queuedLowPriority);

            await queue.EnqueueAsync(queuedLowPriority.Id);
            Assert.False(
                await pipeline.WaitForEnteredAsync(queuedLowPriority.Id, NoDispatchQuietPeriod),
                "The lower-priority queued item must not get a second quota probe after the higher-priority parked item just saw exhaustion.");
            Assert.False(pipeline.HasEntered(waitingHighPriority.Id));

            await queue.EnqueueAsync(queuedLowPriority.Id);
            Assert.True(
                await pipeline.WaitForEnteredAsync(waitingHighPriority.Id, DispatchWaitTimeout),
                "The next available quota probe should promote the higher-priority parked item before the lower-priority queued item can run.");
            Assert.False(pipeline.HasEntered(queuedLowPriority.Id));

            pipeline.Release(waitingHighPriority.Id);
            Assert.True(await pipeline.WaitForDoneAsync(waitingHighPriority.Id, DispatchWaitTimeout));

            if (await pipeline.WaitForEnteredAsync(queuedLowPriority.Id, NoDispatchQuietPeriod))
                pipeline.Release(queuedLowPriority.Id);

            await svc.StopAsync(CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(gitRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Pickup_HighPriorityQueuedItemBeatsLowerPriorityDueQuotaWaitingItem()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        var queuedHighPriority = MakeItem(createdAt: now) with
        {
            Priority = 200,
            AgentClassId = "quota-class",
        };
        var waitingLowPriority = MakeQuotaWaitingItem(now.AddMilliseconds(1), priority: 100);

        await _store.CreateAsync(queuedHighPriority);
        await _store.CreateAsync(waitingLowPriority);

        var promoter = new RecordingQuotaRetryDispatchPromoter(
            new QuotaRetryDispatchPromotionResult(false, "unexpected"));
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            projects: new InMemoryProjectRepository(QuotaProject()),
            quotaRetryDispatchPromoter: promoter);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);

        Assert.Equal(queuedHighPriority.Id, picked);
        Assert.Equal(0, promoter.CallCount);
    }

    [Fact]
    public async Task Pickup_FutureQuotaRetryTimeDoesNotBlockQueuedWork()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        var waitingFutureHighPriority = MakeQuotaWaitingItem(now, priority: 200) with
        {
            NextQuotaRetryAt = now.AddHours(1),
        };
        var queuedLowPriority = MakeItem(createdAt: now.AddMilliseconds(1)) with
        {
            Priority = -1000,
            AgentClassId = "quota-class",
        };

        await _store.CreateAsync(waitingFutureHighPriority);
        await _store.CreateAsync(queuedLowPriority);

        var promoter = new RecordingQuotaRetryDispatchPromoter(
            new QuotaRetryDispatchPromotionResult(false, "unexpected"));
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            projects: new InMemoryProjectRepository(QuotaProject()),
            quotaRetryDispatchPromoter: promoter);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);

        Assert.Equal(queuedLowPriority.Id, picked);
        Assert.Equal(0, promoter.CallCount);
    }

    [Fact]
    public async Task Pickup_RestartsWhenQuotaPromotionLosesStateRace()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        var waitingHighPriority = MakeQuotaWaitingItem(now, priority: 200);
        var queuedLowPriority = MakeItem(createdAt: now.AddMilliseconds(1)) with
        {
            Priority = -1000,
            AgentClassId = "quota-class",
        };

        await _store.CreateAsync(waitingHighPriority);
        await _store.CreateAsync(queuedLowPriority);

        var promoter = new RecordingQuotaRetryDispatchPromoter(async item =>
        {
            var queued = item.With(WorkItemState.Queued, error: null) with
            {
                QuotaRetryAttempts = item.QuotaRetryAttempts + 1,
            };
            Assert.True(await _store.TryUpdateIfStateAsync(
                queued,
                WorkItemState.WaitingForQuotaReset,
                CancellationToken.None));
            return new QuotaRetryDispatchPromotionResult(
                Promoted: false,
                Outcome: "retry-failed",
                Reason: "work item state changed concurrently; retry aborted",
                Disposition: QuotaRetryDispatchDisposition.RestartSelection);
        });
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            projects: new InMemoryProjectRepository(QuotaProject()),
            quotaRetryDispatchPromoter: promoter);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);

        Assert.Equal(waitingHighPriority.Id, picked);
        Assert.Equal(1, promoter.CallCount);
    }

    [Fact]
    public async Task Pickup_BlockedQuotaPromotionPreservesHigherPriorityBlocker()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        var waitingHighPriority = MakeQuotaWaitingItem(now, priority: 200);
        var queuedLowPriority = MakeItem(createdAt: now.AddMilliseconds(1)) with
        {
            Priority = -1000,
            AgentClassId = "quota-class",
        };

        await _store.CreateAsync(waitingHighPriority);
        await _store.CreateAsync(queuedLowPriority);

        var promoter = new RecordingQuotaRetryDispatchPromoter(
            new QuotaRetryDispatchPromotionResult(
                Promoted: false,
                Outcome: "error",
                Reason: "promotion failed",
                Disposition: QuotaRetryDispatchDisposition.Blocked));
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            projects: new InMemoryProjectRepository(QuotaProject()),
            quotaRetryDispatchPromoter: promoter);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);

        Assert.Null(picked);
        Assert.Equal(1, promoter.CallCount);
    }

    [Fact]
    public async Task Pickup_BlockedQuotaPromotionAllowsQueuedWorkWithDifferentRoutablePool()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        var waitingAuditHighPriority = MakeQuotaWaitingItem(now, priority: 200) with
        {
            AgentClassId = "mixed-class",
            QuotaRetryPhase = "audit",
        };
        var queuedLowPriority = MakeItem(createdAt: now.AddMilliseconds(1)) with
        {
            Priority = -1000,
            AgentClassId = "mixed-class",
            RequiredCapabilities = ["bulk"],
        };

        await _store.CreateAsync(waitingAuditHighPriority);
        await _store.CreateAsync(queuedLowPriority);

        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "mixed-class",
                    DisplayName = "Mixed Class",
                    Members =
                    [
                        new AgentMembership
                        {
                            Agent = AgentKind.Codex,
                            Billing = AgentBilling.Subscription,
                            QualityScore = 100,
                            Capabilities = [WellKnownCapabilities.Audit],
                        },
                        new AgentMembership
                        {
                            Agent = AgentKind.Cursor,
                            Billing = AgentBilling.Subscription,
                            QualityScore = 90,
                            Capabilities = ["bulk"],
                        },
                    ],
                },
            ],
            [new FakeProbe(AgentKind.Codex, 0.0), new FakeProbe(AgentKind.Cursor, 100.0)],
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        var promoter = new RecordingQuotaRetryDispatchPromoter(
            new QuotaRetryDispatchPromotionResult(
                Promoted: false,
                Outcome: "skipped:quota-still-gated",
                Disposition: QuotaRetryDispatchDisposition.Blocked));
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            projects: new InMemoryProjectRepository(QuotaProject() with { DefaultAgentClass = "mixed-class" }),
            quotaRetryDispatchPromoter: promoter);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);

        Assert.Equal(queuedLowPriority.Id, picked);
        Assert.Equal(1, promoter.CallCount);
    }

    [Fact]
    public async Task Pickup_BlockedQuotaPromotionAllowsQueuedWorkWithCurrentlyAvailableNonOverlappingRoute()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        var waitingHighPriority = MakeQuotaWaitingItem(now, priority: 200) with
        {
            AgentClassId = "overlap-class",
            MinModelScore = 100,
        };
        var queuedLowPriority = MakeItem(createdAt: now.AddMilliseconds(1)) with
        {
            Priority = -1000,
            AgentClassId = "overlap-class",
        };

        await _store.CreateAsync(waitingHighPriority);
        await _store.CreateAsync(queuedLowPriority);

        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "overlap-class",
                    DisplayName = "Overlap Class",
                    Members =
                    [
                        new AgentMembership
                        {
                            Agent = AgentKind.Codex,
                            Billing = AgentBilling.Subscription,
                            QualityScore = 100,
                        },
                        new AgentMembership
                        {
                            Agent = AgentKind.Cursor,
                            Billing = AgentBilling.Subscription,
                            QualityScore = 90,
                        },
                    ],
                },
            ],
            [new FakeProbe(AgentKind.Codex, 0.0), new FakeProbe(AgentKind.Cursor, 100.0)],
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        var promoter = new RecordingQuotaRetryDispatchPromoter(
            new QuotaRetryDispatchPromotionResult(
                Promoted: false,
                Outcome: "skipped:quota-still-gated",
                Disposition: QuotaRetryDispatchDisposition.Blocked));
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            projects: new InMemoryProjectRepository(QuotaProject() with { DefaultAgentClass = "overlap-class" }),
            quotaRetryDispatchPromoter: promoter);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);

        Assert.Equal(queuedLowPriority.Id, picked);
        Assert.Equal(1, promoter.CallCount);
    }

    [Fact]
    public async Task Pickup_BlockedQuotaPromotionDoesNotBlockDifferentPoolAfterRequiredCapabilityFiltering()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        var waitingHighPriority = MakeQuotaWaitingItem(now, priority: 200) with
        {
            AgentClassId = "capability-split",
            RequiredCapabilities = ["secure"],
        };
        var queuedLowPriority = MakeItem(createdAt: now.AddMilliseconds(1)) with
        {
            Priority = -1000,
            AgentClassId = "capability-split",
            RequiredCapabilities = ["bulk"],
        };

        await _store.CreateAsync(waitingHighPriority);
        await _store.CreateAsync(queuedLowPriority);

        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "capability-split",
                    DisplayName = "Capability Split",
                    Members =
                    [
                        new AgentMembership
                        {
                            Agent = AgentKind.Codex,
                            Billing = AgentBilling.Subscription,
                            QualityScore = 100,
                            Capabilities = ["secure"],
                        },
                        new AgentMembership
                        {
                            Agent = AgentKind.Cursor,
                            Billing = AgentBilling.Subscription,
                            QualityScore = 100,
                            Capabilities = ["bulk"],
                        },
                    ],
                },
            ],
            [new FakeProbe(AgentKind.Codex, 0.0), new FakeProbe(AgentKind.Cursor, 100.0)],
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        var promoter = new RecordingQuotaRetryDispatchPromoter(
            new QuotaRetryDispatchPromotionResult(
                Promoted: false,
                Outcome: "skipped:quota-still-gated",
                Disposition: QuotaRetryDispatchDisposition.Blocked));
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            projects: new InMemoryProjectRepository(QuotaProject() with { DefaultAgentClass = "capability-split" }),
            quotaRetryDispatchPromoter: promoter);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);

        Assert.Equal(queuedLowPriority.Id, picked);
        Assert.Equal(1, promoter.CallCount);
    }

    [Fact]
    public async Task Pickup_BlockedQuotaPromotionDoesNotBlockDifferentPoolAfterMinScoreFiltering()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        var waitingHighPriority = MakeQuotaWaitingItem(now, priority: 200) with
        {
            AgentClassId = "score-split",
            MinModelScore = 95,
        };
        var queuedLowPriority = MakeItem(createdAt: now.AddMilliseconds(1)) with
        {
            Priority = -1000,
            AgentClassId = "score-split",
            RequiredCapabilities = ["bulk"],
        };

        await _store.CreateAsync(waitingHighPriority);
        await _store.CreateAsync(queuedLowPriority);

        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "score-split",
                    DisplayName = "Score Split",
                    Members =
                    [
                        new AgentMembership
                        {
                            Agent = AgentKind.Codex,
                            Billing = AgentBilling.Subscription,
                            QualityScore = 100,
                        },
                        new AgentMembership
                        {
                            Agent = AgentKind.Cursor,
                            Billing = AgentBilling.Subscription,
                            QualityScore = 80,
                            Capabilities = ["bulk"],
                        },
                    ],
                },
            ],
            [new FakeProbe(AgentKind.Codex, 0.0), new FakeProbe(AgentKind.Cursor, 100.0)],
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        var promoter = new RecordingQuotaRetryDispatchPromoter(
            new QuotaRetryDispatchPromotionResult(
                Promoted: false,
                Outcome: "skipped:quota-still-gated",
                Disposition: QuotaRetryDispatchDisposition.Blocked));
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            projects: new InMemoryProjectRepository(QuotaProject() with { DefaultAgentClass = "score-split" }),
            quotaRetryDispatchPromoter: promoter);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);

        Assert.Equal(queuedLowPriority.Id, picked);
        Assert.Equal(1, promoter.CallCount);
    }

    [Fact]
    public async Task Pickup_BlockedDirectDefaultAgentBlocksQueuedWorkAcrossProjects()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var highProject = new ProjectId("high");
        var lowProject = new ProjectId("low");

        var waitingHighPriority = MakeQuotaWaitingItem(now, priority: 200) with
        {
            ProjectId = highProject,
            AgentClassId = null,
        };
        var queuedLowPriority = MakeItem(createdAt: now.AddMilliseconds(1)) with
        {
            ProjectId = lowProject,
            Priority = -1000,
            AgentClassId = null,
            Agent = null,
        };

        await _store.CreateAsync(waitingHighPriority);
        await _store.CreateAsync(queuedLowPriority);

        var promoter = new RecordingQuotaRetryDispatchPromoter(
            new QuotaRetryDispatchPromotionResult(
                Promoted: false,
                Outcome: "skipped:quota-still-gated",
                Disposition: QuotaRetryDispatchDisposition.Blocked));
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            projects: new InMemoryProjectRepository(
                QuotaProject() with { Id = highProject, DefaultAgentClass = null, DefaultAgent = AgentKind.Codex },
                QuotaProject() with { Id = lowProject, DefaultAgentClass = null, DefaultAgent = AgentKind.Codex }),
            quotaRetryDispatchPromoter: promoter);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);

        Assert.Null(picked);
        Assert.Equal(1, promoter.CallCount);
    }

    [Fact]
    public async Task Pickup_UnifiedScanLooksPastFirstPageOfIneligibleCandidates()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var failedDependency = MakeItem(now.AddMilliseconds(-1)) with
        {
            State = WorkItemState.Failed,
        };
        await _store.CreateAsync(failedDependency);

        for (var i = 0; i < 300; i++)
        {
            await _store.CreateAsync(MakeItem(now.AddMilliseconds(i)) with
            {
                Priority = 1000 - i,
                DependsOn = [failedDependency.Id],
            });
        }

        var runnable = MakeItem(now.AddSeconds(1)) with
        {
            Priority = -1000,
        };
        await _store.CreateAsync(runnable);

        var promoter = new RecordingQuotaRetryDispatchPromoter(
            new QuotaRetryDispatchPromotionResult(false, "unexpected"));
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            projects: new InMemoryProjectRepository(QuotaProject()),
            quotaRetryDispatchPromoter: promoter);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);

        Assert.Equal(runnable.Id, picked);
        Assert.Equal(0, promoter.CallCount);
    }

    [Fact]
    public async Task Pickup_NonBlockingQuotaPromotionOutcomeAllowsQueuedWork()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        var waitingHighPriority = MakeQuotaWaitingItem(now, priority: 200);
        var queuedLowPriority = MakeItem(createdAt: now.AddMilliseconds(1)) with
        {
            Priority = -1000,
            AgentClassId = "quota-class",
        };

        await _store.CreateAsync(waitingHighPriority);
        await _store.CreateAsync(queuedLowPriority);

        var promoter = new RecordingQuotaRetryDispatchPromoter(
            new QuotaRetryDispatchPromotionResult(
                Promoted: false,
                Outcome: "skipped:auto-retry-disabled",
                Disposition: QuotaRetryDispatchDisposition.Continue));
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            projects: new InMemoryProjectRepository(QuotaProject()),
            quotaRetryDispatchPromoter: promoter);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);

        Assert.Equal(queuedLowPriority.Id, picked);
        Assert.Equal(1, promoter.CallCount);
    }

    [Fact]
    public async Task QuotaRetryDispatchPromoter_ReturnsContinueForGuardSkips()
    {
        var queue = new ObservedTaskQueue();
        var now = DateTimeOffset.UtcNow;
        var gitRoot = Directory.CreateTempSubdirectory("codeybox-quota-dispatch-git-").FullName;
        try
        {
            using var scheduler = BuildQuotaRetrySchedulerForTest(
                queue,
                gitRoot,
                QuotaRetryOptions());

            var notWaiting = MakeItem(now);
            var notWaitingResult = await scheduler.TryPromoteForDispatchAsync(notWaiting);
            Assert.False(notWaitingResult.Promoted);
            Assert.Equal("skipped:not-waiting-for-quota-reset", notWaitingResult.Outcome);
            Assert.Equal(QuotaRetryDispatchDisposition.Continue, notWaitingResult.Disposition);

            var notDue = MakeQuotaWaitingItem(now, priority: 200) with
            {
                NextQuotaRetryAt = now.AddHours(1),
            };
            var notDueResult = await scheduler.TryPromoteForDispatchAsync(notDue);
            Assert.False(notDueResult.Promoted);
            Assert.Equal("skipped:not-due", notDueResult.Outcome);
            Assert.Equal(QuotaRetryDispatchDisposition.Continue, notDueResult.Disposition);
        }
        finally
        {
            try { Directory.Delete(gitRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task QuotaRetryDispatchPromoter_ReturnsBlockedOnPromotionException()
    {
        var queue = new ObservedTaskQueue();
        var now = DateTimeOffset.UtcNow;
        var gitRoot = Directory.CreateTempSubdirectory("codeybox-quota-dispatch-git-").FullName;
        try
        {
            using var scheduler = BuildQuotaRetrySchedulerForTest(
                queue,
                gitRoot,
                QuotaRetryOptions(),
                router: new ThrowingQuotaRetryRouter(),
                projects: new InMemoryProjectRepository(QuotaProject()));

            var waiting = MakeQuotaWaitingItem(now, priority: 200);
            var result = await scheduler.TryPromoteForDispatchAsync(waiting);

            Assert.False(result.Promoted);
            Assert.Equal("error", result.Outcome);
            Assert.Equal(QuotaRetryDispatchDisposition.Blocked, result.Disposition);
        }
        finally
        {
            try { Directory.Delete(gitRoot, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("skipped:quota-still-gated", null, WorkItemRetryFailureKind.None, QuotaRetryDispatchDisposition.Blocked)]
    [InlineData("skipped:max-retries", null, WorkItemRetryFailureKind.None, QuotaRetryDispatchDisposition.RestartSelection)]
    [InlineData("moved:waiting-for-agent-resume", null, WorkItemRetryFailureKind.None, QuotaRetryDispatchDisposition.RestartSelection)]
    [InlineData("retry-failed", "retry aborted", WorkItemRetryFailureKind.StateChangedConcurrently, QuotaRetryDispatchDisposition.RestartSelection)]
    [InlineData("retry-failed", "work item state changed concurrently; retry aborted", WorkItemRetryFailureKind.None, QuotaRetryDispatchDisposition.Continue)]
    [InlineData("retry-failed", "bare repo missing", WorkItemRetryFailureKind.None, QuotaRetryDispatchDisposition.Continue)]
    [InlineData("skipped:no-eligible-members", null, WorkItemRetryFailureKind.None, QuotaRetryDispatchDisposition.Continue)]
    public void QuotaRetryDispatchPromoter_MapsRealRetryOutcomesToDispatchDispositions(
        string outcome,
        string? reason,
        WorkItemRetryFailureKind failureKind,
        QuotaRetryDispatchDisposition expected)
    {
        var disposition = QuotaRetryScheduler.DispatchDispositionForOutcome(
            new QuotaRetryScheduler.QuotaRetryAttemptResult(outcome, reason, failureKind));

        Assert.Equal(expected, disposition);
    }

    [Fact]
    public async Task UnifiedPickupQuery_OrdersQueuedAndDueQuotaRetryRowsByPriority()
    {
        var now = DateTimeOffset.UtcNow;
        var queuedHighPriority = MakeItem(createdAt: now.AddMilliseconds(10)) with
        {
            Priority = 1000,
        };
        var auditLowPriority = MakeQuotaWaitingItem(now, priority: 1) with
        {
            QuotaRetryPhase = "audit",
        };
        var conflictLowPriority = MakeQuotaWaitingItem(now.AddMilliseconds(1), priority: 2) with
        {
            QuotaRetryPhase = "conflict_rework",
            QuotaRetryFrom = "conflict_rework",
        };
        var mergeLowPriority = MakeQuotaWaitingItem(now.AddMilliseconds(2), priority: 3) with
        {
            QuotaRetryPhase = "merge",
        };
        var upstreamLowPriority = MakeQuotaWaitingItem(now.AddMilliseconds(3), priority: 4) with
        {
            QuotaRetryPhase = "upstream",
        };

        await _store.CreateAsync(queuedHighPriority);
        await _store.CreateAsync(auditLowPriority);
        await _store.CreateAsync(conflictLowPriority);
        await _store.CreateAsync(mergeLowPriority);
        await _store.CreateAsync(upstreamLowPriority);

        var ordered = new List<WorkItemId>();
        await foreach (var item in _store.ListDispatchEligibleIncludingDueQuotaRetryByPriorityAsync(
            new HashSet<WorkItemId>(),
            now,
            limit: 10))
        {
            ordered.Add(item.Id);
        }

        Assert.Contains(mergeLowPriority.Id, ordered);
        Assert.Contains(upstreamLowPriority.Id, ordered);
        Assert.Contains(queuedHighPriority.Id, ordered);
        Assert.Contains(auditLowPriority.Id, ordered);
        Assert.Contains(conflictLowPriority.Id, ordered);

        Assert.Equal(
            [
                upstreamLowPriority.Id,
                mergeLowPriority.Id,
                queuedHighPriority.Id,
                conflictLowPriority.Id,
                auditLowPriority.Id,
            ],
            ordered);
    }

    [Fact]
    public async Task UnifiedPickupQuery_IncludesDueQuotaRetryRowsWithNullRetryTimestamp()
    {
        var now = DateTimeOffset.UtcNow;
        var waiting = MakeQuotaWaitingItem(now, priority: 200) with
        {
            NextQuotaRetryAt = null,
        };
        var queued = MakeItem(now.AddMilliseconds(1)) with { Priority = -1000 };

        await _store.CreateAsync(waiting);
        await _store.CreateAsync(queued);

        var ordered = new List<WorkItemId>();
        await foreach (var item in _store.ListDispatchEligibleIncludingDueQuotaRetryByPriorityAsync(
            new HashSet<WorkItemId>(),
            now,
            limit: 10))
        {
            ordered.Add(item.Id);
        }

        Assert.Contains(waiting.Id, ordered);
        Assert.True(ordered.IndexOf(waiting.Id) < ordered.IndexOf(queued.Id));
    }

    [Fact]
    public async Task UnifiedPickupQuery_ComparesQuotaRetryTimestampsByInstant()
    {
        var now = new DateTimeOffset(2026, 7, 7, 15, 0, 0, TimeSpan.Zero);
        var dueWithOffset = MakeQuotaWaitingItem(now, priority: 200) with
        {
            NextQuotaRetryAt = new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.FromHours(10)),
        };
        var queued = MakeItem(now.AddMilliseconds(1)) with { Priority = -1000 };

        await _store.CreateAsync(dueWithOffset);
        await _store.CreateAsync(queued);

        var ordered = new List<WorkItemId>();
        await foreach (var item in _store.ListDispatchEligibleIncludingDueQuotaRetryByPriorityAsync(
            new HashSet<WorkItemId>(),
            now,
            limit: 10))
        {
            ordered.Add(item.Id);
        }

        Assert.Contains(dueWithOffset.Id, ordered);
        Assert.True(ordered.IndexOf(dueWithOffset.Id) < ordered.IndexOf(queued.Id));
    }

    [Fact]
    public async Task UnifiedPickupQuery_AppliesSkipIdsToDueQuotaRetryRows()
    {
        var now = DateTimeOffset.UtcNow;
        var skippedWaiting = MakeQuotaWaitingItem(now, priority: 200);
        var queued = MakeItem(now.AddMilliseconds(1)) with { Priority = -1000 };

        await _store.CreateAsync(skippedWaiting);
        await _store.CreateAsync(queued);

        var ordered = new List<WorkItemId>();
        await foreach (var item in _store.ListDispatchEligibleIncludingDueQuotaRetryByPriorityAsync(
            new HashSet<WorkItemId> { skippedWaiting.Id },
            now,
            limit: 10))
        {
            ordered.Add(item.Id);
        }

        Assert.DoesNotContain(skippedWaiting.Id, ordered);
        Assert.Contains(queued.Id, ordered);
    }

    [Fact]
    public async Task UnifiedPickupQuery_AppliesLimitWhenMoreRowsAreEligible()
    {
        var now = DateTimeOffset.UtcNow;
        var first = MakeQuotaWaitingItem(now, priority: 500);
        var second = MakeItem(now.AddMilliseconds(1)) with { Priority = 400 };
        var third = MakeQuotaWaitingItem(now.AddMilliseconds(2), priority: 300);
        var fourth = MakeItem(now.AddMilliseconds(3)) with { Priority = 200 };

        await _store.CreateAsync(first);
        await _store.CreateAsync(second);
        await _store.CreateAsync(third);
        await _store.CreateAsync(fourth);

        var ordered = new List<WorkItemId>();
        await foreach (var item in _store.ListDispatchEligibleIncludingDueQuotaRetryByPriorityAsync(
            new HashSet<WorkItemId>(),
            now,
            limit: 2))
        {
            ordered.Add(item.Id);
        }

        Assert.Equal([first.Id, second.Id], ordered);
    }

    [Fact]
    public async Task UnifiedPickupQuery_ExplicitRecoveryModeIncludesFutureQuotaRetryRows()
    {
        var now = DateTimeOffset.UtcNow;
        var futureWaiting = MakeQuotaWaitingItem(now, priority: 500) with
        {
            NextQuotaRetryAt = now.AddHours(1),
        };
        var queued = MakeItem(now.AddMilliseconds(1)) with { Priority = 100 };

        await _store.CreateAsync(futureWaiting);
        await _store.CreateAsync(queued);

        var dueOnly = new List<WorkItemId>();
        await foreach (var item in _store.ListDispatchEligibleIncludingDueQuotaRetryByPriorityAsync(
            new HashSet<WorkItemId>(),
            now,
            limit: 10))
        {
            dueOnly.Add(item.Id);
        }

        var recovery = new List<WorkItemId>();
        await foreach (var item in _store.ListDispatchEligibleIncludingDueQuotaRetryByPriorityAsync(
            new HashSet<WorkItemId>(),
            now,
            limit: 10,
            quotaRetryEligibility: QuotaRetryDispatchEligibility.IncludeFuture))
        {
            recovery.Add(item.Id);
        }

        Assert.DoesNotContain(futureWaiting.Id, dueOnly);
        Assert.Contains(queued.Id, dueOnly);
        Assert.Contains(futureWaiting.Id, recovery);
        Assert.True(recovery.IndexOf(futureWaiting.Id) < recovery.IndexOf(queued.Id));
    }

    [Theory]
    [InlineData("rework", WorkItemState.WorkComplete)]
    [InlineData("plan_review", WorkItemState.PlanReview)]
    [InlineData("plan_approved", WorkItemState.PlanApproved)]
    public void QuotaRetryOrdering_UsesSharedRetryFromPolicy(
        string retryFrom,
        WorkItemState expectedResumeState)
    {
        var item = MakeQuotaWaitingItem(DateTimeOffset.UtcNow, priority: 100) with
        {
            QuotaRetryPhase = null,
            QuotaRetryFrom = retryFrom,
        };

        Assert.Equal(expectedResumeState, QuotaRetryPhasePolicy.OrderingStateForQuotaRetryCandidate(item));
        Assert.Equal(expectedResumeState, RetryFromPolicy.ResumeStateForRetryFrom(retryFrom));
    }

    [Fact]
    public async Task DispatchWake_BoundsDueQuotaRetryPromotionScanAndSchedulesFollowUpWake()
    {
        const int expectedScanBudget = 512;
        var now = DateTimeOffset.UtcNow;
        var queue = new ObservedTaskQueue();
        var fakeTime = new ControllableTimeProvider();
        fakeTime.SetUtcNow(now);
        var promoter = new RecordingQuotaRetryDispatchPromoter(
            new QuotaRetryDispatchPromotionResult(
                Promoted: false,
                Outcome: "skipped:quota-still-gated",
                Disposition: QuotaRetryDispatchDisposition.Blocked));

        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue,
            _store,
            new ReleaseControlledPipeline(_store),
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            quotaRetryDispatchPromoter: promoter,
            timeProvider: fakeTime);

        for (var i = 0; i < expectedScanBudget + 8; i++)
        {
            await _store.CreateAsync(MakeQuotaWaitingItem(now.AddTicks(i), priority: 1000));
        }

        await _store.CreateAsync(MakeItem(now.AddSeconds(1)) with { Priority = -1000 });

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);

        Assert.Null(picked);
        Assert.Equal(expectedScanBudget, promoter.CallCount);
        Assert.True(await AdvanceUntilAsync(
            fakeTime,
            TimeSpan.FromMilliseconds(250),
            () => queue.GenericWakeEnqueueCount >= 1));
    }

    [Fact]
    public async Task SlotReleaseWake_DoesNotClearDeferredBacklogItem()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var running = MakeItem(createdAt: now);
        var deferred = MakeItem(createdAt: now.AddMilliseconds(1));
        await _store.CreateAsync(running);
        await _store.CreateAsync(deferred);
        svc.MarkDeferredForTest(deferred.Id);

        await queue.EnqueueAsync(running.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, DispatchWaitTimeout));

        pipeline.Release(running.Id);
        Assert.True(await pipeline.WaitForDoneAsync(running.Id, DispatchWaitTimeout));
        Assert.True(
            await queue.WaitForCompletedDequeuesAsync(2, DispatchWaitTimeout),
            "The slot-release wake should be consumed as a generic rescan.");

        Assert.True(
            svc.IsDeferredForTest(deferred.Id),
            "A generic slot-release wake must not clear deferred items as retry-now signals.");
        Assert.False(
            await pipeline.WaitForEnteredAsync(deferred.Id, NoDispatchQuietPeriod),
            "The deferred item should remain quiet long enough to prove the generic wake was not treated as retry-now.");

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SlotReleaseWake_DoesNotCollapseCompletedItemDeferral()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        // The cap-retry deferral timer now runs on the injected clock, so the
        // quiet-window assertion below is deterministic: the timer cannot fire
        // until the test advances the fake clock. The previous revision relied
        // on a 5s real-wall-clock window to keep the timer from firing during
        // the assertion (and documented a race observed at ~806ms under load) —
        // routing ScheduleDeferredRequeue's Task.Delay through _time removes
        // that wall-clock dependency entirely.
        var capRetryDelay = TimeSpan.FromSeconds(5);
        var fakeTime = new ControllableTimeProvider();
        var concurrency = new AgentConcurrencyOptions
        {
            Members = { ["codex"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
        };
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: concurrency,
            quotaRouterOptions: new QuotaRouterOptions { CapRetryRecheckInterval = capRetryDelay },
            timeProvider: fakeTime);

        Assert.True(svc.TryReserveAgentSlotForTest(AgentKind.Codex));

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var item = MakeItem(fakeTime.GetUtcNow()) with { Agent = AgentKind.Codex };
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        Assert.True(
            await WaitUntilAsync(() => svc.IsDeferredForTest(item.Id), DispatchWaitTimeout),
            "The item itself should enter the cap deferral path.");
        Assert.True(
            await queue.WaitForCompletedDequeuesAsync(2, DispatchWaitTimeout),
            "The slot-release wake should be consumed as a generic rescan after the deferring worker exits.");

        Assert.Equal(1, queue.EnqueueCount(item.Id));
        Assert.False(pipeline.HasEntered(item.Id));

        // Without advancing the clock the deferral timer cannot fire, so the
        // generic slot-release wake must not produce a retry. This is now a
        // deterministic invariant rather than a real-time quiet-window race.
        Assert.False(
            await WaitUntilAsync(() => queue.EnqueueCount(item.Id) > 1, TimeSpan.FromMilliseconds(300)),
            "The generic slot-release wake must not clear the completed item's deferral or enqueue an immediate retry.");
        Assert.True(svc.IsDeferredForTest(item.Id));

        // Drive the configured cap deferral interval on the injected clock: the
        // item-specific retry must occur only when that timer fires.
        Assert.True(
            await AdvanceUntilAsync(
                fakeTime,
                capRetryDelay,
                () => queue.EnqueueCount(item.Id) > 1),
            "The item-specific retry should occur only when the configured cap deferral interval fires.");

        svc.ReleaseAgentSlotForTest(AgentKind.Codex);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DeferredRequeue_DuplicateScheduleKeepsSingleRetryOwner()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var fakeTime = new ControllableTimeProvider();
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            timeProvider: fakeTime);

        var id = WorkItemId.New();
        var delay = TimeSpan.FromMilliseconds(75);

        svc.ScheduleInfrastructureDeferredRequeue(id, delay);
        svc.ScheduleInfrastructureDeferredRequeue(id, delay);

        Assert.True(svc.IsDeferredForTest(id));
        // Both deferral timers run on the injected clock, so the single-owner
        // contract is exercised deterministically: advancing past the delay
        // fires exactly the owning timer's retry wake.
        Assert.True(
            await AdvanceUntilAsync(fakeTime, delay, () => queue.EnqueueCount(id) == 1),
            "The first deferral owner should emit the retry wake.");
        Assert.False(svc.IsDeferredForTest(id));

        // The duplicate schedule was rejected at registration (TryAdd failed),
        // so it never armed a second timer. Advancing the clock far past the
        // delay must still produce no second retry wake — deterministic now
        // that the timers no longer depend on the wall clock.
        fakeTime.Advance(delay + delay);
        await Task.Delay(50);
        Assert.False(
            await WaitUntilAsync(() => queue.EnqueueCount(id) > 1, TimeSpan.FromMilliseconds(250)),
            "A duplicate deferral schedule must not create a second retry wake for the same item.");
    }

    [Fact]
    public async Task SlotReleaseWake_DoesNotDispatchWhileQueuePaused()
    {
        using var controller = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            queueController: controller);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        // The scenario under test is the pause of a second, already-consumed
        // wake while it waits for the first worker slot. Make the first item
        // unambiguously first in the store-backed dispatcher ordering so the
        // assertion does not depend on timestamp precision under load.
        var running = MakeItem(createdAt: now) with { Priority = 1 };
        var readyBacklog = MakeItem(createdAt: now.AddMilliseconds(1));
        await _store.CreateAsync(running);
        await _store.CreateAsync(readyBacklog);
        await queue.EnqueueAsync(running.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, StarvationBackstopTimeout));

        await controller.PauseAsync("slot release wake suppression test");
        pipeline.Release(running.Id);
        Assert.True(await pipeline.WaitForDoneAsync(running.Id, StarvationBackstopTimeout));
        Assert.True(
            await WaitUntilAsync(() => queue.TotalEnqueueCount >= 2, DispatchWaitTimeout),
            "The slot-release wake should be enqueued even when the queue is paused.");

        // Negative suppression check. This is safe against a starved dispatch
        // loop, not a wall-clock gamble: the pause is fully committed
        // (PauseAsync awaited) BEFORE the slot-release wake can even exist
        // (Release → worker completes → wake enqueued), and the dispatch loop
        // re-reads IsQueuePaused after every dequeue and again after acquiring
        // the concurrency gate, before any PickNextEligibleAsync (see
        // OrchestratorService dispatch loop). So the ready backlog cannot be
        // dispatched while paused regardless of scheduling; the quiet period only
        // needs to give an ERRONEOUS dispatch time to surface. We additionally
        // assert the durable row is still Queued, so suppression is proven by
        // observable state, not solely by the timed window.
        Assert.False(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, NoDispatchQuietPeriod),
            "The paused queue should hold the slot-release wake without dispatching during the quiet period.");
        var stored = await _store.GetAsync(readyBacklog.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);

        await controller.ResumeAsync();
        // WaitForEnteredAsync / WaitForDoneAsync are event-driven (TaskCompletion
        // Source set the instant the pipeline enters/finishes the item), so these
        // are deterministic signals — the timeout is only a backstop. Under the
        // 6-core capped full suite the host can be pushed far past the cap (load
        // has been observed in the 50-90 range from the co-resident orchestrator +
        // VMs), stretching the resume→dispatch→enter→done latency well beyond the
        // 10s DispatchWaitTimeout even though the wake fires promptly. Use a
        // generous backstop so a correct-but-starved resume is not misread as a
        // failure; a genuine "resume does not dispatch" regression still fails
        // because the event never fires at all.
        Assert.True(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, StarvationBackstopTimeout),
            "The paused branch should preserve the slot-release wake so resume picks up the ready backlog.");

        pipeline.Release(readyBacklog.Id);
        Assert.True(await pipeline.WaitForDoneAsync(readyBacklog.Id, StarvationBackstopTimeout));

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StopAsync(stopCts.Token);
    }

    [Fact]
    public async Task QueuePauseSuppressesBufferedKickWaitingForReleasedSlot()
    {
        using var controller = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            queueController: controller);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var running = MakeItem(createdAt: now);
        var readyBacklog = MakeItem(createdAt: now.AddMilliseconds(1));
        await _store.CreateAsync(running);
        await _store.CreateAsync(readyBacklog);
        await queue.EnqueueAsync(running.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, DispatchWaitTimeout));

        await queue.EnqueueAsync(readyBacklog.Id);
        Assert.True(
            await queue.WaitForCompletedDequeuesAsync(2, DispatchWaitTimeout),
            "The ready item kick should be consumed while the dispatcher is blocked on the full pool.");

        await controller.PauseAsync("pause while buffered kick waits for a worker slot");
        pipeline.Release(running.Id);
        Assert.True(await pipeline.WaitForDoneAsync(running.Id, DispatchWaitTimeout));

        Assert.False(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, NoDispatchQuietPeriod),
            "A queued pause must suppress a buffered kick that unblocks after a worker slot is released.");
        var stored = await _store.GetAsync(readyBacklog.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);

        await controller.ResumeAsync();
        Assert.True(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, DispatchWaitTimeout),
            "The suppressed buffered kick should be preserved so resume can pick up the ready backlog.");

        pipeline.Release(readyBacklog.Id);
        Assert.True(await pipeline.WaitForDoneAsync(readyBacklog.Id, DispatchWaitTimeout));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QueuePauseAfterPickupDuringSpawnPacing_UnreservesAndPreservesWake()
    {
        using var controller = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        // Force a pacing window and pause from the reservation hook so the
        // second item exercises the post-pacing queue-pause branch
        // deterministically. Reset the timestamp before resume so the
        // carried-over first spawn does not block the post-resume dispatch.
        WorkItemId? pauseOnReserve = null;
        var pauseApplied = false;
        var reservedSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                MinSpawnInterval = SpawnPacingBranchInterval,
                OnWorkerReservedForTest = id =>
                {
                    if (id != pauseOnReserve || pauseApplied)
                        return Task.CompletedTask;

                    pauseApplied = true;
                    reservedSecond.TrySetResult();
                    return controller.PauseAsync("pause after pickup during spawn pacing");
                },
            },
            NullLogger<OrchestratorService>.Instance,
            queueController: controller);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var first = MakeItem(createdAt: now);
        var second = MakeItem(createdAt: now.AddMilliseconds(1));
        pauseOnReserve = second.Id;
        await _store.CreateAsync(first);
        await _store.CreateAsync(second);
        await queue.EnqueueAsync(first.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(first.Id, DispatchWaitTimeout));
        svc.SetLastSpawnAtForTest(DateTimeOffset.UtcNow);
        pipeline.Release(first.Id);
        Assert.True(await pipeline.WaitForDoneAsync(first.Id, DispatchWaitTimeout));

        await reservedSecond.Task.WaitAsync(DispatchWaitTimeout);

        Assert.True(
            await WaitUntilAsync(() => !svc.IsActiveForTest(second.Id), SpawnPacingEarlyExitTimeout),
            "The queue-pause branch after spawn pacing must unreserve the item and release the gate.");
        Assert.False(pipeline.HasEntered(second.Id));

        svc.SetLastSpawnAtForTest(DateTimeOffset.UtcNow - SpawnPacingBranchInterval);
        await controller.ResumeAsync();
        Assert.True(
            await pipeline.WaitForEnteredAsync(second.Id, DispatchWaitTimeout + SpawnPacingBranchInterval),
            "The post-pickup queue-pause branch should preserve a wake so resume dispatches the item.");

        pipeline.Release(second.Id);
        Assert.True(await pipeline.WaitForDoneAsync(second.Id, DispatchWaitTimeout));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QueuePauseBetweenSpawnReservationAndPipelineStart_SkipsPipelineAndPreservesItem()
    {
        // Covers the IsQueuePaused branch inside the worker's Task.Run body:
        // when the queue pauses after spawn pacing completed but before the
        // pipeline starts, the worker must log+return without running the
        // item, leaving the work item Queued so a later resume can dispatch
        // it.
        using var controller = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);

        // OnWorkerSpawned runs synchronously between the spawn timestamp
        // write and Task.Run. Pausing the queue once on the first spawn
        // guarantees the Task.Run body sees IsQueuePaused == true while
        // leaving later spawns (post-resume) untouched.
        SqliteQueueController? capturedController = null;
        var pausesIssued = 0;
        var pauseIssued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseOnSpawn = new Action(() =>
        {
            if (Interlocked.Increment(ref pausesIssued) != 1) return;
            capturedController?.PauseAsync("test: pause between spawn and pipeline").GetAwaiter().GetResult();
            pauseIssued.TrySetResult();
        });
        capturedController = controller;
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                OnWorkerSpawned = pauseOnSpawn,
            },
            NullLogger<OrchestratorService>.Instance,
            queueController: controller);

        await svc.StartAsync(CancellationToken.None);
        // Positive event-driven waits below use StarvationBackstopTimeout: they
        // are deterministic signals (TaskCompletionSource-backed) that can be
        // correct-but-slow under the 6-core capped full suite on a co-resident
        // host, so the timeout is headroom only. The negative suppression check
        // stays on the short NoDispatchQuietPeriod (a bug would surface fast).
        await queue.WaitForFirstDequeueAsync(StarvationBackstopTimeout);

        var item = MakeItem(createdAt: DateTimeOffset.UtcNow);
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await pauseIssued.Task.WaitAsync(StarvationBackstopTimeout);
        Assert.Equal(QueueState.Paused, controller.State);
        Assert.False(
            await pipeline.WaitForEnteredAsync(item.Id, NoDispatchQuietPeriod),
            "The worker must not enter the pipeline when the queue paused between spawn and pipeline start.");
        Assert.True(
            await WaitUntilAsync(() => !svc.IsActiveForTest(item.Id), StarvationBackstopTimeout),
            "The skipped worker's finally block must release the slot and reservation.");

        var stored = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);

        await controller.ResumeAsync();
        Assert.True(
            await pipeline.WaitForEnteredAsync(item.Id, StarvationBackstopTimeout),
            "After resume the item must dispatch normally.");
        pipeline.Release(item.Id);
        Assert.True(await pipeline.WaitForDoneAsync(item.Id, StarvationBackstopTimeout));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SpawnPacingDelay_BreaksPromptlyOnQueuePauseDuringWait()
    {
        // Asserts the pause-detection latency of WaitForSpawnPacingDelayAsync:
        // when the queue pauses while the worker is mid-wait, the wait must
        // exit well before the configured MinSpawnInterval would otherwise
        // elapse. A regression that lost the IsQueuePaused check inside the
        // polling loop would block for the full MinSpawnInterval.
        using var controller = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var minSpawnInterval = TimeSpan.FromSeconds(5);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                MinSpawnInterval = minSpawnInterval,
            },
            NullLogger<OrchestratorService>.Instance,
            queueController: controller);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var first = MakeItem(createdAt: now);
        var second = MakeItem(createdAt: now.AddMilliseconds(1));
        await _store.CreateAsync(first);
        await _store.CreateAsync(second);
        await queue.EnqueueAsync(first.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(first.Id, DispatchWaitTimeout));
        pipeline.Release(first.Id);
        Assert.True(await pipeline.WaitForDoneAsync(first.Id, DispatchWaitTimeout));

        Assert.True(
            await WaitUntilAsync(() => svc.IsActiveForTest(second.Id), DispatchWaitTimeout),
            "The second item should be reserved before the spawn-pacing delay completes.");

        // The pacing wait should be at least a few seconds at this point
        // (MinSpawnInterval=5s less first-item processing). Pausing the
        // queue must break the wait far faster than that residual interval,
        // proving the polling loop observes IsQueuePaused mid-wait. A
        // regression that lost the check would block until the full pacing
        // window elapsed.
        var pauseStart = DateTimeOffset.UtcNow;
        await controller.PauseAsync("pause during spawn pacing wait");
        Assert.True(
            await WaitUntilAsync(() => !svc.IsActiveForTest(second.Id), TimeSpan.FromSeconds(2)),
            "The polling loop in WaitForSpawnPacingDelayAsync must observe IsQueuePaused and exit promptly.");
        var detectionLatency = DateTimeOffset.UtcNow - pauseStart;
        Assert.True(
            detectionLatency < TimeSpan.FromMilliseconds(1500),
            $"Pause detection in the spawn-pacing wait took {detectionLatency} which is far longer than the polling interval; the wait did not observe the pause.");
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ShutdownPauseAfterPickupDuringSpawnPacing_UnreservesAndStopsDispatch()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        WorkItemId? pauseOnReserve = null;
        var pauseApplied = false;
        var reservedSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OrchestratorService? svcRef = null;
        using var svc = svcRef = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                MinSpawnInterval = SpawnPacingBranchInterval,
                OnWorkerReservedForTest = id =>
                {
                    if (id == pauseOnReserve && !pauseApplied)
                    {
                        pauseApplied = true;
                        reservedSecond.TrySetResult();
                        svcRef!.PauseDispatch();
                    }
                    return Task.CompletedTask;
                },
            },
            NullLogger<OrchestratorService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var first = MakeItem(createdAt: now);
        var second = MakeItem(createdAt: now.AddMilliseconds(1));
        pauseOnReserve = second.Id;
        await _store.CreateAsync(first);
        await _store.CreateAsync(second);
        await queue.EnqueueAsync(first.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(first.Id, DispatchWaitTimeout));
        svc.SetLastSpawnAtForTest(DateTimeOffset.UtcNow);
        pipeline.Release(first.Id);
        Assert.True(await pipeline.WaitForDoneAsync(first.Id, DispatchWaitTimeout));

        await reservedSecond.Task.WaitAsync(DispatchWaitTimeout);

        Assert.True(
            await WaitUntilAsync(() => !svc.IsActiveForTest(second.Id), SpawnPacingEarlyExitTimeout),
            "The shutdown-pause branch after spawn pacing must unreserve the item and release the gate.");
        Assert.False(
            await pipeline.WaitForEnteredAsync(second.Id, NoDispatchQuietPeriod),
            "Shutdown dispatch pause must suppress the reserved item after it is unreserved.");

        var stored = await _store.GetAsync(second.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SlotReleaseWake_DoesNotDispatchAfterShutdownPause()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var running = MakeItem(createdAt: now);
        var readyBacklog = MakeItem(createdAt: now.AddMilliseconds(1));
        await _store.CreateAsync(running);
        await _store.CreateAsync(readyBacklog);
        await queue.EnqueueAsync(running.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, DispatchWaitTimeout));
        Assert.True(
            await queue.WaitForDequeueCallsAsync(2, DispatchWaitTimeout),
            "The dispatch loop should be blocked on the next queue wake before shutdown is paused.");

        queue.DropDispatchWakeEnqueueCount = 1;
        svc.PauseDispatch();
        pipeline.Release(running.Id);
        Assert.True(await pipeline.WaitForDoneAsync(running.Id, DispatchWaitTimeout));
        Assert.True(
            await WaitUntilAsync(() => queue.TotalEnqueueCount >= 3, DispatchWaitTimeout),
            "The slot-release wake should be enqueued even after shutdown dispatch is paused.");
        Assert.True(
            await queue.WaitForCompletedDequeuesAsync(2, DispatchWaitTimeout),
            "The slot-release wake should be delivered to the loop and suppressed by IsDispatchPaused.");

        Assert.False(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, NoDispatchQuietPeriod),
            "The shutdown dispatch gate should suppress the delivered slot-release wake during the quiet period.");
        var stored = await _store.GetAsync(readyBacklog.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);

        await svc.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(EnqueueFailureMode.ThrowSynchronously)]
    [InlineData(EnqueueFailureMode.FaultAsynchronously)]
    public async Task SlotReleaseWake_EnqueueFailure_RetriesUntilWakeIsDelivered(
        EnqueueFailureMode failureMode)
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        var logger = new CapturingLogger<OrchestratorService>();
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            logger);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var running = MakeItem(now);
        var readyBacklog = MakeItem(now.AddMilliseconds(1));
        await _store.CreateAsync(running);
        await _store.CreateAsync(readyBacklog);
        await queue.EnqueueAsync(running.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, DispatchWaitTimeout));

        queue.FailureMode = failureMode;
        pipeline.Release(running.Id);

        Assert.True(await pipeline.WaitForDoneAsync(running.Id, DispatchWaitTimeout));
        Assert.True(
            await WaitUntilAsync(
                () => logger.Entries.Any(e =>
                    e.Level == LogLevel.Error
                    && e.Exception is InvalidOperationException
                    && e.Message.Contains("required slot-release wake-up kick failed", StringComparison.Ordinal)),
                DispatchWaitTimeout),
            "A slot-release enqueue failure should be logged as an invariant failure before retrying.");
        Assert.False(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, NoDispatchQuietPeriod),
            "The ready backlog should remain parked while the wake enqueue keeps failing.");

        queue.FailureMode = EnqueueFailureMode.None;
        Assert.True(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, DispatchWaitTimeout),
            "The retry loop should deliver the slot-release wake once the queue accepts writes again.");

        pipeline.Release(readyBacklog.Id);
        Assert.True(await pipeline.WaitForDoneAsync(readyBacklog.Id, DispatchWaitTimeout));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RecoveredSlotRelease_CanSuppressWakeBeforeRecoveryStateTransition()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var workerRegistry = new SqliteWorkerRegistry(_dbPath, NullLogger<SqliteWorkerRegistry>.Instance);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            workerRegistry: workerRegistry,
            deadWorkerOpts: new DeadWorkerOptions());

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var running = MakeItem(createdAt: now);
        var readyBacklog = MakeItem(createdAt: now.AddMilliseconds(1));
        await _store.CreateAsync(running);
        await _store.CreateAsync(readyBacklog);
        await queue.EnqueueAsync(running.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, DispatchWaitTimeout));
        var worker = await WaitForWorkerRegistrationAsync(workerRegistry, running.Id, DispatchWaitTimeout);
        Assert.NotNull(worker);
        await _store.UpdateAsync(running with
        {
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        Assert.True(await svc.TryReleaseRecoveredWorkerSlotAsync(
            worker!.WorkerId,
            running.Id,
            "test recovery release while durable row is still worker-owned"));

        Assert.Equal(0, queue.GenericWakeEnqueueCount);
        Assert.False(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, NoDispatchQuietPeriod),
            "Recovery release must not emit a generic wake before the recovery path updates or parks the item.");

        pipeline.Release(running.Id);
        Assert.True(await pipeline.WaitForDoneAsync(running.Id, DispatchWaitTimeout));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RecoveredSlotRelease_WakesDispatcherAfterRecoveryStateTransition()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var workerRegistry = new SqliteWorkerRegistry(_dbPath, NullLogger<SqliteWorkerRegistry>.Instance);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            workerRegistry: workerRegistry,
            deadWorkerOpts: new DeadWorkerOptions());

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var running = MakeItem(createdAt: now);
        var readyBacklog = MakeItem(createdAt: now.AddMilliseconds(1));
        await _store.CreateAsync(running);
        await _store.CreateAsync(readyBacklog);
        await queue.EnqueueAsync(running.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, DispatchWaitTimeout));
        var worker = await WaitForWorkerRegistrationAsync(workerRegistry, running.Id, DispatchWaitTimeout);
        Assert.NotNull(worker);

        var failed = running.With(WorkItemState.Failed, "test recovery transition");
        await _store.UpdateAsync(failed);

        Assert.True(await svc.TryReleaseRecoveredWorkerSlotAsync(
            worker!.WorkerId,
            running.Id,
            "test recovery release after durable transition"));

        Assert.True(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, DispatchWaitTimeout),
            "A recovered slot release with a safe durable state should wake the dispatcher for unrelated ready work.");

        pipeline.Release(running.Id);
        pipeline.Release(readyBacklog.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    private static WorkItem MakeItem(DateTimeOffset createdAt) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };

    private static WorkItem MakeQuotaWaitingItem(DateTimeOffset createdAt, int priority) =>
        MakeItem(createdAt) with
        {
            State = WorkItemState.WaitingForQuotaReset,
            Priority = priority,
            AgentClassId = "quota-class",
            QuotaRetryFrom = "work",
            QuotaRetryPhase = "work",
            NextQuotaRetryAt = createdAt.AddHours(-1),
        };

    private static Project QuotaProject() => new()
    {
        Id = new ProjectId("test"),
        DisplayName = "Test",
        RepositoryUrl = "http://fake",
        DefaultAgent = AgentKind.Codex,
        DefaultAgentClass = "quota-class",
    };

    private static AgentClassRouter BuildQuotaRouter(params IAgentQuotaProbe[] probes) =>
        new(
            [
                new AgentClass
                {
                    Id = "quota-class",
                    DisplayName = "Quota Class",
                    Members =
                    [
                        new AgentMembership
                        {
                            Agent = AgentKind.Codex,
                            Billing = AgentBilling.Subscription,
                            QualityScore = 100,
                        },
                    ],
                },
            ],
            probes,
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);

    private static OrchestratorOptions QuotaRetryOptions() => new()
    {
        MaxConcurrentWorkers = 1,
        AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
        {
            Enabled = true,
            PeriodicCheckInterval = TimeSpan.FromHours(1),
            ClockDriftSafetyMargin = TimeSpan.Zero,
            MaxAutoRetriesPerWorkItem = 3,
        },
    };

    private QuotaRetryScheduler BuildQuotaRetrySchedulerForTest(
        ITaskQueue queue,
        string gitRoot,
        OrchestratorOptions options,
        IQuotaRetryRouter? router = null,
        IProjectRepository? projects = null)
    {
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var retrier = new WorkItemRetrier(
            _store,
            queue,
            gitHost,
            NullLogger<WorkItemRetrier>.Instance);
        return new QuotaRetryScheduler(
            _store,
            retrier,
            options,
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects);
    }

    private sealed class SequenceProbe : IAgentQuotaProbe
    {
        private readonly AgentQuotaSnapshot[] _snapshots;
        private int _nextIndex;

        public SequenceProbe(AgentKind kind, params AgentQuotaSnapshot[] snapshots)
        {
            Kind = kind;
            _snapshots = snapshots.Length == 0
                ? [AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient)]
                : snapshots;
        }

        public AgentKind Kind { get; }
        public int CallCount => Volatile.Read(ref _nextIndex);

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            var index = Interlocked.Increment(ref _nextIndex) - 1;
            if (index >= _snapshots.Length)
                index = _snapshots.Length - 1;
            return Task.FromResult(_snapshots[index]);
        }
    }

    private sealed class ThrowingQuotaRetryRouter : IQuotaRetryRouter
    {
        public Task<QuotaRetryRoutingDecision> ResolveQuotaRetryAsync(
            WorkItem item,
            Project? project,
            CancellationToken ct,
            string? requiredCapability = null) =>
            throw new InvalidOperationException("quota retry router failed");

        public Task<DateTimeOffset?> ComputeEarliestExhaustedResetAsync(
            WorkItem item,
            Project? project,
            CancellationToken ct,
            string? requiredCapability = null) =>
            Task.FromResult<DateTimeOffset?>(null);
    }

    private sealed class RecordingQuotaRetryDispatchPromoter : IQuotaRetryDispatchPromoter
    {
        private readonly Func<WorkItem, Task<QuotaRetryDispatchPromotionResult>> _handler;
        private int _callCount;

        public RecordingQuotaRetryDispatchPromoter(QuotaRetryDispatchPromotionResult result)
            : this(_ => Task.FromResult(result))
        {
        }

        public RecordingQuotaRetryDispatchPromoter(Func<WorkItem, Task<QuotaRetryDispatchPromotionResult>> handler) =>
            _handler = handler;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<QuotaRetryDispatchPromotionResult> TryPromoteForDispatchAsync(
            WorkItem item,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            return _handler(item);
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    /// <summary>
    /// Advances the injected fake clock by <paramref name="step"/> in a loop,
    /// yielding between advances so the deferral-timer continuation and the
    /// subsequent SQLite enqueue can run, until <paramref name="predicate"/>
    /// trips. The fake clock — not the wall clock — is what fires the deferral
    /// timer; there is an unavoidable scheduling gap between the moment
    /// ScheduleDeferredRequeue registers its timer and the moment we advance,
    /// so the loop re-advances (each Advance fires any already-registered timer)
    /// and yields. The 30s wall-clock backstop only guards against a genuine
    /// non-firing regression and is never the mechanism that fires the timer,
    /// so it does not reintroduce wall-clock flakiness.
    /// </summary>
    private static async Task<bool> AdvanceUntilAsync(
        ControllableTimeProvider fakeTime,
        TimeSpan step,
        Func<bool> predicate)
    {
        var backstop = DateTime.UtcNow.AddSeconds(30);
        while (!predicate())
        {
            fakeTime.Advance(step);
            await Task.Yield();
            if (predicate())
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(2));
            if (DateTime.UtcNow > backstop)
                return predicate();
        }
        return true;
    }

    private static async Task<WorkerRegistration?> WaitForWorkerRegistrationAsync(
        IWorkerRegistry registry,
        WorkItemId workItemId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var worker = (await registry.ListAsync())
                .FirstOrDefault(w => w.CurrentWorkItemId == workItemId.ToString());
            if (worker is not null)
                return worker;

            await Task.Delay(25);
        }

        return (await registry.ListAsync())
            .FirstOrDefault(w => w.CurrentWorkItemId == workItemId.ToString());
    }

    private sealed class ObservedTaskQueue : ITaskQueue
    {
        private readonly Channel<ObservedDispatch> _channel = Channel.CreateUnbounded<ObservedDispatch>();
        private readonly ConcurrentQueue<ObservedDispatch> _enqueued = new();
        private readonly ConcurrentQueue<ObservedDispatch> _dequeued = new();
        private readonly TaskCompletionSource _firstDequeue =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dequeueCalls;
        private int _dropDispatchWakeEnqueueCount;

        public int DropDispatchWakeEnqueueCount
        {
            get => Volatile.Read(ref _dropDispatchWakeEnqueueCount);
            set => Volatile.Write(ref _dropDispatchWakeEnqueueCount, value);
        }
        public EnqueueFailureMode FailureMode { get; set; } = EnqueueFailureMode.None;

        public ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default)
        {
            var dispatch = ObservedDispatch.ForWorkItem(id);
            _enqueued.Enqueue(dispatch);

            return FailureMode switch
            {
                EnqueueFailureMode.ThrowSynchronously =>
                    throw new InvalidOperationException("synthetic synchronous enqueue failure"),
                EnqueueFailureMode.FaultAsynchronously =>
                    new ValueTask(Task.FromException(new InvalidOperationException("synthetic asynchronous enqueue failure"))),
                _ => _channel.Writer.WriteAsync(dispatch, ct),
            };
        }

        public ValueTask EnqueueDispatchWakeAsync(CancellationToken ct = default)
        {
            var dispatch = ObservedDispatch.GenericWake;
            _enqueued.Enqueue(dispatch);
            while (true)
            {
                var remaining = Volatile.Read(ref _dropDispatchWakeEnqueueCount);
                if (remaining <= 0) break;
                if (Interlocked.CompareExchange(ref _dropDispatchWakeEnqueueCount, remaining - 1, remaining) == remaining)
                    return ValueTask.CompletedTask;
            }

            return FailureMode switch
            {
                EnqueueFailureMode.ThrowSynchronously =>
                    throw new InvalidOperationException("synthetic synchronous enqueue failure"),
                EnqueueFailureMode.FaultAsynchronously =>
                    new ValueTask(Task.FromException(new InvalidOperationException("synthetic asynchronous enqueue failure"))),
                _ => _channel.Writer.WriteAsync(dispatch, ct),
            };
        }

        public int Count => _channel.Reader.Count;
        public int TotalEnqueueCount => _enqueued.Count;
        public int CompletedDequeueCount => _dequeued.Count;
        public int DequeueCallCount => Volatile.Read(ref _dequeueCalls);
        public int GenericWakeEnqueueCount => _enqueued.Count(static d => d.IsGenericWake);

        public int EnqueueCount(WorkItemId id)
        {
            var count = 0;
            foreach (var enqueued in _enqueued)
                if (enqueued.WorkItemId == id) count++;
            return count;
        }

        public async ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default)
        {
            try
            {
                var dispatch = await ReadObservedDispatchAsync(ct);
                return dispatch.WorkItemId;
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public async ValueTask<bool> DequeueDispatchSignalAsync(CancellationToken ct = default)
        {
            try
            {
                await ReadObservedDispatchAsync(ct);
                return true;
            }
            catch (ChannelClosedException)
            {
                return false;
            }
        }

        public Task WaitForFirstDequeueAsync(TimeSpan timeout) =>
            _firstDequeue.Task.WaitAsync(timeout);

        public Task<bool> WaitForDequeueCallsAsync(int count, TimeSpan timeout) =>
            WaitUntilAsync(() => DequeueCallCount >= count, timeout);

        public Task<bool> WaitForCompletedDequeuesAsync(int count, TimeSpan timeout) =>
            WaitUntilAsync(() => CompletedDequeueCount >= count, timeout);

        private async ValueTask<ObservedDispatch> ReadObservedDispatchAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _dequeueCalls);
            _firstDequeue.TrySetResult();
            var dispatch = await _channel.Reader.ReadAsync(ct);
            _dequeued.Enqueue(dispatch);
            return dispatch;
        }

        private readonly record struct ObservedDispatch(WorkItemId? WorkItemId, bool IsGenericWake)
        {
            public static ObservedDispatch ForWorkItem(WorkItemId id) => new(id, false);
            public static ObservedDispatch GenericWake { get; } = new(null, true);
        }
    }

    public enum EnqueueFailureMode
    {
        None,
        ThrowSynchronously,
        FaultAsynchronously,
    }

    private sealed class ReleaseControlledPipeline : IPipelineRunner
    {
        private readonly IWorkItemStore _store;
        private readonly ConcurrentDictionary<WorkItemId, byte> _actualEntered = new();
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _entered = new();
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _released = new();
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _done = new();

        public ReleaseControlledPipeline(IWorkItemStore store) => _store = store;

        public bool HasEntered(WorkItemId id) => _actualEntered.ContainsKey(id);

        public void Release(WorkItemId id) =>
            _released.GetOrAdd(id, static _ => NewSignal()).TrySetResult();

        public Task<bool> WaitForEnteredAsync(WorkItemId id, TimeSpan timeout) =>
            WaitForSignalAsync(_entered.GetOrAdd(id, static _ => NewSignal()), timeout);

        public Task<bool> WaitForDoneAsync(WorkItemId id, TimeSpan timeout) =>
            WaitForSignalAsync(_done.GetOrAdd(id, static _ => NewSignal()), timeout);

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            _actualEntered.TryAdd(item.Id, 0);
            _entered.GetOrAdd(item.Id, static _ => NewSignal()).TrySetResult();
            await _released.GetOrAdd(item.Id, static _ => NewSignal()).Task.WaitAsync(ct);
            await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
            _done.GetOrAdd(item.Id, static _ => NewSignal()).TrySetResult();
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task<bool> WaitForSignalAsync(TaskCompletionSource signal, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(signal.Task, Task.Delay(timeout));
            return completed == signal.Task;
        }
    }
}
