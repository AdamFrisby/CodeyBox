using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DeadWorkerReaper"/>. Synthesises stale registry rows
/// and mid-flight work items, then asserts the state-mapping rules and
/// webhook events.
/// </summary>
public sealed class DeadWorkerReaperTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-reaper-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;
    private readonly InMemoryTaskQueue _queue;
    private readonly CapturingWebhookDispatcher _webhooks;
    private readonly DeadWorkerOptions _opts;
    private readonly DeadWorkerReaper _reaper;

    public DeadWorkerReaperTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        _registry = new SqliteWorkerRegistry(_dbPath);
        _queue = new InMemoryTaskQueue();
        _webhooks = new CapturingWebhookDispatcher();
        _opts = new DeadWorkerOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(5),
            DeadWorkerThreshold = TimeSpan.FromSeconds(15),
            CheckInterval = TimeSpan.FromMinutes(60),
            MaxRecoveryAttempts = 2,
        };
        _reaper = new DeadWorkerReaper(
            _registry, _store, _queue, _opts,
            NullLogger<DeadWorkerReaper>.Instance,
            _webhooks);
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem MakeItem(WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = state,
    };

    private async Task PlantDeadWorkerAsync(string workerId, string? workItemId)
    {
        var reg = new WorkerRegistration
        {
            WorkerId = workerId,
            HostName = "host",
            ProcessId = 1,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            CurrentWorkItemId = workItemId,
        };
        await _registry.RegisterAsync(reg);
    }

    [Theory]
    [InlineData(WorkItemState.Reworking, WorkItemState.Queued)]
    [InlineData(WorkItemState.Auditing, WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.Merging, WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.UpstreamPushing, WorkItemState.Merged)]
    public async Task Reaper_TransitionsEachMidFlightState_ToCorrectRecoveryState(
        WorkItemState fromState, WorkItemState expectedTo)
    {
        var item = MakeItem(fromState);
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(Guid.NewGuid().ToString(), item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(expectedTo, after.State);
        Assert.Equal(1, after.RecoveryAttempts);
    }

    [Fact]
    public async Task Reaper_WorkingWithoutPreempt_MarksFailed()
    {
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(Guid.NewGuid().ToString(), item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Failed, after.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Contains("without a preempt checkpoint", after.LastError);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Reaper_WorkItemIdNull_DeletesRowWithoutTouchingAnyItem()
    {
        var workerId = Guid.NewGuid().ToString();
        await PlantDeadWorkerAsync(workerId, null);

        await _reaper.RunOnceAsync(CancellationToken.None);

        // Registry row should be gone.
        Assert.Empty(await _registry.ListAsync());
        // No webhook should fire.
        Assert.Empty(_webhooks.Events);
    }

    [Fact]
    public async Task Reaper_FiresWebhookEvent_OnRecovery()
    {
        var item = MakeItem(WorkItemState.Auditing);
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(Guid.NewGuid().ToString(), item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        var evt = Assert.Single(_webhooks.Events);
        Assert.Equal("work_item.recovered", evt.Event);
        Assert.NotNull(evt.WorkItem);
        Assert.Equal(item.Id, evt.WorkItem!.Id);
    }

    [Fact]
    public async Task Reaper_RequeuesItem_AfterRecovery()
    {
        var item = MakeItem(WorkItemState.Auditing);
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(Guid.NewGuid().ToString(), item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        // Item should be back in the in-memory queue.
        Assert.Equal(1, _queue.Count);
    }

    [Theory]
    [InlineData(WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.Merged)]
    public async Task Reaper_RedispatchesDurablePhaseBoundaryStates_WithoutIncrementingRecoveryAttempts(
        WorkItemState state)
    {
        var item = MakeItem(state) with
        {
            LastError = "stale worker died",
            RecoveryAttempts = 3,
        };
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(Guid.NewGuid().ToString(), item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(state, after.State);
        Assert.Equal(3, after.RecoveryAttempts);
        Assert.Null(after.LastError);
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task Reaper_TerminalState_SkipsItem()
    {
        var item = MakeItem(WorkItemState.Done);
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(Guid.NewGuid().ToString(), item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, after!.State);
        Assert.Empty(_webhooks.Events);
    }

    [Fact]
    public async Task Reaper_FreshWorker_NotClaimed()
    {
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item);

        // Fresh registration — last_heartbeat_at is recent.
        var reg = new WorkerRegistration
        {
            WorkerId = Guid.NewGuid().ToString(),
            HostName = "host",
            ProcessId = 1,
            StartedAt = DateTimeOffset.UtcNow,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            CurrentWorkItemId = item.Id.ToString(),
        };
        await _registry.RegisterAsync(reg);

        await _reaper.RunOnceAsync(CancellationToken.None);

        // Still alive — item should be untouched.
        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Single(await _registry.ListAsync());
    }

    [Fact]
    public async Task MapToRecoveryState_NonWorkerStates_ReturnNull()
    {
        foreach (var state in new[] {
            WorkItemState.Queued, WorkItemState.Done, WorkItemState.Failed,
            WorkItemState.Cancelled, WorkItemState.AuditFailed })
        {
            Assert.Null(DeadWorkerReaper.MapToRecoveryState(state));
        }
    }
}
