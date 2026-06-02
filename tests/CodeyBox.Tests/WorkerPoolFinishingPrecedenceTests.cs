using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class WorkerPoolFinishingPrecedenceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-finishing-precedence-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public WorkerPoolFinishingPrecedenceTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task PickNextEligible_SkipsWaitingForQuotaResetRows()
    {
        var parked = Item(WorkItemState.WaitingForQuotaReset, priority: 100);
        var ready = Item(WorkItemState.Queued, priority: 0);
        await _store.CreateAsync(parked);
        await _store.CreateAsync(ready);

        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            new InMemoryTaskQueue(), _store, new FinishingPrecedencePipeline(_store),
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);

        Assert.Equal(ready.Id, picked);
    }

    [Theory]
    [InlineData(WorkItemState.AuditPassed, WorkItemState.Merging)]
    [InlineData(WorkItemState.Merging, WorkItemState.Merging)]
    [InlineData(WorkItemState.Merged, WorkItemState.UpstreamPushing)]
    [InlineData(WorkItemState.UpstreamPushing, WorkItemState.UpstreamPushing)]
    public async Task FullPool_DispatchesFinishingPhaseBeforeHigherPriorityQueuedItem(
        WorkItemState finishingState,
        WorkItemState expectedActiveState)
    {
        var queue = new InMemoryTaskQueue();
        var pipeline = new FinishingPrecedencePipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance);

        await svc.StartAsync(CancellationToken.None);

        var reworkA = Item(WorkItemState.Reworking);
        var reworkB = Item(WorkItemState.Reworking);
        await _store.CreateAsync(reworkA);
        await _store.CreateAsync(reworkB);
        await queue.EnqueueAsync(reworkA.Id);
        await queue.EnqueueAsync(reworkB.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(reworkA.Id, TimeSpan.FromSeconds(5)));
        Assert.True(await pipeline.WaitForEnteredAsync(reworkB.Id, TimeSpan.FromSeconds(5)));

        var highPriorityQueued = Item(WorkItemState.Queued, priority: 100);
        var finishing = Item(finishingState, priority: 0);
        await _store.CreateAsync(highPriorityQueued);
        await _store.CreateAsync(finishing);

        // Queue the fresh high-priority work first. With the pool full, the
        // dispatcher can consume this kick and wait on the gate; when a slot
        // frees, the DB phase precedence must still choose the finishing item.
        await queue.EnqueueAsync(highPriorityQueued.Id);
        await queue.EnqueueAsync(finishing.Id);

        pipeline.Release(reworkA.Id);

        Assert.True(await pipeline.WaitForStateAsync(finishing.Id, expectedActiveState, TimeSpan.FromSeconds(5)));
        Assert.False(pipeline.HasEntered(highPriorityQueued.Id));
        var thirdEntered = pipeline.ThirdEntered;
        Assert.True(thirdEntered.HasValue);
        Assert.Equal(finishing.Id, thirdEntered.Value);

        pipeline.Release(finishing.Id);
        Assert.True(await pipeline.WaitForDoneAsync(finishing.Id, TimeSpan.FromSeconds(5)));

        pipeline.Release(reworkB.Id);
        pipeline.Release(highPriorityQueued.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FullPool_Ms95ProjectDefaultClassRoutesAuditPassedToIdleClaudeBeforeQueuedBacklog()
    {
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("p"),
            DisplayName = "p",
            RepositoryUrl = "https://github.com/test/repo",
            DefaultAgentClass = "frontier",
        });

        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Gemini, Billing = AgentBilling.Subscription, QualityScore = 95 },
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 95 },
            ],
        };
        var lowTier = new AgentClass
        {
            Id = "low-tier",
            DisplayName = "Low tier",
            Members =
            [
                new AgentMembership { Agent = AgentKind.Cursor, Billing = AgentBilling.Subscription, QualityScore = 80 },
                new AgentMembership { Agent = AgentKind.Opencode, Billing = AgentBilling.Subscription, QualityScore = 80 },
            ],
        };

        var router = new AgentClassRouter(
            [frontier, lowTier],
            [
                new FakeProbe(AgentKind.Codex, 0.0),
                new FakeProbe(AgentKind.Gemini, 0.0),
                new FakeProbe(AgentKind.Claude, new AgentQuotaSnapshot
                {
                    AvailablePct = 68.0,
                    Windows = [new WindowQuota { Name = "five_hour", AvailablePct = 94.0 }],
                }),
                new FakeProbe(AgentKind.Cursor, 90.0),
                new FakeProbe(AgentKind.Opencode, 90.0),
            ],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance);

        var concurrency = new AgentConcurrencyOptions
        {
            Members =
            {
                ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 1 },
                ["cursor"] = new AgentConcurrencyEntry { MaxConcurrent = 2 },
                ["opencode"] = new AgentConcurrencyEntry { MaxConcurrent = 2 },
            },
        };

        var queue = new InMemoryTaskQueue();
        var pipeline = new FinishingPrecedencePipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            projects: projectRepo,
            agentConcurrency: concurrency);

        var reworks = Enumerable.Range(0, 4)
            .Select(_ => Item(WorkItemState.Reworking) with { AgentClassId = "low-tier" })
            .ToList();
        foreach (var rework in reworks)
        {
            await _store.CreateAsync(rework);
            await queue.EnqueueAsync(rework.Id);
        }

        await svc.StartAsync(CancellationToken.None);

        foreach (var rework in reworks)
            Assert.True(await pipeline.WaitForEnteredAsync(rework.Id, TimeSpan.FromSeconds(5)));

        Assert.Equal(0, svc.Snapshot().GetValueOrDefault(AgentKind.Claude));

        var highPriorityQueued = Item(WorkItemState.Queued, priority: 100) with { MinModelScore = 95 };
        var auditPassed = Item(WorkItemState.AuditPassed, priority: 0) with { MinModelScore = 95 };
        await _store.CreateAsync(highPriorityQueued);
        await _store.CreateAsync(auditPassed);

        // Queue the high-priority starting work first. When a slot frees, the
        // DB phase bucket must still choose the already-audited item, and the
        // project default class must route that ms>=95 item to idle Claude while
        // Codex/Gemini are quota-exhausted.
        await queue.EnqueueAsync(highPriorityQueued.Id);
        await queue.EnqueueAsync(auditPassed.Id);

        pipeline.Release(reworks[0].Id);

        Assert.True(await pipeline.WaitForStateAsync(auditPassed.Id, WorkItemState.Merging, TimeSpan.FromSeconds(5)));
        Assert.False(pipeline.HasEntered(highPriorityQueued.Id));
        Assert.Equal(auditPassed.Id, pipeline.NthEntered(5));
        Assert.Equal(1, svc.Snapshot().GetValueOrDefault(AgentKind.Claude));

        var stored = await _store.GetAsync(auditPassed.Id);
        Assert.Equal(AgentKind.Claude, stored?.Agent);

        pipeline.Release(highPriorityQueued.Id);
        pipeline.Release(auditPassed.Id);
        foreach (var rework in reworks.Skip(1))
            pipeline.Release(rework.Id);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DispatchEligible_FinishingBucketOrdersByPriorityThenCreatedAt()
    {
        var now = DateTimeOffset.UtcNow;
        var queued = Item(WorkItemState.Queued, priority: 100) with { CreatedAt = now.AddSeconds(-10) };
        var lowPriorityFinishing = Item(WorkItemState.AuditPassed, priority: 0) with { CreatedAt = now.AddSeconds(-5) };
        var samePriorityNewer = Item(WorkItemState.Merging, priority: 50) with { CreatedAt = now.AddSeconds(2) };
        var samePriorityOlder = Item(WorkItemState.Merged, priority: 50) with { CreatedAt = now.AddSeconds(1) };
        var midPriorityFinishing = Item(WorkItemState.UpstreamPushing, priority: 25) with { CreatedAt = now };

        foreach (var item in new[] { queued, lowPriorityFinishing, samePriorityNewer, samePriorityOlder, midPriorityFinishing })
            await _store.CreateAsync(item);

        var ordered = new List<WorkItemId>();
        await foreach (var item in _store.ListDispatchEligibleByPriorityAsync(new HashSet<WorkItemId>()))
            ordered.Add(item.Id);

        Assert.Equal(
            [samePriorityOlder.Id, samePriorityNewer.Id, midPriorityFinishing.Id, lowPriorityFinishing.Id, queued.Id],
            ordered);
    }

    private static WorkItem Item(WorkItemState state, int priority = 0) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("p"),
        Title = "t",
        Prompt = "p",
        State = state,
        Priority = priority,
        PushUpstream = false,
    };

    private sealed class FinishingPrecedencePipeline : IPipelineRunner
    {
        private readonly IWorkItemStore _store;
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _entered = new();
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _released = new();
        private readonly ConcurrentDictionary<(WorkItemId, WorkItemState), TaskCompletionSource> _stateReached = new();
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _done = new();
        private readonly ConcurrentQueue<WorkItemId> _entryOrder = new();

        public FinishingPrecedencePipeline(IWorkItemStore store) => _store = store;

        public WorkItemId? ThirdEntered
            => NthEntered(3);

        public WorkItemId? NthEntered(int n)
        {
            var entries = _entryOrder.ToArray();
            return entries.Length >= n ? entries[n - 1] : null;
        }

        public bool HasEntered(WorkItemId id) => _entered.ContainsKey(id);

        public void Release(WorkItemId id) =>
            _released.GetOrAdd(id, static _ => NewSignal()).TrySetResult();

        public Task<bool> WaitForEnteredAsync(WorkItemId id, TimeSpan timeout) =>
            WaitForSignalAsync(_entered.GetOrAdd(id, static _ => NewSignal()), timeout);

        public Task<bool> WaitForStateAsync(WorkItemId id, WorkItemState state, TimeSpan timeout) =>
            WaitForSignalAsync(_stateReached.GetOrAdd((id, state), static _ => NewSignal()), timeout);

        public Task<bool> WaitForDoneAsync(WorkItemId id, TimeSpan timeout) =>
            WaitForSignalAsync(_done.GetOrAdd(id, static _ => NewSignal()), timeout);

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            _entryOrder.Enqueue(item.Id);
            _entered.GetOrAdd(item.Id, static _ => NewSignal()).TrySetResult();

            if (ActiveFinishingState(item.State) is { } activeState)
            {
                await _store.UpdateAsync(item.With(activeState), ct);
                _stateReached.GetOrAdd((item.Id, activeState), static _ => NewSignal()).TrySetResult();
                await _released.GetOrAdd(item.Id, static _ => NewSignal()).Task.WaitAsync(ct);
                await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
                _done.GetOrAdd(item.Id, static _ => NewSignal()).TrySetResult();
                return;
            }

            if (item.State == WorkItemState.Queued)
                await _store.UpdateAsync(item.With(WorkItemState.Working), ct);

            await _released.GetOrAdd(item.Id, static _ => NewSignal()).Task.WaitAsync(ct);
            await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
            _done.GetOrAdd(item.Id, static _ => NewSignal()).TrySetResult();
        }

        private static WorkItemState? ActiveFinishingState(WorkItemState state) => state switch
        {
            WorkItemState.AuditPassed or WorkItemState.Merging => WorkItemState.Merging,
            WorkItemState.Merged or WorkItemState.UpstreamPushing => WorkItemState.UpstreamPushing,
            _ => null,
        };

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task<bool> WaitForSignalAsync(TaskCompletionSource signal, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(signal.Task, Task.Delay(timeout));
            return completed == signal.Task;
        }
    }
}
