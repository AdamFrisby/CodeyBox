using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the R8-core sandbox suspend/resume cycle: shutdown freezes every
/// active sandbox via <see cref="ISuspendableSandbox.SuspendAsync"/> and writes
/// <see cref="WorkItem.SuspendedVmName"/>; startup calls
/// <see cref="ISuspendingSandboxProvider.ResumeSandboxAsync"/> and clears the
/// bookkeeping. The leak reaper skips VMs named in the suspended set.
/// </summary>
public sealed class SandboxSuspendResumeTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-suspend-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public SandboxSuspendResumeTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem MakeItem(WorkItemState state = WorkItemState.Working) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = state,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
    };

    // ── Schema round-trip ────────────────────────────────────────────────────

    [Fact]
    public async Task SuspendedVmName_AndSuspendedAt_RoundTripThroughStore()
    {
        var item = MakeItem();
        await _store.CreateAsync(item);

        var suspendedAt = DateTimeOffset.UtcNow;
        await _store.UpdateAsync(item with
        {
            SuspendedVmName = "codeybox-abc123",
            SuspendedAt = suspendedAt,
        });

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal("codeybox-abc123", after.SuspendedVmName);
        Assert.NotNull(after.SuspendedAt);
        // SQLite text round-trip preserves DateTimeOffset to whole microseconds.
        Assert.Equal(suspendedAt.ToUnixTimeMilliseconds(), after.SuspendedAt.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task LegacyRow_WithoutSuspendedColumns_ReadsAsNull()
    {
        // Default WorkItem has SuspendedVmName=null and SuspendedAt=null.
        var item = MakeItem(WorkItemState.WorkComplete);
        await _store.CreateAsync(item);
        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
    }

    // ── Suspend-on-shutdown handler ──────────────────────────────────────────

    [Fact]
    public async Task ShutdownHandler_SuspendsEveryActiveSandbox_AndPersistsVmName()
    {
        var item1 = MakeItem();
        var item2 = MakeItem();
        await _store.CreateAsync(item1);
        await _store.CreateAsync(item2);

        var sandbox1 = new FakeSuspendableSandbox("vm-1");
        var sandbox2 = new FakeSuspendableSandbox("vm-2");
        var provider = new FakeSuspendingProvider();
        provider.Register(item1.Id, sandbox1);
        provider.Register(item2.Id, sandbox2);

        var svc = new SandboxSuspendOnShutdownService(
            provider, _store,
            NullLogger<SandboxSuspendOnShutdownService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await svc.SuspendAllAsync();

        Assert.True(sandbox1.SuspendCalled);
        Assert.True(sandbox2.SuspendCalled);

        var after1 = await _store.GetAsync(item1.Id);
        var after2 = await _store.GetAsync(item2.Id);
        Assert.Equal("vm-1", after1!.SuspendedVmName);
        Assert.Equal("vm-2", after2!.SuspendedVmName);
        Assert.NotNull(after1.SuspendedAt);
        Assert.NotNull(after2.SuspendedAt);
    }

    [Fact]
    public async Task ShutdownHandler_SuspendFailure_DoesNotPersistVmName()
    {
        var item = MakeItem();
        await _store.CreateAsync(item);
        var sandbox = new FakeSuspendableSandbox("vm-bad", shouldThrow: true);
        var provider = new FakeSuspendingProvider();
        provider.Register(item.Id, sandbox);

        var svc = new SandboxSuspendOnShutdownService(
            provider, _store,
            NullLogger<SandboxSuspendOnShutdownService>.Instance);
        await svc.SuspendAllAsync();

        var after = await _store.GetAsync(item.Id);
        // No persisted bookkeeping when suspend failed — the item flows through
        // the standard stranded-item recovery path on the next start.
        Assert.Null(after!.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
    }

    [Fact]
    public async Task ShutdownHandler_SuspendTimeout_DoesNotPersistVmName()
    {
        // The suspend handler bounds each multipass suspend with a per-VM
        // timeout. When the inner suspend exceeds it (OperationCanceledException),
        // the handler logs and returns WITHOUT persisting SuspendedVmName so
        // the item flows through the standard stranded-item recovery path.
        // Distinct from the generic Exception branch — both must lead to the
        // same "no bookkeeping" outcome.
        var item = MakeItem();
        await _store.CreateAsync(item);

        var sandbox = new SlowSuspendingSandbox("vm-slow");
        var provider = new FakeSuspendingProvider();
        provider.Register(item.Id, sandbox);

        var svc = new SandboxSuspendOnShutdownService(
            provider, _store,
            NullLogger<SandboxSuspendOnShutdownService>.Instance,
            perSuspendTimeout: TimeSpan.FromMilliseconds(50));
        await svc.SuspendAllAsync();

        var after = await _store.GetAsync(item.Id);
        // Timeout fired before the sandbox SuspendAsync could complete; no
        // bookkeeping must have been persisted.
        Assert.Null(after!.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
    }

    [Fact]
    public async Task ShutdownHandler_NonSuspendingProvider_IsNoOp()
    {
        var item = MakeItem();
        await _store.CreateAsync(item);

        // A provider that doesn't implement ISuspendingSandboxProvider — e.g.
        // process or bubblewrap. The handler must silently skip and leave the
        // suspend fields untouched so the existing PreemptCheckpoint flow
        // continues to be the recovery mechanism.
        var nonSuspending = new NonSuspendingProvider();
        var svc = new SandboxSuspendOnShutdownService(
            nonSuspending, _store,
            NullLogger<SandboxSuspendOnShutdownService>.Instance);
        await svc.SuspendAllAsync();

        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.SuspendedVmName);
    }

    // ── Startup resume handler ───────────────────────────────────────────────

    [Fact]
    public async Task StartupResume_StartsEveryPersistedVm_AndClearsBookkeeping()
    {
        var item1 = MakeItem();
        var item2 = MakeItem();
        await _store.CreateAsync(item1 with { SuspendedVmName = "vm-1", SuspendedAt = DateTimeOffset.UtcNow });
        await _store.CreateAsync(item2 with { SuspendedVmName = "vm-2", SuspendedAt = DateTimeOffset.UtcNow });

        var provider = new FakeSuspendingProvider();
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        Assert.Contains("vm-1", provider.ResumedNames);
        Assert.Contains("vm-2", provider.ResumedNames);

        var after1 = await _store.GetAsync(item1.Id);
        var after2 = await _store.GetAsync(item2.Id);
        Assert.Null(after1!.SuspendedVmName);
        Assert.Null(after1.SuspendedAt);
        Assert.Null(after2!.SuspendedVmName);
        Assert.Null(after2.SuspendedAt);
    }

    [Fact]
    public async Task StartupResume_ResumeFailure_StillClearsBookkeeping()
    {
        // If multipassd is unavailable or the VM was operator-deleted, we
        // can't bring it back. The item flows through the standard stranded-
        // item recovery path; the bookkeeping must be cleared so the orphaned
        // VM (if any) can be reaped on the leak reaper's normal schedule.
        var item = MakeItem();
        await _store.CreateAsync(item with { SuspendedVmName = "vm-gone", SuspendedAt = DateTimeOffset.UtcNow });

        var provider = new FakeSuspendingProvider { ResumeThrows = true };
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
    }

    [Fact]
    public async Task StartupResume_NoSuspendedItems_DoesNothing()
    {
        var item = MakeItem();
        await _store.CreateAsync(item); // No SuspendedVmName.

        var provider = new FakeSuspendingProvider();
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        Assert.Empty(provider.ResumedNames);
    }

    [Fact]
    public async Task StartupResume_NonSuspendingProvider_IsNoOpEvenWithSuspendedRows()
    {
        // Regression guard for the early-return when the provider does not
        // implement ISuspendingSandboxProvider. The service must not NRE and
        // must leave the persisted bookkeeping untouched so a subsequent
        // restart with the right provider can still resume.
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-orphan",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var svc = new SandboxResumeOnStartupService(
            new NonSuspendingProvider(), _store,
            NullLogger<SandboxResumeOnStartupService>.Instance);
        await svc.ResumeAllForTestAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal("vm-orphan", after!.SuspendedVmName);
        Assert.NotNull(after.SuspendedAt);
    }

    [Fact]
    public async Task StartupResume_NullProvider_IsNoOp()
    {
        // No sandbox provider registered at all (host doesn't run sandboxes).
        // Must not throw even when the DB still has suspended rows from a
        // previous configuration.
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-old",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var svc = new SandboxResumeOnStartupService(
            provider: null, _store,
            NullLogger<SandboxResumeOnStartupService>.Instance);

        await svc.ResumeAllForTestAsync(CancellationToken.None);
        // Rows untouched — operator-visible signal that the suspended VM
        // bookkeeping was inherited from a different configuration.
        var after = await _store.GetAsync(item.Id);
        Assert.Equal("vm-old", after!.SuspendedVmName);
    }

    [Fact]
    public async Task StartupResume_ItemDeletedBetweenResumeAndClear_DoesNotThrow()
    {
        // Race: an operator-cancel-and-delete arrives between the multipass
        // start succeeding and the suspend-bookkeeping clear. The fresh
        // GetAsync returns null; we must log + audit + return cleanly.
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-disappearing",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var provider = new DeleteOnResumeProvider(_store, item.Id);
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        Assert.Contains("vm-disappearing", provider.ResumedNames);
        var after = await _store.GetAsync(item.Id);
        Assert.Null(after); // Was deleted by the racing provider.
    }

    [Fact]
    public async Task StartupResume_WithAgentLogPath_WaitsForAdoptionAndClearsLogPath()
    {
        // When the work item carries AgentLogPath, the resume service should
        // both call ResumeSandboxAsync and call WaitForAdoptedAgentCompletion
        // (which the fake records). On adoption success the bookkeeping
        // including AgentLogPath is cleared.
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-adopt",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/abc.log",
        });

        var provider = new FakeSuspendingProvider { AdoptionExitCodeToReturn = 0 };
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        Assert.Single(provider.AdoptionCalls);
        var adoption = provider.AdoptionCalls[0];
        Assert.Equal("vm-adopt", adoption.VmName);
        Assert.Equal("/work/.codeybox/agent-logs/abc.log", adoption.AgentLogPath);
        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.SuspendedVmName);
        Assert.Null(after.AgentLogPath);
    }

    // ── Restart-resume checkpoint promotion (R8-core regression #1) ──────────

    [Fact]
    public async Task StartupResume_AdoptionExit0_PromotesVmHeadToPreemptCheckpoint()
    {
        // The audit's primary regression: a successful suspend+resume+adoption
        // used to clear suspend bookkeeping with no PreemptCheckpoint, after
        // which DeadWorkerReaper.SweepStrandedItemsAsync would mark the item
        // Failed for "Working without a preempt checkpoint". The resume service
        // must now push the adopted VM's HEAD to a real preempt-checkpoint ref
        // (via the new provider hook) and persist that ref on the work item so
        // the standard with-checkpoint recovery branch fires instead.
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-promote",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/abc.log",
        });

        var provider = new FakeSuspendingProvider { AdoptionExitCodeToReturn = 0 };
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        var pushed = Assert.Single(provider.CheckpointPushCalls);
        Assert.Equal("vm-promote", pushed.VmName);
        Assert.Equal("/work", pushed.WorkingDir);
        Assert.Equal($"refs/heads/codeybox/preempt/{item.Id}", pushed.RefName);
        Assert.Contains(item.Id.ToString(), pushed.CommitMessage);

        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
        Assert.Null(after.AgentLogPath);
        Assert.Equal($"refs/heads/codeybox/preempt/{item.Id}", after.PreemptCheckpoint);
        Assert.NotNull(after.PreemptedAt);
    }

    [Fact]
    public async Task StartupResume_AdoptionNonZeroExit_DoesNotPromoteCheckpoint()
    {
        // A non-zero exit from the adopted agent means the in-VM work failed
        // — promoting that state to a PreemptCheckpoint would cause the
        // pipeline to resume from a known-bad ref. Leave the item without a
        // checkpoint so stranded-item recovery (which re-runs the iteration)
        // takes over.
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-failed-exit",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/x.log",
        });

        var provider = new FakeSuspendingProvider { AdoptionExitCodeToReturn = 7 };
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        Assert.Empty(provider.CheckpointPushCalls);
        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.PreemptCheckpoint);
        Assert.Null(after.SuspendedVmName);
    }

    [Fact]
    public async Task StartupResume_AdoptionDeadlineNull_DoesNotPromoteCheckpoint()
    {
        // WaitForAdoptedAgentCompletionAsync returns null when the deadline
        // elapses before the .exit marker appears. The orchestrator does not
        // know whether the agent finished or wedged, so we MUST NOT promote
        // unknown in-VM state to a checkpoint ref. Bookkeeping clears so the
        // stranded-item path can take over.
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-deadline",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/d.log",
        });

        var provider = new FakeSuspendingProvider { AdoptionExitCodeToReturn = null };
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        Assert.Empty(provider.CheckpointPushCalls);
        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.PreemptCheckpoint);
    }

    [Fact]
    public async Task StartupResume_AdoptionExit0_ButPushReturnsFalse_DoesNotSetCheckpoint()
    {
        // The in-VM git push can fail for reasons outside the orchestrator
        // (no upstream, write-protected ref, network blip inside the VM). On
        // any push failure the resume service must leave PreemptCheckpoint
        // null so the pipeline doesn't try to resume from a ref that doesn't
        // exist on origin.
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-push-fails",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/p.log",
        });

        var provider = new FakeSuspendingProvider
        {
            AdoptionExitCodeToReturn = 0,
            CheckpointPushReturns = false,
        };
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        Assert.Single(provider.CheckpointPushCalls);
        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.PreemptCheckpoint);
        Assert.Null(after.SuspendedVmName);
    }

    [Fact]
    public async Task StartupResume_AdoptionExit0_ButPushThrows_DoesNotSetCheckpoint()
    {
        // Symmetric to the push-returns-false branch: an exception during the
        // push must not propagate (it would block other items in the resume
        // fan-out) and must NOT leave a stale PreemptCheckpoint pointing at a
        // ref that was never created.
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-push-throws",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/t.log",
        });

        var provider = new FakeSuspendingProvider
        {
            AdoptionExitCodeToReturn = 0,
            CheckpointPushThrows = true,
        };
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.PreemptCheckpoint);
        Assert.Null(after.SuspendedVmName);
    }

    [Fact]
    public async Task StartupResume_ResumeFailure_SkipsCheckpointPromotion()
    {
        // If multipass start failed, the VM is not running and any git push
        // would hit a dead VM. The promotion step must be skipped.
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-resume-failed",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/r.log",
        });

        var provider = new FakeSuspendingProvider
        {
            ResumeThrows = true,
            AdoptionExitCodeToReturn = 0,
        };
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        Assert.Empty(provider.CheckpointPushCalls);
        Assert.Empty(provider.AdoptionCalls);
    }

    [Fact]
    public async Task StartupResume_AdoptionExit0_NoAgentLogPath_DoesNotPromote()
    {
        // Without an agent log path there was no adoption attempt (the resume
        // service short-circuits adoption), so there is no exit code to act on.
        // Promotion must NOT fire because adoption did not occur.
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-no-log-path",
            SuspendedAt = DateTimeOffset.UtcNow,
            // AgentLogPath intentionally null.
        });

        var provider = new FakeSuspendingProvider { AdoptionExitCodeToReturn = 0 };
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        Assert.Empty(provider.AdoptionCalls);
        Assert.Empty(provider.CheckpointPushCalls);
        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.PreemptCheckpoint);
    }

    [Fact]
    public void SandboxResumeOnStartupService_PreemptCheckpointRefFor_MatchesPipelineRunnerFormat()
    {
        // The resume service's ref-name builder must produce exactly the same
        // string PipelineRunner.PreemptRefFor produces — the resumable agent
        // runner's ValidatePreemptCheckpoint rejects anything else, so a
        // format drift would mean items get re-enqueued with a checkpoint the
        // runner refuses to use. PipelineRunner.PreemptRefFor is private, so
        // duplicate the canonical literal here as the contract under test.
        var id = WorkItemId.New();
        Assert.Equal(
            $"refs/heads/codeybox/preempt/{id}",
            SandboxResumeOnStartupService.PreemptCheckpointRefFor(id));
    }

    [Fact]
    public async Task StartupResume_AdoptionExit0_WithCheckpoint_DoesNotMarkFailed()
    {
        // Integration assertion that the resume promotion is wired through
        // end-to-end: after a successful resume + exit 0 + checkpoint push,
        // simulate the very next pass of DeadWorkerReaper.SweepStrandedItemsAsync
        // (which OrchestratorService.ExecuteAsync invokes immediately after
        // StartingAsync returns) and verify the item is re-enqueued for clean
        // resume instead of being marked Failed.
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-restart-flow",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/flow.log",
        });

        var provider = new FakeSuspendingProvider { AdoptionExitCodeToReturn = 0 };
        var svc = MakeResumeService(provider);
        await svc.ResumeAllForTestAsync(CancellationToken.None);

        // Verify the resume side set the checkpoint.
        var afterResume = await _store.GetAsync(item.Id);
        Assert.Equal($"refs/heads/codeybox/preempt/{item.Id}", afterResume!.PreemptCheckpoint);

        // Now drive the dead-worker reaper across the same row. With the
        // checkpoint promoted, the recovery path must re-enqueue, NOT mark
        // Failed. A regression that dropped the promotion would surface as
        // State == Failed here.
        var queue = new InMemoryTaskQueue();
        var registry = new SqliteWorkerRegistry(_dbPath);
        var reaper = new DeadWorkerReaper(
            registry, _store, queue,
            new DeadWorkerOptions(),
            NullLogger<DeadWorkerReaper>.Instance,
            new NullWebhookDispatcher());
        await reaper.SweepStrandedItemsAsync(CancellationToken.None);

        var afterSweep = await _store.GetAsync(item.Id);
        Assert.NotEqual(WorkItemState.Failed, afterSweep!.State);
        // Re-enqueue clears StartedAt so the item is again pickup-eligible.
        Assert.Null(afterSweep.StartedAt);
        Assert.Equal($"refs/heads/codeybox/preempt/{item.Id}", afterSweep.PreemptCheckpoint);
    }

    // ── Leak reaper integration ──────────────────────────────────────────────

    [Fact]
    public async Task LeakReaper_SkipsVmsNamedInSuspendedVmName()
    {
        // Two VMs reported by the provider: one is suspended (mapped to a
        // mid-flight work item), one is a legitimate untracked leak. The
        // reaper must skip the suspended one and dispose the other.
        var suspendedItem = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(suspendedItem with
        {
            SuspendedVmName = "vm-suspended",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var provider = new FakeSandboxLeakProvider();
        var oldEnough = DateTimeOffset.UtcNow.AddHours(-1);
        provider.SeedManaged(new("vm-suspended", oldEnough, 1024L * 1024, IsTrackedActive: false));
        provider.SeedManaged(new("vm-leaked", oldEnough, 1024L * 1024, IsTrackedActive: false));

        var webhooks = new CapturingWebhookDispatcher();
        var reaper = new SandboxLeakReaper(
            provider, webhooks,
            () => new SandboxLeakOptions { LeakAgeThreshold = TimeSpan.FromMinutes(30), AutoDispose = true },
            NullLogger<SandboxLeakReaper>.Instance,
            _store);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Contains("vm-leaked", provider.DisposedNames);
        Assert.DoesNotContain("vm-suspended", provider.DisposedNames);
    }

    [Theory]
    [InlineData(WorkItemState.Auditing, "vm-auditing", true)]
    [InlineData(WorkItemState.Reworking, "vm-reworking", true)]
    [InlineData(WorkItemState.Merging, "vm-merging", true)]
    [InlineData(WorkItemState.AuditPassed, "vm-auditpassed", false)]
    [InlineData(WorkItemState.Done, "vm-done", false)]
    public async Task LeakReaper_ProtectsSuspendedVm_ForEveryMidFlightState(
        WorkItemState state, string vmName, bool expectedProtected)
    {
        // Each of (Working, Auditing, Reworking, Merging) is a mid-flight
        // state where the agent could legitimately be running inside a
        // suspended VM. A regression that drops one would cause that state's
        // suspended VMs to be reaped during the restart window. The
        // separately-tested terminal cases (Done, AuditPassed-as-resting) act
        // as negative controls.
        var item = MakeItem(state);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = vmName,
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var provider = new FakeSandboxLeakProvider();
        provider.SeedManaged(new(vmName, DateTimeOffset.UtcNow.AddHours(-1), 1024L * 1024, IsTrackedActive: false));

        var webhooks = new CapturingWebhookDispatcher();
        var reaper = new SandboxLeakReaper(
            provider, webhooks,
            () => new SandboxLeakOptions { LeakAgeThreshold = TimeSpan.FromMinutes(30), AutoDispose = true },
            NullLogger<SandboxLeakReaper>.Instance,
            _store);

        await reaper.RunSweepAsync(CancellationToken.None);

        if (expectedProtected)
            Assert.DoesNotContain(vmName, provider.DisposedNames);
        else
            Assert.Contains(vmName, provider.DisposedNames);
    }

    [Fact]
    public async Task LeakReaper_SuspendedVmName_OnTerminalItem_IsNotProtected()
    {
        // Operator cancelled the item between suspend and the next sweep:
        // SuspendedVmName is set but state is Cancelled. The reaper must
        // treat the orphaned VM as a leak so disk space is reclaimed.
        var item = MakeItem(WorkItemState.Cancelled);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-stale-suspended",
            SuspendedAt = DateTimeOffset.UtcNow.AddHours(-1),
        });

        var provider = new FakeSandboxLeakProvider();
        provider.SeedManaged(new("vm-stale-suspended",
            DateTimeOffset.UtcNow.AddHours(-1), 1024L * 1024, IsTrackedActive: false));

        var webhooks = new CapturingWebhookDispatcher();
        var reaper = new SandboxLeakReaper(
            provider, webhooks,
            () => new SandboxLeakOptions { LeakAgeThreshold = TimeSpan.FromMinutes(30), AutoDispose = true },
            NullLogger<SandboxLeakReaper>.Instance,
            _store);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Contains("vm-stale-suspended", provider.DisposedNames);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private SandboxResumeOnStartupService MakeResumeService(ISandboxProvider provider) =>
        new(provider, _store, NullLogger<SandboxResumeOnStartupService>.Instance);

    private sealed class DeleteOnResumeProvider : ISandboxProvider, ISuspendingSandboxProvider
    {
        private readonly IWorkItemStore _store;
        private readonly WorkItemId _itemToDelete;
        public DeleteOnResumeProvider(IWorkItemStore store, WorkItemId itemToDelete)
        {
            _store = store;
            _itemToDelete = itemToDelete;
        }
        public List<string> ResumedNames { get; } = new();
        public string Name => "fake-delete-on-resume";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
        public IReadOnlyList<(WorkItemId WorkItemId, ISuspendableSandbox Sandbox)> SnapshotSuspendableActive() => [];
        public async Task ResumeSandboxAsync(string name, CancellationToken ct)
        {
            ResumedNames.Add(name);
            // Drop the row before the caller can re-read it.
            await DeleteRowAsync(_itemToDelete, ct);
        }

        private async Task DeleteRowAsync(WorkItemId id, CancellationToken ct)
        {
            // Force the SQLite store to drop the row by issuing a raw DELETE.
            // The IWorkItemStore interface does not expose Delete, but the test
            // store has a Dispose hook so we approximate by setting state and
            // letting the test verify GetAsync returns null. We use direct
            // ADO.NET for the test only.
            if (_store is SqliteWorkItemStore sqlite)
            {
                await sqlite.DeleteRowForTestAsync(id, ct);
            }
        }
    }

    private sealed class FakeSuspendableSandbox : ISuspendableSandbox
    {
        public bool SuspendCalled { get; private set; }
        private readonly bool _shouldThrow;
        public FakeSuspendableSandbox(string id, bool shouldThrow = false)
        {
            Id = id;
            _shouldThrow = shouldThrow;
        }
        public string Id { get; }
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task SuspendAsync(CancellationToken ct = default)
        {
            SuspendCalled = true;
            if (_shouldThrow) throw new InvalidOperationException("simulated suspend failure");
            return Task.CompletedTask;
        }
    }

    private sealed class SlowSuspendingSandbox : ISuspendableSandbox
    {
        public SlowSuspendingSandbox(string id) { Id = id; }
        public string Id { get; }
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task SuspendAsync(CancellationToken ct = default)
        {
            // Block until cancellation fires; the per-suspend timeout in
            // SandboxSuspendOnShutdownService will cancel this.
            var tcs = new TaskCompletionSource();
            ct.Register(() => tcs.TrySetException(new OperationCanceledException(ct)));
            return tcs.Task;
        }
    }

    private sealed class FakeSuspendingProvider : ISandboxProvider, ISuspendingSandboxProvider
    {
        private readonly ConcurrentDictionary<WorkItemId, ISuspendableSandbox> _active = new();
        // Resume runs items in parallel (SandboxResumeOnStartupService fans out
        // via Task.Run with a SemaphoreSlim gate), so the recording lists MUST
        // be thread-safe — a plain List<T>.Add from two concurrent resumes
        // intermittently loses one entry, which historically surfaced as a
        // flaky "vm-1 not in ResumedNames" assertion.
        private readonly ConcurrentQueue<string> _resumedNames = new();
        private readonly ConcurrentQueue<AdoptionCall> _adoptionCalls = new();
        private readonly ConcurrentQueue<CheckpointPushCall> _checkpointPushCalls = new();
        public IReadOnlyList<string> ResumedNames => _resumedNames.ToArray();
        public IReadOnlyList<AdoptionCall> AdoptionCalls => _adoptionCalls.ToArray();
        public IReadOnlyList<CheckpointPushCall> CheckpointPushCalls => _checkpointPushCalls.ToArray();
        public bool ResumeThrows { get; set; }
        public int? AdoptionExitCodeToReturn { get; set; }
        public bool CheckpointPushReturns { get; set; } = true;
        public bool CheckpointPushThrows { get; set; }

        public void Register(WorkItemId id, ISuspendableSandbox sandbox) => _active[id] = sandbox;

        public string Name => "fake-suspending";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public IReadOnlyList<(WorkItemId WorkItemId, ISuspendableSandbox Sandbox)> SnapshotSuspendableActive()
        {
            var list = new List<(WorkItemId, ISuspendableSandbox)>();
            foreach (var kv in _active) list.Add((kv.Key, kv.Value));
            return list;
        }

        public Task ResumeSandboxAsync(string name, CancellationToken ct)
        {
            _resumedNames.Enqueue(name);
            if (ResumeThrows) throw new InvalidOperationException("simulated multipass failure");
            return Task.CompletedTask;
        }

        public Task<int?> WaitForAdoptedAgentCompletionAsync(
            string vmName,
            string agentLogPath,
            Action<string>? logSink,
            TimeSpan? deadline,
            CancellationToken ct)
        {
            _adoptionCalls.Enqueue(new AdoptionCall(vmName, agentLogPath, deadline));
            return Task.FromResult(AdoptionExitCodeToReturn);
        }

        public Task<bool> PushSuspendedVmCheckpointRefAsync(
            string vmName,
            string workingDir,
            string refName,
            string commitMessage,
            CancellationToken ct)
        {
            _checkpointPushCalls.Enqueue(new CheckpointPushCall(vmName, workingDir, refName, commitMessage));
            if (CheckpointPushThrows)
                throw new InvalidOperationException("simulated in-VM git push failure");
            return Task.FromResult(CheckpointPushReturns);
        }
    }

    private sealed record AdoptionCall(string VmName, string AgentLogPath, TimeSpan? Deadline);
    private sealed record CheckpointPushCall(string VmName, string WorkingDir, string RefName, string CommitMessage);

    private sealed class NonSuspendingProvider : ISandboxProvider
    {
        public string Name => "fake-non-suspending";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeSandboxLeakProvider : ISandboxProvider
    {
        private readonly List<ManagedSandboxInfo> _managed = new();
        public List<string> DisposedNames { get; } = new();

        public void SeedManaged(ManagedSandboxInfo info) => _managed.Add(info);

        public string Name => "fake-leak-provider";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(_managed);
        public Task DisposeLeakedAsync(string name, CancellationToken ct)
        {
            DisposedNames.Add(name);
            _managed.RemoveAll(m => m.Name == name);
            return Task.CompletedTask;
        }
    }

}
