using CodeyBox.Core;
using CodeyBox.Orchestrator;
using System.Diagnostics;

namespace CodeyBox.Tests;

public sealed class OrchestratorServiceProgressClockTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-orch-clock-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public OrchestratorServiceProgressClockTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
    };

    [Fact]
    public async Task WorkerCompletion_StampsProgressClock()
    {
        var item = NewItem();
        await _store.CreateAsync(item);

        var clock = new OrchestratorProgressClock();
        var queue = new InMemoryTaskQueue();
        await queue.EnqueueAsync(item.Id);

        using var service = new OrchestratorService(
            queue,
            _store,
            new NoOpPipelineRunner(),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorService>.Instance,
            progressClock: clock);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(CancellationToken.None);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10_000 && clock.LastTransition == DateTimeOffset.MinValue)
            await Task.Delay(50);

        Assert.True(clock.LastTransition > DateTimeOffset.MinValue,
            "Progress clock should have been stamped after work item completion");

        await service.StopAsync(stopCts.Token);
    }

    private sealed class NoOpPipelineRunner : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
            => Task.CompletedTask;
    }
}
