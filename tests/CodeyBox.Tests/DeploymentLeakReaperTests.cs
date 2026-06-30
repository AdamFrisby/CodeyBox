using CodeyBox.Core;
using CodeyBox.Deployment;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class DeploymentLeakReaperTests
{
    private static SandboxSpec DeploymentSpec() => new()
    {
        ImageReference = "x",
        Purpose = SandboxPurpose.Deployment,
    };

    private static DeploymentLeakOptions Opts(
        TimeSpan? leakAgeThreshold = null,
        bool autoDispose = true,
        TimeSpan? suspendOrphanGrace = null) => new()
        {
            Enabled = true,
            CheckInterval = TimeSpan.FromHours(1),   // never fires automatically
            LeakAgeThreshold = leakAgeThreshold ?? TimeSpan.FromMinutes(30),
            AutoDispose = autoDispose,
            DisposeTimeout = TimeSpan.FromSeconds(30),
            SuspendOrphanGrace = suspendOrphanGrace ?? TimeSpan.FromMinutes(30),
        };

    [Fact]
    public async Task ManagedSandbox_NotInActiveSet_AndOldEnough_IsDisposed()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
        // Disposing the sandbox flips the fake's IsTrackedActive listing to false
        // (mapping the production semantic "no live phase owns this VM" to the
        // fake's "disposed" state — close enough for the reaper's filter logic).
        await s.DisposeAsync();

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.Contains(s.Id, provider.DisposedNames);
    }

    [Fact]
    public async Task NonDeploymentSandbox_IsIgnored()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(new SandboxSpec { ImageReference = "x" });
        await s.DisposeAsync();

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.DoesNotContain(s.Id, provider.DisposedNames);
    }

    [Fact]
    public async Task DisabledOptions_DoNotSweep()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
        await s.DisposeAsync();

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);
        var reaper = new DeploymentLeakReaper(
            provider,
            manager,
            () =>
            {
                var opts = Opts();
                opts.Enabled = false;
                return opts;
            },
            NullLogger<DeploymentLeakReaper>.Instance,
            clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.DoesNotContain(s.Id, provider.DisposedNames);
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
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
        // Override the fake's listing to report the production semantic
        // directly: IsTrackedActive=false (no live phase owner) while the
        // sandbox itself remains undisposed.
        provider.ManagedInfoOverride = sb => new ManagedSandboxInfo(
            sb.Id, sb.CreatedAt, DiskBytes: null, IsTrackedActive: false,
            Purpose: sb.Spec.Purpose);

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.Contains(s.Id, provider.DisposedNames);
        Assert.True(s.IsDisposed); // DisposeLeakedAsync flips the fake's flag too
    }

    [Fact]
    public async Task ManagedSandbox_InActiveSet_NotReportedAsLeak()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
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
    public async Task ManagedSandbox_TrackedActiveByProvider_NotReportedAsLeak()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
        provider.ManagedInfoOverride = sb => new ManagedSandboxInfo(
            sb.Id, sb.CreatedAt, DiskBytes: null, IsTrackedActive: true,
            Purpose: sb.Spec.Purpose);

        var manager = new StubManager(active: []);
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
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
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
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
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
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
        // Simulate the VM "stopped" (not tracked-active) while the work item's
        // SuspendedVmName still names it — orchestrator restart in progress.
        provider.ManagedInfoOverride = sb => new ManagedSandboxInfo(
            sb.Id, sb.CreatedAt, DiskBytes: null, IsTrackedActive: false,
            Purpose: sb.Spec.Purpose);

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
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
        provider.ManagedInfoOverride = sb => new ManagedSandboxInfo(
            sb.Id, sb.CreatedAt, DiskBytes: null, IsTrackedActive: false,
            HasPreemptMarker: true,
            Purpose: sb.Spec.Purpose);

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);  // Past leak threshold but within 24h preempt retention
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.DoesNotContain(s.Id, provider.DisposedNames);
    }

    [Fact]
    public async Task PreemptMarkedSandbox_ReapedAfterPreemptRetention()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
        provider.ManagedInfoOverride = sb => new ManagedSandboxInfo(
            sb.Id, sb.CreatedAt, DiskBytes: null, IsTrackedActive: false,
            HasPreemptMarker: true,
            Purpose: sb.Spec.Purpose);

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(26);
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.Contains(s.Id, provider.DisposedNames);
    }

    /// <summary>
    /// Regression: a deployment VM in the multipass Suspending/Suspended
    /// lifecycle must not be destroyed immediately. Deployment sandboxes are
    /// no longer owned by SandboxLeakReaper, so the deployment reaper applies
    /// its own first-seen suspend grace.
    /// </summary>
    [Fact]
    public async Task SuspendLifecycleSandbox_IsSkipped()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
        provider.ManagedInfoOverride = sb => new ManagedSandboxInfo(
            sb.Id, sb.CreatedAt, DiskBytes: null, IsTrackedActive: false,
            HasPreemptMarker: false, IsSuspendLifecycleOrFrozen: true,
            Purpose: sb.Spec.Purpose);

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.DoesNotContain(s.Id, provider.DisposedNames);
    }

    [Fact]
    public async Task SuspendLifecycleSandbox_IsReapedAfterSuspendOrphanGrace()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
        provider.ManagedInfoOverride = sb => new ManagedSandboxInfo(
            sb.Id, sb.CreatedAt, DiskBytes: null, IsTrackedActive: false,
            HasPreemptMarker: false, IsSuspendLifecycleOrFrozen: true,
            Purpose: sb.Spec.Purpose);

        var manager = new StubManager(active: []);
        var now = s.CreatedAt + TimeSpan.FromHours(2);
        var reaper = new DeploymentLeakReaper(
            provider,
            manager,
            () => Opts(suspendOrphanGrace: TimeSpan.FromMinutes(10)),
            NullLogger<DeploymentLeakReaper>.Instance,
            () => now);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.DoesNotContain(s.Id, provider.DisposedNames);

        now += TimeSpan.FromMinutes(11);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(reaper.GetLatestLeaks());
        Assert.Contains(s.Id, provider.DisposedNames);
    }

    [Fact]
    public async Task UnknownCreatedAt_IsReportedAndDisposed()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
        provider.ManagedInfoOverride = sb => new ManagedSandboxInfo(
            sb.Id, CreatedAt: null, DiskBytes: null, IsTrackedActive: false,
            Purpose: sb.Spec.Purpose);

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.Contains(s.Id, provider.DisposedNames);
    }

    [Fact]
    public async Task DisposeFailure_DoesNotAbortSweep()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var first = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
        var second = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
        await first.DisposeAsync();
        await second.DisposeAsync();
        provider.DisposeThrowsFor.Add(first.Id);

        var manager = new StubManager(active: []);
        var clock = () => first.CreatedAt + TimeSpan.FromHours(2);
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        await reaper.RunSweepAsync(CancellationToken.None);

        var remaining = Assert.Single(reaper.GetLatestLeaks());
        Assert.Equal(first.Id, remaining.Name);
        Assert.DoesNotContain(first.Id, provider.DisposedNames);
        Assert.Contains(second.Id, provider.DisposedNames);
    }

    [Fact]
    public async Task ProviderListFailure_IsSwallowed()
    {
        var reaper = new DeploymentLeakReaper(
            new ThrowingListProvider(),
            new StubManager(active: []),
            () => Opts(),
            NullLogger<DeploymentLeakReaper>.Instance);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task ManagerGetActiveFailure_IsSwallowedAndDoesNotDispose()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
        provider.ManagedInfoOverride = sb => new ManagedSandboxInfo(
            sb.Id, sb.CreatedAt, DiskBytes: null, IsTrackedActive: false,
            Purpose: sb.Spec.Purpose);

        var reaper = new DeploymentLeakReaper(
            provider,
            new StubManager(active: [], throwOnGetActive: true),
            () => Opts(),
            NullLogger<DeploymentLeakReaper>.Instance,
            () => s.CreatedAt + TimeSpan.FromHours(2));

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.DoesNotContain(s.Id, provider.DisposedNames);
    }

    [Fact]
    public async Task SuspendedNameProviderFailure_IsSwallowedAndDoesNotDispose()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(DeploymentSpec());
        provider.ManagedInfoOverride = sb => new ManagedSandboxInfo(
            sb.Id, sb.CreatedAt, DiskBytes: null, IsTrackedActive: false,
            Purpose: sb.Spec.Purpose);
        Func<CancellationToken, Task<IReadOnlySet<string>>> suspendedNames =
            _ => throw new InvalidOperationException("suspended index unavailable");

        var reaper = new DeploymentLeakReaper(
            provider,
            new StubManager(active: []),
            () => Opts(),
            NullLogger<DeploymentLeakReaper>.Instance,
            () => s.CreatedAt + TimeSpan.FromHours(2),
            suspendedNameProvider: suspendedNames);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.DoesNotContain(s.Id, provider.DisposedNames);
    }

    [Fact]
    public async Task RequestedCancellation_Propagates()
    {
        var reaper = new DeploymentLeakReaper(
            new CancellationAwareListProvider(),
            new StubManager(active: []),
            () => Opts(),
            NullLogger<DeploymentLeakReaper>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => reaper.RunSweepAsync(cts.Token));
    }

    private sealed class StubManager : IDeploymentManager
    {
        private readonly IReadOnlyList<ActiveDeploymentInfo> _active;
        private readonly bool _throwOnGetActive;

        public StubManager(IReadOnlyList<ActiveDeploymentInfo> active, bool throwOnGetActive = false)
        {
            _active = active;
            _throwOnGetActive = throwOnGetActive;
        }

        public Task<IDeploymentHandle> StartAsync(DeploymentRecipe recipe, DeploymentContext context, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IReadOnlyList<ActiveDeploymentInfo> GetActive()
        {
            if (_throwOnGetActive)
                throw new InvalidOperationException("active set unavailable");
            return _active;
        }
    }

    private sealed class ThrowingListProvider : ISandboxProvider
    {
        public string Name => "throwing-list";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => throw new InvalidOperationException("list unavailable");
        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class CancellationAwareListProvider : ISandboxProvider
    {
        public string Name => "cancelling-list";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        }
        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
