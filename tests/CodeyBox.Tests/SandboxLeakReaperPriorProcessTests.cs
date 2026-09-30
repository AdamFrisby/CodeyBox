using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;
using FakeClock = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Prior-process orphan fast path for <see cref="SandboxLeakReaper"/>: untracked
/// sandboxes created before this process started are reaped once
/// <see cref="SandboxLeakOptions.PriorProcessOrphanGrace"/> elapses after process
/// start, instead of burning host resources until the (possibly hours-long)
/// <see cref="SandboxLeakOptions.LeakAgeThreshold"/>. All tests drive a
/// <see cref="FakeClock"/> — no real VMs.
/// </summary>
public sealed class SandboxLeakReaperPriorProcessTests : IDisposable
{
    private static readonly DateTimeOffset ProcessStart =
        new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-priorproc-");

    public void Dispose() => _scratch.Dispose();

    private static SandboxLeakReaper BuildReaper(
        FakeSandboxProvider provider,
        FakeClock clock,
        SandboxLeakOptions opts,
        IWorkItemStore? store = null) =>
        new(
            provider,
            new NullWebhookDispatcher(),
            () => opts,
            NullLogger<SandboxLeakReaper>.Instance,
            store,
            timeProvider: clock);

    private static SandboxLeakOptions SixHourThresholdOpts() => new()
    {
        Enabled = true,
        CheckInterval = TimeSpan.FromHours(1),
        LeakAgeThreshold = TimeSpan.FromHours(6),
        PriorProcessOrphanGrace = TimeSpan.FromMinutes(2),
        AutoDispose = false,
        MaxConcurrentAutoDispose = 4,
    };

