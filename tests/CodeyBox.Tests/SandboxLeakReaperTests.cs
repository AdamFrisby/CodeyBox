using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="SandboxLeakReaper"/> logic. Uses a fake
/// <see cref="ISandboxProvider"/> to control what the reaper sees on the host,
/// without shelling out to multipass.
/// </summary>
public sealed class SandboxLeakReaperTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static SandboxLeakReaper BuildReaper(
        FakeSandboxProvider provider,
        bool autoDispose = false,
        TimeSpan? leakAgeThreshold = null,
        TimeSpan? preemptRetention = null)
    {
        var opts = new SandboxLeakOptions
        {
            Enabled = true,
            CheckInterval = TimeSpan.FromHours(1), // never fires automatically in tests
            LeakAgeThreshold = leakAgeThreshold ?? TimeSpan.FromMinutes(30),
            PreemptRetention = preemptRetention ?? TimeSpan.FromHours(24),
            AutoDispose = autoDispose,
        };
        return new SandboxLeakReaper(provider, new NullWebhookDispatcher(), opts, NullLogger<SandboxLeakReaper>.Instance);
    }

    private static DateTimeOffset OldEnough(TimeSpan threshold) =>
        DateTimeOffset.UtcNow - threshold - TimeSpan.FromMinutes(1);

    private static DateTimeOffset TooNew(TimeSpan threshold) =>
        DateTimeOffset.UtcNow - threshold + TimeSpan.FromMinutes(1);

    // ── Leak detection ───────────────────────────────────────────────────────

    [Fact]
    public async Task NoSandboxes_ReturnsEmptyLeakList()
    {
        var provider = new FakeSandboxProvider();
        var reaper = BuildReaper(provider);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task ActiveSandbox_NotReportedAsLeak()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-aabbcc00112233",
            OldEnough(threshold),
            DiskBytes: null,
            IsTrackedActive: true));   // active in current process

        var reaper = BuildReaper(provider, leakAgeThreshold: threshold);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task InactiveSandbox_OlderThanThreshold_ReportedAsLeak()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-stale000000000",
            OldEnough(threshold),
            DiskBytes: null,
            IsTrackedActive: false));

        var reaper = BuildReaper(provider, leakAgeThreshold: threshold);
        await reaper.RunSweepAsync(CancellationToken.None);

        var leaks = reaper.GetLatestLeaks();
        Assert.Single(leaks);
        Assert.Equal("codeybox-stale000000000", leaks[0].Name);
    }

    [Fact]
    public async Task InactiveSandbox_WithPreemptMarker_NotReportedAsLeak()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-preempt000000",
            OldEnough(threshold),
            DiskBytes: null,
            IsTrackedActive: false,
            HasPreemptMarker: true));

        var reaper = BuildReaper(provider, leakAgeThreshold: threshold);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task AutoDispose_WithPreemptMarker_DoesNotDispose()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-preempt111111",
            OldEnough(threshold),
            DiskBytes: null,
            IsTrackedActive: false,
            HasPreemptMarker: true));

        var reaper = BuildReaper(provider, autoDispose: true, leakAgeThreshold: threshold);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(provider.DisposedNames);
        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task InactiveSandbox_WithExpiredPreemptMarker_ReportedAsLeak()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-expired-preempt",
            DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
            DiskBytes: null,
            IsTrackedActive: false,
            HasPreemptMarker: true));

        var reaper = BuildReaper(provider,
            leakAgeThreshold: threshold,
            preemptRetention: TimeSpan.FromHours(1));
        await reaper.RunSweepAsync(CancellationToken.None);

        var leak = Assert.Single(reaper.GetLatestLeaks());
        Assert.Equal("codeybox-expired-preempt", leak.Name);
    }

    [Fact]
    public async Task InactiveSandbox_YoungerThanThreshold_NotReportedAsLeak()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-fresh00000000",
            TooNew(threshold),
            DiskBytes: null,
            IsTrackedActive: false));

        var reaper = BuildReaper(provider, leakAgeThreshold: threshold);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task SandboxWithNullCreatedAt_NotReportedAsLeak()
    {
        // If we can't determine the creation time, be conservative — skip it.
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-unknown0000000",
            CreatedAt: null,
            DiskBytes: null,
            IsTrackedActive: false));

        var reaper = BuildReaper(provider);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task MultipleSandboxes_OnlyOldInactiveOnesReported()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo("codeybox-leaked0000000", OldEnough(threshold), null, false));
        provider.AddSandbox(new ManagedSandboxInfo("codeybox-active0000000", OldEnough(threshold), null, true));
        provider.AddSandbox(new ManagedSandboxInfo("codeybox-toofresh00000", TooNew(threshold), null, false));
        provider.AddSandbox(new ManagedSandboxInfo("codeybox-notime000000", null, null, false));

        var reaper = BuildReaper(provider, leakAgeThreshold: threshold);
        await reaper.RunSweepAsync(CancellationToken.None);

        var leaks = reaper.GetLatestLeaks();
        Assert.Single(leaks);
        Assert.Equal("codeybox-leaked0000000", leaks[0].Name);
    }

    // ── Auto-dispose ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AutoDispose_Disabled_DoesNotCallDispose()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo("codeybox-stale111111111", OldEnough(threshold), null, false));

        var reaper = BuildReaper(provider, autoDispose: false, leakAgeThreshold: threshold);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(provider.DisposedNames);
        Assert.Single(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task AutoDispose_Enabled_CallsDisposeOnEachLeak()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo("codeybox-leak1111111111", OldEnough(threshold), null, false));
        provider.AddSandbox(new ManagedSandboxInfo("codeybox-leak2222222222", OldEnough(threshold), null, false));

        var reaper = BuildReaper(provider, autoDispose: true, leakAgeThreshold: threshold);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Contains("codeybox-leak1111111111", provider.DisposedNames);
        Assert.Contains("codeybox-leak2222222222", provider.DisposedNames);
    }

    [Fact]
    public async Task AutoDispose_OneFailure_ContinuesWithRemainingLeaks()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo("codeybox-willthrow0000", OldEnough(threshold), null, false));
        provider.AddSandbox(new ManagedSandboxInfo("codeybox-willsucceed00", OldEnough(threshold), null, false));
        provider.SetDisposeThrows("codeybox-willthrow0000");

        var reaper = BuildReaper(provider, autoDispose: true, leakAgeThreshold: threshold);

        // Must not throw — failures are handled internally.
        await reaper.RunSweepAsync(CancellationToken.None);

        // The sandbox that didn't throw should still be disposed.
        Assert.Contains("codeybox-willsucceed00", provider.DisposedNames);
    }

    // ── Sweep resilience ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunSweepAsync_WhenListAllThrows_SwallowsExceptionAndLeavesLeaksUnchanged()
    {
        var provider = new FakeSandboxProvider();
        provider.SetListThrows();
        var reaper = BuildReaper(provider);

        // Capture the initial (empty) leak list reference.
        var initialLeaks = reaper.GetLatestLeaks();

        // Must not propagate the exception — sweep failures must be swallowed
        // so the BackgroundService host does not terminate.
        await reaper.RunSweepAsync(CancellationToken.None);

        // _latestLeaks must remain the same object — the failed sweep must not
        // have overwritten it with partial or empty results.
        Assert.Same(initialLeaks, reaper.GetLatestLeaks());
    }

    // ── Age threshold boundary ───────────────────────────────────────────────

    [Theory]
    [InlineData(29)]  // just under threshold: NOT leaked
    [InlineData(30)]  // exactly at threshold: leaked (age >= threshold)
    [InlineData(31)]  // over threshold: leaked
    public async Task AgeThreshold_BoundaryBehaviour(int ageMinutes)
    {
        var threshold = TimeSpan.FromMinutes(30);
        var createdAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(ageMinutes);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo("codeybox-boundary00000", createdAt, null, false));

        var reaper = BuildReaper(provider, leakAgeThreshold: threshold);
        await reaper.RunSweepAsync(CancellationToken.None);

        if (ageMinutes < 30)
            Assert.Empty(reaper.GetLatestLeaks());
        else
            Assert.Single(reaper.GetLatestLeaks());
    }
}

// ── Test double ─────────────────────────────────────────────────────────────

internal sealed class FakeSandboxProvider : ISandboxProvider
{
    private readonly List<ManagedSandboxInfo> _sandboxes = [];
    private readonly HashSet<string> _throwOnDispose = new(StringComparer.Ordinal);
    private bool _throwOnList;

    public List<string> DisposedNames { get; } = [];

    public void AddSandbox(ManagedSandboxInfo info) => _sandboxes.Add(info);
    public void SetDisposeThrows(string name) => _throwOnDispose.Add(name);
    public void SetListThrows() => _throwOnList = true;

    public string Name => "fake";

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
        throw new NotSupportedException("Fake provider does not create sandboxes");

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        if (_throwOnList)
            throw new InvalidOperationException("Simulated ListAllManagedAsync failure");
        return Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(_sandboxes.ToList());
    }

    public Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        if (_throwOnDispose.Contains(name))
            throw new InvalidOperationException($"Simulated dispose failure for {name}");
        DisposedNames.Add(name);
        return Task.CompletedTask;
    }
}
