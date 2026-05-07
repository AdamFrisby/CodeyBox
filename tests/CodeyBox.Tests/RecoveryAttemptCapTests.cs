using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that a work item at <see cref="DeadWorkerOptions.MaxRecoveryAttempts"/>
/// transitions to <see cref="WorkItemState.Failed"/> rather than being re-queued.
/// </summary>
public sealed class RecoveryAttemptCapTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-captest-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;
    private readonly InMemoryTaskQueue _queue;
    private readonly CapturingWebhookDispatcher _webhooks;
    private readonly DeadWorkerReaper _reaper;
    private const int MaxAttempts = 2;

    public RecoveryAttemptCapTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        _registry = new SqliteWorkerRegistry(_dbPath);
        _queue = new InMemoryTaskQueue();
        _webhooks = new CapturingWebhookDispatcher();
        var opts = new DeadWorkerOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(5),
            DeadWorkerThreshold = TimeSpan.FromSeconds(15),
            MaxRecoveryAttempts = MaxAttempts,
        };
        _reaper = new DeadWorkerReaper(
            _registry, _store, _queue, opts,
            NullLogger<DeadWorkerReaper>.Instance,
            _webhooks);
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task PlantDeadWorkerAsync(string workItemId)
    {
        var reg = new WorkerRegistration
        {
            WorkerId = Guid.NewGuid().ToString(),
            HostName = "host",
            ProcessId = 1,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            CurrentWorkItemId = workItemId,
        };
        await _registry.RegisterAsync(reg);
    }

    [Fact]
    public async Task AtCap_TransitionsToFailed_NotQueued()
    {
        // Create an item already at the cap.
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Auditing,
            RecoveryAttempts = MaxAttempts,
        };
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Failed, after.State);
        Assert.Equal("exceeded MaxRecoveryAttempts", after.LastError);
        Assert.Equal(MaxAttempts + 1, after.RecoveryAttempts);
    }

    [Fact]
    public async Task AtCap_DoesNotRequeue()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Auditing,
            RecoveryAttempts = MaxAttempts,
        };
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task BelowCap_IsRequeued_AndAttemptIncremented()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Auditing,
            RecoveryAttempts = MaxAttempts - 1,
        };
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, after!.State);
        Assert.Equal(MaxAttempts, after.RecoveryAttempts);
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task MultipleReaperRuns_CapIsRespected_EvenAfterStuckCycles()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Auditing,
            RecoveryAttempts = 0,
        };
        await _store.CreateAsync(item);

        // Simulate MaxAttempts+1 crash cycles.
        for (int i = 0; i <= MaxAttempts; i++)
        {
            // Reset queue between runs.
            while (_queue.Count > 0) await _queue.DequeueAsync();

            await PlantDeadWorkerAsync(item.Id.ToString());
            await _reaper.RunOnceAsync(CancellationToken.None);

            item = (await _store.GetAsync(item.Id))!;

            // Back-simulate: re-put the item in Auditing state for the next crash,
            // unless it's already Failed.
            if (item.State == WorkItemState.WorkComplete)
                await _store.UpdateAsync(item with { State = WorkItemState.Auditing });
        }

        // After MaxAttempts+1 reaper runs the item should be Failed.
        Assert.Equal(WorkItemState.Failed, item.State);
    }
}
