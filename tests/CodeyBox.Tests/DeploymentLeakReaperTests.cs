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
        // Add a sandbox via CreateAsync, then "lose" the handle by disposing
        // the manager wrapper (simulates an aborted deploy where the driver
        // returned but the orchestrator crashed before tracking).
        var s = (FakeDeploymentSandbox)await provider.CreateAsync(new SandboxSpec { ImageReference = "x" });
        // Mark created old enough — we cheat by reading-then-writing CreatedAt via reflection-free helper.
        var managedList = await provider.ListAllManagedAsync(CancellationToken.None);
        // The fake's listing reports IsTrackedActive=true while not yet disposed; force the reaper
        // path by disposing the sandbox so it stops reporting as tracked active.
        await s.DisposeAsync();

        var manager = new StubManager(active: []);
        var clock = () => s.CreatedAt + TimeSpan.FromHours(2);
        var reaper = new DeploymentLeakReaper(
            provider, manager, () => Opts(), NullLogger<DeploymentLeakReaper>.Instance, clock);

        // The sandbox is disposed already (IsTrackedActive=false). Reaper sees an orphan
        // older than the leak threshold and counts it; AutoDispose tries to dispose
        // again (idempotent on the fake).
        await reaper.RunSweepAsync(CancellationToken.None);
        var leaks = reaper.GetLatestLeaks();
        Assert.Single(leaks);
        Assert.Equal(s.Id, leaks[0].Name);
        Assert.Contains(s.Id, provider.DisposedNames);
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

    private sealed class StubManager : IDeploymentManager
    {
        private readonly IReadOnlyList<ActiveDeploymentInfo> _active;
        public StubManager(IReadOnlyList<ActiveDeploymentInfo> active) => _active = active;
        public Task<IDeploymentHandle> StartAsync(DeploymentRecipe recipe, DeploymentContext context, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IReadOnlyList<ActiveDeploymentInfo> GetActive() => _active;
    }
}
