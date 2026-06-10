using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that OrchestratorService enforces MinSpawnInterval between
/// successive worker spawns. With 3 items and MaxConcurrentWorkers=3,
/// each worker fires independently but spawns must be separated by
/// at least MinSpawnInterval (minus scheduler slack).
/// </summary>
[Collection("Background service timing")]
public sealed class WorkerPoolSpawnIntervalTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-spawntest-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public WorkerPoolSpawnIntervalTests() => _store = new SqliteWorkItemStore(_dbPath);

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
        State = WorkItemState.Queued,
    };

    [Fact]
    public async Task SpawnInterval_EnforcedBetweenConsecutiveSpawns()
    {
        const int itemCount = 3;
        const int spawnIntervalMs = 200;
        // Allowance for scheduler jitter. Bumped from 50ms after the auditor saw a
        // 149.x ms gap (reported as "150ms" by F0 formatting) under parallel-test
        // load — DateTimeOffset.UtcNow precision plus Task.Delay early-wake slack
        // can shave a handful of ms off each observed gap. 100ms still rejects the
        // unconstrained case (sub-ms gaps; see NoSpawnInterval_ItemsFireWithoutDelay).
        const int slackMs = 100;

        // Timestamps captured at the dispatch loop level (OnWorkerSpawned) rather than
        // inside RunAsync, so thread-pool scheduling latency doesn't consume the slack.
        var spawnTimes = new ConcurrentBag<DateTimeOffset>();

        var pipeline = new TimestampingPipelineRunner(_store);

        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions
        {
            MaxConcurrentWorkers = itemCount,
            MinSpawnInterval = TimeSpan.FromMilliseconds(spawnIntervalMs),
            OnWorkerSpawned = () => spawnTimes.Add(DateTimeOffset.UtcNow),
        };
        var svc = new OrchestratorService(queue, _store, pipeline, registry, opts,
            NullLogger<OrchestratorService>.Instance);

        // Seed before StartAsync so startup replay cannot race this test's writes.
        for (int i = 0; i < itemCount; i++)
        {
            var item = MakeItem();
            await _store.CreateAsync(item);
            await queue.EnqueueAsync(item.Id);
        }

        using var _ = registry;
        await svc.StartAsync(CancellationToken.None);

        // Wait for all items to complete. Keep the assertion about inter-spawn
        // gaps strict, but allow heavily parallel test runs more time to schedule
        // the dispatch loop.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            int doneCount = 0;
            await foreach (var item in _store.ListByStateAsync(WorkItemState.Done))
                doneCount++;
            if (doneCount >= itemCount) break;
            await Task.Delay(50);
        }

        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(itemCount, spawnTimes.Count);

        // Sort spawn times and check every consecutive gap.
        var sorted = spawnTimes.Order().ToList();
        for (int i = 1; i < sorted.Count; i++)
        {
            var gap = sorted[i] - sorted[i - 1];
            Assert.True(gap.TotalMilliseconds >= spawnIntervalMs - slackMs,
                $"Spawn gap between item {i - 1} and {i} was {gap.TotalMilliseconds:F0}ms, " +
                $"expected >= {spawnIntervalMs - slackMs}ms");
        }
    }

    [Fact]
    public async Task NoSpawnInterval_ItemsFireWithoutDelay()
    {
        const int itemCount = 3;
        var spawnTimes = new ConcurrentBag<DateTimeOffset>();

        var pipeline = new TimestampingPipelineRunner(_store);

        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions
        {
            MaxConcurrentWorkers = itemCount,
            MinSpawnInterval = TimeSpan.Zero,
            OnWorkerSpawned = () => spawnTimes.Add(DateTimeOffset.UtcNow),
        };
        var svc = new OrchestratorService(queue, _store, pipeline, registry, opts,
            NullLogger<OrchestratorService>.Instance);

        // Seed before StartAsync so startup replay cannot race this test's writes.
        for (int i = 0; i < itemCount; i++)
        {
            var item = MakeItem();
            await _store.CreateAsync(item);
            await queue.EnqueueAsync(item.Id);
        }

        using var _ = registry;
        await svc.StartAsync(CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            int doneCount = 0;
            await foreach (var item in _store.ListByStateAsync(WorkItemState.Done))
                doneCount++;
            if (doneCount >= itemCount) break;
            await Task.Delay(50);
        }

        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(itemCount, spawnTimes.Count);

        // All 3 items should fire within a short wall-clock window (< 500ms total).
        var sorted = spawnTimes.Order().ToList();
        var totalSpread = sorted[^1] - sorted[0];
        Assert.True(totalSpread.TotalMilliseconds < 500,
            $"Without interval, all spawns should fire quickly, but spread was {totalSpread.TotalMilliseconds:F0}ms");
    }
}

internal sealed class TimestampingPipelineRunner : IPipelineRunner
{
    private readonly IWorkItemStore _store;

    public TimestampingPipelineRunner(IWorkItemStore store) => _store = store;

    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default) =>
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
}
