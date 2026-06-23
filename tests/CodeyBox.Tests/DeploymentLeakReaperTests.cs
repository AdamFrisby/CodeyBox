using CodeyBox.Core;
using CodeyBox.Deployment;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class DeploymentLeakReaperTests
{
    private static DeploymentLeakOptions Opts(
        TimeSpan? leakAgeThreshold = null,
        bool autoDispose = true) => new()
        {
            Enabled = true,
            CheckInterval = TimeSpan.FromHours(1),   // never fires automatically
            LeakAgeThreshold = leakAgeThreshold ?? TimeSpan.FromMinutes(30),
            AutoDispose = autoDispose,
            DisposeTimeout = TimeSpan.FromSeconds(30),
        };

    [Fact]
    public async Task ManagedSandbox_NotInActiveSet_AndOldEnough_IsDisposed()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(new SandboxSpec { ImageReference = "x" });
        // Disposing the sandbox flips the fake's IsTrackedActive listing to false
        // (mapping the production semantic "no live phase owns this VM" to the
        // fake's "disposed" state — close enough for the reaper's filter logic).
        await s.DisposeAsync();

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        var leaks = reaper.GetLatestLeaks();
        Assert.Single(leaks);
        Assert.Equal(s.Id, leaks[0].Name);
        Assert.Contains(s.Id, provider.DisposedNames);
    }

    /// <summary>
    /// Genuine orphan-from-crash scenario: the sandbox is undisposed (the
    /// orchestrator crashed before reaching DisposeAsync) but no live phase
    /// in the current process owns it. This faithfully models the production
    /// "crash mid-deploy" path that the disposed-then-listed test above
    /// emulates via the fake's dispose-state mapping.
    /// </summary>
    [Fact]
    public async Task UndisposedOrphan_FromPriorProcess_IsDisposed()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(new SandboxSpec { ImageReference = "x" });
        // Override the fake's listing to report the production semantic
        // directly: IsTrackedActive=false (no live phase owner) while the
        // sandbox itself remains undisposed.
        provider.ManagedInfoOverride = sb => new ManagedSandboxInfo(
            sb.Id, sb.CreatedAt, DiskBytes: null, IsTrackedActive: false);

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        var leaks = reaper.GetLatestLeaks();
        Assert.Single(leaks);
        Assert.Equal(s.Id, leaks[0].Name);
        Assert.Contains(s.Id, provider.DisposedNames);
        Assert.True(s.IsDisposed); // DisposeLeakedAsync flips the fake's flag too
    }

    [Fact]
    public async Task ManagedSandbox_InActiveSet_NotReportedAsLeak()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(new SandboxSpec { ImageReference = "x" });
        await s.DisposeAsync();  // No longer IsTrackedActive

        var info = new ActiveDeploymentInfo(
            "dep-1", DeploymentKinds.WebApp, null, s.Id, s.CreatedAt, new DeploymentEndpoint
            {
                Kind = DeploymentEndpointKind.Http,
            });
        var manager = new StubManager(active: [info]);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.DoesNotContain(s.Id, provider.DisposedNames);
    }

    [Fact]
    public async Task ManagedSandbox_YoungerThanThreshold_NotReportedAsLeak()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(new SandboxSpec { ImageReference = "x" });
        await s.DisposeAsync();

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromMinutes(5);  // way under threshold
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task AutoDisposeFalse_LeaksReportedButNotDisposed()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(new SandboxSpec { ImageReference = "x" });
        await s.DisposeAsync();

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(autoDispose: false), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Single(reaper.GetLatestLeaks());
        // The fake's MarkDisposed was called above by our test code, but provider.DisposeLeakedAsync was NOT.
        Assert.DoesNotContain(s.Id, provider.DisposedNames);
    }

    /// <summary>
    /// Regression: the deployment reaper must NOT reap a VM whose name is in
    /// the work-item store's SuspendedVmName index — the startup resume
    /// handler will multipass-start it back to Running. Destroying it
    /// strands the work item.
    /// </summary>
    [Fact]
    public async Task SuspendedVmName_IsSkipped_EvenWhenOldAndOrphan()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(new SandboxSpec { ImageReference = "x" });
        // Simulate the VM "stopped" (not tracked-active) while the work item's
        // SuspendedVmName still names it — orchestrator restart in progress.
        provider.ManagedInfoOverride = sb => new ManagedSandboxInfo(
            sb.Id, sb.CreatedAt, DiskBytes: null, IsTrackedActive: false);

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);
        var suspended = new HashSet<string>(StringComparer.Ordinal) { s.Id };
        Func<CancellationToken, Task<IReadOnlySet<string>>> provider2 = _ =>
            Task.FromResult<IReadOnlySet<string>>(suspended);
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance,
            clock, suspendedNameProvider: provider2);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.DoesNotContain(s.Id, provider.DisposedNames);
    }

    /// <summary>
    /// Regression: preempt-marked (graceful-shutdown-preserved) VMs must
    /// inherit the same PreemptRetention grace SandboxLeakReaper applies.
    /// Otherwise the deployment reaper destroys them at LeakAgeThreshold
    /// (30 min default) even though SandboxLeakReaper preserves them for 24h.
    /// </summary>
    [Fact]
    public async Task PreemptMarkedSandbox_NotReaped_WithinPreemptRetention()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(new SandboxSpec { ImageReference = "x" });
        provider.ManagedInfoOverride = sb => new ManagedSandboxInfo(
            sb.Id, sb.CreatedAt, DiskBytes: null, IsTrackedActive: false,
            HasPreemptMarker: true);

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);  // Past leak threshold but within 24h preempt retention
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.DoesNotContain(s.Id, provider.DisposedNames);
    }

    /// <summary>
    /// Regression: a VM in the multipass Suspending/Suspended lifecycle (the
    /// Claude session worker's stop/resume contract) must not be destroyed
    /// by the deployment reaper. SandboxLeakReaper applies a dedicated
    /// SuspendOrphanGrace here; this reaper conservatively skips them.
    /// </summary>
    [Fact]
    public async Task SuspendLifecycleSandbox_IsSkipped()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(new SandboxSpec { ImageReference = "x" });
        provider.ManagedInfoOverride = sb => new ManagedSandboxInfo(
            sb.Id, sb.CreatedAt, DiskBytes: null, IsTrackedActive: false,
            HasPreemptMarker: false, IsSuspendLifecycleOrFrozen: true);

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
    }

    /// <summary>
    /// Regression: unknown CreatedAt is conservative (skip), not aggressive.
    /// Previously the code defaulted to (now - LeakAgeThreshold), producing
    /// `age == LeakAgeThreshold` and failing the `age &lt; threshold` skip
    /// check — every metadata-less sandbox got reaped on first sight.
    /// </summary>
    [Fact]
    public async Task UnknownCreatedAt_IsSkipped()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(new SandboxSpec { ImageReference = "x" });
        provider.ManagedInfoOverride = sb => new ManagedSandboxInfo(
            sb.Id, CreatedAt: null, DiskBytes: null, IsTrackedActive: false);

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.DoesNotContain(s.Id, provider.DisposedNames);
    }

    private sealed class StubManager : IDeploymentManager
    {
        private readonly IReadOnlyList<ActiveDeploymentInfo> _active;
        public StubManager(IReadOnlyList<ActiveDeploymentInfo> active) => _active = active;
        public Task<IDeploymentHandle> StartAsync(DeploymentRecipe recipe, DeploymentContext context, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IReadOnlyList<ActiveDeploymentInfo> GetActive() => _active;
    }
}