    private static WorkItem MakeItem(WorkItemState state = WorkItemState.Working) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = state,
        StartedAt = ProcessStart.AddMinutes(-5),
    };

    [Fact]
    public async Task PriorProcessOrphan_ReapedAfterGrace_DespiteSixHourThreshold()
    {
        var clock = new FakeClock(ProcessStart);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-prior-orphan",
            ProcessStart.AddHours(-1),
            DiskBytes: null,
            IsTrackedActive: false));
        var opts = SixHourThresholdOpts();
        var reaper = BuildReaper(provider, clock, opts);

        clock.Advance(opts.PriorProcessOrphanGrace);
        await reaper.RunSweepAsync(CancellationToken.None);

        var leak = Assert.Single(reaper.GetLatestLeaks());
        Assert.Equal("codeybox-prior-orphan", leak.Name);
        Assert.Equal(SandboxLeakReasons.PriorProcessOrphan, leak.Reason);
    }

    [Fact]
    public async Task CurrentProcessSandbox_NotReapedBeforeLeakAgeThreshold()
    {
        var clock = new FakeClock(ProcessStart);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-current-proc",
            ProcessStart.AddMinutes(1),
            DiskBytes: null,
            IsTrackedActive: false));
        var opts = SixHourThresholdOpts();
        var reaper = BuildReaper(provider, clock, opts);

        clock.Advance(TimeSpan.FromMinutes(30));
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task PriorProcessSandbox_OldEnoughForThreshold_KeepsThresholdReason()
    {
        var clock = new FakeClock(ProcessStart);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-prior-old",
            ProcessStart.AddHours(-7),
            DiskBytes: null,
            IsTrackedActive: false));
        var opts = SixHourThresholdOpts();
        var reaper = BuildReaper(provider, clock, opts);

        await reaper.RunSweepAsync(CancellationToken.None);

        var leak = Assert.Single(reaper.GetLatestLeaks());
        Assert.Equal(SandboxLeakReasons.UntrackedSandbox, leak.Reason);
    }

    [Theory]
    [InlineData(60, false)] // 1s before the 2-minute grace: not yet eligible
    [InlineData(0, true)]   // exactly at the grace boundary: eligible
    [InlineData(-60, true)] // past the grace: eligible
    public async Task PriorProcessOrphan_GraceBoundary(int secondsBeforeGrace, bool expectLeak)
    {
        var clock = new FakeClock(ProcessStart);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-grace-edge",
            ProcessStart.AddMinutes(-10),
            DiskBytes: null,
            IsTrackedActive: false));
        var opts = SixHourThresholdOpts();
        var reaper = BuildReaper(provider, clock, opts);

        clock.Advance(opts.PriorProcessOrphanGrace - TimeSpan.FromSeconds(secondsBeforeGrace));
        await reaper.RunSweepAsync(CancellationToken.None);

        if (expectLeak)
            Assert.Single(reaper.GetLatestLeaks());
        else
            Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task PriorProcessOrphan_WithLiveSuspendMapping_NotReaped()
    {
        var clock = new FakeClock(ProcessStart);
        var provider = new FakeSandboxProvider();
        const string vmName = "codeybox-suspend-kept";
        provider.AddSandbox(new ManagedSandboxInfo(
            vmName,
            ProcessStart.AddHours(-1),
            DiskBytes: null,
            IsTrackedActive: false));
        using var store = new SqliteWorkItemStore(_scratch.DbPath("priorproc-suspend.db"));
        await store.CreateAsync(MakeItem(WorkItemState.Working) with
        {
            SuspendedVmName = vmName,
            SuspendedAt = ProcessStart.AddMinutes(-30),
        });
        var opts = SixHourThresholdOpts();
        var reaper = BuildReaper(provider, clock, opts, store);

        clock.Advance(TimeSpan.FromHours(1));
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task PriorProcessOrphan_WithRetainedRecoveryLease_NotReaped()
    {
        var clock = new FakeClock(ProcessStart);
        var provider = new FakeSandboxProvider();
        const string vmName = "codeybox-lease-kept";
        provider.AddSandbox(new ManagedSandboxInfo(
            vmName,
            ProcessStart.AddHours(-1),
            DiskBytes: null,
            IsTrackedActive: false));
        var item = MakeItem(WorkItemState.Working);
        using var store = new SqliteWorkItemStore(_scratch.DbPath("priorproc-lease.db"));
        await store.CreateAsync(item with
        {
            AgentTurnResumeCheckpoint = new AgentTurnResumeCheckpoint(
                AgentKind.Claude,
                "claude/default",
                modelId: null,
                reasoningMode: null,
                nativeSessionId: null,
                WorkItemState.Working,
                AgentTurnResumePhase.Work,
                iteration: null,
                item.PromptRevision,
                ProcessStart.AddMinutes(-30)),
            AgentTurnRecoveryLease = new SandboxRecoveryLease("fake", vmName, "token"),
        });
        var opts = SixHourThresholdOpts();
        var reaper = BuildReaper(provider, clock, opts, store);

        clock.Advance(TimeSpan.FromHours(1));
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task PriorProcessOrphan_WithFreshPreemptMarker_NotReaped()
    {
        var clock = new FakeClock(ProcessStart);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-preempt-kept",
            ProcessStart.AddHours(-1),
            DiskBytes: null,
            IsTrackedActive: false,
            HasPreemptMarker: true));
        var opts = SixHourThresholdOpts();
        var reaper = BuildReaper(provider, clock, opts);

        clock.Advance(opts.PriorProcessOrphanGrace);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task PriorProcessOrphan_WithDuplicateActiveSnapshot_NotReaped()
    {
        var clock = new FakeClock(ProcessStart);
        var provider = new FakeSandboxProvider();
        const string vmName = "codeybox-dup-kept";
        provider.AddSandbox(new ManagedSandboxInfo(
            vmName,
            ProcessStart.AddHours(-1),
            DiskBytes: null,
            IsTrackedActive: true,
            HostId: "executor-a"));
        provider.AddSandbox(new ManagedSandboxInfo(
            vmName,
            ProcessStart.AddHours(-1),
            DiskBytes: null,
            IsTrackedActive: false,
            HostId: "executor-b"));
        var opts = SixHourThresholdOpts();
        var reaper = BuildReaper(provider, clock, opts);

        clock.Advance(TimeSpan.FromHours(1));
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task PriorProcessOrphanGrace_HotReload_TakesEffectOnNextSweep()
    {
        var clock = new FakeClock(ProcessStart);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-grace-reload",
            ProcessStart.AddMinutes(-10),
            DiskBytes: null,
            IsTrackedActive: false));
        var opts = SixHourThresholdOpts();
        var reaper = BuildReaper(provider, clock, opts);

        opts.PriorProcessOrphanGrace = TimeSpan.FromHours(1);
        clock.Advance(TimeSpan.FromMinutes(2));
        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());

        opts.PriorProcessOrphanGrace = TimeSpan.FromMinutes(2);
        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Single(reaper.GetLatestLeaks());
    }
}
