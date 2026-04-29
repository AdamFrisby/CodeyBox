using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that an OrchestratorOptions built via the legacy Concurrency
/// property (the backward-compat alias) still produces a working pool that
/// respects the configured max concurrency.
/// </summary>
public sealed class WorkerPoolLegacyConcurrencyKeyTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-legacytest-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public WorkerPoolLegacyConcurrencyKeyTests() => _store = new SqliteWorkItemStore(_dbPath);

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
#pragma warning disable CS0618 // intentionally testing the obsolete alias
    public async Task LegacyConcurrencyProperty_PoolFunctions()
    {
        // Simulate what the legacy DI factory did: set Concurrency via the
        // deprecated alias. MaxConcurrentWorkers should honour the value.
        var opts = new OrchestratorOptions { Concurrency = 2 };
#pragma warning restore CS0618

        Assert.Equal(2, opts.MaxConcurrentWorkers);

        const int itemCount = 4;
        var executed = new ConcurrentBag<WorkItemId>();

        var pipeline = new RecordingPipelineRunner(_store, executed);
        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(queue, _store, pipeline, registry, opts,
            NullLogger<OrchestratorService>.Instance);

        for (int i = 0; i < itemCount; i++)
        {
            var item = MakeItem();
            await _store.CreateAsync(item);
            await queue.EnqueueAsync(item.Id);
        }

        using var _ = registry;
        await svc.StartAsync(CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            int doneCount = 0;
            await foreach (var item in _store.ListByStateAsync(WorkItemState.Done))
                doneCount++;
            if (doneCount >= itemCount) break;
            await Task.Delay(50);
        }

        await svc.StopAsync(CancellationToken.None);

        // All items ran.
        Assert.Equal(itemCount, executed.Count);

        // Concurrency was respected (checked via WorkerPoolConcurrencyTests;
        // here we just verify the pool ran at all — the alias worked).
        int stillQueued = 0;
        await foreach (var item in _store.ListByStateAsync(WorkItemState.Queued))
            stillQueued++;
        Assert.Equal(0, stillQueued);
    }
}

internal sealed class RecordingPipelineRunner : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    private readonly ConcurrentBag<WorkItemId> _executed;

    public RecordingPipelineRunner(IWorkItemStore store, ConcurrentBag<WorkItemId> executed)
    {
        _store = store;
        _executed = executed;
    }

    public async Task RunAsync(WorkItem item, CancellationToken ct)
    {
        _executed.Add(item.Id);
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }
}
