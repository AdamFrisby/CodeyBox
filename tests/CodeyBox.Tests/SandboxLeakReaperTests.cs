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
        TimeSpan? preemptRetention = null,
        int? maxConcurrentAutoDispose = null,
        IWebhookDispatcher? webhooks = null)
    {
        var opts = new SandboxLeakOptions
        {
            Enabled = true,
            CheckInterval = TimeSpan.FromHours(1), // never fires automatically in tests
            LeakAgeThreshold = leakAgeThreshold ?? TimeSpan.FromMinutes(30),
            PreemptRetention = preemptRetention ?? TimeSpan.FromHours(24),
            AutoDispose = autoDispose,
            MaxConcurrentAutoDispose = maxConcurrentAutoDispose ?? 4,
        };
        return new SandboxLeakReaper(provider, webhooks ?? new NullWebhookDispatcher(), opts, NullLogger<SandboxLeakReaper>.Instance);
    }

    private static DateTimeOffset OldEnough(TimeSpan threshold) =>
        DateTimeOffset.UtcNow - threshold - TimeSpan.FromMinutes(1);

    private static DateTimeOffset TooNew(TimeSpan threshold) =>
        DateTimeOffset.UtcNow - threshold + TimeSpan.FromMinutes(1);

    private static WorkItem MakeWorkItem(WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = $"{state} item",
        Prompt = "exercise sandbox ownership mapping",
        State = state,
    };

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
        var owner = MakeWorkItem(WorkItemState.Working) with
        {
            StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
        };
        provider.AddSandboxForWorkItem(
            owner,
            "codeybox-aabbcc00112233",
            OldEnough(threshold),
            diskBytes: null);
        provider.MarkCurrentPhaseActive("codeybox-aabbcc00112233");

        var reaper = BuildReaper(provider, leakAgeThreshold: threshold);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task AutoDispose_DoesNotDisposeTrackedActiveSandbox()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-active-auto",
            OldEnough(threshold),
            DiskBytes: null,
            IsTrackedActive: true));

        var reaper = BuildReaper(provider, autoDispose: true, leakAgeThreshold: threshold);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(provider.DisposedNames);
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
        Assert.Equal(SandboxLeakReasons.UntrackedSandbox, leaks[0].Reason);
    }

    [Fact]
    public async Task WaitingForQuotaResetItemDoesNotPreserveInactiveSandbox()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        var parkedItem = MakeWorkItem(WorkItemState.WaitingForQuotaReset) with
        {
            FailureKind = "quota",
            QuotaResetAt = DateTimeOffset.UtcNow.AddHours(2),
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddHours(2),
            QuotaRetryFrom = "audit",
            StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(3),
        };
        // The work item still exists in a parked state, but the provider's
        // current-phase ownership snapshot says no active phase owns this VM.
        provider.AddSandboxForWorkItem(
            parkedItem,
            "codeybox-parkedquota",
            OldEnough(threshold),
            diskBytes: null);
        var runningItem = MakeWorkItem(WorkItemState.Working) with
        {
            StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
        };
        provider.AddSandboxForWorkItem(
            runningItem,
            "codeybox-runningphase",
            OldEnough(threshold),
            diskBytes: null);
        var ownershipSnapshot = await provider.ListAllManagedAsync(CancellationToken.None);
        Assert.False(ownershipSnapshot.Single(s => s.Name == "codeybox-parkedquota").IsTrackedActive);
        Assert.True(ownershipSnapshot.Single(s => s.Name == "codeybox-runningphase").IsTrackedActive);

        var reaper = BuildReaper(provider, autoDispose: true, leakAgeThreshold: threshold);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Equal(WorkItemState.WaitingForQuotaReset, provider.OwnerOf("codeybox-parkedquota")?.State);
        Assert.Contains("codeybox-parkedquota", provider.DisposedNames);
        Assert.DoesNotContain("codeybox-runningphase", provider.DisposedNames);
        Assert.Empty(reaper.GetLatestLeaks());
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
        Assert.Equal(SandboxLeakReasons.ExpiredPreemptRetention, leak.Reason);
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
    public async Task SandboxWithNullCreatedAt_IsReportedAsMissingMetadataLeak()
    {
        // Missing staging metadata used to skip stale VMs forever. Once a VM is
        // untracked by the provider ownership snapshot, missing CreatedAt is
        // itself actionable and carries a distinct reason.
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-unknown0000000",
            CreatedAt: null,
            DiskBytes: null,
            IsTrackedActive: false));

        var reaper = BuildReaper(provider);
        await reaper.RunSweepAsync(CancellationToken.None);

        var leak = Assert.Single(reaper.GetLatestLeaks());
        Assert.Equal("codeybox-unknown0000000", leak.Name);
        Assert.Equal(SandboxLeakReasons.UntrackedSandboxMissingCreationMetadata, leak.Reason);
        Assert.True(leak.Age >= TimeSpan.FromMinutes(30));
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
        Assert.Equal(
            ["codeybox-leaked0000000", "codeybox-notime000000"],
            leaks.Select(l => l.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
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
        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task AutoDispose_DefaultEnabled_DisposesEligibleLeak()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo("codeybox-defaultauto", OldEnough(threshold), null, false));
        var opts = new SandboxLeakOptions
        {
            Enabled = true,
            CheckInterval = TimeSpan.FromHours(1),
            LeakAgeThreshold = threshold,
        };
        var reaper = new SandboxLeakReaper(provider, new NullWebhookDispatcher(), opts, NullLogger<SandboxLeakReaper>.Instance);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Contains("codeybox-defaultauto", provider.DisposedNames);
        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task AutoDispose_RespectsConfiguredConcurrencyLimit()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.SetDisposeDelay(TimeSpan.FromMilliseconds(25));
        for (var i = 0; i < 6; i++)
            provider.AddSandbox(new ManagedSandboxInfo($"codeybox-bounded{i}", OldEnough(threshold), null, false));

        var reaper = BuildReaper(
            provider,
            autoDispose: true,
            leakAgeThreshold: threshold,
            maxConcurrentAutoDispose: 2);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Equal(6, provider.DisposedNames.Count);
        Assert.InRange(provider.MaxConcurrentDisposesObserved, 1, 2);
        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task AutoDispose_ClampsNonPositiveConcurrencyLimitToOne(int configuredLimit)
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.SetDisposeDelay(TimeSpan.FromMilliseconds(25));
        for (var i = 0; i < 3; i++)
            provider.AddSandbox(new ManagedSandboxInfo($"codeybox-clamped{i}", OldEnough(threshold), null, false));

        var reaper = BuildReaper(
            provider,
            autoDispose: true,
            leakAgeThreshold: threshold,
            maxConcurrentAutoDispose: configuredLimit);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Equal(3, provider.DisposedNames.Count);
        Assert.Equal(1, provider.MaxConcurrentDisposesObserved);
        Assert.Empty(reaper.GetLatestLeaks());
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
        var leak = Assert.Single(reaper.GetLatestLeaks());
        Assert.Equal("codeybox-willthrow0000", leak.Name);
    }

    [Fact]
    public async Task AutoDispose_DisposeTimeout_KeepsFailedLeakInLatestLeaks()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo("codeybox-timeout0000", OldEnough(threshold), null, false));
        provider.SetDisposeThrowsOperationCanceled("codeybox-timeout0000");
        var webhooks = new CapturingWebhookDispatcher();

        var reaper = BuildReaper(provider, autoDispose: true, leakAgeThreshold: threshold, webhooks: webhooks);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(provider.DisposedNames);
        var leak = Assert.Single(reaper.GetLatestLeaks());
        Assert.Equal("codeybox-timeout0000", leak.Name);
        Assert.Equal(SandboxLeakReasons.UntrackedSandbox, leak.Reason);
        var failed = Assert.Single(webhooks.Events, e => e.Event == "sandbox.leak_dispose_failed");
        var details = Assert.IsType<SandboxLeakDetails>(failed.Details);
        Assert.Equal("timeout", details.Error);
        Assert.Equal(SandboxLeakReasons.UntrackedSandbox, details.Reason);
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
    private readonly object _gate = new();
    private readonly List<FakeSandboxRecord> _sandboxes = [];
    private readonly List<string> _disposedNames = [];
    private readonly HashSet<string> _throwOnDispose = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cancelOnDispose = new(StringComparer.Ordinal);
    private readonly HashSet<string> _currentPhaseSandboxNames = new(StringComparer.Ordinal);
    private TimeSpan _disposeDelay = TimeSpan.Zero;
    private int _activeDisposes;
    private int _maxConcurrentDisposesObserved;
    private bool _throwOnList;

    public IReadOnlyList<string> DisposedNames
    {
        get
        {
            lock (_gate)
                return _disposedNames.ToList();
        }
    }

    public int MaxConcurrentDisposesObserved => _maxConcurrentDisposesObserved;

    public void AddSandbox(ManagedSandboxInfo info)
    {
        lock (_gate)
            _sandboxes.Add(new FakeSandboxRecord(info, Owner: null));
    }

    public void AddSandboxForWorkItem(
        WorkItem owner,
        string name,
        DateTimeOffset createdAt,
        long? diskBytes)
    {
        // Feed the owner state into the provider snapshot so parked quota items
        // are exercised as a distinct non-active state, not just as stored test data.
        var info = new ManagedSandboxInfo(
            name,
            createdAt,
            diskBytes,
            OwnerStateCountsAsActivePhase(owner.State));
        lock (_gate)
            _sandboxes.Add(new FakeSandboxRecord(info, owner));
    }

    public void MarkCurrentPhaseActive(string name)
    {
        lock (_gate)
            _currentPhaseSandboxNames.Add(name);
    }

    public WorkItem? OwnerOf(string name)
    {
        lock (_gate)
            return _sandboxes.FirstOrDefault(s => s.Info.Name == name)?.Owner;
    }

    public void SetDisposeThrows(string name) => _throwOnDispose.Add(name);
    public void SetDisposeThrowsOperationCanceled(string name) => _cancelOnDispose.Add(name);
    public void SetDisposeDelay(TimeSpan delay) => _disposeDelay = delay;
    public void SetListThrows() => _throwOnList = true;

    public string Name => "fake";

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
        throw new NotSupportedException("Fake provider does not create sandboxes");

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        if (_throwOnList)
            throw new InvalidOperationException("Simulated ListAllManagedAsync failure");
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(
                _sandboxes
                    .Select(s => s.Info with
                    {
                        IsTrackedActive = s.Info.IsTrackedActive ||
                            _currentPhaseSandboxNames.Contains(s.Info.Name),
                    })
                    .ToList());
        }
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        if (_throwOnDispose.Contains(name))
            throw new InvalidOperationException($"Simulated dispose failure for {name}");
        if (_cancelOnDispose.Contains(name))
            throw new OperationCanceledException($"Simulated dispose timeout for {name}");

        var active = Interlocked.Increment(ref _activeDisposes);
        RecordMaxConcurrentDisposes(active);
        try
        {
            if (_disposeDelay > TimeSpan.Zero)
                await Task.Delay(_disposeDelay, ct);
            lock (_gate)
                _disposedNames.Add(name);
        }
        finally
        {
            Interlocked.Decrement(ref _activeDisposes);
        }
    }

    private void RecordMaxConcurrentDisposes(int active)
    {
        while (true)
        {
            var observed = _maxConcurrentDisposesObserved;
            if (active <= observed)
                return;
            if (Interlocked.CompareExchange(ref _maxConcurrentDisposesObserved, active, observed) == observed)
                return;
        }
    }

    private static bool OwnerStateCountsAsActivePhase(WorkItemState state) =>
        state is WorkItemState.Working
            or WorkItemState.Reworking
            or WorkItemState.Auditing
            or WorkItemState.Merging
            or WorkItemState.UpstreamPushing;

    private sealed record FakeSandboxRecord(
        ManagedSandboxInfo Info,
        WorkItem? Owner);
}
