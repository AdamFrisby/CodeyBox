using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that when a work item reaches a terminal state the orchestrator
/// deregisters its worker row from the registry, so dead-worker detection
/// does not trigger for cleanly-completed items.
/// </summary>
[Collection("Pipeline integration")]
public sealed class TerminalClearsWorkerLinkTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-terminal-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;

    public TerminalClearsWorkerLinkTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        _registry = new SqliteWorkerRegistry(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task<WorkItem> RunToTerminalAsync(WorkItemState terminalState)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        await queue.EnqueueAsync(item.Id);

        var pipeline = new TerminalStatePipeline(_store, terminalState);
        var deadWorkerOpts = new DeadWorkerOptions { HeartbeatInterval = TimeSpan.FromSeconds(30) };
        var reaper = new DeadWorkerReaper(
            _registry, _store, queue, deadWorkerOpts,
            NullLogger<DeadWorkerReaper>.Instance);

        var cancellations = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        using var svc = new OrchestratorService(
            queue, _store, pipeline, cancellations, opts,
            NullLogger<OrchestratorService>.Instance,
            workerRegistry: _registry,
            deadWorkerOpts: deadWorkerOpts,
            reaper: reaper);

        await svc.StartAsync(CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        WorkItem? final = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            final = await _store.GetAsync(item.Id);
            if (final?.State == terminalState) break;
            await Task.Delay(20);
        }

        await svc.StopAsync(CancellationToken.None);
        return final ?? item;
    }

    [Theory]
    [InlineData(WorkItemState.Done)]
    [InlineData(WorkItemState.Failed)]
    public async Task TerminalTransition_ClearsWorkerRegistryRow(WorkItemState terminal)
    {
        await RunToTerminalAsync(terminal);

        // Registry should be empty — worker row was deleted on clean finish.
        var remaining = await _registry.ListAsync();
        Assert.Empty(remaining);
    }
}

/// <summary>Pipeline that sets a work item to an arbitrary terminal state.</summary>
internal sealed class TerminalStatePipeline : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    private readonly WorkItemState _terminalState;

    public TerminalStatePipeline(IWorkItemStore store, WorkItemState terminalState)
    {
        _store = store;
        _terminalState = terminalState;
    }

    public async Task RunAsync(WorkItem item, CancellationToken ct)
        => await _store.UpdateAsync(item.With(_terminalState), ct);
}
