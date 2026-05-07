using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class OrchestratorHostShutdownTokenTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-host-shutdown-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public OrchestratorHostShutdownTokenTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task ServiceStop_CancelsHostTokenButNotPerItemOperatorToken()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await _store.CreateAsync(item);

        var pipeline = new HostShutdownObservingPipeline();
        var service = new OrchestratorService(
            new InMemoryTaskQueue(),
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await pipeline.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Assert.True(pipeline.HostShutdownWasCancelled);
        Assert.False(pipeline.ItemTokenWasCancelledWhenHostStopped);
    }

    private sealed class HostShutdownObservingPipeline : IPipelineRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HostShutdownWasCancelled { get; private set; }
        public bool ItemTokenWasCancelledWhenHostStopped { get; private set; }

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, hostShutdownToken);
            }
            catch (OperationCanceledException)
            {
                HostShutdownWasCancelled = hostShutdownToken.IsCancellationRequested;
                ItemTokenWasCancelledWhenHostStopped = ct.IsCancellationRequested;
                throw new OperationCanceledException(hostShutdownToken);
            }
        }
    }
}
