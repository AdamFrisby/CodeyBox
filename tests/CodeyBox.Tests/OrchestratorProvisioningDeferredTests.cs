using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Serilog;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class OrchestratorProvisioningDeferredTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-provisioningdefer-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly TestSink _sink = new();

    public OrchestratorProvisioningDeferredTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(_sink).CreateLogger();
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task ProvisioningDeferredException_ResetsWorkingItemPublishesWebhookAndRequeues()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var webhooks = new RecordingWebhookDispatcher();
        var pipeline = new ProvisioningDeferOncePipeline(
            _store,
            new SandboxProvisioningDeferredException(
                provider: "multipass",
                operation: "start",
                errorClass: "multipass-start-argument-not-found",
                detail: "multipass start failed after retries",
                recheckIn: TimeSpan.FromMilliseconds(100)));
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            webhooks: webhooks);

        await queue.EnqueueAsync(item.Id);
        await svc.StartAsync(CancellationToken.None);

        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (pipeline.CallCount < 2 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(20);

            Assert.Equal(2, pipeline.CallCount);
            Assert.Equal(WorkItemState.Queued, pipeline.SecondCallState);

            var stored = await _store.GetAsync(item.Id);
            Assert.NotNull(stored);
            Assert.Equal(WorkItemState.Done, stored!.State);
            Assert.Null(stored.FailureKind);

            var evt = Assert.Single(webhooks.Events);
            Assert.Equal("sandbox.provisioning_deferred", evt.Event);
            Assert.Equal(WorkItemState.Queued, evt.WorkItem!.State);
            Assert.NotNull(evt.Details);
            var detailType = evt.Details!.GetType();
            Assert.Equal("multipass", (string)detailType.GetProperty("provider")!.GetValue(evt.Details)!);
            Assert.Equal("start", (string)detailType.GetProperty("operation")!.GetValue(evt.Details)!);
            Assert.Equal("multipass-start-argument-not-found", (string)detailType.GetProperty("errorClass")!.GetValue(evt.Details)!);
            Assert.Equal("Queued", (string)detailType.GetProperty("resumeState")!.GetValue(evt.Details)!);

            Assert.Contains(_sink.Events, e =>
                e.Properties.TryGetValue("EventName", out var name)
                && name.ToString() == "\"sandbox.provisioning_deferred\"");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(WorkItemState.Reworking, WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.Auditing, WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.Merging, WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.ReworkingForConflict, WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.UpstreamPushing, WorkItemState.Merged)]
    public async Task ProvisioningDeferredException_PreservesBranchAndPhaseResumeState(
        WorkItemState deferredFrom,
        WorkItemState resumesAt)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var workBranch = $"codeybox/{item.Id.ToString()[..8]}";
        var pipeline = new ProvisioningDeferFromStateOncePipeline(
            _store,
            deferredFrom,
            workBranch,
            new SandboxProvisioningDeferredException(
                provider: "multipass",
                operation: "mount",
                errorClass: "multipass-mount-retry-exhausted",
                detail: "mount retry exhausted",
                recheckIn: TimeSpan.FromMilliseconds(10)));
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            projects: new InMemoryProjectRepository(new Project
            {
                Id = item.ProjectId,
                DisplayName = "Test",
                RepositoryUrl = "unused",
                Budget = new ProjectBudget { MaxConcurrentForProject = 1 },
            }));

        await queue.EnqueueAsync(item.Id);
        await svc.StartAsync(CancellationToken.None);

        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (pipeline.CallCount < 2 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(20);

            Assert.Equal(2, pipeline.CallCount);
            Assert.Equal(resumesAt, pipeline.SecondCallState);
            Assert.Equal(workBranch, pipeline.SecondCallWorkBranch);
            Assert.Null(pipeline.SecondCallLastError);
            Assert.Null(pipeline.SecondCallFailureKind);
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(WorkItemState.Working)]
    [InlineData(WorkItemState.Reworking)]
    public async Task ProvisioningDeferredException_PreservesCheckpointedResumeState(
        WorkItemState deferredFrom)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var workBranch = $"codeybox/{item.Id.ToString()[..8]}";
        var checkpoint = $"refs/heads/codeybox/preempt/{item.Id}";
        var pipeline = new ProvisioningDeferFromStateOncePipeline(
            _store,
            deferredFrom,
            workBranch,
            new SandboxProvisioningDeferredException(
                provider: "multipass",
                operation: "start",
                errorClass: "multipass-start-argument-not-found",
                detail: "start retry exhausted",
                recheckIn: TimeSpan.FromMilliseconds(10)),
            checkpoint);
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        await queue.EnqueueAsync(item.Id);
        await svc.StartAsync(CancellationToken.None);

        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (pipeline.CallCount < 2 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(20);

            Assert.Equal(2, pipeline.CallCount);
            Assert.Equal(deferredFrom, pipeline.SecondCallState);
            Assert.Equal(workBranch, pipeline.SecondCallWorkBranch);
            Assert.Equal(checkpoint, pipeline.SecondCallPreemptCheckpoint);
            Assert.NotNull(pipeline.SecondCallPreemptedAt);
            Assert.Null(pipeline.SecondCallLastError);
            Assert.Null(pipeline.SecondCallFailureKind);
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    private sealed class ProvisioningDeferOncePipeline : IPipelineRunner
    {
        private readonly IWorkItemStore _store;
        private readonly SandboxProvisioningDeferredException _exception;
        private int _callCount;
        private WorkItemState? _secondCallState;

        public ProvisioningDeferOncePipeline(
            IWorkItemStore store,
            SandboxProvisioningDeferredException exception)
        {
            _store = store;
            _exception = exception;
        }

        public int CallCount => Volatile.Read(ref _callCount);
        public WorkItemState? SecondCallState => _secondCallState;

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                await _store.UpdateAsync(item.With(WorkItemState.Working), ct);
                throw _exception;
            }

            if (call == 2)
            {
                _secondCallState = item.State;
                await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
            }
        }
    }

    private sealed class ProvisioningDeferFromStateOncePipeline : IPipelineRunner
    {
        private readonly IWorkItemStore _store;
        private readonly WorkItemState _deferredFrom;
        private readonly string _workBranch;
        private readonly SandboxProvisioningDeferredException _exception;
        private readonly string? _preemptCheckpoint;
        private int _callCount;

        public ProvisioningDeferFromStateOncePipeline(
            IWorkItemStore store,
            WorkItemState deferredFrom,
            string workBranch,
            SandboxProvisioningDeferredException exception,
            string? preemptCheckpoint = null)
        {
            _store = store;
            _deferredFrom = deferredFrom;
            _workBranch = workBranch;
            _exception = exception;
            _preemptCheckpoint = preemptCheckpoint;
        }

        public int CallCount => Volatile.Read(ref _callCount);
        public WorkItemState? SecondCallState { get; private set; }
        public string? SecondCallWorkBranch { get; private set; }
        public string? SecondCallLastError { get; private set; }
        public string? SecondCallFailureKind { get; private set; }
        public DateTimeOffset? SecondCallStartedAt { get; private set; }
        public DateTimeOffset? SecondCallPreemptedAt { get; private set; }
        public string? SecondCallPreemptCheckpoint { get; private set; }

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                await _store.UpdateAsync(item with
                {
                    State = _deferredFrom,
                    WorkBranch = _workBranch,
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    LastError = "previous transient error",
                    FailureKind = "other",
                    PreemptedAt = _preemptCheckpoint is null ? null : DateTimeOffset.UtcNow.AddMinutes(-2),
                    PreemptCheckpoint = _preemptCheckpoint,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }, ct);
                throw _exception;
            }

            if (call == 2)
            {
                SecondCallState = item.State;
                SecondCallWorkBranch = item.WorkBranch;
                SecondCallLastError = item.LastError;
                SecondCallFailureKind = item.FailureKind;
                SecondCallStartedAt = item.StartedAt;
                SecondCallPreemptedAt = item.PreemptedAt;
                SecondCallPreemptCheckpoint = item.PreemptCheckpoint;
                await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
            }
        }
    }

    private sealed class RecordingWebhookDispatcher : IWebhookDispatcher
    {
        private readonly List<WebhookEvent> _events = new();
        private readonly object _lock = new();

        public IReadOnlyList<WebhookEvent> Events
        {
            get { lock (_lock) return _events.ToArray(); }
        }

        public Task PublishAsync(WebhookEvent evt, CancellationToken ct)
        {
            lock (_lock) _events.Add(evt);
            return Task.CompletedTask;
        }
    }
}
