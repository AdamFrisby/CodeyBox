using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the SQLite write-gate outage resilience contract:
/// a gate-acquisition timeout in the dispatch pickup path is absorbed
/// (Warning + backoff + next iteration) instead of faulting the host,
/// while a sustained outage escalates fatally instead of looping silently.
/// All fault injection goes through a real <see cref="SqliteWorkItemStore"/>
/// behind a decorating <see cref="IWorkItemStore"/> — no mocks.
/// </summary>
[Collection("Background service timing")]
public sealed class SqliteGateOutageResilienceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-gate-outage-{Guid.NewGuid():N}.db");
    private readonly string _regPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-gate-outage-reg-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _regPath, _dbPath + "-wal", _dbPath + "-shm", _regPath + "-wal", _regPath + "-shm" })
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static WorkItem MakeItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    // ── T1: single pickup timeout does not escape ExecuteAsync ───────────────

    [Fact]
    public async Task PickupGateTimeout_DoesNotEscapeExecuteAsync_AndDispatchContinues()
    {
        using var inner = new SqliteWorkItemStore(_dbPath);
        var flaky = new GateTimeoutInjectingStore(inner, failuresBeforeSuccess: 1);
        var log = new CapturingLogger<OrchestratorService>();
        var queue = new InMemoryTaskQueue();
        var pipeline = new OrderedPipelineRunner(flaky);
        var opts = new OrchestratorOptions
        {
            MaxConcurrentWorkers = 1,
            DispatchGateAcquisitionBackoff = TimeSpan.FromMilliseconds(100),
            MaxConsecutiveDispatchGateTimeoutsBeforeEscalation = 10,
        };
        using var svc = new OrchestratorService(
            queue, flaky, pipeline, new CancellationRegistry(CancellationToken.None), opts, log);

        var item = MakeItem();
        await inner.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(
                await WaitForLogAsync(log, LogLevel.Warning, "could not acquire the SQLite write gate", TimeSpan.FromSeconds(15)),
                "Expected the injected gate timeout to be absorbed with a Warning, not propagated.");

            // The first kick was consumed by the timed-out pickup; wake again
            // so the loop demonstrates dispatch on a following iteration.
            await queue.EnqueueDispatchWakeAsync(CancellationToken.None);

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            WorkItem? current = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                current = await inner.GetAsync(item.Id);
                if (current?.State == WorkItemState.Done) break;
                await Task.Delay(100);
            }
            Assert.Equal(WorkItemState.Done, current?.State);
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    // ── T2: sustained timeouts escalate ──────────────────────────────────────

    [Fact]
    public async Task SustainedPickupGateTimeouts_EscalateFatally()
    {
        using var inner = new SqliteWorkItemStore(_dbPath);
        var flaky = new GateTimeoutInjectingStore(inner, failuresBeforeSuccess: int.MaxValue);
        var log = new CapturingLogger<OrchestratorService>();
        var opts = new OrchestratorOptions
        {
            MaxConcurrentWorkers = 1,
            DispatchGateAcquisitionBackoff = TimeSpan.Zero,
            MaxConsecutiveDispatchGateTimeoutsBeforeEscalation = 3,
        };
        using var svc = new OrchestratorService(
            new InMemoryTaskQueue(), flaky,
            new OrderedPipelineRunner(flaky),
            new CancellationRegistry(CancellationToken.None), opts, log);

        var item = MakeItem();
        await inner.CreateAsync(item);

        Assert.Null(await svc.PickNextEligibleResilientAsync(CancellationToken.None));
        Assert.Null(await svc.PickNextEligibleResilientAsync(CancellationToken.None));
        var escalation = await Assert.ThrowsAsync<SqliteWriteGatePersistentlyUnavailableException>(
            () => svc.PickNextEligibleResilientAsync(CancellationToken.None));
        Assert.Equal(3, escalation.ConsecutiveTimeouts);
        Assert.Contains("gate holder (test)", escalation.Message);

        Assert.Equal(3, log.Count(LogLevel.Warning, "could not acquire the SQLite write gate"));
        Assert.Equal(1, log.Count(LogLevel.Critical, "consecutive times"));
    }

    // ── T3: reads complete while a writer holds the gate ────────────────────

    [Fact]
    public async Task Reads_CompleteWhileWriterHoldsWriteGate()
    {
        var factory = new SqliteDatabaseWriteGateFactory(
            static () => new SqliteWriteGateOptions
            {
                AcquisitionTimeout = TimeSpan.FromSeconds(5),
                MaxHoldDuration = TimeSpan.FromSeconds(30),
            },
            NullLoggerFactory.Instance);
        using var store = new SqliteWorkItemStore(_dbPath, writeGateFactory: factory);
        var item = MakeItem();
        await store.CreateAsync(item);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() =>
        {
            using var _ = store.AcquireConnectionGateForTesting();
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert.Equal(1, await store.CountByStateAsync(WorkItemState.Queued, cts.Token));
            Assert.NotNull(await store.GetAsync(item.Id, cts.Token));

            var eligible = new List<WorkItem>();
            await foreach (var w in store.ListDispatchEligibleByPriorityAsync(
                new HashSet<WorkItemId>(), cts.Token))
                eligible.Add(w);
            Assert.Contains(eligible, w => w.Id == item.Id);

            var eligibleWithQuota = new List<WorkItem>();
            await foreach (var w in store.ListDispatchEligibleIncludingDueQuotaRetryByPriorityAsync(
                new HashSet<WorkItemId>(), DateTimeOffset.UtcNow, 10, ct: cts.Token))
                eligibleWithQuota.Add(w);
            Assert.Contains(eligibleWithQuota, w => w.Id == item.Id);

            var (refactor, other) = await store.CountInFlightSplitByRefactorAsync(
                item.ProjectId, cts.Token, item.Id);
            Assert.Equal((0, 0), (refactor, other));
        }
        finally
        {
            release.SetResult();
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task RegistryReads_CompleteWhileWriterHoldsWriteGate()
    {
        using var registry = new SqliteWorkerRegistry(_regPath);
        var now = DateTimeOffset.UtcNow;
        await registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = "w-1",
            HostName = "host",
            ProcessId = 123,
            StartedAt = now,
            LastHeartbeatAt = now,
        });

        var factory = new SqliteDatabaseWriteGateFactory(
            static () => new SqliteWriteGateOptions
            {
                AcquisitionTimeout = TimeSpan.FromSeconds(5),
                MaxHoldDuration = TimeSpan.FromSeconds(30),
            },
            NullLoggerFactory.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() =>
        {
            using var gate = factory.ForPath(_regPath);
            gate.Wait();
            try
            {
                entered.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
            finally
            {
                gate.Release();
            }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var listed = await registry.ListAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Single(listed);
        }
        finally
        {
            release.SetResult();
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    // ── T4: exit code distinguishes fault from intentional stop ──────────────

    [Fact]
    public void ExitCode_DiffersBetweenIntentionalShutdownAndBackgroundServiceFault()
    {
        Assert.Equal(0, BackgroundServiceFailureTracker.ResolveExitCode(backgroundServiceFaulted: false));
        var faultExit = BackgroundServiceFailureTracker.ResolveExitCode(backgroundServiceFaulted: true);
        Assert.NotEqual(BackgroundServiceFailureTracker.IntentionalShutdownExitCode, faultExit);
        Assert.NotEqual(0, faultExit);

        var tracker = new BackgroundServiceFailureTracker();
        Assert.Equal(0, tracker.ResolveExitCode());

        var first = new InvalidOperationException("boom");
        tracker.ReportFailure(first);
        Assert.Equal(faultExit, tracker.ResolveExitCode());

        tracker.ReportFailure(new InvalidOperationException("second"));
        Assert.Same(first, tracker.Fault);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<bool> WaitForLogAsync<T>(
        CapturingLogger<T> log, LogLevel level, string substring, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (log.Count(level, substring) > 0) return true;
            await Task.Delay(50);
        }
        return false;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public int Count(LogLevel level, string substring) =>
            _entries.Count(e => e.Level == level && e.Message.Contains(substring, StringComparison.Ordinal));

        IDisposable ILogger.BeginScope<TState>(TState state) => null!;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// <see cref="IWorkItemStore"/> decorator over a real
    /// <see cref="SqliteWorkItemStore"/> that throws a genuine
    /// <see cref="SqliteWriteGateAcquisitionTimeoutException"/> from the
    /// dispatch-list query a fixed number of times before delegating.
    /// </summary>
    private sealed class GateTimeoutInjectingStore(IWorkItemStore inner, int failuresBeforeSuccess)
        : IWorkItemStore
    {
        private int _remaining = failuresBeforeSuccess;

        public async IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(
            IReadOnlySet<WorkItemId> skipIds,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (Interlocked.Decrement(ref _remaining) >= 0)
                throw new SqliteWriteGateAcquisitionTimeoutException(
                    "dispatch pickup (test)", "gate holder (test)", TimeSpan.FromMilliseconds(1));
            await foreach (var item in inner.ListDispatchEligibleByPriorityAsync(skipIds, ct))
                yield return item;
        }

        public Task CreateAsync(WorkItem item, CancellationToken ct = default) => inner.CreateAsync(item, ct);
        public Task UpdateAsync(WorkItem item, CancellationToken ct = default) => inner.UpdateAsync(item, ct);
        public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) =>
            inner.TryUpdateIfStateAsync(item, onlyIfState, ct);
        public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.UpdatePriorityAsync(id, priority, updatedAt, ct);
        public Task<DependsOnUpdateResult> UpdateDependsOnAsync(WorkItemId id, IReadOnlyList<WorkItemId> dependsOn, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.UpdateDependsOnAsync(id, dependsOn, updatedAt, ct);
        public Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(WorkItemId id, int? auditMaxIterations, string? auditComplexity, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.UpdateAuditBudgetAsync(id, auditMaxIterations, auditComplexity, updatedAt, ct);
        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => inner.ListByStateAsync(state, ct);
        public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => inner.CountByStateAsync(state, ct);
        public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => inner.ReorderAsync(orderedIds, ct);
        public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) =>
            inner.CountStartedInWindowAsync(projectId, since, ct);
        public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => inner.CountInFlightAsync(projectId, ct);
        public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) =>
            inner.GetByExternalIdAsync(projectId, externalId, ct);
        public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) =>
            inner.GetByNamespacedExternalIdAsync(projectId, @namespace, externalId, ct);
        public Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.ReplaceExternalIdsAsync(id, externalIds, updatedAt, ct);
        public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) =>
            inner.GetFleetStateCountsAsync(ct);
        public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) =>
            inner.GetFleetRecentOutcomesAsync(perProject, ct);
        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) => inner.GetFleetPauseStatesAsync(ct);
        public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) =>
            inner.ListByReplaySourceAsync(sourceId, ct);
        public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => inner.ListSuspendedAsync(ct);
        public Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) => inner.GetActiveBaselineImageRefsAsync(ct);
        public Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) =>
            inner.ListWorkItemsForBaselineAsync(baselineImageRef, ct);
        public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => inner.OrphanReplaysAsync(sourceId, ct);
        public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) =>
            inner.ListByReleaseAsync(releaseId, ct);
        public Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.TryReplacePromptAsync(id, newPrompt, updatedAt, ct);
        public Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) =>
            inner.RecordIterationDispatchAsync(workItemId, iteration, promptRevisionAtDispatch, dispatchedAt, ct);
        public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) =>
            inner.GetIterationsAsync(workItemId, ct);
    }
}
