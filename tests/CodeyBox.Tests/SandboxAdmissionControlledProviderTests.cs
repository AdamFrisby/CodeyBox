using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class SandboxAdmissionControlledProviderTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task CreateAsync_BoundsConcurrentLiveSandboxesUntilDispose()
    {
        var inner = new CountingSandboxProvider();
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 2, NullLogger.Instance);
        using var timeout = new CancellationTokenSource(TestDeadline);

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var sandbox = await provider.CreateAsync(Spec(), timeout.Token);
            await Task.Delay(40, timeout.Token);
        });

        await Task.WhenAll(tasks).WaitAsync(timeout.Token);

        Assert.Equal(8, inner.Created);
        Assert.True(inner.PeakActive <= 2, $"Peak active sandboxes {inner.PeakActive} exceeded budget 2");
        Assert.Equal(0, inner.Active);
    }

    [Fact]
    public async Task Stress_MultipleItemsFanOutAuditors_DrainsBelowFanoutBudget()
    {
        const int itemCount = 5;
        const int auditorsPerItem = 3;
        const int globalBudget = 4;
        var inner = new CountingSandboxProvider();
        var provider = SandboxAdmissionControlledProvider.Wrap(
            inner,
            globalBudget,
            NullLogger.Instance);
        using var timeout = new CancellationTokenSource(TestDeadline);

        async Task RunItemAsync(int item)
        {
            await using (var workerSandbox = await provider.CreateAsync(Spec(item), timeout.Token))
            {
                await Task.Delay(25, timeout.Token);
            }

            var auditorTasks = Enumerable.Range(0, auditorsPerItem).Select(async auditor =>
            {
                await using var auditorSandbox = await provider.CreateAsync(Spec(item, auditor), timeout.Token);
                await Task.Delay(25, timeout.Token);
            });
            await Task.WhenAll(auditorTasks);
        }

        var tasks = Enumerable.Range(0, itemCount).Select(RunItemAsync);
        await Task.WhenAll(tasks).WaitAsync(timeout.Token);

        Assert.Equal(itemCount * (1 + auditorsPerItem), inner.Created);
        Assert.True(
            inner.PeakActive <= globalBudget,
            $"Peak active sandboxes {inner.PeakActive} exceeded budget {globalBudget}");
        Assert.Equal(0, inner.Active);
    }

    [Fact]
    public async Task QueuedCreateCancellation_DoesNotConsumeAdmissionToken()
    {
        var inner = new CountingSandboxProvider();
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);

        var first = await provider.CreateAsync(Spec(), CancellationToken.None);
        using var queuedCts = new CancellationTokenSource();
        var queued = provider.CreateAsync(Spec(), queuedCts.Token);

        await Task.Delay(50);
        Assert.False(queued.IsCompleted);
        Assert.Equal(1, inner.Created);

        await queuedCts.CancelAsync();
        var ex = await Record.ExceptionAsync(() => queued);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);

        await first.DisposeAsync();
        await using var next = await provider.CreateAsync(Spec(), CancellationToken.None);

        Assert.Equal(2, inner.Created);
        Assert.True(inner.PeakActive <= 1, $"Peak active sandboxes {inner.PeakActive} exceeded budget 1");
    }

    [Fact]
    public async Task CreateFailure_ReleasesAdmissionToken()
    {
        var inner = new CountingSandboxProvider { FailNextCreate = true };
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(Spec()));

        await using var next = await provider.CreateAsync(Spec()).WaitAsync(TestDeadline);

        Assert.Equal(2, inner.CreateAttempts);
        Assert.Equal(1, inner.Created);
    }

    [Fact]
    public async Task DisposeFailure_ReleasesAdmissionToken()
    {
        var inner = new CountingSandboxProvider { ThrowOnNextSandboxDispose = true };
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);

        var sandbox = await provider.CreateAsync(Spec());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sandbox.DisposeAsync());

        await using var next = await provider.CreateAsync(Spec()).WaitAsync(TestDeadline);

        Assert.Equal(2, inner.Created);
    }

    [Fact]
    public async Task ActiveSnapshot_ReturnsAdmissionControlledHandleThatReleasesTokenOnDispose()
    {
        var inner = new CountingSandboxProvider();
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var activeProvider = Assert.IsAssignableFrom<IActiveSandboxProvider>(provider);

        var originalHandle = await provider.CreateAsync(Spec(), CancellationToken.None);
        var snapshot = activeProvider.SnapshotActiveSandboxes();
        var (_, shutdownHandle) = Assert.Single(snapshot);

        await shutdownHandle.DisposeAsync();

        Assert.Equal(0, inner.Active);
        await using var next = await provider.CreateAsync(Spec(), CancellationToken.None);
        Assert.Equal(2, inner.Created);

        await originalHandle.DisposeAsync();
    }

    [Fact]
    public async Task BaselineProvisioning_AcquiresAdmissionToken()
    {
        var inner = new CountingSandboxProvider { BlockEnsureBaseline = true };
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);
        var provisioner = Assert.IsAssignableFrom<IBaselineImageProvisioner>(provider);

        var ensure = provisioner.EnsureBaselineImageAsync(
            "default",
            SandboxProfileFlavor.Headless,
            pinnedBaselineRef: null,
            CancellationToken.None);

        await inner.EnsureBaselineStarted.Task.WaitAsync(TestDeadline);
        Assert.Equal(1, admission.CurrentAdmittedSandboxes);

        inner.AllowEnsureBaseline.SetResult();
        Assert.Equal("baseline", await ensure.WaitAsync(TestDeadline));
        Assert.Equal(0, admission.CurrentAdmittedSandboxes);
    }

    [Fact]
    public async Task ResumeAdmissionLease_IsHeldUntilResumedVmIsDisposed()
    {
        var inner = new CountingSandboxProvider();
        inner.ManagedSandboxes =
        [
            new ManagedSandboxInfo("codeybox-resume", DateTimeOffset.UtcNow, DiskBytes: null, IsTrackedActive: false),
        ];
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);
        var suspending = Assert.IsAssignableFrom<ISuspendingSandboxProvider>(provider);
        var resumeTracker = Assert.IsAssignableFrom<ISandboxResumeAdmissionTracker>(provider);

        await suspending.ResumeSandboxAsync("codeybox-resume", CancellationToken.None);

        Assert.Equal(1, inner.ResumeCalls);
        Assert.Equal(1, admission.CurrentAdmittedSandboxes);

        var queuedCreate = provider.CreateAsync(Spec(), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(queuedCreate.IsCompleted);

        resumeTracker.ReleaseResumeAdmission("codeybox-resume");
        await Task.Delay(50);
        Assert.False(queuedCreate.IsCompleted);

        await provider.DisposeLeakedAsync("codeybox-resume", CancellationToken.None);
        await using var sandbox = await queuedCreate.WaitAsync(TestDeadline);

        Assert.Equal(1, admission.CurrentAdmittedSandboxes);
    }

    [Fact]
    public async Task ResumeAdmissionReleaseBeforeProviderCompletes_RetainsLeaseIfResumeEventuallySucceeds()
    {
        var inner = new CountingSandboxProvider { BlockResume = true };
        inner.ManagedSandboxes =
        [
            new ManagedSandboxInfo("codeybox-slow-resume", DateTimeOffset.UtcNow, DiskBytes: null, IsTrackedActive: false),
        ];
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);
        var suspending = Assert.IsAssignableFrom<ISuspendingSandboxProvider>(provider);
        var resumeTracker = Assert.IsAssignableFrom<ISandboxResumeAdmissionTracker>(provider);
        using var resumeCts = new CancellationTokenSource();

        var resume = suspending.ResumeSandboxAsync("codeybox-slow-resume", resumeCts.Token);
        await inner.ResumeStarted.Task.WaitAsync(TestDeadline);
        Assert.Equal(1, admission.CurrentAdmittedSandboxes);

        resumeTracker.ReleaseResumeAdmission("codeybox-slow-resume");
        await resumeCts.CancelAsync();
        inner.AllowResume.SetResult();
        await resume.WaitAsync(TestDeadline);

        var queuedCreate = provider.CreateAsync(Spec(), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(queuedCreate.IsCompleted);
        Assert.Equal(1, admission.CurrentAdmittedSandboxes);

        await provider.DisposeLeakedAsync("codeybox-slow-resume", CancellationToken.None);
        await using var sandbox = await queuedCreate.WaitAsync(TestDeadline);
    }

    [Fact]
    public async Task ResumeAdmissionLease_IsReleasedWhenManagedVmDisappears()
    {
        var inner = new CountingSandboxProvider();
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);
        var suspending = Assert.IsAssignableFrom<ISuspendingSandboxProvider>(provider);

        await suspending.ResumeSandboxAsync("codeybox-missing-resume", CancellationToken.None);
        Assert.Equal(1, admission.CurrentAdmittedSandboxes);

        var managed = await provider.ListAllManagedAsync(CancellationToken.None);

        Assert.Empty(managed);
        Assert.Equal(0, admission.CurrentAdmittedSandboxes);
    }

    [Fact]
    public async Task LeakManagementCalls_AreDelegatedThroughWrapper()
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var inner = new CountingSandboxProvider
        {
            ManagedSandboxes =
            [
                new ManagedSandboxInfo("codeybox-leak", createdAt, DiskBytes: 4096, IsTrackedActive: false),
            ],
        };
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);

        var managed = await provider.ListAllManagedAsync(CancellationToken.None);
        await provider.DisposeLeakedAsync("codeybox-leak", CancellationToken.None);

        var info = Assert.Single(managed);
        Assert.Equal("codeybox-leak", info.Name);
        Assert.Equal(1, inner.ListManagedCalls);
        Assert.Equal(["codeybox-leak"], inner.DisposedLeaks);
    }

    [Fact]
    public async Task WrappedProviderAndSandbox_PreserveMultipassCapabilitiesWithInnerEffects()
    {
        var inner = new CountingSandboxProvider();
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 2, NullLogger.Instance);

        var active = Assert.IsAssignableFrom<IActiveSandboxProvider>(provider);
        var suspending = Assert.IsAssignableFrom<ISuspendingSandboxProvider>(provider);
        var disk = Assert.IsAssignableFrom<IDiskGuardedSandboxProvider>(provider);
        var resolver = Assert.IsAssignableFrom<IBaselineImageResolver>(provider);
        var provisioner = Assert.IsAssignableFrom<IBaselineImageProvisioner>(provider);
        var resumeTracker = Assert.IsAssignableFrom<ISandboxResumeAdmissionTracker>(provider);

        await suspending.ResumeSandboxAsync("codeybox-cap", CancellationToken.None);
        resumeTracker.ReleaseResumeAdmission("codeybox-cap");
        Assert.Equal(1, inner.ResumeCalls);

        Assert.Equal(17, await suspending.WaitForAdoptedAgentCompletionAsync(
            "codeybox-cap",
            "/work/.codeybox/agent.log",
            logSink: null,
            deadline: TimeSpan.FromSeconds(1),
            CancellationToken.None));
        Assert.True(await suspending.PushSuspendedVmCheckpointRefAsync(
            "codeybox-cap",
            "/work",
            "refs/heads/codeybox/preempt/test",
            "checkpoint",
            CancellationToken.None));
        Assert.Empty(await suspending.ReconcileStuckSandboxesAsync(new HashSet<string>(), CancellationToken.None));

        Assert.Single(disk.SampleDiskGuardState());
        Assert.Equal("baseline", resolver.ResolveBaselineRef("default", SandboxProfileFlavor.Headless));
        Assert.Empty(await resolver.ListBaselineImagesAsync(CancellationToken.None));
        await resolver.DisposeBaselineImageAsync("baseline", CancellationToken.None);
        Assert.Equal("baseline", await provisioner.EnsureBaselineImageAsync(
            "default",
            SandboxProfileFlavor.Headless,
            pinnedBaselineRef: null,
            CancellationToken.None));

        await using var sandbox = await provider.CreateAsync(Spec(), CancellationToken.None);
        Assert.Single(active.SnapshotActiveSandboxes());

        var preemptible = Assert.IsAssignableFrom<IPreemptibleSandbox>(sandbox);
        var suspendable = Assert.IsAssignableFrom<ISuspendableSandbox>(sandbox);
        var shutdown = Assert.IsAssignableFrom<IShutdownTeardownSandbox>(sandbox);

        await preemptible.StopAndPreserveAsync(CancellationToken.None);
        await suspendable.SuspendAsync(CancellationToken.None);
        shutdown.MarkOwnedByShutdownHandler();

        Assert.True(suspendable.IsSuspended);
        Assert.True(shutdown.IsOwnedByShutdownHandler);
        Assert.Equal(1, inner.StopAndPreserveCalls);
        Assert.Equal(1, inner.SuspendCalls);
        Assert.Equal(1, inner.MarkOwnedCalls);
        Assert.Equal(1, inner.WaitForAdoptionCalls);
        Assert.Equal(1, inner.PushCheckpointCalls);
        Assert.Equal(1, inner.ReconcileCalls);
        Assert.Equal(1, inner.DiskSampleCalls);
        Assert.Equal(1, inner.ResolveBaselineCalls);
        Assert.Equal(1, inner.ListBaselineCalls);
        Assert.Equal(1, inner.DisposeBaselineCalls);
        Assert.Equal(1, inner.EnsureBaselineCalls);
    }

    [Fact]
    public async Task Wrap_PlainProvider_AdmissionControlsCreateWithoutAddingOptionalCapabilities()
    {
        var inner = new PlainCountingProvider();
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var first = await provider.CreateAsync(Spec(), CancellationToken.None);

        Assert.False(provider is IActiveSandboxProvider);
        Assert.False(provider is ISuspendingSandboxProvider);
        Assert.False(provider is IDiskGuardedSandboxProvider);

        var queued = provider.CreateAsync(Spec(), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(queued.IsCompleted);

        await first.DisposeAsync();
        await using var next = await queued.WaitAsync(TestDeadline);
    }

    [Fact]
    public async Task Wrap_ActiveOnlyProvider_PreservesActiveSnapshot()
    {
        var inner = new ActiveOnlyProvider();
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var active = Assert.IsAssignableFrom<IActiveSandboxProvider>(provider);

        await using var sandbox = await provider.CreateAsync(Spec(), CancellationToken.None);

        Assert.Single(active.SnapshotActiveSandboxes());
        Assert.False(provider is ISuspendingSandboxProvider);
    }

    [Fact]
    public async Task Wrap_SuspendingOnlyProvider_PreservesSuspendingCapability()
    {
        var inner = new SuspendingOnlyProvider();
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var suspending = Assert.IsAssignableFrom<ISuspendingSandboxProvider>(provider);
        var resumeTracker = Assert.IsAssignableFrom<ISandboxResumeAdmissionTracker>(provider);

        await suspending.ResumeSandboxAsync("codeybox-suspending", CancellationToken.None);
        resumeTracker.ReleaseResumeAdmission("codeybox-suspending");

        Assert.Equal(1, inner.ResumeCalls);
        Assert.False(provider is IActiveSandboxProvider);
    }

    [Fact]
    public void Wrap_DiskGuardOnlyProvider_PreservesDiskGuardCapability()
    {
        var inner = new DiskGuardOnlyProvider();
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var disk = Assert.IsAssignableFrom<IDiskGuardedSandboxProvider>(provider);

        Assert.Single(disk.SampleDiskGuardState());
        Assert.Equal(1, inner.DiskSampleCalls);
        Assert.False(provider is IActiveSandboxProvider);
    }

    [Fact]
    public async Task Wrap_ActiveSuspendingProvider_PreservesBothCapabilities()
    {
        var inner = new ActiveSuspendingOnlyProvider();
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var active = Assert.IsAssignableFrom<IActiveSandboxProvider>(provider);
        var suspending = Assert.IsAssignableFrom<ISuspendingSandboxProvider>(provider);
        var resumeTracker = Assert.IsAssignableFrom<ISandboxResumeAdmissionTracker>(provider);

        await suspending.ResumeSandboxAsync("codeybox-active-suspending", CancellationToken.None);
        resumeTracker.ReleaseResumeAdmission("codeybox-active-suspending");
        await provider.DisposeLeakedAsync("codeybox-active-suspending", CancellationToken.None);
        await using var sandbox = await provider.CreateAsync(Spec(), CancellationToken.None);

        Assert.Single(active.SnapshotActiveSandboxes());
        Assert.Equal(1, inner.ResumeCalls);
    }

    [Fact]
    public void Wrap_ActiveDiskGuardProvider_PreservesBothCapabilities()
    {
        var inner = new ActiveDiskGuardProvider();
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);

        Assert.IsAssignableFrom<IActiveSandboxProvider>(provider);
        var disk = Assert.IsAssignableFrom<IDiskGuardedSandboxProvider>(provider);

        Assert.Single(disk.SampleDiskGuardState());
        Assert.Equal(1, inner.DiskSampleCalls);
        Assert.False(provider is ISuspendingSandboxProvider);
    }

    private static SandboxSpec Spec(int item = 0, int auditor = -1) => new()
    {
        ImageReference = "test",
        TimingWorkItemId = new WorkItemId(Guid.NewGuid()),
        TimingPhase = auditor < 0 ? $"item-{item}" : $"item-{item}-auditor-{auditor}",
    };

    private class PlainCountingProvider : ISandboxProvider
    {
        private int _active;
        private int _created;
        private int _createAttempts;
        private int _listManagedCalls;
        private int _peakActive;
        private readonly List<string> _disposedLeaks = [];

        public int Active => Volatile.Read(ref _active);
        public int Created => Volatile.Read(ref _created);
        public int CreateAttempts => Volatile.Read(ref _createAttempts);
        public int ListManagedCalls => Volatile.Read(ref _listManagedCalls);
        public int PeakActive => Volatile.Read(ref _peakActive);
        public IReadOnlyList<string> DisposedLeaks
        {
            get
            {
                lock (_disposedLeaks) return _disposedLeaks.ToArray();
            }
        }
        public IReadOnlyList<ManagedSandboxInfo> ManagedSandboxes { get; set; } = [];
        public bool FailNextCreate { get; set; }
        public bool ThrowOnNextSandboxDispose { get; set; }

        public string Name => "counting";

        public virtual Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _createAttempts);
            if (FailNextCreate)
            {
                FailNextCreate = false;
                throw new InvalidOperationException("create failed");
            }

            var active = Interlocked.Increment(ref _active);
            var created = Interlocked.Increment(ref _created);
            UpdatePeak(active);
            var disposeThrows = ThrowOnNextSandboxDispose;
            ThrowOnNextSandboxDispose = false;
            return Task.FromResult<ISandbox>(new CountingSandbox(this, $"sandbox-{created}", disposeThrows));
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _listManagedCalls);
            return Task.FromResult(ManagedSandboxes);
        }

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
        {
            lock (_disposedLeaks) _disposedLeaks.Add(name);
            return Task.CompletedTask;
        }

        public void Release()
        {
            Interlocked.Decrement(ref _active);
        }

        private void UpdatePeak(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _peakActive);
                if (active <= current)
                    return;
                if (Interlocked.CompareExchange(ref _peakActive, active, current) == current)
                    return;
            }
        }
    }

    private sealed class ActiveOnlyProvider : PlainCountingProvider, IActiveSandboxProvider
    {
        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
            [];
    }

    private class SuspendingOnlyProvider : PlainCountingProvider, ISuspendingSandboxProvider
    {
        private int _resumeCalls;

        public int ResumeCalls => Volatile.Read(ref _resumeCalls);

        public Task ResumeSandboxAsync(string name, CancellationToken ct)
        {
            Interlocked.Increment(ref _resumeCalls);
            return Task.CompletedTask;
        }
    }

    private sealed class ActiveSuspendingOnlyProvider : SuspendingOnlyProvider, IActiveSandboxProvider
    {
        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
            [];
    }

    private sealed class DiskGuardOnlyProvider : PlainCountingProvider, IDiskGuardedSandboxProvider
    {
        private int _diskSampleCalls;

        public int DiskSampleCalls => Volatile.Read(ref _diskSampleCalls);

        public IReadOnlyList<DiskGuardSample> SampleDiskGuardState()
        {
            Interlocked.Increment(ref _diskSampleCalls);
            return [new DiskGuardSample("/tmp", 1024, 512)];
        }
    }

    private sealed class ActiveDiskGuardProvider : PlainCountingProvider, IActiveSandboxProvider, IDiskGuardedSandboxProvider
    {
        private int _diskSampleCalls;

        public int DiskSampleCalls => Volatile.Read(ref _diskSampleCalls);

        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
            [];

        public IReadOnlyList<DiskGuardSample> SampleDiskGuardState()
        {
            Interlocked.Increment(ref _diskSampleCalls);
            return [new DiskGuardSample("/tmp", 1024, 512)];
        }
    }

    private sealed class CountingSandboxProvider :
        PlainCountingProvider,
        IActiveSandboxProvider,
        ISuspendingSandboxProvider,
        IDiskGuardedSandboxProvider,
        IBaselineImageResolver,
        IBaselineImageProvisioner
    {
        private int _resumeCalls;
        private int _waitForAdoptionCalls;
        private int _pushCheckpointCalls;
        private int _reconcileCalls;
        private int _diskSampleCalls;
        private int _resolveBaselineCalls;
        private int _listBaselineCalls;
        private int _disposeBaselineCalls;
        private int _ensureBaselineCalls;
        private int _stopAndPreserveCalls;
        private int _suspendCalls;
        private int _markOwnedCalls;

        public int ResumeCalls => Volatile.Read(ref _resumeCalls);
        public int WaitForAdoptionCalls => Volatile.Read(ref _waitForAdoptionCalls);
        public int PushCheckpointCalls => Volatile.Read(ref _pushCheckpointCalls);
        public int ReconcileCalls => Volatile.Read(ref _reconcileCalls);
        public int DiskSampleCalls => Volatile.Read(ref _diskSampleCalls);
        public int ResolveBaselineCalls => Volatile.Read(ref _resolveBaselineCalls);
        public int ListBaselineCalls => Volatile.Read(ref _listBaselineCalls);
        public int DisposeBaselineCalls => Volatile.Read(ref _disposeBaselineCalls);
        public int EnsureBaselineCalls => Volatile.Read(ref _ensureBaselineCalls);
        public int StopAndPreserveCalls => Volatile.Read(ref _stopAndPreserveCalls);
        public int SuspendCalls => Volatile.Read(ref _suspendCalls);
        public int MarkOwnedCalls => Volatile.Read(ref _markOwnedCalls);
        public bool BlockEnsureBaseline { get; init; }
        public bool BlockResume { get; init; }
        public TaskCompletionSource ResumeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowResume { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EnsureBaselineStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowEnsureBaseline { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            var sandbox = await base.CreateAsync(spec, ct);
            var counting = Assert.IsType<CountingSandbox>(sandbox);
            counting.OnStopAndPreserve = () => Interlocked.Increment(ref _stopAndPreserveCalls);
            counting.OnSuspend = () => Interlocked.Increment(ref _suspendCalls);
            counting.OnMarkOwned = () => Interlocked.Increment(ref _markOwnedCalls);
            return sandbox;
        }

        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
            [];

        public async Task ResumeSandboxAsync(string name, CancellationToken ct)
        {
            Interlocked.Increment(ref _resumeCalls);
            if (BlockResume)
            {
                ResumeStarted.SetResult();
                await AllowResume.Task;
            }
        }

        public Task<int?> WaitForAdoptedAgentCompletionAsync(
            string vmName,
            string agentLogPath,
            Action<string>? logSink,
            TimeSpan? deadline,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _waitForAdoptionCalls);
            return Task.FromResult<int?>(17);
        }

        public Task<bool> PushSuspendedVmCheckpointRefAsync(
            string vmName,
            string workingDir,
            string refName,
            string commitMessage,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _pushCheckpointCalls);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<string>> ReconcileStuckSandboxesAsync(
            IReadOnlySet<string> liveSuspendedNames,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _reconcileCalls);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public IReadOnlyList<DiskGuardSample> SampleDiskGuardState()
        {
            Interlocked.Increment(ref _diskSampleCalls);
            return [new DiskGuardSample("/tmp", 1024, 512)];
        }

        public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
        {
            Interlocked.Increment(ref _resolveBaselineCalls);
            return "baseline";
        }

        public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _listBaselineCalls);
            return Task.FromResult<IReadOnlyList<BaselineImageInfo>>([]);
        }

        public Task DisposeBaselineImageAsync(string name, CancellationToken ct)
        {
            Interlocked.Increment(ref _disposeBaselineCalls);
            return Task.CompletedTask;
        }

        public async Task<string?> EnsureBaselineImageAsync(
            string profileName,
            SandboxProfileFlavor flavor,
            string? pinnedBaselineRef,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _ensureBaselineCalls);
            if (BlockEnsureBaseline)
            {
                EnsureBaselineStarted.SetResult();
                await AllowEnsureBaseline.Task.WaitAsync(ct);
            }
            return "baseline";
        }
    }

    private sealed class CountingSandbox(PlainCountingProvider provider, string id, bool disposeThrows) :
        IPreemptibleSandbox,
        ISuspendableSandbox,
        IShutdownTeardownSandbox
    {
        private int _disposed;
        private bool _ownedByShutdownHandler;
        private bool _suspended;

        public Action? OnStopAndPreserve { get; set; }
        public Action? OnSuspend { get; set; }
        public Action? OnMarkOwned { get; set; }

        public string Id { get; } = id;

        public bool IsOwnedByShutdownHandler => _ownedByShutdownHandler || _suspended;

        public bool IsSuspended => _suspended;

        public long? MemoryBytes => 1024;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task StopAndPreserveAsync(CancellationToken ct = default)
        {
            _ownedByShutdownHandler = true;
            OnStopAndPreserve?.Invoke();
            return Task.CompletedTask;
        }

        public Task SuspendAsync(CancellationToken ct = default)
        {
            _suspended = true;
            OnSuspend?.Invoke();
            return Task.CompletedTask;
        }

        public void MarkOwnedByShutdownHandler()
        {
            _ownedByShutdownHandler = true;
            OnMarkOwned?.Invoke();
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                provider.Release();
                if (disposeThrows)
                    return ValueTask.FromException(new InvalidOperationException("dispose failed"));
            }
            return ValueTask.CompletedTask;
        }
    }
}
