using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        await WaitUntilAsync(() => Task.FromResult(condition()));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(1);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.True(await condition(), "condition was not met before the timeout elapsed");
    }

    private static void AssertResumeTimeoutHonored(TimeSpan elapsed, TimeSpan configuredTimeout)
    {
        // The slack here covers fixed-cost overhead unrelated to the timeout
        // itself: starting the dedicated LongRunning thread for the resume
        // task, the post-cancellation ObserveProviderTaskAfterCancellationAsync
        // 250ms grace, and the SQLite UPDATE that records the Failed state.
        // Under CI load each of these can dilate non-trivially. The bound is
        // generous enough that genuine timeout regressions (resume that never
        // honors the configured cap) still fall well outside it.
        var upperBound = configuredTimeout + TimeSpan.FromSeconds(3);
        Assert.True(elapsed < upperBound,
            $"configured {configuredTimeout} resume timeout was not honored promptly; elapsed {elapsed}");
    }

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

    // ── Shutdown teardown handler ──────────────────────────────────────────

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

        var svc = new SandboxShutdownTeardownService(
            provider, _store,
            NullLogger<SandboxShutdownTeardownService>.Instance,
            teardownMode: SandboxTeardownMode.Suspend);

        await svc.StartAsync(CancellationToken.None);
        await svc.TeardownAllAsync();

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

        var svc = new SandboxShutdownTeardownService(
            provider, _store,
            NullLogger<SandboxShutdownTeardownService>.Instance,
            teardownMode: SandboxTeardownMode.Suspend);
        await svc.TeardownAllAsync();

        // The handler persists the (work item → VM) mapping BEFORE awaiting
        // suspend, so suspend must actually have been attempted...
        Assert.True(sandbox.SuspendCalled);
        var after = await _store.GetAsync(item.Id);
        // ...and on a genuine (non-cancellation) suspend failure the handler
        // CLEARS the bookkeeping again: the VM is left Running and DisposeAsync
        // tears it down, so the item flows through the standard stranded-item
        // recovery path on the next start with no dangling resume mapping.
        Assert.Null(after!.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
    }

    [Fact]
    public async Task ShutdownHandler_WorkItemMissingFromStore_DoesNotSuspend()
    {
        // The persist-before-await guard: SuspendOneAsync calls
        // TryPersistSuspendBookkeepingAsync first, which returns false when the
        // work item is no longer in the store (e.g. operator-deleted between
        // dispatch and shutdown). In that case there is nowhere to record the
        // resume mapping, so the handler must NOT call multipass suspend — a
        // suspended-but-unmapped VM would just leak.
        var item = MakeItem();
        // Intentionally NOT created in the store.
        var sandbox = new FakeSuspendableSandbox("vm-orphan");
        var provider = new FakeSuspendingProvider();
        provider.Register(item.Id, sandbox);

        var svc = new SandboxShutdownTeardownService(
            provider, _store,
            NullLogger<SandboxShutdownTeardownService>.Instance,
            teardownMode: SandboxTeardownMode.Suspend);
        await svc.TeardownAllAsync();

        Assert.False(sandbox.SuspendCalled);
        Assert.Null(await _store.GetAsync(item.Id));
    }

    [Fact]
    public async Task ShutdownHandler_SuspendTimeout_PersistsVmNameSoResumeCanReattach()
    {
        // The suspend handler bounds each multipass suspend with a per-VM
        // timeout. When the inner suspend exceeds it (OperationCanceledException),
        // multipassd is still writing the RAM snapshot in the background and the
        // VM WILL reach Suspended — so the handler must leave the persisted
        // (work item → VM) mapping in place so the next startup can resume that
        // VM. (Regression: the timeout used to clear the mapping, which is why
        // R8-core's first real-world test fell back to stranded recovery and
        // discarded ~2.5h of in-flight work.)
        var item = MakeItem();
        await _store.CreateAsync(item);

        var sandbox = new SlowSuspendingSandbox("vm-slow");
        var provider = new FakeSuspendingProvider();
        provider.Register(item.Id, sandbox);

        var svc = new SandboxShutdownTeardownService(
            provider, _store,
            NullLogger<SandboxShutdownTeardownService>.Instance,
            teardownMode: SandboxTeardownMode.Suspend,
            perSuspendTimeout: TimeSpan.FromMilliseconds(50));
        await svc.TeardownAllAsync();

        var after = await _store.GetAsync(item.Id);
        // Mapping persisted up front and kept on timeout — the VM is still being
        // suspended by multipassd, so the startup resume handler must know about it.
        Assert.Equal("vm-slow", after!.SuspendedVmName);
        Assert.NotNull(after.SuspendedAt);
    }

    [Fact]
    public async Task ShutdownHandler_SuspendOneAsync_UsesRamScaledTimeout_NotTheFlatFloor()
    {
        // Regression guard for the core production fix: SuspendOneAsync must build
        // its per-VM CancellationTokenSource from SuspendTimeoutFor(sandbox)
        // (RAM-scaled), NOT the flat floor (_perSuspendTimeout). The reported bug
        // was a loaded 4 GiB VM timing out under the old uniform cap; if the
        // handler regressed to use the floor again, the SuspendTimeoutFor unit
        // tests would still pass while large VMs got only the floor.
        //
        // Construction: floor = 100ms (would cancel almost immediately), but the
        // RAM-scaled budget for 4 GiB is 4 × 3000ms = 12s. The fake suspend takes
        // ~500ms. Under the correct scaled budget it completes cleanly; a
        // regression to the 100ms floor would cancel it (OperationCanceledException).
        const long gib = 1024L * 1024 * 1024;
        var item = MakeItem();
        await _store.CreateAsync(item);

        var sandbox = new MemoryAwareDelaySandbox(
            "vm-4g", memoryBytes: 4 * gib, delay: TimeSpan.FromMilliseconds(500));
        var provider = new FakeSuspendingProvider();
        provider.Register(item.Id, sandbox);

        var svc = new SandboxShutdownTeardownService(
            provider, _store,
            NullLogger<SandboxShutdownTeardownService>.Instance,
            teardownMode: SandboxTeardownMode.Suspend,
            perSuspendTimeout: TimeSpan.FromMilliseconds(100),
            perGiBSuspendBudget: TimeSpan.FromMilliseconds(3000));

        // The budget SuspendOneAsync must apply is the scaled 12s, not the 100ms floor.
        Assert.Equal(TimeSpan.FromSeconds(12), svc.SuspendTimeoutFor(sandbox));

        await svc.TeardownAllAsync();

        Assert.True(sandbox.Completed,
            "suspend should run to completion under the RAM-scaled budget");
        Assert.False(sandbox.TimedOut,
            "suspend must not be cancelled by the flat floor — that is the regressed behaviour");

        var after = await _store.GetAsync(item.Id);
        Assert.Equal("vm-4g", after!.SuspendedVmName);
    }

    [Fact]
    public async Task ShutdownHandler_PersistsVmName_BEFORE_AwaitingSuspend()
    {
        // Acceptance criterion #3: the (work item → VM) mapping must be written
        // BEFORE the multipass suspend is awaited, so a SIGKILL landing mid-suspend
        // still leaves a resume mapping. Asserting the store only AFTER
        // TeardownAllAsync returns can't distinguish "persisted before the await"
        // from "persisted in the post-timeout handler" — both leave the same final
        // row. So here we read the store WHILE SuspendAsync is still blocked and
        // require the mapping to already be present.
        var item = MakeItem();
        await _store.CreateAsync(item);

        var sandbox = new SlowSuspendingSandbox("vm-ordering");
        var provider = new FakeSuspendingProvider();
        provider.Register(item.Id, sandbox);

        // Long per-VM timeout: the suspend stays blocked (it does NOT time out)
        // until we explicitly release it, so any persistence we observe must have
        // happened on the pre-await path, not in the OperationCanceledException
        // handler.
        var svc = new SandboxShutdownTeardownService(
            provider, _store,
            NullLogger<SandboxShutdownTeardownService>.Instance,
            teardownMode: SandboxTeardownMode.Suspend,
            perSuspendTimeout: TimeSpan.FromMinutes(5));

        var suspendAll = svc.TeardownAllAsync();

        // Wait until the suspend await has actually begun.
        await sandbox.SuspendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The mapping is already persisted even though SuspendAsync has NOT returned.
        var midFlight = await _store.GetAsync(item.Id);
        Assert.Equal("vm-ordering", midFlight!.SuspendedVmName);
        Assert.NotNull(midFlight.SuspendedAt);

        // Let the suspend complete cleanly so the handler finishes.
        sandbox.Release();
        await suspendAll;

        // Mapping survives a clean suspend too (resume reattaches the frozen VM).
        var after = await _store.GetAsync(item.Id);
        Assert.Equal("vm-ordering", after!.SuspendedVmName);
    }

    [Fact]
    public async Task SuspendTimeoutFor_ScalesWithVmMemory_AboveTheFloor()
    {
        // A 12 GiB VM has 3× the RAM of a 4 GiB VM to flush to disk, so it must
        // get a proportionally longer suspend timeout — a uniform cap would
        // truncate the large VM's snapshot. Below the floor (or with no reported
        // RAM size) the flat floor applies.
        var svc = new SandboxShutdownTeardownService(
            new FakeSuspendingProvider(), _store,
            NullLogger<SandboxShutdownTeardownService>.Instance,
            teardownMode: SandboxTeardownMode.Suspend,
            perSuspendTimeout: TimeSpan.FromMinutes(10),
            perGiBSuspendBudget: TimeSpan.FromSeconds(150));

        const long gib = 1024L * 1024 * 1024;

        // No reported memory → floor.
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            svc.SuspendTimeoutFor(new FakeSuspendableSandbox("vm-unknown")));

        // 4 GiB → 4 × 150s = 600s, equal to the floor.
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            svc.SuspendTimeoutFor(new FakeSuspendableSandbox("vm-4g", memoryBytes: 4 * gib)));

        // 8 GiB → 8 × 150s = 1200s = 20 min, strictly between floor and the
        // 12 GiB case so a wrong GiB divisor or off-by-one scaling can't pass by
        // coincidentally landing on the floor or a round endpoint.
        Assert.Equal(
            TimeSpan.FromMinutes(20),
            svc.SuspendTimeoutFor(new FakeSuspendableSandbox("vm-8g", memoryBytes: 8 * gib)));

        // 12 GiB → 12 × 150s = 1800s, well above the floor.
        Assert.Equal(
            TimeSpan.FromMinutes(30),
            svc.SuspendTimeoutFor(new FakeSuspendableSandbox("vm-12g", memoryBytes: 12 * gib)));

        // 1 GiB → 150s, below the floor → floor wins (catches a min/max swap:
        // min would yield 150s here, not the 10-min floor).
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            svc.SuspendTimeoutFor(new FakeSuspendableSandbox("vm-1g", memoryBytes: 1 * gib)));

        // Zero / negative reported RAM is treated the same as "unknown" → floor,
        // never a zero or negative timeout that would cancel suspend instantly.
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            svc.SuspendTimeoutFor(new FakeSuspendableSandbox("vm-0", memoryBytes: 0)));
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            svc.SuspendTimeoutFor(new FakeSuspendableSandbox("vm-neg", memoryBytes: -1)));
    }

    [Fact]
    public void SuspendTimeoutFor_DefaultConstructor_UsesShippedConstants()
    {
        // The production DI registration constructs this service WITHOUT
        // injected timeouts, so the regression fix only holds if the default
        // constructor actually applies the raised floor (10 min) and the
        // RAM-scaling budget (150s/GiB). Pin both via wall-clock literals so a
        // silent revert of either constant fails here.
        Assert.Equal(TimeSpan.FromMinutes(10), SandboxShutdownTeardownService.DefaultPerSuspendTimeout);
        Assert.Equal(TimeSpan.FromSeconds(150), SandboxShutdownTeardownService.DefaultPerGiBSuspendBudget);

        var svc = new SandboxShutdownTeardownService(
            new FakeSuspendingProvider(), _store,
            NullLogger<SandboxShutdownTeardownService>.Instance,
            teardownMode: SandboxTeardownMode.Suspend);

        const long gib = 1024L * 1024 * 1024;

        // No reported RAM → the 10-minute default floor.
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            svc.SuspendTimeoutFor(new FakeSuspendableSandbox("vm-default")));

        // 20 GiB → 20 × 150s = 3000s = 50 min, scaled off the default budget.
        Assert.Equal(
            TimeSpan.FromMinutes(50),
            svc.SuspendTimeoutFor(new FakeSuspendableSandbox("vm-20g", memoryBytes: 20 * gib)));
    }

    [Fact]
    public void HostShutdownReserve_ScalesByParallelSuspendWaveCount()
    {
        const long gib = 1024L * 1024 * 1024;
        var floor = TimeSpan.FromMinutes(10);
        var perGiB = TimeSpan.FromSeconds(150);
        // Default 12 GiB profile → 30 min per VM.
        var perVm = SuspendTimeoutPolicy.For(12 * gib, floor, perGiB);
        Assert.Equal(TimeSpan.FromMinutes(30), perVm);

        // ≤ batch size → a single wave, ceiling == one per-VM budget.
        Assert.Equal(perVm, SuspendTimeoutPolicy.HostShutdownReserve(1, 8, 12 * gib, floor, perGiB));
        Assert.Equal(perVm, SuspendTimeoutPolicy.HostShutdownReserve(8, 8, 12 * gib, floor, perGiB));

        // 9 VMs across batches of 8 → 2 waves → 60 min. This is the regression the
        // single-wave ceiling missed: the host must not SIGKILL before wave 2.
        Assert.Equal(perVm * 2, SuspendTimeoutPolicy.HostShutdownReserve(9, 8, 12 * gib, floor, perGiB));
        Assert.Equal(perVm * 2, SuspendTimeoutPolicy.HostShutdownReserve(16, 8, 12 * gib, floor, perGiB));

        // 17 VMs → 3 waves → 90 min.
        Assert.Equal(perVm * 3, SuspendTimeoutPolicy.HostShutdownReserve(17, 8, 12 * gib, floor, perGiB));

        // Defensive clamps: zero / negative inputs never yield a zero-wave or
        // divide-by-zero ceiling — at minimum one wave of the floor.
        Assert.Equal(floor, SuspendTimeoutPolicy.HostShutdownReserve(0, 8, null, floor, perGiB));
        Assert.Equal(floor, SuspendTimeoutPolicy.HostShutdownReserve(1, 0, null, floor, perGiB));
    }

    [Fact]
    public void ResolveHostShutdownTimeout_RaisesCeilingForSuspendingProvidersOnly()
    {
        var grace = TimeSpan.FromSeconds(60);

        // Providers that don't suspend on shutdown → ceiling stays at the grace
        // window regardless of worker count. The decision is capability-driven
        // (providerSupportsSuspend=false), not a provider-name comparison.
        Assert.Equal(grace,
            SuspendTimeoutPolicy.ResolveHostShutdownTimeout(false, grace, 32));

        // Suspend-capable provider with a single worker → one wave of the default
        // 12 GiB profile budget (30 min), STACKED on top of the 60s drain grace:
        // the suspend drain (StoppingAsync) and the post-suspend preempt-checkpoint
        // / listener drain are sequential windows, not overlapping.
        Assert.Equal(TimeSpan.FromMinutes(30) + grace,
            SuspendTimeoutPolicy.ResolveHostShutdownTimeout(true, grace, 1));

        // More workers than the parallel-suspend cap (8) → the reserve scales by
        // wave count (16 workers → 2 waves → 60 min), again plus the drain grace.
        Assert.Equal(TimeSpan.FromMinutes(60) + grace,
            SuspendTimeoutPolicy.ResolveHostShutdownTimeout(true, grace, 16));

        // A long configured grace is ADDED to the suspend reserve, not max()'d
        // against it: 4h grace + one 30-min wave = 4h30m.
        var hugeGrace = TimeSpan.FromHours(4);
        Assert.Equal(hugeGrace + TimeSpan.FromMinutes(30),
            SuspendTimeoutPolicy.ResolveHostShutdownTimeout(true, hugeGrace, 1));
    }

    [Fact]
    public async Task ShutdownHandler_NonSuspendingProvider_IsNoOp()
    {
        var item = MakeItem();
        await _store.CreateAsync(item);

        // A provider that doesn't implement IActiveSandboxProvider — e.g.
        // process or bubblewrap. The handler must silently skip and leave the
        // suspend fields untouched so the existing PreemptCheckpoint flow
        // continues to be the recovery mechanism.
        var nonSuspending = new NonSuspendingProvider();
        var svc = new SandboxShutdownTeardownService(
            nonSuspending, _store,
            NullLogger<SandboxShutdownTeardownService>.Instance,
            teardownMode: SandboxTeardownMode.Suspend);
        await svc.TeardownAllAsync();

        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.SuspendedVmName);
    }

    [Fact]
    public async Task ShutdownHandler_SuspendMode_NonSuspendableActiveSandbox_LeavesNormalRecoveryPath()
    {
        var item = MakeItem();
        await _store.CreateAsync(item);

        var sandbox = new NonSuspendableShutdownSandbox("vm-no-suspend");
        var provider = new FakeSuspendingProvider();
        provider.Register(item.Id, sandbox);

        var svc = new SandboxShutdownTeardownService(
            provider, _store,
            NullLogger<SandboxShutdownTeardownService>.Instance,
            teardownMode: SandboxTeardownMode.Suspend);
        await svc.TeardownAllAsync();

        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
        Assert.Null(after.AgentLogPath);
        Assert.False(sandbox.DisposeCalled);
        Assert.False(sandbox.IsOwnedByShutdownHandler);
    }

    [Fact]
    public async Task ShutdownHandler_InvalidTeardownMode_ThrowsInsteadOfFallingThrough()
    {
        var item = MakeItem();
        await _store.CreateAsync(item);

        var sandbox = new NonSuspendableShutdownSandbox("vm-invalid-mode");
        var provider = new FakeSuspendingProvider();
        provider.Register(item.Id, sandbox);

        var svc = new SandboxShutdownTeardownService(
            provider, _store,
            NullLogger<SandboxShutdownTeardownService>.Instance,
            teardownMode: (SandboxTeardownMode)42);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.TeardownAllAsync());
        Assert.Contains("SandboxTeardownMode 42 is not handled", ex.Message);
        Assert.False(sandbox.DisposeCalled);
        Assert.False(sandbox.IsOwnedByShutdownHandler);
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
    public async Task StartupResume_WithAdmissionWrapper_PurgesSuccessfulResumeAndReleasesAdmission()
    {
        var item = MakeItem();
        await _store.CreateAsync(item with { SuspendedVmName = "vm-retained", SuspendedAt = DateTimeOffset.UtcNow });

        var inner = new FakeSuspendingProvider();
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        Assert.Equal(0, admission.CurrentAdmittedSandboxes);
        Assert.Contains("vm-retained", inner.DisposedNames);
        await using var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "test" }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, admission.CurrentAdmittedSandboxes);
    }

    [Fact]
    public async Task StartupResume_WithAdmissionWrapper_RetainsAdmissionWhenPurgeFails()
    {
        var item = MakeItem();
        await _store.CreateAsync(item with { SuspendedVmName = "vm-purge-fails", SuspendedAt = DateTimeOffset.UtcNow });

        var inner = new FakeSuspendingProvider { DisposeThrows = true };
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        Assert.Equal(1, admission.CurrentAdmittedSandboxes);
        var queued = provider.CreateAsync(new SandboxSpec { ImageReference = "test" }, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(queued.IsCompleted);

        inner.DisposeThrows = false;
        await provider.DisposeLeakedAsync("vm-purge-fails", CancellationToken.None);
        await using var sandbox = await queued.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Contains("vm-purge-fails", inner.DisposedNames);
        Assert.Equal(1, admission.CurrentAdmittedSandboxes);
    }

    [Fact]
    public async Task StartupResume_WithAdmissionWrapper_ReleasesFailedResumeAdmission()
    {
        var item = MakeItem();
        await _store.CreateAsync(item with { SuspendedVmName = "vm-failed-release", SuspendedAt = DateTimeOffset.UtcNow });

        var inner = new FakeSuspendingProvider { ResumeThrows = true };
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        Assert.Equal(0, admission.CurrentAdmittedSandboxes);
        await using var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "test" }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, admission.CurrentAdmittedSandboxes);
    }

    [Fact]
    public async Task StartupResume_WithAdmissionWrapper_ResumeTimeoutRetainsAdmissionUntilLeakDisposal()
    {
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-timeout-retained",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var resumeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new FakeSuspendingProvider
        {
            ResumeReleaseSources = new Dictionary<string, TaskCompletionSource>(StringComparer.Ordinal)
            {
                ["vm-timeout-retained"] = resumeRelease,
            },
        };
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);
        var svc = new SandboxResumeOnStartupService(
            provider,
            _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            NoopStartupRecoveryInputSink.Instance,
            resumeTimeout: TimeSpan.FromMilliseconds(50));

        await svc.ResumeAllForTestAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, admission.CurrentAdmittedSandboxes);
        var queued = provider.CreateAsync(new SandboxSpec { ImageReference = "test" }, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(queued.IsCompleted);

        resumeRelease.SetResult();
        await provider.DisposeLeakedAsync("vm-timeout-retained", CancellationToken.None);
        await using var sandbox = await queued.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, admission.CurrentAdmittedSandboxes);
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
        var log = new CapturingLogger<SandboxResumeOnStartupService>();
        var svc = new SandboxResumeOnStartupService(
            provider,
            _store,
            log,
            NoopStartupRecoveryInputSink.Instance);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, after!.State);
        Assert.Null(after!.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
        Assert.Contains(log.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Properties.TryGetValue("VmName", out var vmName)
            && string.Equals(vmName?.ToString(), "vm-gone", StringComparison.Ordinal)
            && entry.Properties.TryGetValue("WorkItemId", out var workItemId)
            && string.Equals(workItemId?.ToString(), item.Id.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartupResume_ResumeTimeout_MarksWorkingItemFailedAndClearsBookkeeping()
    {
        var configuredTimeout = TimeSpan.FromMilliseconds(50);
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-hung-start",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var provider = new FakeSuspendingProvider { ResumeHangs = true };
        var log = new CapturingLogger<SandboxResumeOnStartupService>();
        var svc = new SandboxResumeOnStartupService(
            provider,
            _store,
            log,
            NoopStartupRecoveryInputSink.Instance,
            resumeTimeout: configuredTimeout);

        var sw = Stopwatch.StartNew();
        await svc.ResumeAllForTestAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, after!.State);
        Assert.Null(after.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Contains("timed out", after.LastError);
        Assert.Contains($"timed out after {configuredTimeout}", after.LastError);
        Assert.Contains(log.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            && entry.Properties.TryGetValue("VmName", out var vmName)
            && string.Equals(vmName?.ToString(), "vm-hung-start", StringComparison.Ordinal)
            && entry.Properties.TryGetValue("WorkItemId", out var workItemId)
            && string.Equals(workItemId?.ToString(), item.Id.ToString(), StringComparison.Ordinal));
        AssertResumeTimeoutHonored(sw.Elapsed, configuredTimeout);
    }

    [Fact]
    public async Task StartupResume_ProviderCancellation_MarksWorkingItemFailedAndClearsBookkeeping()
    {
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-provider-cancel",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var provider = new FakeSuspendingProvider { ResumeThrowsCancellation = true };
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, after!.State);
        Assert.Null(after.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Contains("provider cancelled resume", after.LastError);
    }

    [Fact]
    public async Task StartupResume_ResumeFailure_WithPreemptCheckpoint_RemainsRecoverable()
    {
        var item = MakeItem(WorkItemState.Working);
        var checkpoint = $"refs/heads/codeybox/preempt/{item.Id}";
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-failed-with-checkpoint",
            SuspendedAt = DateTimeOffset.UtcNow,
            PreemptCheckpoint = checkpoint,
        });

        var provider = new FakeSuspendingProvider { ResumeThrows = true };
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Equal(checkpoint, after.PreemptCheckpoint);
        Assert.Null(after.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
        Assert.Equal(0, after.RecoveryAttempts);
    }

    [Fact]
    public async Task StartupResume_ProvisioningDeferred_RequeuesWithoutFailingAndSchedulesDelay()
    {
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-start-deferred",
            SuspendedAt = DateTimeOffset.UtcNow,
            LastError = "previous infrastructure failure",
            FailureKind = "other",
        });
        var deferred = new SandboxProvisioningDeferredException(
            provider: "multipass",
            operation: "start",
            errorClass: "multipass-start-argument-not-found",
            detail: "start retry exhausted",
            recheckIn: TimeSpan.FromMilliseconds(75));
        var provider = new FakeSuspendingProvider { ResumeProvisioningDeferred = deferred };
        var scheduler = new RecordingInfrastructureDeferralScheduler();
        var svc = new SandboxResumeOnStartupService(
            provider,
            _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            NoopStartupRecoveryInputSink.Instance,
            infrastructureDeferrals: scheduler);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Null(after.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
        Assert.Null(after.AgentLogPath);
        Assert.Null(after.LastError);
        Assert.Null(after.FailureKind);
        Assert.Equal(0, after.RecoveryAttempts);
        var scheduled = Assert.Single(scheduler.Scheduled);
        Assert.Equal(item.Id, scheduled.Id);
        Assert.Equal(deferred.RecheckIn, scheduled.Delay);
    }

    [Theory]
    [InlineData(WorkItemState.Auditing)]
    [InlineData(WorkItemState.Reworking)]
    [InlineData(WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.Merging)]
    [InlineData(WorkItemState.ReworkingForConflict)]
    public async Task StartupResume_ResumeFailure_ForNonWorkingSuspendedItems_ClearsBookkeepingWithoutFailing(
        WorkItemState state)
    {
        var item = MakeItem(state);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = $"vm-{state}",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var provider = new FakeSuspendingProvider { ResumeThrows = true };
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(state, after!.State);
        Assert.Null(after.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
        Assert.Null(after.LastError);
        Assert.Equal(0, after.RecoveryAttempts);
    }

    [Fact]
    public async Task StartupResume_BlockingMode_RunsFromStartingAsyncAndHonorsTimeout()
    {
        var configuredTimeout = TimeSpan.FromMilliseconds(50);
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-blocking-timeout",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var provider = new FakeSuspendingProvider { ResumeHangs = true };
        var svc = new SandboxResumeOnStartupService(
            provider,
            _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            NoopStartupRecoveryInputSink.Instance,
            resumeTimeout: configuredTimeout,
            mode: SandboxStartupResumeMode.Blocking);

        var sw = Stopwatch.StartNew();
        await svc.StartingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();
        await svc.StartAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, after!.State);
        Assert.Null(after.SuspendedVmName);
        Assert.Single(provider.ResumedNames);
        Assert.Contains($"timed out after {configuredTimeout}", after.LastError);
        AssertResumeTimeoutHonored(sw.Elapsed, configuredTimeout);
    }

    [Fact]
    public async Task StartupResume_ProviderBlocksBeforeReturningTask_StillHonorsTimeout()
    {
        var configuredTimeout = TimeSpan.FromMilliseconds(50);
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-sync-blocking-provider",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var provider = new FakeSuspendingProvider { ResumeBlocksBeforeReturningTask = true };
        var svc = new SandboxResumeOnStartupService(
            provider,
            _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            NoopStartupRecoveryInputSink.Instance,
            resumeTimeout: configuredTimeout,
            mode: SandboxStartupResumeMode.Blocking);

        var sw = Stopwatch.StartNew();
        var resumeTask = Task.Run(() => svc.StartingAsync(CancellationToken.None));
        await provider.ResumeBlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            await resumeTask.WaitAsync(TimeSpan.FromSeconds(5));
            sw.Stop();
        }
        finally
        {
            provider.ReleaseBlockedResume();
        }

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, after!.State);
        Assert.Null(after.SuspendedVmName);
        Assert.Contains("timed out", after.LastError);
        Assert.Contains($"timed out after {configuredTimeout}", after.LastError);
        AssertResumeTimeoutHonored(sw.Elapsed, configuredTimeout);
    }

    [Fact]
    public async Task StartupResume_ModeReloadBetweenLifecycleCallbacks_UsesReloadedBlockingMode()
    {
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-mode-race",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var provider = new FakeSuspendingProvider { ResumeBlocksBeforeReturningTask = true };
        var barrier = new StartupRecoveryBarrier();
        var options = new SandboxStartupResumeOptions
        {
            Mode = SandboxStartupResumeMode.Background,
            ResumeTimeout = TimeSpan.FromSeconds(5),
        };
        var svc = new SandboxResumeOnStartupService(
            provider,
            _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            () => options,
            barrier);

        await svc.StartingAsync(CancellationToken.None);
        options = options with { Mode = SandboxStartupResumeMode.Blocking };
        var startTask = svc.StartAsync(CancellationToken.None);

        await provider.ResumeBlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(startTask.IsCompleted);
        provider.ReleaseBlockedResume();
        await startTask.WaitAsync(TimeSpan.FromSeconds(1));

        await barrier.RecoveryInputReady.WaitAsync(TimeSpan.FromSeconds(1));
        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Null(after!.SuspendedVmName);
        Assert.Contains("vm-mode-race", provider.ResumedNames);
    }

    [Fact]
    public async Task StartupResume_CancellationObservingTimeout_MarksFailedInsteadOfHostCancellation()
    {
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-cancel-observing",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var provider = new FakeSuspendingProvider { ResumeObservesCancellation = true };
        var svc = new SandboxResumeOnStartupService(
            provider,
            _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            NoopStartupRecoveryInputSink.Instance,
            resumeTimeout: TimeSpan.FromMilliseconds(50));

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, after!.State);
        Assert.Null(after.SuspendedVmName);
        Assert.True(provider.ResumeCancellationObserved);
        Assert.Contains("timed out", after.LastError);
    }

    [Fact]
    public async Task StartupResume_ReloadedOptions_AreReadForLaterResumeAndAdoption()
    {
        var gate = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(gate with
        {
            SuspendedVmName = "vm-0-hot-gate",
            SuspendedAt = DateTimeOffset.UtcNow,
        });
        var hung = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(hung with
        {
            SuspendedVmName = "vm-1-hot-timeout",
            SuspendedAt = DateTimeOffset.UtcNow,
        });
        var adopted = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(adopted with
        {
            SuspendedVmName = "vm-2-hot-adoption",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/hot.log",
        });

        var options = new SandboxStartupResumeOptions
        {
            Mode = SandboxStartupResumeMode.Background,
            MaxParallelResumes = 1,
            ResumeTimeout = TimeSpan.FromSeconds(30),
            AdoptionDeadline = TimeSpan.FromMinutes(30),
        };
        var gateRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeSuspendingProvider
        {
            ResumeReleaseSources = new Dictionary<string, TaskCompletionSource>(StringComparer.Ordinal)
            {
                ["vm-0-hot-gate"] = gateRelease,
            },
            ResumeNamesToHang = new HashSet<string>(StringComparer.Ordinal) { "vm-1-hot-timeout" },
            AdoptionExitCodeToReturn = 0,
        };
        var svc = new SandboxResumeOnStartupService(
            provider,
            _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            () => options,
            NoopStartupRecoveryInputSink.Instance);

        var resumeTask = svc.ResumeAllForTestAsync(CancellationToken.None);
        await WaitUntilAsync(() => provider.ResumedNames.Contains("vm-0-hot-gate"));

        var hotDeadline = TimeSpan.FromSeconds(7);
        options = options with
        {
            ResumeTimeout = TimeSpan.FromMilliseconds(50),
            AdoptionDeadline = hotDeadline,
        };
        gateRelease.SetResult();

        await resumeTask.WaitAsync(TimeSpan.FromSeconds(2));

        var timedOut = await _store.GetAsync(hung.Id);
        Assert.Equal(WorkItemState.Failed, timedOut!.State);
        Assert.Contains("timed out", timedOut.LastError);

        var adoption = Assert.Single(provider.AdoptionCalls);
        Assert.Equal("vm-2-hot-adoption", adoption.VmName);
        Assert.Equal(hotDeadline, adoption.Deadline);
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
    public async Task StartupResume_NoSuspendedItems_CompletesBackgroundBarrier()
    {
        var barrier = new StartupRecoveryBarrier();
        var svc = new SandboxResumeOnStartupService(
            new FakeSuspendingProvider(),
            _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            barrier);

        await svc.StartAsync(CancellationToken.None);

        await barrier.RecoveryInputReady.WaitAsync(TimeSpan.FromSeconds(5));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupResume_BackgroundResume_IgnoresStartupTokenCancellationAfterStart()
    {
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-startup-token-cancel",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var provider = new FakeSuspendingProvider { ResumeHangs = true };
        var barrier = new StartupRecoveryBarrier();
        var svc = new SandboxResumeOnStartupService(
            provider,
            _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            barrier,
            resumeTimeout: TimeSpan.FromMilliseconds(50),
            mode: SandboxStartupResumeMode.Background);

        using var startupCts = new CancellationTokenSource();
        await svc.StartAsync(startupCts.Token);
        startupCts.Cancel();

        await barrier.RecoveryInputReady.WaitAsync(TimeSpan.FromSeconds(1));

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, after!.State);
        Assert.Null(after.SuspendedVmName);
        Assert.Contains("timed out", after.LastError);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupResume_StopAsync_CancelsBackgroundResume_AndIsIdempotent()
    {
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-stop-cancel",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var provider = new FakeSuspendingProvider { ResumeObservesCancellation = true };
        var barrier = new StartupRecoveryBarrier();
        var svc = new SandboxResumeOnStartupService(
            provider,
            _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            barrier,
            resumeTimeout: TimeSpan.FromHours(1),
            mode: SandboxStartupResumeMode.Background);

        await svc.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => provider.ResumedNames.Contains("vm-stop-cancel"));

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await svc.StopAsync(stopCts.Token);
        await barrier.RecoveryInputReady.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => provider.ResumeCancellationObserved);

        Assert.True(provider.ResumeCancellationObserved);
        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Equal("vm-stop-cancel", after.SuspendedVmName);
        Assert.NotNull(after.SuspendedAt);
        Assert.Null(after.LastError);
        Assert.Equal(0, after.RecoveryAttempts);

        // Host/factory disposal paths may call StopAsync more than once.
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupResume_BackgroundRecoveryInput_StaysBlockedDuringSlowAdoption()
    {
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-slow-input",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/slow-input.log",
        });

        var barrier = new StartupRecoveryBarrier();
        var provider = new FakeSuspendingProvider
        {
            AdoptionResultSource = new TaskCompletionSource<int?>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var svc = new SandboxResumeOnStartupService(
            provider,
            _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            barrier,
            adoptionDeadline: TimeSpan.FromSeconds(30),
            mode: SandboxStartupResumeMode.Background);

        await svc.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => provider.AdoptionCalls.Count == 1);

            Assert.False(barrier.RecoveryInputReady.IsCompleted);
            var duringAdoption = await _store.GetAsync(item.Id);
            Assert.Equal("vm-slow-input", duringAdoption!.SuspendedVmName);

            provider.AdoptionResultSource!.SetResult(0);
            await barrier.RecoveryInputReady.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
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
            NullLogger<SandboxResumeOnStartupService>.Instance,
            NoopStartupRecoveryInputSink.Instance);
        await svc.ResumeAllForTestAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal("vm-orphan", after!.SuspendedVmName);
        Assert.NotNull(after.SuspendedAt);
    }

    [Fact]
    public async Task StartupResume_NonSuspendingProvider_CompletesBackgroundBarrier()
    {
        var barrier = new StartupRecoveryBarrier();
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-no-provider-barrier",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var svc = new SandboxResumeOnStartupService(
            new NonSuspendingProvider(), _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            barrier);

        await svc.StartAsync(CancellationToken.None);

        await barrier.RecoveryInputReady.WaitAsync(TimeSpan.FromSeconds(1));
        await svc.StopAsync(CancellationToken.None);
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
            NullLogger<SandboxResumeOnStartupService>.Instance,
            NoopStartupRecoveryInputSink.Instance);

        await svc.ResumeAllForTestAsync(CancellationToken.None);
        // Rows untouched — operator-visible signal that the suspended VM
        // bookkeeping was inherited from a different configuration.
        var after = await _store.GetAsync(item.Id);
        Assert.Equal("vm-old", after!.SuspendedVmName);
    }

    [Fact]
    public async Task StartupResume_NullProvider_CompletesBackgroundBarrier()
    {
        var barrier = new StartupRecoveryBarrier();
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-null-provider-barrier",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var svc = new SandboxResumeOnStartupService(
            provider: null, _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            barrier);

        await svc.StartAsync(CancellationToken.None);

        await barrier.RecoveryInputReady.WaitAsync(TimeSpan.FromSeconds(1));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupResume_BackgroundSweepException_CompletesBarrier()
    {
        var barrier = new StartupRecoveryBarrier();
        var svc = new SandboxResumeOnStartupService(
            new FakeSuspendingProvider(),
            new StubWorkItemStore(),
            NullLogger<SandboxResumeOnStartupService>.Instance,
            barrier);

        await svc.StartAsync(CancellationToken.None);

        await barrier.RecoveryInputReady.WaitAsync(TimeSpan.FromSeconds(1));
        await svc.StopAsync(CancellationToken.None);
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

    // ── Adoption deadline wiring ─────────────────────────────────────────────

    [Fact]
    public async Task AdoptionDeadline_ConfiguredValue_IsPassedToProvider()
    {
        // Verifies the SandboxAdoptionDeadlineSeconds option flows through the
        // startup resume wiring into WaitForAdoptedAgentCompletionAsync.
        var customDeadline = TimeSpan.FromMinutes(10);
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-dl-config",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/abc.log",
        });

        var provider = new FakeSuspendingProvider { AdoptionExitCodeToReturn = 0 };
        var svc = new SandboxResumeOnStartupService(
            provider, _store, NullLogger<SandboxResumeOnStartupService>.Instance,
            NoopStartupRecoveryInputSink.Instance,
            adoptionDeadline: customDeadline);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        var adoption = Assert.Single(provider.AdoptionCalls);
        Assert.Equal("vm-dl-config", adoption.VmName);
        Assert.Equal(customDeadline, adoption.Deadline);
    }

    [Fact]
    public async Task AdoptionDeadline_AboveMaximum_IsCappedBeforeProvider()
    {
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-dl-max",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/max.log",
        });

        var provider = new FakeSuspendingProvider { AdoptionExitCodeToReturn = 0 };
        var svc = new SandboxResumeOnStartupService(
            provider, _store, NullLogger<SandboxResumeOnStartupService>.Instance,
            NoopStartupRecoveryInputSink.Instance,
            adoptionDeadline: SandboxResumeOnStartupService.MaximumAdoptionDeadline + TimeSpan.FromTicks(1));

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        var adoption = Assert.Single(provider.AdoptionCalls);
        Assert.Equal("vm-dl-max", adoption.VmName);
        Assert.Equal(SandboxResumeOnStartupService.MaximumAdoptionDeadline, adoption.Deadline);
    }

    [Fact]
    public async Task AdoptionDeadline_DefaultValue_IsUsedWhenNotConfigured()
    {
        // Without an explicit adoptionDeadline the constructor must fall back
        // to DefaultAdoptionDeadline (30 min) — the same value that the
        // SandboxAdoptionDeadlineSeconds config key defaults to.
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-dl-default",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/abc.log",
        });

        var provider = new FakeSuspendingProvider { AdoptionExitCodeToReturn = 0 };
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        var adoption = Assert.Single(provider.AdoptionCalls);
        Assert.Equal("vm-dl-default", adoption.VmName);
        Assert.Equal(SandboxResumeOnStartupService.DefaultAdoptionDeadline, adoption.Deadline);
    }

    [Fact]
    public async Task AdoptionDeadline_ZeroOrNegative_ClampedToDefault()
    {
        // The constructor guard (d > TimeSpan.Zero) must reject TimeSpan.Zero
        // and negative values, falling back to DefaultAdoptionDeadline so a
        // misconfiguration doesn't produce an immediate timeout.
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-dl-zero",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/abc.log",
        });

        var provider = new FakeSuspendingProvider { AdoptionExitCodeToReturn = 0 };
        var svc = new SandboxResumeOnStartupService(
            provider, _store, NullLogger<SandboxResumeOnStartupService>.Instance,
            NoopStartupRecoveryInputSink.Instance,
            adoptionDeadline: TimeSpan.Zero);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        var adoption = Assert.Single(provider.AdoptionCalls);
        Assert.Equal("vm-dl-zero", adoption.VmName);
        Assert.Equal(SandboxResumeOnStartupService.DefaultAdoptionDeadline, adoption.Deadline);
    }

    [Fact]
    public async Task AdoptionDeadline_HungProviderWait_IsBoundedByConfiguredDeadline()
    {
        var configuredDeadline = TimeSpan.FromMilliseconds(50);
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-adoption-hang",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/hung.log",
        });

        var provider = new FakeSuspendingProvider
        {
            AdoptionResultSource = new TaskCompletionSource<int?>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var log = new CapturingLogger<SandboxResumeOnStartupService>();
        var svc = new SandboxResumeOnStartupService(
            provider,
            _store,
            log,
            NoopStartupRecoveryInputSink.Instance,
            adoptionDeadline: configuredDeadline);

        var sw = Stopwatch.StartNew();
        await svc.ResumeAllForTestAsync(CancellationToken.None);
        sw.Stop();

        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.SuspendedVmName);
        Assert.Null(after.AgentLogPath);
        Assert.Contains(log.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("adoption timed out", StringComparison.OrdinalIgnoreCase)
            && entry.Properties.TryGetValue("VmName", out var vmName)
            && string.Equals(vmName?.ToString(), "vm-adoption-hang", StringComparison.Ordinal)
            && entry.Properties.TryGetValue("WorkItemId", out var workItemId)
            && string.Equals(workItemId?.ToString(), item.Id.ToString(), StringComparison.Ordinal));
        Assert.True(sw.Elapsed < configuredDeadline + TimeSpan.FromSeconds(1),
            $"configured {configuredDeadline} adoption deadline was not honored; elapsed {sw.Elapsed}");
    }

    [Theory]
    [InlineData("sync-cancel")]
    [InlineData("async-cancel")]
    [InlineData("sync-fault")]
    [InlineData("async-fault")]
    public async Task StartupResume_AdoptionProviderCancellationOrFault_ClearsBookkeepingWithoutPromotingCheckpoint(
        string behavior)
    {
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = $"vm-adoption-{behavior}",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/fault.log",
        });

        var provider = new FakeSuspendingProvider
        {
            AdoptionThrowsCancellation = behavior == "sync-cancel",
            AdoptionFaultsCancellation = behavior == "async-cancel",
            AdoptionThrows = behavior == "sync-fault",
            AdoptionFaults = behavior == "async-fault",
        };
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        Assert.Single(provider.AdoptionCalls);
        Assert.Empty(provider.CheckpointPushCalls);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Null(after.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
        Assert.Null(after.AgentLogPath);
        Assert.Null(after.PreemptCheckpoint);
        Assert.Null(after.PreemptedAt);
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
    public async Task StartupResume_AdoptionExit0_ButPushProviderCancels_DoesNotSetCheckpoint()
    {
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-push-cancel",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/c.log",
        });

        var provider = new FakeSuspendingProvider
        {
            AdoptionExitCodeToReturn = 0,
            CheckpointPushThrowsCancellation = true,
        };
        var svc = MakeResumeService(provider);

        await svc.ResumeAllForTestAsync(CancellationToken.None);

        Assert.Single(provider.CheckpointPushCalls);
        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.PreemptCheckpoint);
        Assert.Null(after.SuspendedVmName);
        Assert.Null(after.AgentLogPath);
    }

    [Fact]
    public async Task StartupResume_AdoptionExit0_ButPushHangs_IsBoundedByResumeTimeout()
    {
        // Unlike the resume-hangs bound tests (which can use 50ms because the
        // resume itself is what's being cancelled), this test needs the resume
        // and adoption phases to SUCCEED so the promotion path is reached and
        // CheckpointPushCalls gets recorded. The resume side spins up a
        // LongRunning thread to call ResumeSandboxAsync; under CI load the
        // thread can take well over a second to be scheduled, and if the
        // resume-side timeout fires first the promotion never runs and the
        // Assert.Single below sees an empty queue. 3s is far above the
        // observed scheduling lag (≤2s on the worst CI runs) yet still well
        // under the 15s outer WaitAsync, so the bound (push hang cancelled by
        // resume timeout) is still verified.
        var configuredTimeout = TimeSpan.FromSeconds(3);
        var item = MakeItem();
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-push-hangs",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/h.log",
        });

        var provider = new FakeSuspendingProvider
        {
            AdoptionExitCodeToReturn = 0,
            CheckpointPushHangs = true,
        };
        var svc = new SandboxResumeOnStartupService(
            provider,
            _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            NoopStartupRecoveryInputSink.Instance,
            resumeTimeout: configuredTimeout);

        await svc.ResumeAllForTestAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Single(provider.CheckpointPushCalls);
        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.PreemptCheckpoint);
        Assert.Null(after.SuspendedVmName);
        Assert.Null(after.AgentLogPath);
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

    [Fact]
    public async Task DeadWorkerReaper_PeriodicSweep_WaitsForSlowStartupAdoption()
    {
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-slow-adopt",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/slow.log",
        });

        using var registry = new SqliteWorkerRegistry(_dbPath);
        await registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = "stale-worker",
            HostName = "host",
            ProcessId = 123,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CurrentWorkItemId = item.Id.ToString(),
        });

        var barrier = new StartupRecoveryBarrier();
        var queue = new InMemoryTaskQueue();
        var provider = new FakeSuspendingProvider
        {
            AdoptionResultSource = new TaskCompletionSource<int?>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var resume = new SandboxResumeOnStartupService(
            provider,
            _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            barrier);
        var reaper = new DeadWorkerReaper(
            registry,
            _store,
            queue,
            new DeadWorkerOptions
            {
                HeartbeatInterval = TimeSpan.FromMilliseconds(5),
                DeadWorkerThreshold = TimeSpan.FromMilliseconds(20),
                CheckInterval = TimeSpan.FromMilliseconds(20),
                MaxRecoveryAttempts = 2,
            },
            NullLogger<DeadWorkerReaper>.Instance,
            new NullWebhookDispatcher(),
            startupRecoveryBarrier: barrier);

        await reaper.StartAsync(CancellationToken.None);
        try
        {
            await resume.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => provider.AdoptionCalls.Count == 1);

            await Task.Delay(TimeSpan.FromMilliseconds(120));
            var duringAdoption = await _store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Working, duringAdoption!.State);
            Assert.Equal("vm-slow-adopt", duringAdoption.SuspendedVmName);
            Assert.Null(duringAdoption.PreemptCheckpoint);
            Assert.Equal(0, queue.Count);
            Assert.Single(await registry.ListAsync(CancellationToken.None));

            provider.AdoptionResultSource!.SetResult(0);
            await barrier.RecoveryInputReady.WaitAsync(TimeSpan.FromSeconds(1));
            barrier.MarkInitialRecoveryCompleted();
            await WaitUntilAsync(async () =>
            {
                var after = await _store.GetAsync(item.Id);
                return queue.Count == 1
                    && after?.PreemptCheckpoint == $"refs/heads/codeybox/preempt/{item.Id}"
                    && after.StartedAt is null;
            });

            var recovered = await _store.GetAsync(item.Id);
            Assert.NotEqual(WorkItemState.Failed, recovered!.State);
            Assert.Null(recovered.SuspendedVmName);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await reaper.StopAsync(stopCts.Token);
            await resume.StopAsync(CancellationToken.None);
        }
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
    [InlineData(WorkItemState.Working, "vm-working", true)]
    [InlineData(WorkItemState.Auditing, "vm-auditing", true)]
    [InlineData(WorkItemState.Reworking, "vm-reworking", true)]
    [InlineData(WorkItemState.Merging, "vm-merging", true)]
    [InlineData(WorkItemState.AuditPassed, "vm-auditpassed", true)]
    [InlineData(WorkItemState.ReworkingForConflict, "vm-reworkingconflict", true)]
    [InlineData(WorkItemState.Done, "vm-done", false)]
    [InlineData(WorkItemState.Cancelled, "vm-cancelled", false)]
    public async Task LeakReaper_ProtectsSuspendedVm_ForEveryMidFlightState(
        WorkItemState state, string vmName, bool expectedProtected)
    {
        // Any non-terminal state can hold a live suspended VM: the suspend-on-
        // shutdown handler persists a (work item → VM) mapping for every entry
        // SnapshotActiveSandboxes returns, regardless of which in-flight phase
        // it is in (Working, Auditing, Reworking, Merging, AuditPassed,
        // ReworkingForConflict, ...). The reaper must therefore protect by
        // "not terminal" rather than an allow-list that silently drops a state
        // and reaps a VM the startup resume handler is about to reattach. The
        // terminal cases (Done, Cancelled) are negative controls: a stale
        // mapping on a terminal item must NOT shield its VM from reaping.
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

    [Fact]
    public async Task LeakReaper_OrphanedSuspendingVm_WithoutMapping_IsReapedAfterSuspendGrace()
    {
        // A crash (or per-VM suspend timeout followed by SIGKILL) can leave a VM
        // in multipass `Suspending`/`Suspended` state with NO live SuspendedVmName
        // mapping. SuspendAsync drops a .codeybox-preempt marker, so without
        // suspend-state awareness the reaper would grant such a VM the full 24h
        // PreemptRetention grace and it would leak for a day. But the reaper must
        // ALSO not purge it the instant it appears: multipassd may still be writing
        // the RAM image. The dedicated suspend grace is measured from when the
        // reaper first observes the VM Suspending, NOT from its (here hour-old)
        // CreatedAt — so the first sweep leaves it alone and only a later sweep,
        // once the grace has elapsed, reaps it as an orphaned suspending VM.
        var provider = new FakeSandboxLeakProvider();
        // CreatedAt an hour ago: past LeakAgeThreshold (30m) and within
        // PreemptRetention (24h). A CreatedAt-based gate would purge it immediately.
        var aged = DateTimeOffset.UtcNow.AddHours(-1);
        provider.SeedManaged(new("vm-orphan-suspending", aged, 1024L * 1024,
            IsTrackedActive: false, HasPreemptMarker: true, IsSuspendLifecycleOrFrozen: true));

        var webhooks = new CapturingWebhookDispatcher();
        var clockNow = DateTimeOffset.UtcNow;
        var reaper = new SandboxLeakReaper(
            provider, webhooks,
            () => new SandboxLeakOptions
            {
                LeakAgeThreshold = TimeSpan.FromMinutes(30),
                PreemptRetention = TimeSpan.FromHours(24),
                SuspendOrphanGrace = TimeSpan.FromMinutes(10),
                AutoDispose = true,
            },
            NullLogger<SandboxLeakReaper>.Instance,
            _store,
            () => clockNow);

        // First sweep: just observed Suspending → within the dedicated grace
        // despite the hour-old CreatedAt → must NOT be purged mid-snapshot.
        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.DoesNotContain("vm-orphan-suspending", provider.DisposedNames);

        // Advance past the suspend grace: multipassd had its snapshot window and the
        // VM is still orphaned with no live mapping → reap it now.
        clockNow = clockNow.AddMinutes(11);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Contains("vm-orphan-suspending", provider.DisposedNames);
        // Classified specifically as a suspend orphan (not a generic untracked
        // leak), so the reason wired into the webhook/audit trail can't silently
        // regress to the wrong bucket.
        var leakEvent = Assert.Single(
            webhooks.Events,
            e => e.Event == "sandbox.leak_detected" &&
                 e.Details is SandboxLeakDetails d && d.Name == "vm-orphan-suspending");
        Assert.Equal(
            SandboxLeakReasons.OrphanedSuspendingVm,
            ((SandboxLeakDetails)leakEvent.Details!).Reason);
    }

    [Fact]
    public async Task LeakReaper_OrphanedSuspendingVm_WithinSuspendGrace_IsNotReaped()
    {
        // Negative control for the suspend-orphan path: a Suspending VM with a
        // preempt marker, no live mapping, and a CreatedAt well past LeakAgeThreshold
        // must STILL be left alone while it is inside the dedicated suspend grace.
        // A regression that purged every IsSuspendLifecycleOrFrozen VM immediately
        // (ignoring the grace) would kill it mid-snapshot — this test fails if that
        // grace gate is dropped.
        var provider = new FakeSandboxLeakProvider();
        provider.SeedManaged(new("vm-fresh-suspending", DateTimeOffset.UtcNow.AddHours(-2),
            1024L * 1024, IsTrackedActive: false, HasPreemptMarker: true, IsSuspendLifecycleOrFrozen: true));

        var webhooks = new CapturingWebhookDispatcher();
        var reaper = new SandboxLeakReaper(
            provider, webhooks,
            () => new SandboxLeakOptions
            {
                LeakAgeThreshold = TimeSpan.FromMinutes(30),
                PreemptRetention = TimeSpan.FromHours(24),
                SuspendOrphanGrace = TimeSpan.FromMinutes(30),
                AutoDispose = true,
            },
            NullLogger<SandboxLeakReaper>.Instance,
            _store);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.DoesNotContain("vm-fresh-suspending", provider.DisposedNames);
        Assert.DoesNotContain(
            webhooks.Events,
            e => e.Event == "sandbox.leak_detected" &&
                 e.Details is SandboxLeakDetails d && d.Name == "vm-fresh-suspending");
    }

    [Fact]
    public async Task LeakReaper_SuspendingVm_WithLiveMapping_IsStillProtected()
    {
        // Control for the orphan case: a Suspending VM that DOES have a live
        // mid-flight SuspendedVmName mapping is being held across a restart and
        // must never be reaped, even though its state is transitional.
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-mapped-suspending",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var provider = new FakeSandboxLeakProvider();
        provider.SeedManaged(new("vm-mapped-suspending", DateTimeOffset.UtcNow.AddHours(-1),
            1024L * 1024, IsTrackedActive: false, HasPreemptMarker: true, IsSuspendLifecycleOrFrozen: true));

        var webhooks = new CapturingWebhookDispatcher();
        var reaper = new SandboxLeakReaper(
            provider, webhooks,
            () => new SandboxLeakOptions { LeakAgeThreshold = TimeSpan.FromMinutes(30), AutoDispose = true },
            NullLogger<SandboxLeakReaper>.Instance,
            _store);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.DoesNotContain("vm-mapped-suspending", provider.DisposedNames);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private SandboxResumeOnStartupService MakeResumeService(ISandboxProvider provider) =>
        new(
            provider,
            _store,
            NullLogger<SandboxResumeOnStartupService>.Instance,
            NoopStartupRecoveryInputSink.Instance);

    private sealed class NoopStartupRecoveryInputSink : IStartupRecoveryInputSink
    {
        public static readonly NoopStartupRecoveryInputSink Instance = new();

        private NoopStartupRecoveryInputSink() { }

        public void MarkRecoveryInputReady() { }
    }

    private sealed class RecordingInfrastructureDeferralScheduler : IInfrastructureDeferralScheduler
    {
        private readonly ConcurrentQueue<(WorkItemId Id, TimeSpan Delay)> _scheduled = new();

        public IReadOnlyList<(WorkItemId Id, TimeSpan Delay)> Scheduled => _scheduled.ToArray();

        public void ScheduleInfrastructureDeferredRequeue(WorkItemId id, TimeSpan delay, CancellationToken stoppingToken = default)
        {
            _ = stoppingToken;
            _scheduled.Enqueue((id, delay));
        }
    }

    private sealed class DeleteOnResumeProvider : ISandboxProvider, IActiveSandboxProvider, ISuspendingSandboxProvider
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
        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() => [];
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

    private sealed class FakeSuspendableSandbox : ISuspendableSandbox, IShutdownTeardownSandbox
    {
        public bool SuspendCalled { get; private set; }
        private readonly bool _shouldThrow;
        public FakeSuspendableSandbox(string id, bool shouldThrow = false, long? memoryBytes = null)
        {
            Id = id;
            _shouldThrow = shouldThrow;
            MemoryBytes = memoryBytes;
        }
        public string Id { get; }
        public long? MemoryBytes { get; }
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

    private sealed class NonSuspendableShutdownSandbox : IShutdownTeardownSandbox
    {
        public NonSuspendableShutdownSandbox(string id) => Id = id;
        public string Id { get; }
        public bool DisposeCalled { get; private set; }
        public bool IsOwnedByShutdownHandler { get; private set; }
        public void MarkOwnedByShutdownHandler() => IsOwnedByShutdownHandler = true;
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));
        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            return ValueTask.CompletedTask;
        }
    }

    // A suspendable sandbox that reports a RAM size and runs a real (short) delay
    // inside SuspendAsync, honouring the cancellation token. Lets a test prove the
    // per-VM timeout the handler applies is the RAM-scaled budget (delay completes)
    // rather than a smaller flat floor (delay would be cancelled).
    private sealed class MemoryAwareDelaySandbox : ISuspendableSandbox, IShutdownTeardownSandbox
    {
        private readonly TimeSpan _delay;
        public MemoryAwareDelaySandbox(string id, long memoryBytes, TimeSpan delay)
        {
            Id = id;
            MemoryBytes = memoryBytes;
            _delay = delay;
        }
        public string Id { get; }
        public long? MemoryBytes { get; }
        public bool Completed { get; private set; }
        public bool TimedOut { get; private set; }
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async Task SuspendAsync(CancellationToken ct = default)
        {
            try
            {
                await Task.Delay(_delay, ct);
                Completed = true;
            }
            catch (OperationCanceledException)
            {
                TimedOut = true;
                throw;
            }
        }
    }

    private sealed class SlowSuspendingSandbox : ISuspendableSandbox, IShutdownTeardownSandbox
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SlowSuspendingSandbox(string id) { Id = id; }
        public string Id { get; }

        /// <summary>Completes the moment <see cref="SuspendAsync"/> is entered, so
        /// a test can observe store state WHILE the suspend await is in flight.</summary>
        public TaskCompletionSource SuspendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets a test unblock a clean, successful suspend return (instead
        /// of relying on the per-VM timeout cancelling the call).</summary>
        public void Release() => _release.TrySetResult();

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task SuspendAsync(CancellationToken ct = default)
        {
            SuspendEntered.TrySetResult();
            // Block until either the per-suspend timeout cancels us (the timeout
            // test) or a test explicitly releases for a clean return (the ordering
            // test).
            var tcs = new TaskCompletionSource();
            ct.Register(() => tcs.TrySetException(new OperationCanceledException(ct)));
            _release.Task.ContinueWith(_ => tcs.TrySetResult(), TaskScheduler.Default);
            return tcs.Task;
        }
    }

    private sealed class FakeSuspendingProvider : ISandboxProvider, IActiveSandboxProvider, ISuspendingSandboxProvider
    {
        private readonly ConcurrentDictionary<WorkItemId, IShutdownTeardownSandbox> _active = new();
        // Resume runs items in parallel (SandboxResumeOnStartupService fans out
        // with a SemaphoreSlim gate), so the recording lists MUST be thread-safe
        // — a plain List<T>.Add from two concurrent resumes intermittently loses
        // one entry, which historically surfaced as a flaky "vm-1 not in
        // ResumedNames" assertion.
        private readonly ConcurrentQueue<string> _resumedNames = new();
        private readonly ConcurrentQueue<string> _disposedNames = new();
        private readonly ConcurrentQueue<AdoptionCall> _adoptionCalls = new();
        private readonly ConcurrentQueue<CheckpointPushCall> _checkpointPushCalls = new();
        private readonly ManualResetEventSlim _resumeBlockRelease = new();
        public IReadOnlyList<string> ResumedNames => _resumedNames.ToArray();
        public IReadOnlyList<string> DisposedNames => _disposedNames.ToArray();
        public IReadOnlyList<AdoptionCall> AdoptionCalls => _adoptionCalls.ToArray();
        public IReadOnlyList<CheckpointPushCall> CheckpointPushCalls => _checkpointPushCalls.ToArray();
        public bool ResumeThrows { get; set; }
        public bool ResumeThrowsCancellation { get; set; }
        public SandboxProvisioningDeferredException? ResumeProvisioningDeferred { get; set; }
        public bool ResumeHangs { get; set; }
        public bool ResumeBlocksBeforeReturningTask { get; set; }
        public bool DisposeThrows { get; set; }
        public TaskCompletionSource ResumeBlockEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlySet<string> ResumeNamesToHang { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public IDictionary<string, TaskCompletionSource> ResumeReleaseSources { get; set; } =
            new Dictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        public bool ResumeObservesCancellation { get; set; }
        public bool ResumeCancellationObserved { get; private set; }
        public int? AdoptionExitCodeToReturn { get; set; }
        public TaskCompletionSource<int?>? AdoptionResultSource { get; set; }
        public bool AdoptionThrowsCancellation { get; set; }
        public bool AdoptionFaultsCancellation { get; set; }
        public bool AdoptionThrows { get; set; }
        public bool AdoptionFaults { get; set; }
        public bool CheckpointPushReturns { get; set; } = true;
        public bool CheckpointPushThrows { get; set; }
        public bool CheckpointPushThrowsCancellation { get; set; }
        public bool CheckpointPushHangs { get; set; }

        public void Register(WorkItemId id, IShutdownTeardownSandbox sandbox) => _active[id] = sandbox;

        public string Name => "fake-suspending";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => Task.FromResult<ISandbox>(new NoopSandbox());
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct)
        {
            if (DisposeThrows)
                throw new InvalidOperationException("simulated dispose leaked failure");
            _disposedNames.Enqueue(name);
            return Task.CompletedTask;
        }
        public void ReleaseBlockedResume() => _resumeBlockRelease.Set();

        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes()
        {
            var list = new List<(WorkItemId, IShutdownTeardownSandbox)>();
            foreach (var kv in _active) list.Add((kv.Key, kv.Value));
            return list;
        }

        public Task ResumeSandboxAsync(string name, CancellationToken ct)
        {
            _resumedNames.Enqueue(name);
            if (ResumeThrows) throw new InvalidOperationException("simulated multipass failure");
            if (ResumeThrowsCancellation) throw new OperationCanceledException("provider cancelled resume");
            if (ResumeProvisioningDeferred is not null) throw ResumeProvisioningDeferred;
            if (ResumeBlocksBeforeReturningTask)
            {
                ResumeBlockEntered.TrySetResult();
                _resumeBlockRelease.Wait();
            }
            if (ResumeReleaseSources.TryGetValue(name, out var resumeRelease))
                return resumeRelease.Task;
            if (ResumeHangs || ResumeNamesToHang.Contains(name))
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            if (ResumeObservesCancellation)
            {
                while (!ct.IsCancellationRequested)
                    Thread.Sleep(1);
                ResumeCancellationObserved = true;
                throw new OperationCanceledException(ct);
            }
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
            if (AdoptionThrowsCancellation)
                throw new OperationCanceledException("provider cancelled adoption");
            if (AdoptionThrows)
                throw new InvalidOperationException("simulated adoption failure");
            if (AdoptionFaultsCancellation)
                return Task.FromException<int?>(new OperationCanceledException("provider cancelled adoption"));
            if (AdoptionFaults)
                return Task.FromException<int?>(new InvalidOperationException("simulated adoption failure"));
            if (AdoptionResultSource is not null)
                return AdoptionResultSource.Task;
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
            if (CheckpointPushThrowsCancellation)
                throw new OperationCanceledException("provider cancelled checkpoint promotion");
            if (CheckpointPushHangs)
                return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            return Task.FromResult(CheckpointPushReturns);
        }
    }

    private sealed class NoopSandbox : ISandbox
    {
        public string Id { get; } = $"sandbox-{Guid.NewGuid():N}";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, "", ""));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
