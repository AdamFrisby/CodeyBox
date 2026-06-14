using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

[Collection("Background service timing")]
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

        await pipeline.HostShutdownObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(pipeline.HostShutdownWasCancelled);
        Assert.False(pipeline.ItemTokenWasCancelledWhenHostStopped);
    }

    [Fact]
    public async Task ServiceStop_HostShutdownCancellation_RequeuesInFlightWorkInsteadOfFailingIt()
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

        var pipeline = new HostCancellationWorkingPipeline(_store);
        var service = new OrchestratorService(
            new InMemoryTaskQueue(),
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                ShutdownDrainTimeout = TimeSpan.FromSeconds(10),
            },
            NullLogger<OrchestratorService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await pipeline.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Assert.True(pipeline.HostShutdownWasCancelled);
        var after = Assert.IsType<WorkItem>(await _store.GetAsync(item.Id));
        Assert.Equal(WorkItemState.Queued, after.State);
        Assert.Null(after.StartedAt);
        Assert.Null(after.PreemptCheckpoint);
        Assert.Contains("host shutdown cancellation", after.LastError);
        service.Dispose();
    }

    [Fact]
    public async Task ServiceStop_HostShutdownCancellation_AtRecoveryCapAbandonsInsteadOfRequeueing()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
            RecoveryAttempts = 2,
        };
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var pipeline = new HostCancellationWorkingPipeline(_store);
        var service = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                MaxRecoveryAttempts = 2,
                ShutdownDrainTimeout = TimeSpan.FromSeconds(10),
            },
            NullLogger<OrchestratorService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await pipeline.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        var after = Assert.IsType<WorkItem>(await _store.GetAsync(item.Id));
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, after.State);
        Assert.Equal(3, after.RecoveryAttempts);
        Assert.Contains("MaxRecoveryAttempts", after.LastError);
        Assert.Equal(0, queue.Count);
        service.Dispose();
    }

    [Fact]
    public async Task ServiceStop_DrainTimeout_DoesNotRequeueWhileWorkerStillRunning()
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

        var pipeline = new ShutdownIgnoringWorkingPipeline(_store);
        var queue = new InMemoryTaskQueue();
        var service = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                ShutdownDrainTimeout = TimeSpan.FromMilliseconds(50),
            },
            NullLogger<OrchestratorService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await pipeline.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var elapsed = Stopwatch.StartNew();
        await service.StopAsync(stopCts.Token);
        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(10),
            $"StopAsync should return before the host stop token while a worker ignores shutdown; elapsed {elapsed.Elapsed}");
        // The worker continuation that flips HostShutdownWasCancelled runs on the
        // threadpool independently of StopAsync's drain wait, so under parallel
        // test load it may not have been scheduled by the time StopAsync returns.
        // Wait for the explicit observed signal rather than the derived bool.
        await pipeline.ShutdownObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(pipeline.HostShutdownWasCancelled);

        var after = Assert.IsType<WorkItem>(await _store.GetAsync(item.Id));
        Assert.Equal(WorkItemState.Working, after.State);
        Assert.NotNull(after.StartedAt);
        Assert.Null(after.PreemptCheckpoint);
        Assert.Null(after.LastError);
        Assert.Equal(0, queue.Count);

        pipeline.Release.SetResult();
        await pipeline.Exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var final = Assert.IsType<WorkItem>(await _store.GetAsync(item.Id));
        Assert.Equal(WorkItemState.Done, final.State);
        service.Dispose();
    }

    private sealed class HostShutdownObservingPipeline : IPipelineRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HostShutdownObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                HostShutdownObserved.SetResult();
                throw new OperationCanceledException(hostShutdownToken);
            }
        }
    }

    private sealed class HostCancellationWorkingPipeline : IPipelineRunner
    {
        private readonly IWorkItemStore _store;

        public HostCancellationWorkingPipeline(IWorkItemStore store) => _store = store;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HostShutdownWasCancelled { get; private set; }

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            await _store.UpdateAsync(item.With(WorkItemState.Working), CancellationToken.None);
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, hostShutdownToken);
            }
            catch (OperationCanceledException) when (hostShutdownToken.IsCancellationRequested)
            {
                HostShutdownWasCancelled = true;
                throw new OperationCanceledException(hostShutdownToken);
            }
        }
    }

    private sealed class ShutdownIgnoringWorkingPipeline : IPipelineRunner
    {
        private readonly IWorkItemStore _store;

        public ShutdownIgnoringWorkingPipeline(IWorkItemStore store) => _store = store;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ShutdownObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HostShutdownWasCancelled { get; private set; }

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            await _store.UpdateAsync(item.With(WorkItemState.Working), CancellationToken.None);
            Started.SetResult();
            try
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, hostShutdownToken);
                }
                catch (OperationCanceledException) when (hostShutdownToken.IsCancellationRequested)
                {
                    HostShutdownWasCancelled = true;
                    ShutdownObserved.TrySetResult();
                    await Release.Task;
                    var current = await _store.GetAsync(item.Id, CancellationToken.None) ?? item;
                    await _store.UpdateAsync(current.With(WorkItemState.Done), CancellationToken.None);
                }
            }
            finally
            {
                Exited.SetResult();
            }
        }
    }
}
