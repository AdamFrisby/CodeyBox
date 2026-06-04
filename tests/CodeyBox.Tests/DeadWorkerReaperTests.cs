using Microsoft.Data.Sqlite;
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

    private static async Task<SqliteConnection> OpenExternalWriterLockAsync(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
            await pragma.ExecuteNonQueryAsync();
        }

        using var begin = conn.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        await begin.ExecuteNonQueryAsync();
        return conn;
    }

    private static async Task ReleaseExternalWriterLockAsync(SqliteConnection conn)
    {
        try
        {
            using var rollback = conn.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(WorkItemState.Reworking, WorkItemState.WorkComplete)]
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
        var slotReleaser = new RecordingWorkerPoolRecoverySlotReleaser();
        _reaper.AttachWorkerPoolSlotReleaser(slotReleaser);
        var workerId = Guid.NewGuid().ToString();
        await PlantDeadWorkerAsync(workerId, null);

        await _reaper.RunOnceAsync(CancellationToken.None);

        // Registry row should be gone.
        Assert.Empty(await _registry.ListAsync());
        // No webhook should fire.
        Assert.Empty(_webhooks.Events);
        var release = Assert.Single(slotReleaser.Releases);
        Assert.Equal(workerId, release.WorkerId);
        Assert.Null(release.WorkItemId);
    }

    [Fact]
    public async Task Reaper_DoesNotClaimWorkerAfterSingleMissedHeartbeatWithinThreshold()
    {
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item);
        var workerId = Guid.NewGuid().ToString();
        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = workerId,
            HostName = "host",
            ProcessId = 1,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastHeartbeatAt = DateTimeOffset.UtcNow - _opts.HeartbeatInterval - TimeSpan.FromMilliseconds(100),
            CurrentWorkItemId = item.Id.ToString(),
        });

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Working, after.State);
        var worker = Assert.Single(await _registry.ListAsync());
        Assert.Equal(workerId, worker.WorkerId);
        Assert.Equal(0, _queue.Count);
        Assert.Empty(_webhooks.Events);
    }

    [Fact]
    public async Task TransientlyFailedHeartbeat_DoesNotCauseWorkerToBeReaped()
    {
        _opts.DeadWorkerThreshold = TimeSpan.FromMinutes(5);
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item);
        var workerId = Guid.NewGuid().ToString();
        // Keep the row comfortably inside the stale threshold. The external
        // SQLite writer-lock path intentionally exercises a transient heartbeat
        // failure, and a full-suite run can spend several seconds in that setup
        // before the reaper sweep runs.
        var seededHeartbeatAt = DateTimeOffset.UtcNow - _opts.HeartbeatInterval;
        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = workerId,
            HostName = "host",
            ProcessId = 1,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastHeartbeatAt = seededHeartbeatAt,
            CurrentWorkItemId = item.Id.ToString(),
        });

        var replacementWorkItemId = WorkItemId.New().ToString();
        using var heartbeatRegistry = new SqliteWorkerRegistry(_dbPath, busyTimeoutMilliseconds: 1);
        var writerLock = await OpenExternalWriterLockAsync(_dbPath);
        try
        {
            await heartbeatRegistry.HeartbeatAsync(workerId, replacementWorkItemId);
        }
        finally
        {
            await ReleaseExternalWriterLockAsync(writerLock);
        }

        var staleWorker = Assert.Single(await _registry.ListAsync());
        Assert.Equal(workerId, staleWorker.WorkerId);
        Assert.Equal(seededHeartbeatAt, staleWorker.LastHeartbeatAt);
        Assert.Equal(item.Id.ToString(), staleWorker.CurrentWorkItemId);

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Working, after.State);
        var worker = Assert.Single(await _registry.ListAsync());
        Assert.Equal(workerId, worker.WorkerId);
        Assert.Equal(0, _queue.Count);
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
        Assert.Equal("stale worker died", after.LastError);
        Assert.Equal(1, _queue.Count);
        Assert.Empty(_webhooks.Events);
    }

    [Fact]
    public async Task Reaper_TerminalState_SkipsItem()
    {
        var slotReleaser = new RecordingWorkerPoolRecoverySlotReleaser();
        _reaper.AttachWorkerPoolSlotReleaser(slotReleaser);
        var item = MakeItem(WorkItemState.Done);
        var workerId = Guid.NewGuid().ToString();
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(workerId, item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, after!.State);
        Assert.Empty(_webhooks.Events);
        var release = Assert.Single(slotReleaser.Releases);
        Assert.Equal(workerId, release.WorkerId);
        Assert.Equal(item.Id, release.WorkItemId);
    }

    [Fact]
    public async Task Reaper_MaxRecoveryAttemptsFailure_ReleasesWorkerSlot()
    {
        var slotReleaser = new RecordingWorkerPoolRecoverySlotReleaser();
        _reaper.AttachWorkerPoolSlotReleaser(slotReleaser);
        var item = MakeItem(WorkItemState.Auditing) with
        {
            RecoveryAttempts = _opts.MaxRecoveryAttempts,
        };
        var workerId = Guid.NewGuid().ToString();
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(workerId, item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, after!.State);
        var release = Assert.Single(slotReleaser.Releases);
        Assert.Equal(workerId, release.WorkerId);
        Assert.Equal(item.Id, release.WorkItemId);
    }

    [Fact]
    public async Task Reaper_CheckAndActAtRecoveryCap_MarksFailedAndReleasesWorkerSlot()
    {
        var slotReleaser = new RecordingWorkerPoolRecoverySlotReleaser();
        _reaper.AttachWorkerPoolSlotReleaser(slotReleaser);
        var item = MakeItem(WorkItemState.Working) with
        {
            JobType = JobType.CheckAndAct,
            RecoveryAttempts = _opts.MaxRecoveryAttempts,
            Check = new CheckAndActSpec
            {
                Question = "Is action needed?",
                OnYes = new OnYesActionSpec
                {
                    Title = "Act",
                    Prompt = "Act on the check.",
                },
            },
        };
        var workerId = Guid.NewGuid().ToString();
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(workerId, item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, after!.State);
        Assert.Equal("exceeded MaxRecoveryAttempts", after.LastError);
        Assert.Equal(_opts.MaxRecoveryAttempts + 1, after.RecoveryAttempts);
        Assert.Null(after.StartedAt);
        Assert.Null(after.PreemptCheckpoint);
        Assert.Equal(0, _queue.Count);

        var release = Assert.Single(slotReleaser.Releases);
        Assert.Equal(workerId, release.WorkerId);
        Assert.Equal(item.Id, release.WorkItemId);
    }

    [Fact]
    public async Task Reaper_AgentControlWorkingWithoutPreempt_RequeuesAndPreservesControlSpec()
    {
        var item = MakeItem(WorkItemState.Working) with
        {
            JobType = JobType.AgentControl,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            AgentControl = new AgentControlSpec
            {
                Action = AgentControlAction.Resume,
                Agent = AgentKind.Claude.Value,
                Reason = "provider recovered",
            },
        };
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(Guid.NewGuid().ToString(), item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Null(after.StartedAt);
        Assert.Null(after.LastError);
        Assert.NotNull(after.AgentControl);
        Assert.Equal(AgentControlAction.Resume, after.AgentControl!.Action);
        Assert.Equal(AgentKind.Claude.Value, after.AgentControl.Agent);
        Assert.Equal("provider recovered", after.AgentControl.Reason);
        Assert.Equal(1, _queue.Count);

        var evt = Assert.Single(_webhooks.Events);
        Assert.Equal("work_item.recovered", evt.Event);
        Assert.Equal(item.Id, evt.WorkItem!.Id);
        Assert.Equal(WorkItemState.Queued, evt.WorkItem.State);
    }

    [Fact]
    public async Task Reaper_AgentControlAtRecoveryCap_MarksFailedAndReleasesWorkerSlot()
    {
        var slotReleaser = new RecordingWorkerPoolRecoverySlotReleaser();
        _reaper.AttachWorkerPoolSlotReleaser(slotReleaser);
        var item = MakeItem(WorkItemState.Working) with
        {
            JobType = JobType.AgentControl,
            RecoveryAttempts = _opts.MaxRecoveryAttempts,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            AgentControl = new AgentControlSpec
            {
                Action = AgentControlAction.Pause,
                Agent = AgentKind.Claude.Value,
                Reason = "maintenance",
            },
        };
        var workerId = Guid.NewGuid().ToString();
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(workerId, item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, after!.State);
        Assert.Equal("exceeded MaxRecoveryAttempts", after.LastError);
        Assert.Equal(_opts.MaxRecoveryAttempts + 1, after.RecoveryAttempts);
        Assert.Null(after.StartedAt);
        Assert.Equal(0, _queue.Count);

        var release = Assert.Single(slotReleaser.Releases);
        Assert.Equal(workerId, release.WorkerId);
        Assert.Equal(item.Id, release.WorkItemId);
    }

    [Fact]
    public async Task Reaper_CheckAndActWithPersistedFollowup_CompletesWithoutDuplicateFollowup()
    {
        var slotReleaser = new RecordingWorkerPoolRecoverySlotReleaser();
        _reaper.AttachWorkerPoolSlotReleaser(slotReleaser);
        var item = MakeItem(WorkItemState.Working) with
        {
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is action needed?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec
                {
                    Title = "Act",
                    Prompt = "Act on the check.",
                },
            },
            Verdict = new CheckVerdict
            {
                Answer = true,
                Evidence = "actionable",
            },
        };
        var followup = MakeItem(WorkItemState.Queued) with
        {
            JobType = JobType.Normal,
            OriginCheckWorkItemId = item.Id,
        };
        await _store.CreateAsync(item);
        await _store.CreateAsync(followup);
        var workerId = Guid.NewGuid().ToString();
        await PlantDeadWorkerAsync(workerId, item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, after!.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Null(after.StartedAt);
        Assert.Equal(1, _queue.Count);

        var allItems = new List<WorkItem>();
        await foreach (var stored in _store.ListAsync())
            allItems.Add(stored);
        var followups = allItems.Where(i => i.OriginCheckWorkItemId == item.Id).ToList();
        Assert.Single(followups);

        var release = Assert.Single(slotReleaser.Releases);
        Assert.Equal(workerId, release.WorkerId);
        Assert.Equal(item.Id, release.WorkItemId);
        Assert.Contains("persisted verdict", release.Reason, StringComparison.OrdinalIgnoreCase);

        var evt = Assert.Single(_webhooks.Events);
        Assert.Equal("work_item.recovered", evt.Event);
        Assert.Equal(item.Id, evt.WorkItem!.Id);
        Assert.Equal(WorkItemState.Done, evt.WorkItem.State);
    }

    [Fact]
    public async Task Reaper_RedispatchedItem_DoesNotReleaseWorkerSlot()
    {
        var slotReleaser = new RecordingWorkerPoolRecoverySlotReleaser();
        _reaper.AttachWorkerPoolSlotReleaser(slotReleaser);
        var item = MakeItem(WorkItemState.AuditPassed);
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(Guid.NewGuid().ToString(), item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        Assert.Empty(slotReleaser.Releases);
        Assert.Equal(1, _queue.Count);
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

    private sealed class RecordingWorkerPoolRecoverySlotReleaser : IWorkerPoolRecoverySlotReleaser
    {
        public List<(string WorkerId, WorkItemId? WorkItemId, string Reason)> Releases { get; } = [];

        public ValueTask<bool> TryReleaseRecoveredWorkerSlotAsync(
            string workerId,
            WorkItemId? workItemId,
            string reason,
            CancellationToken ct = default)
        {
            Releases.Add((workerId, workItemId, reason));
            return ValueTask.FromResult(true);
        }
    }
}
