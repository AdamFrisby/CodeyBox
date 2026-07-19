using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that OrchestratorService never exceeds MaxConcurrentWorkers
/// simultaneous in-flight items regardless of queue depth.
///
/// <para>Pinned to the "Background service timing" collection because the
/// assertions count in-flight pipelines under wall-clock deadlines — suite-
/// level threadpool contention from parallel fixtures was producing
/// "At least one item should have run" failures.</para>
/// </summary>
// Serialised with other BackgroundService timing-sensitive tests: the test gives
// the orchestrator 15s to dispatch and complete N items, which can miss under
// parallel CPU contention from other suites running alongside.
[Collection("Background service timing")]
public sealed class WorkerPoolConcurrencyTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-conctest-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public WorkerPoolConcurrencyTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
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
    public async Task AtMostMaxConcurrentWorkers_RunAtOnce()
    {
        const int maxConcurrent = 2;
        const int totalItems = 5;

        // Track the peak concurrency observed during the run.
        int current = 0;
        int peakConcurrent = 0;
        var peakLock = new object();

        var pipeline = new LatchedPipelineRunner(_store, onEnter: () =>
        {
            var c = Interlocked.Increment(ref current);
            lock (peakLock) { if (c > peakConcurrent) peakConcurrent = c; }
        }, onExit: () =>
        {
            Interlocked.Decrement(ref current);
        });

        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = maxConcurrent };
        var svc = new OrchestratorService(queue, _store, pipeline, registry, opts,
            NullLogger<OrchestratorService>.Instance);

        // Seed all items.
        for (int i = 0; i < totalItems; i++)
        {
            var item = MakeItem();
            await _store.CreateAsync(item);
            await queue.EnqueueAsync(item.Id);
        }

        using var _ = registry;
        await svc.StartAsync(CancellationToken.None);

        // Wait until all items reach Done (generous timeout for CI). 45 s, not 15 s:
        // under parallel audit-suite load the worker loop can be starved of a
        // thread-pool thread long enough that no item is even picked up inside 15 s
        // (peakConcurrent stays 0). The loop breaks the instant all items are Done,
        // so a healthy run still finishes fast — this only widens the headroom.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            int doneCount = 0;
            await foreach (var item in _store.ListByStateAsync(WorkItemState.Done))
                doneCount++;
            if (doneCount >= totalItems) break;
            await Task.Delay(50);
        }

        await svc.StopAsync(CancellationToken.None);

        Assert.True(peakConcurrent <= maxConcurrent,
            $"Peak concurrency {peakConcurrent} exceeded MaxConcurrentWorkers={maxConcurrent}");
        Assert.True(peakConcurrent >= 1, "At least one item should have run");
    }
}

/// <summary>
/// Pipeline that tracks enter/exit concurrency and marks items Done.
/// Uses a small artificial delay so concurrent execution is observable.
/// </summary>
internal sealed class LatchedPipelineRunner : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    private readonly Action _onEnter;
    private readonly Action _onExit;

    public LatchedPipelineRunner(IWorkItemStore store, Action onEnter, Action onExit)
    {
        _store = store;
        _onEnter = onEnter;
        _onExit = onExit;
    }

    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        _onEnter();
        try
        {
            await Task.Delay(80, ct); // long enough for multiple items to overlap
            await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
        }
        finally
        {
            _onExit();
        }
    }
}
