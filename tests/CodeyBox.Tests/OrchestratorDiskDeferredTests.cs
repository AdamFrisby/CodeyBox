using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Serilog;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage for the orchestrator's disk-deferred handler:
/// SandboxDiskDeferredException raised from the pipeline must trigger the
/// same machinery as a budget defer — audit emission, the
/// <c>disk.deferred</c> webhook with mountPath/freeBytes/thresholdBytes/
/// suggestedRetryAt details, and a re-enqueue after the recheck delay.
/// Without these the bug-report contract ("operator gets the same webhook
/// event so existing alerting fires") is not met.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class OrchestratorDiskDeferredTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-diskdefer-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly TestSink _sink = new();

    public OrchestratorDiskDeferredTests()
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
    public async Task DiskDeferredException_PublishesWebhook_AuditsAndRequeues()
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

        var recheckIn = TimeSpan.FromMilliseconds(150);
        var pipeline = new DiskDeferThrowingPipeline(
            _store,
            mountPath: "/fake/mp",
            freeBytes: 1024 * 1024,
            thresholdBytes: 10L * 1024 * 1024 * 1024,
            recheckIn: recheckIn);

        var queue = new InMemoryTaskQueue();
        var webhooks = new RecordingWebhookDispatcher();
        var registry = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };

        var svc = new OrchestratorService(
            queue, _store, pipeline, registry, opts,
            NullLogger<OrchestratorService>.Instance,
            webhooks: webhooks);

        await queue.EnqueueAsync(item.Id);
        await svc.StartAsync(CancellationToken.None);

        try
        {
            // Wait for the pipeline to be invoked the first time so we know
            // the disk-defer handler has run. Deadlines here are backstops for a
            // deterministic-but-starved event (the 20ms poll observes state that
            // WILL be reached); 60s gives headroom under the 6-core capped full
            // suite on a co-resident host without weakening any assertion.
            var firstCallDeadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (pipeline.CallCount == 0 && DateTimeOffset.UtcNow < firstCallDeadline)
                await Task.Delay(20);

            Assert.True(pipeline.CallCount >= 1, "pipeline should have been invoked at least once");

            // Wait briefly for the async webhook publish + audit emit + schedule.
            var webhookDeadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (webhooks.Events.Count == 0 && DateTimeOffset.UtcNow < webhookDeadline)
                await Task.Delay(20);

            var evt = Assert.Single(webhooks.Events);
            Assert.Equal("disk.deferred", evt.Event);
            Assert.Equal(item.Id, evt.WorkItem!.Id);
            Assert.Equal(WorkItemState.WorkComplete, evt.WorkItem.State);
            Assert.Equal(pipeline.WorkBranch, evt.WorkItem.WorkBranch);
            Assert.Null(evt.WorkItem.LastError);
            Assert.Null(evt.WorkItem.FailureKind);
            // Details: anonymous object with mountPath / freeBytes / thresholdBytes / suggestedRetryAt.
            Assert.NotNull(evt.Details);
            var d = evt.Details!.GetType();
            Assert.Equal("/fake/mp", (string)d.GetProperty("mountPath")!.GetValue(evt.Details)!);
            Assert.Equal(1024L * 1024, (long)d.GetProperty("freeBytes")!.GetValue(evt.Details)!);
            Assert.Equal(10L * 1024 * 1024 * 1024, (long)d.GetProperty("thresholdBytes")!.GetValue(evt.Details)!);
            var suggestedRetryAt = (DateTimeOffset)d.GetProperty("suggestedRetryAt")!.GetValue(evt.Details)!;
            Assert.True(suggestedRetryAt > DateTimeOffset.UtcNow,
                "suggestedRetryAt must be in the future (recheckIn ADDED, not subtracted)");

            // Audit log assertion: disk.deferred event must be emitted with the structured properties.
            var auditEvent = _sink.Events.FirstOrDefault(e =>
                e.Properties.TryGetValue("EventName", out var name) && name.ToString() == "\"disk.deferred\"");
            Assert.NotNull(auditEvent);

            // Recheck-and-requeue: ScheduleDeferredRequeue uses Task.Delay(recheckIn),
            // after which the item is enqueued again. Observe by waiting for the
            // pipeline to be invoked a second time.
            var requeueDeadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (pipeline.CallCount < 2 && DateTimeOffset.UtcNow < requeueDeadline)
                await Task.Delay(20);

            Assert.True(pipeline.CallCount >= 2,
                $"expected re-enqueue after recheckIn={recheckIn}, but pipeline was called {pipeline.CallCount} time(s)");
            Assert.Equal(WorkItemState.WorkComplete, pipeline.SecondCallState);
            Assert.Equal(pipeline.WorkBranch, pipeline.SecondCallWorkBranch);
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Pipeline that throws <see cref="SandboxDiskDeferredException"/> for the
    /// first <paramref name="ThrowForFirstN"/> calls and then succeeds, so a
    /// test can observe both the defer and the re-pickup behaviour.
    /// </summary>
    private sealed class DiskDeferThrowingPipeline : IPipelineRunner
    {
        private readonly IWorkItemStore _store;
        private readonly string _mountPath;
        private readonly long _freeBytes;
        private readonly long _thresholdBytes;
        private readonly TimeSpan _recheckIn;
        private int _callCount;
        private readonly string _workBranch = "codeybox/disk-deferred";

        public DiskDeferThrowingPipeline(
            IWorkItemStore store,
            string mountPath,
            long freeBytes,
            long thresholdBytes,
            TimeSpan recheckIn)
        {
            _store = store;
            _mountPath = mountPath;
            _freeBytes = freeBytes;
            _thresholdBytes = thresholdBytes;
            _recheckIn = recheckIn;
        }

        public int CallCount => _callCount;
        public string WorkBranch => _workBranch;
        public WorkItemState? SecondCallState { get; private set; }
        public string? SecondCallWorkBranch { get; private set; }

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                await _store.UpdateAsync(item with
                {
                    State = WorkItemState.Auditing,
                    WorkBranch = _workBranch,
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    LastError = "previous transient error",
                    FailureKind = "other",
                    UpdatedAt = DateTimeOffset.UtcNow,
                }, ct);
                throw new SandboxDiskDeferredException(_mountPath, _freeBytes, _thresholdBytes, _recheckIn);
            }

            if (call == 2)
            {
                SecondCallState = item.State;
                SecondCallWorkBranch = item.WorkBranch;
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
