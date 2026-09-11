using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the WorkComplete watchdog gap: a worker holding an item
/// at <see cref="WorkItemState.WorkComplete"/> while heartbeating normally
/// used to escape both <see cref="WorkerProgressWatchdog"/> and
/// <see cref="ItemStaleProgressWatchdog"/>, pinning its pool slot (and the
/// work-phase sandbox behind the cancelled pipeline) until the pool
/// deadlocked with queued work starving behind it.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkCompleteRecoveryTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-workcomplete-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;
    private readonly InMemoryTaskQueue _queue;
    private readonly FakeTimeProvider _time;

    public WorkCompleteRecoveryTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        _registry = new SqliteWorkerRegistry(_dbPath);
        _queue = new InMemoryTaskQueue();
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private WorkItem FrozenWorkCompleteItem(string? workBranch = "codeybox/auto/work-done") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.WorkComplete,
        WorkBranch = workBranch,
        StartedAt = _time.GetUtcNow().AddHours(-2),
        UpdatedAt = _time.GetUtcNow().AddHours(-2),
    };

    private async Task PlantHeartbeatingWorkerAsync(string workerId, WorkItemId itemId)
    {
        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = workerId,
            HostName = "host",
            ProcessId = 4242,
            StartedAt = _time.GetUtcNow().AddHours(-2),
            LastHeartbeatAt = _time.GetUtcNow(),
            CurrentWorkItemId = itemId.ToString(),
        });
    }

    private static WorkerProgressWatchdogOptions WatchdogOptions() => new()
    {
        ProgressTimeout = TimeSpan.FromMinutes(30),
        CheckInterval = TimeSpan.FromMinutes(1),
        AutoRecover = true,
        ItemStaleTimeout = TimeSpan.FromMinutes(90),
        ItemStaleCheckInterval = TimeSpan.FromMinutes(5),
        ItemStaleMaxRecoveryAttempts = 3,
        MaxRecoveryAttempts = 3,
        PostAgentTransitionTimeout = TimeSpan.FromMinutes(2),
    };

    [Fact]
    public async Task WorkerProgressWatchdog_RecoversFrozenWorkComplete_PreservesBranchAndReleasesSlot()
    {
        const string workBranch = "codeybox/auto/work-frozen-complete";
        var item = FrozenWorkCompleteItem(workBranch);
        await _store.CreateAsync(item);
        const string workerId = "wedged-workcomplete-worker";
        await PlantHeartbeatingWorkerAsync(workerId, item.Id);

        var slotReleaser = new RecordingSlotReleaser();
        using var cancellations = new CancellationRegistry(CancellationToken.None);
        using var registration = cancellations.Register(item.Id);
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, WatchdogOptions(),
            NullLogger<WorkerProgressWatchdog>.Instance,
            streams: null, webhooks: null, slotReleaser: slotReleaser,
            cancellationRegistry: cancellations,
            timeProvider: _time);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.WorkComplete, after!.State);
        Assert.Equal(workBranch, after.WorkBranch);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Contains("watchdog", after.LastError);

        var release = Assert.Single(slotReleaser.Releases);
        Assert.Equal(workerId, release.WorkerId);
        Assert.True(registration.Token.IsCancellationRequested);

        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task ItemStaleWatchdog_RecoversFrozenWorkComplete_PreservesBranchAndReleasesSlot()
    {
        const string workBranch = "codeybox/auto/work-stale-complete";
        var item = FrozenWorkCompleteItem(workBranch);
        await _store.CreateAsync(item);
        const string workerId = "stale-workcomplete-worker";
        await PlantHeartbeatingWorkerAsync(workerId, item.Id);

        var slotReleaser = new RecordingSlotReleaser();
        using var cancellations = new CancellationRegistry(CancellationToken.None);
        using var registration = cancellations.Register(item.Id);
        var watchdog = new ItemStaleProgressWatchdog(
            _store, _queue, _registry, WatchdogOptions(),
            NullLogger<ItemStaleProgressWatchdog>.Instance,
            webhooks: null,
            slotReleaser: slotReleaser,
            cancellations: cancellations,
            timeProvider: _time);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.WorkComplete, after!.State);
        Assert.Equal(workBranch, after.WorkBranch);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Contains("item-stale", after.LastError);

        var release = Assert.Single(slotReleaser.Releases);
        Assert.Equal(workerId, release.WorkerId);
        Assert.True(registration.Token.IsCancellationRequested);

        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task WorkCompleteRecovery_ReentersAuditPhase_WithBranchIntact()
    {
        const string workBranch = "codeybox/auto/work-audit-reentry";
        var item = FrozenWorkCompleteItem(workBranch);
        await _store.CreateAsync(item);

        var slotReleaser = new RecordingSlotReleaser();
        var watchdog = new ItemStaleProgressWatchdog(
            _store, _queue, _registry, WatchdogOptions(),
            NullLogger<ItemStaleProgressWatchdog>.Instance,
            webhooks: null,
            slotReleaser: slotReleaser,
            timeProvider: _time);

        var result = await watchdog.RecoverItemAsync(item, "operator: re-enter audit", CancellationToken.None);

        Assert.True(result.Recovered);
        Assert.Equal(WorkItemState.WorkComplete, result.FromState);
        Assert.Equal(WorkItemState.WorkComplete, result.NewState);
        Assert.True(result.BranchPreserved);

        // Recovery from WorkComplete is the same resume point as an operator
        // retry from="audit": the next pickup skips work and runs the audit
        // loop on the preserved branch instead of re-running work.
        Assert.True(RetryFromPolicy.TryGetResumeState("audit", out var auditResume));
        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(auditResume, after!.State);
        Assert.Equal(workBranch, after.WorkBranch);
        Assert.False(after.PreserveWorkBranchOnQueuedPickup);
    }

    [Fact]
    public void WatchedStates_CoverEveryWorkerOccupiableState_AndPartitionTheEnum()
    {
        var expectedWatched = new[]
        {
            WorkItemState.Planning,
            WorkItemState.PlanReview,
            WorkItemState.PlanApproved,
            WorkItemState.Working,
            WorkItemState.Reworking,
            WorkItemState.WorkComplete,
            WorkItemState.Auditing,
            WorkItemState.AuditPassed,
            WorkItemState.Merging,
            WorkItemState.ReworkingForConflict,
            WorkItemState.Merged,
            WorkItemState.UpstreamPushing,
        };
        var expectedUnwatched = new[]
        {
            WorkItemState.Queued,
            WorkItemState.Done,
            WorkItemState.Failed,
            WorkItemState.Cancelled,
            WorkItemState.AuditFailed,
            WorkItemState.MergeConflictResolutionFailed,
            WorkItemState.AbandonedAfterRecoveryAttempts,
            WorkItemState.NeedsOperatorInput,
            WorkItemState.WaitingForQuotaReset,
            WorkItemState.WaitingForAgentResume,
            WorkItemState.WaitingForTransientRetry,
        };

        // A newly added WorkItemState lands in neither list and fails this
        // count, so it cannot silently escape recovery: the author must
        // decide which side it belongs on.
        var all = Enum.GetValues<WorkItemState>();
        Assert.Equal(all.Length, expectedWatched.Length + expectedUnwatched.Length);

        Assert.Equal(
            expectedWatched.OrderBy(s => s).ToArray(),
            WorkItemRecoveryPolicy.WorkerOccupiedStates.OrderBy(s => s).ToArray());

        foreach (var state in expectedWatched)
        {
            Assert.True(WorkerProgressWatchdog.IsWatchedState(state), $"{state} must be watched by WorkerProgressWatchdog");
            Assert.True(WorkItemRecoveryPolicy.IsItemStaleWatchedState(state), $"{state} must be watched by ItemStaleProgressWatchdog");
            Assert.True(WorkItemRecoveryPolicy.HandlesRecoveryState(state), $"{state} must be handled by DeadWorkerReaper");
        }

        foreach (var state in expectedUnwatched)
        {
            Assert.False(WorkerProgressWatchdog.IsWatchedState(state), $"{state} must not be watched by WorkerProgressWatchdog");
            Assert.False(WorkItemRecoveryPolicy.IsItemStaleWatchedState(state), $"{state} must not be watched by ItemStaleProgressWatchdog");
        }
    }

    [Fact]
    public async Task Recovery_WhenAllSlotsHeldByNonAdvancingItems_RestoresDispatchAndQueuedItemIsPickedUp()
    {
        var queue = new InMemoryTaskQueue();
        var pipeline = new WedgedPipeline();
        using var cancellations = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, cancellations,
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance,
            workerRegistry: _registry,
            deadWorkerOpts: new DeadWorkerOptions { HeartbeatInterval = TimeSpan.FromSeconds(30) });
        await svc.StartAsync(CancellationToken.None);

        try
        {
            var wedgeA = NewQueuedItem();
            var wedgeB = NewQueuedItem();
            await _store.CreateAsync(wedgeA);
            await _store.CreateAsync(wedgeB);
            await queue.EnqueueAsync(wedgeA.Id);
            await queue.EnqueueAsync(wedgeB.Id);

            Assert.True(await pipeline.WaitForEnteredAsync(wedgeA.Id, TimeSpan.FromSeconds(10)));
            Assert.True(await pipeline.WaitForEnteredAsync(wedgeB.Id, TimeSpan.FromSeconds(10)));

            var queued = NewQueuedItem();
            await _store.CreateAsync(queued);
            await queue.EnqueueAsync(queued.Id);
            Assert.False(
                await pipeline.WaitForEnteredAsync(queued.Id, TimeSpan.FromMilliseconds(500)),
                "pool is full so the queued item must wait");

            // Advance both holders to WorkComplete with committed branches,
            // then freeze them there: the real pipeline writes this
            // transition when work finishes, and the wedge under test sits
            // between the commit and the audit loop while heartbeating.
            var frozenAt = DateTimeOffset.UtcNow.AddHours(-2);
            var branches = new Dictionary<WorkItemId, string>();
            foreach (var id in new[] { wedgeA.Id, wedgeB.Id })
            {
                var current = await _store.GetAsync(id);
                Assert.NotNull(current);
                var branch = $"codeybox/auto/wedge-{id.ToString()[..8]}";
                branches[id] = branch;
                await _store.UpdateAsync(current! with
                {
                    State = WorkItemState.WorkComplete,
                    WorkBranch = branch,
                    StartedAt = frozenAt,
                    UpdatedAt = frozenAt,
                });
            }

            var watchdog = new WorkerProgressWatchdog(
                _registry, _store, queue, WatchdogOptions(),
                NullLogger<WorkerProgressWatchdog>.Instance,
                streams: null, webhooks: null, slotReleaser: svc,
                cancellationRegistry: cancellations);
            await watchdog.RunOnceAsync(CancellationToken.None);

            // Both wedged holders were recovered in place: still at
            // WorkComplete with their branches intact (audit re-entry), not
            // requeued from scratch.
            foreach (var id in new[] { wedgeA.Id, wedgeB.Id })
            {
                var recovered = await _store.GetAsync(id);
                Assert.NotNull(recovered);
                Assert.Equal(WorkItemState.WorkComplete, recovered!.State);
                Assert.Equal(branches[id], recovered.WorkBranch);
                Assert.True(recovered.RecoveryAttempts >= 1, $"wedged item {id} must have been recovered");
            }

            // The freed slots are re-dispatched (recovery re-enqueues the
            // holders, so they compete with the queued item for the slots).
            // Wait for the dispatcher to cycle, then let every held pipeline
            // run to completion so the queued item's turn arrives.
            var reentryDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            static async Task<DateTimeOffset?> StartedAtOf(SqliteWorkItemStore store, WorkItemId id)
                => (await store.GetAsync(id))?.StartedAt;
            while (DateTimeOffset.UtcNow < reentryDeadline)
            {
                var aAt = await StartedAtOf(_store, wedgeA.Id);
                var bAt = await StartedAtOf(_store, wedgeB.Id);
                var cAt = await StartedAtOf(_store, queued.Id);
                if (cAt is not null)
                    break;
                if (aAt > frozenAt && bAt > frozenAt)
                    break;
                await Task.Delay(50);
            }
            pipeline.ReleaseAll();

            Assert.True(
                await pipeline.WaitForEnteredAsync(queued.Id, TimeSpan.FromSeconds(15)),
                "recovering the wedged holders must release their slots so the queued item is picked up");
        }
        finally
        {
            pipeline.ReleaseAll();
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(WorkItemState.WorkComplete, true)]
    [InlineData(WorkItemState.Working, true)]
    [InlineData(WorkItemState.Auditing, true)]
    [InlineData(WorkItemState.PlanApproved, true)]
    [InlineData(WorkItemState.Queued, false)]
    [InlineData(WorkItemState.Done, false)]
    [InlineData(WorkItemState.Failed, false)]
    [InlineData(WorkItemState.NeedsOperatorInput, false)]
    public void IsStaleWorkerRetryEligible_GatesOnWatchedStateAndStaleTimeout(
        WorkItemState state, bool watched)
    {
        var now = DateTimeOffset.UtcNow;
        var stale = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            Title = "t",
            Prompt = "p",
            State = state,
            UpdatedAt = now.AddHours(-2),
        };
        var fresh = stale with { UpdatedAt = now.AddMinutes(-1) };

        Assert.Equal(watched, WorkItemEndpoints.IsStaleWorkerRetryEligible(stale, now, TimeSpan.FromMinutes(75)));
        Assert.False(WorkItemEndpoints.IsStaleWorkerRetryEligible(fresh, now, TimeSpan.FromMinutes(75)));
        Assert.False(WorkItemEndpoints.IsStaleWorkerRetryEligible(stale, now, TimeSpan.Zero));
    }

    [Fact]
    public async Task Retry_StaleWorkerHeldItem_FencesAndRetries()
    {
        using var factory = new WorkItemApiFactory();
        var client = factory.CreateClient();
        try
        {
            // Freeze the item past the default item-stale window: the retry
            // fence only admits items whose UpdatedAt has not advanced inside
            // WorkerProgressWatchdogOptions.ItemStaleTimeout, so the fixture
            // is derived from that default (plus margin) instead of a
            // hardcoded age that rots when the default moves.
            var staleWindow = new WorkerProgressWatchdogOptions().ItemStaleTimeout;
            var frozenAt = DateTimeOffset.UtcNow - staleWindow - TimeSpan.FromMinutes(30);
            var item = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("test-project"),
                Title = "wedged work",
                Prompt = "p",
                State = WorkItemState.Working,
                StartedAt = frozenAt,
                UpdatedAt = frozenAt,
            };
            await factory.Store.CreateAsync(item);

            var registry = factory.Services.GetRequiredService<IWorkerRegistry>();
            await registry.RegisterAsync(new WorkerRegistration
            {
                WorkerId = "wedged-http-worker",
                HostName = "host",
                ProcessId = 4242,
                StartedAt = frozenAt,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
                CurrentWorkItemId = item.Id.ToString(),
            });

            var resp = await client.PostAsJsonAsync($"/workitems/{item.Id}/retry", new { from = "work" });
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            var after = await factory.Store.GetAsync(item.Id);
            Assert.NotNull(after);
            Assert.Equal(WorkItemState.Queued, after!.State);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task Retry_FreshWorkerHeldItem_IsStillRejected()
    {
        using var factory = new WorkItemApiFactory();
        var client = factory.CreateClient();
        try
        {
            var item = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("test-project"),
                Title = "live work",
                Prompt = "p",
                State = WorkItemState.Working,
                StartedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await factory.Store.CreateAsync(item);

            var registry = factory.Services.GetRequiredService<IWorkerRegistry>();
            await registry.RegisterAsync(new WorkerRegistration
            {
                WorkerId = "live-http-worker",
                HostName = "host",
                ProcessId = 4243,
                StartedAt = DateTimeOffset.UtcNow,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
                CurrentWorkItemId = item.Id.ToString(),
            });

            var resp = await client.PostAsJsonAsync($"/workitems/{item.Id}/retry", new { from = "work" });
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

            var after = await factory.Store.GetAsync(item.Id);
            Assert.NotNull(after);
            Assert.Equal(WorkItemState.Working, after!.State);
            Assert.Equal(0, after.RecoveryAttempts);
        }
        finally
        {
            client.Dispose();
        }
    }

    private static WorkItem NewQueuedItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
    };

    private sealed class RecordingSlotReleaser : IWorkerPoolRecoverySlotReleaser
    {
        public List<(string WorkerId, WorkItemId? WorkItemId, string Reason)> Releases { get; } = [];

        public ValueTask<bool> TryReleaseRecoveredWorkerSlotAsync(
            string workerId,
            WorkItemId? workItemId,
            string reason,
            CancellationToken ct = default)
        {
            Releases.Add((workerId, workItemId, reason));
            return ValueTask.FromResult(true);
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class WedgedPipeline : IPipelineRunner
    {
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _entered = new();
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _released = new();

        public Task<bool> WaitForEnteredAsync(WorkItemId id, TimeSpan timeout) =>
            WaitForSignalAsync(_entered.GetOrAdd(id, static _ => NewSignal()), timeout);

        public void ReleaseAll()
        {
            foreach (var pair in _released)
                pair.Value.TrySetResult();
        }

        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            _entered.GetOrAdd(item.Id, static _ => NewSignal()).TrySetResult();
            return _released.GetOrAdd(item.Id, static _ => NewSignal()).Task.WaitAsync(ct);
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
