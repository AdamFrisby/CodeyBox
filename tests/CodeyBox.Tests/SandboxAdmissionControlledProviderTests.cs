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
    public async Task WrappedProviderAndSandbox_PreserveMultipassCapabilities()
    {
        var inner = new CountingSandboxProvider();
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 2, NullLogger.Instance);

        Assert.IsAssignableFrom<IActiveSandboxProvider>(provider);
        Assert.IsAssignableFrom<ISuspendingSandboxProvider>(provider);
        Assert.IsAssignableFrom<IDiskGuardedSandboxProvider>(provider);
        Assert.IsAssignableFrom<IBaselineImageResolver>(provider);
        Assert.IsAssignableFrom<IBaselineImageProvisioner>(provider);

        await using var sandbox = await provider.CreateAsync(Spec(), CancellationToken.None);

        Assert.IsAssignableFrom<IPreemptibleSandbox>(sandbox);
        Assert.IsAssignableFrom<ISuspendableSandbox>(sandbox);
        Assert.IsAssignableFrom<IShutdownTeardownSandbox>(sandbox);
    }

    private static SandboxSpec Spec(int item = 0, int auditor = -1) => new()
    {
        ImageReference = "test",
        TimingWorkItemId = new WorkItemId(Guid.NewGuid()),
        TimingPhase = auditor < 0 ? $"item-{item}" : $"item-{item}-auditor-{auditor}",
    };

    private sealed class CountingSandboxProvider :
        ISandboxProvider,
        IActiveSandboxProvider,
        ISuspendingSandboxProvider,
        IDiskGuardedSandboxProvider,
        IBaselineImageResolver,
        IBaselineImageProvisioner
    {
        private int _active;
        private int _created;
        private int _peakActive;

        public int Active => Volatile.Read(ref _active);
        public int Created => Volatile.Read(ref _created);
        public int PeakActive => Volatile.Read(ref _peakActive);

        public string Name => "counting";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var active = Interlocked.Increment(ref _active);
            var created = Interlocked.Increment(ref _created);
            UpdatePeak(active);
            return Task.FromResult<ISandbox>(new CountingSandbox(this, $"sandbox-{created}"));
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() => [];

        public Task ResumeSandboxAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public Task<int?> WaitForAdoptedAgentCompletionAsync(
            string vmName,
            string agentLogPath,
            Action<string>? logSink,
            TimeSpan? deadline,
            CancellationToken ct) =>
            Task.FromResult<int?>(0);

        public Task<bool> PushSuspendedVmCheckpointRefAsync(
            string vmName,
            string workingDir,
            string refName,
            string commitMessage,
            CancellationToken ct) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<string>> ReconcileStuckSandboxesAsync(
            IReadOnlySet<string> liveSuspendedNames,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public IReadOnlyList<DiskGuardSample> SampleDiskGuardState() =>
            [new DiskGuardSample("/tmp", 1024, 512)];

        public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor) => "baseline";

        public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BaselineImageInfo>>([]);

        public Task DisposeBaselineImageAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> EnsureBaselineImageAsync(
            string profileName,
            SandboxProfileFlavor flavor,
            string? pinnedBaselineRef,
            CancellationToken ct) =>
            Task.FromResult<string?>("baseline");

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

    private sealed class CountingSandbox(CountingSandboxProvider provider, string id) :
        IPreemptibleSandbox,
        ISuspendableSandbox,
        IShutdownTeardownSandbox
    {
        private int _disposed;
        private bool _ownedByShutdownHandler;
        private bool _suspended;

        public string Id { get; } = id;

        public bool IsOwnedByShutdownHandler => _ownedByShutdownHandler || _suspended;

        public bool IsSuspended => _suspended;

        public long? MemoryBytes => 1024;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task StopAndPreserveAsync(CancellationToken ct = default)
        {
            _ownedByShutdownHandler = true;
            return Task.CompletedTask;
        }

        public Task SuspendAsync(CancellationToken ct = default)
        {
            _suspended = true;
            return Task.CompletedTask;
        }

        public void MarkOwnedByShutdownHandler()
        {
            _ownedByShutdownHandler = true;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                provider.Release();
            return ValueTask.CompletedTask;
        }
    }
}
