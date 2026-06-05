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
    public async Task CreateFailureWithRetainedSandbox_RetainsAdmissionUntilLeakDisposal()
    {
        var inner = new CountingSandboxProvider
        {
            FailNextCreateRetainedSandboxName = "sandbox-retained",
        };
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);

        await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() => provider.CreateAsync(Spec()));

        Assert.Equal(1, admission.CurrentAdmittedSandboxes);
        var queued = provider.CreateAsync(Spec(), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(queued.IsCompleted);

        await provider.DisposeLeakedAsync("sandbox-retained", CancellationToken.None);
        await using var next = await queued.WaitAsync(TestDeadline);

        Assert.Equal(["sandbox-retained"], inner.DisposedLeaks);
        Assert.Equal(2, inner.CreateAttempts);
    }

    [Fact]
    public async Task DisposeFailure_RetainsAdmissionTokenUntilManagedVmDisappears()
    {
        var inner = new CountingSandboxProvider { ThrowOnNextSandboxDispose = true };
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);

        var sandbox = await provider.CreateAsync(Spec());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sandbox.DisposeAsync());

        var queuedCreate = provider.CreateAsync(Spec(), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(queuedCreate.IsCompleted);

        await provider.ListAllManagedAsync(CancellationToken.None);
        await using var next = await queuedCreate.WaitAsync(TestDeadline);

        Assert.Equal(2, inner.Created);
    }

    [Fact]
    public async Task DisposeLeavesHostSandbox_RetainsAdmissionTokenUntilLeakDisposalSucceeds()
    {
        var inner = new CountingSandboxProvider { LeaveHostSandboxAfterNextDispose = true };
        inner.ManagedSandboxes =
        [
            new ManagedSandboxInfo("sandbox-1", DateTimeOffset.UtcNow, DiskBytes: null, IsTrackedActive: false),
        ];
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);

        var sandbox = await provider.CreateAsync(Spec());
        await sandbox.DisposeAsync();

        var queuedCreate = provider.CreateAsync(Spec(), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(queuedCreate.IsCompleted);

        await provider.DisposeLeakedAsync("sandbox-1", CancellationToken.None);
        await using var next = await queuedCreate.WaitAsync(TestDeadline);

        Assert.Equal(["sandbox-1"], inner.DisposedLeaks);
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
    public async Task BaselineProvisioningFailure_ReleasesAdmissionToken()
    {
        var inner = new CountingSandboxProvider { ThrowOnEnsureBaseline = true };
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);
        var provisioner = Assert.IsAssignableFrom<IBaselineImageProvisioner>(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.EnsureBaselineImageAsync(
            "default",
            SandboxProfileFlavor.Headless,
            pinnedBaselineRef: null,
            CancellationToken.None));

        Assert.Equal(0, admission.CurrentAdmittedSandboxes);
        await using var sandbox = await provider.CreateAsync(Spec()).WaitAsync(TestDeadline);
    }

    [Fact]
    public async Task DisposeLeakedAsync_ReleasesRetainedResumeLease()
    {
        var inner = new CountingSandboxProvider();
        inner.ManagedSandboxes =
        [
            new ManagedSandboxInfo("codeybox-resume", DateTimeOffset.UtcNow, DiskBytes: null, IsTrackedActive: false),
        ];
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);
        var suspending = Assert.IsAssignableFrom<ISuspendingSandboxProvider>(provider);

        await suspending.ResumeSandboxAsync("codeybox-resume", CancellationToken.None);

        Assert.Equal(1, inner.ResumeCalls);
        Assert.Equal(1, admission.CurrentAdmittedSandboxes);

        var queuedCreate = provider.CreateAsync(Spec(), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(queuedCreate.IsCompleted);

        await provider.DisposeLeakedAsync("codeybox-resume", CancellationToken.None);
        await using var sandbox = await queuedCreate.WaitAsync(TestDeadline);

        Assert.Equal(1, admission.CurrentAdmittedSandboxes);
    }

    [Fact]
    public async Task DisposeLeakedBeforeResumeCompletes_ReleasesLeaseIfResumeEventuallySucceeds()
    {
        var inner = new CountingSandboxProvider { BlockResume = true };
        inner.ManagedSandboxes =
        [
            new ManagedSandboxInfo("codeybox-slow-resume", DateTimeOffset.UtcNow, DiskBytes: null, IsTrackedActive: false),
        ];
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);
        var suspending = Assert.IsAssignableFrom<ISuspendingSandboxProvider>(provider);
        using var resumeCts = new CancellationTokenSource();

        var resume = suspending.ResumeSandboxAsync("codeybox-slow-resume", resumeCts.Token);
        await inner.ResumeStarted.Task.WaitAsync(TestDeadline);
        Assert.Equal(1, admission.CurrentAdmittedSandboxes);

        await provider.DisposeLeakedAsync("codeybox-slow-resume", CancellationToken.None);
        await resumeCts.CancelAsync();
        inner.AllowResume.SetResult();
        await resume.WaitAsync(TestDeadline);

        Assert.Equal(0, admission.CurrentAdmittedSandboxes);
        await using var sandbox = await provider.CreateAsync(Spec(), CancellationToken.None).WaitAsync(TestDeadline);
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

        await suspending.ResumeSandboxAsync("codeybox-cap", CancellationToken.None);
        await provider.DisposeLeakedAsync("codeybox-cap", CancellationToken.None);
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

        using var execCts = new CancellationTokenSource();
        var execResult = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }, execCts.Token);
        Assert.Equal(0, execResult.ExitCode);
        Assert.Equal([1, 2, 3], await sandbox.GetScreenshotAsync(CancellationToken.None));
        await sandbox.SynthesizeInputAsync(
            [new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "A" }],
            CancellationToken.None);
        Assert.NotNull(await sandbox.GetAccessibilityAtPointAsync(10, 20, CancellationToken.None));
        Assert.Equal("{}", await sandbox.GetAccessibilityTreeJsonAsync(CancellationToken.None));

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
        Assert.Equal(1, inner.ExecCalls);
        Assert.True(inner.ExecSawCancellableToken);
        Assert.Equal(1, inner.ScreenshotCalls);
        Assert.Equal(1, inner.InputCalls);
        Assert.Equal(1, inner.AccessibilityAtPointCalls);
        Assert.Equal(1, inner.AccessibilityTreeCalls);
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

        await suspending.ResumeSandboxAsync("codeybox-suspending", CancellationToken.None);
        await provider.DisposeLeakedAsync("codeybox-suspending", CancellationToken.None);

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

        await suspending.ResumeSandboxAsync("codeybox-active-suspending", CancellationToken.None);
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

    public static IEnumerable<object[]> ProviderCapabilityCases()
    {
        for (var value = 0; value < 32; value++)
            yield return [(ProviderCapabilitySet)value];
    }

    [Theory]
    [MemberData(nameof(ProviderCapabilityCases))]
    public async Task Wrap_PreservesEveryProviderCapabilityCombination(ProviderCapabilitySet capabilities)
    {
        var inner = CreateProviderFor(capabilities);
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 2, NullLogger.Instance);

        Assert.Equal(capabilities.HasFlag(ProviderCapabilitySet.Active), provider is IActiveSandboxProvider);
        Assert.Equal(capabilities.HasFlag(ProviderCapabilitySet.Suspending), provider is ISuspendingSandboxProvider);
        Assert.Equal(capabilities.HasFlag(ProviderCapabilitySet.DiskGuard), provider is IDiskGuardedSandboxProvider);
        Assert.Equal(capabilities.HasFlag(ProviderCapabilitySet.BaselineResolver), provider is IBaselineImageResolver);
        Assert.Equal(capabilities.HasFlag(ProviderCapabilitySet.BaselineProvisioner), provider is IBaselineImageProvisioner);

        if (provider is ISuspendingSandboxProvider suspending)
        {
            await suspending.ResumeSandboxAsync($"codeybox-resume-{(int)capabilities}", CancellationToken.None);
            await provider.DisposeLeakedAsync($"codeybox-resume-{(int)capabilities}", CancellationToken.None);
            Assert.Equal(1, inner.ResumeCalls);
        }

        if (provider is IDiskGuardedSandboxProvider disk)
        {
            Assert.Single(disk.SampleDiskGuardState());
            Assert.Equal(1, inner.DiskSampleCalls);
        }

        if (provider is IBaselineImageResolver resolver)
        {
            Assert.Equal("baseline", resolver.ResolveBaselineRef("default", SandboxProfileFlavor.Headless));
            Assert.Empty(await resolver.ListBaselineImagesAsync(CancellationToken.None));
            await resolver.DisposeBaselineImageAsync("baseline", CancellationToken.None);
            Assert.Equal(1, inner.ResolveBaselineCalls);
            Assert.Equal(1, inner.ListBaselineCalls);
            Assert.Equal(1, inner.DisposeBaselineCalls);
        }

        if (provider is IBaselineImageProvisioner provisioner)
        {
            Assert.Equal("baseline", await provisioner.EnsureBaselineImageAsync(
                "default",
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None));
            Assert.Equal(1, inner.EnsureBaselineCalls);
        }

        await using var sandbox = await provider.CreateAsync(Spec(), CancellationToken.None);
        if (provider is IActiveSandboxProvider active)
            Assert.Single(active.SnapshotActiveSandboxes());
    }

    public static IEnumerable<object[]> SandboxCapabilityCases()
    {
        for (var value = 0; value < 8; value++)
            yield return [(SandboxCapabilitySet)value];
    }

    [Theory]
    [MemberData(nameof(SandboxCapabilityCases))]
    public async Task WrapSandbox_PreservesEverySandboxCapabilityCombination(SandboxCapabilitySet capabilities)
    {
        var inner = new SandboxCapabilityProvider(capabilities);
        var provider = SandboxAdmissionControlledProvider.Wrap(inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);

        var sandbox = await provider.CreateAsync(Spec(), CancellationToken.None);
        Assert.Equal(1, admission.CurrentAdmittedSandboxes);
        Assert.Equal(capabilities.HasFlag(SandboxCapabilitySet.Preemptible), sandbox is IPreemptibleSandbox);
        Assert.Equal(capabilities.HasFlag(SandboxCapabilitySet.Suspendable), sandbox is ISuspendableSandbox);
        Assert.Equal(capabilities.HasFlag(SandboxCapabilitySet.Shutdown), sandbox is IShutdownTeardownSandbox);

        if (sandbox is IPreemptibleSandbox preemptible)
            await preemptible.StopAndPreserveAsync(CancellationToken.None);
        if (sandbox is ISuspendableSandbox suspendable)
        {
            await suspendable.SuspendAsync(CancellationToken.None);
            Assert.True(suspendable.IsSuspended);
            Assert.Equal(1024, suspendable.MemoryBytes);
        }
        if (sandbox is IShutdownTeardownSandbox shutdown)
        {
            shutdown.MarkOwnedByShutdownHandler();
            Assert.True(shutdown.IsOwnedByShutdownHandler);
        }

        await sandbox.DisposeAsync();
        Assert.Equal(0, admission.CurrentAdmittedSandboxes);
        Assert.True(inner.LastSandbox!.Disposed);
    }

    private static CapabilityProviderBase CreateProviderFor(ProviderCapabilitySet capabilities) => capabilities switch
    {
        ProviderCapabilitySet.None => new MatrixNoneProvider(),
        ProviderCapabilitySet.Active => new MatrixActiveProvider(),
        ProviderCapabilitySet.Suspending => new MatrixSuspendingProvider(),
        ProviderCapabilitySet.DiskGuard => new MatrixDiskGuardProvider(),
        ProviderCapabilitySet.BaselineResolver => new MatrixBaselineResolverProvider(),
        ProviderCapabilitySet.BaselineProvisioner => new MatrixBaselineProvisionerProvider(),
        ProviderCapabilitySet.Active | ProviderCapabilitySet.Suspending => new MatrixActiveSuspendingProvider(),
        ProviderCapabilitySet.Active | ProviderCapabilitySet.DiskGuard => new MatrixActiveDiskGuardProvider(),
        ProviderCapabilitySet.Active | ProviderCapabilitySet.BaselineResolver => new MatrixActiveBaselineResolverProvider(),
        ProviderCapabilitySet.Active | ProviderCapabilitySet.BaselineProvisioner => new MatrixActiveBaselineProvisionerProvider(),
        ProviderCapabilitySet.Suspending | ProviderCapabilitySet.DiskGuard => new MatrixSuspendingDiskGuardProvider(),
        ProviderCapabilitySet.Suspending | ProviderCapabilitySet.BaselineResolver => new MatrixSuspendingBaselineResolverProvider(),
        ProviderCapabilitySet.Suspending | ProviderCapabilitySet.BaselineProvisioner => new MatrixSuspendingBaselineProvisionerProvider(),
        ProviderCapabilitySet.DiskGuard | ProviderCapabilitySet.BaselineResolver => new MatrixDiskGuardBaselineResolverProvider(),
        ProviderCapabilitySet.DiskGuard | ProviderCapabilitySet.BaselineProvisioner => new MatrixDiskGuardBaselineProvisionerProvider(),
        ProviderCapabilitySet.BaselineResolver | ProviderCapabilitySet.BaselineProvisioner => new MatrixBaselineProvider(),
        ProviderCapabilitySet.Active | ProviderCapabilitySet.Suspending | ProviderCapabilitySet.DiskGuard => new MatrixActiveSuspendingDiskGuardProvider(),
        ProviderCapabilitySet.Active | ProviderCapabilitySet.Suspending | ProviderCapabilitySet.BaselineResolver => new MatrixActiveSuspendingBaselineResolverProvider(),
        ProviderCapabilitySet.Active | ProviderCapabilitySet.Suspending | ProviderCapabilitySet.BaselineProvisioner => new MatrixActiveSuspendingBaselineProvisionerProvider(),
        ProviderCapabilitySet.Active | ProviderCapabilitySet.DiskGuard | ProviderCapabilitySet.BaselineResolver => new MatrixActiveDiskGuardBaselineResolverProvider(),
        ProviderCapabilitySet.Active | ProviderCapabilitySet.DiskGuard | ProviderCapabilitySet.BaselineProvisioner => new MatrixActiveDiskGuardBaselineProvisionerProvider(),
        ProviderCapabilitySet.Active | ProviderCapabilitySet.BaselineResolver | ProviderCapabilitySet.BaselineProvisioner => new MatrixActiveBaselineProvider(),
        ProviderCapabilitySet.Suspending | ProviderCapabilitySet.DiskGuard | ProviderCapabilitySet.BaselineResolver => new MatrixSuspendingDiskGuardBaselineResolverProvider(),
        ProviderCapabilitySet.Suspending | ProviderCapabilitySet.DiskGuard | ProviderCapabilitySet.BaselineProvisioner => new MatrixSuspendingDiskGuardBaselineProvisionerProvider(),
        ProviderCapabilitySet.Suspending | ProviderCapabilitySet.BaselineResolver | ProviderCapabilitySet.BaselineProvisioner => new MatrixSuspendingBaselineProvider(),
        ProviderCapabilitySet.DiskGuard | ProviderCapabilitySet.BaselineResolver | ProviderCapabilitySet.BaselineProvisioner => new MatrixDiskGuardBaselineProvider(),
        ProviderCapabilitySet.Active | ProviderCapabilitySet.Suspending | ProviderCapabilitySet.DiskGuard | ProviderCapabilitySet.BaselineResolver => new MatrixActiveSuspendingDiskGuardBaselineResolverProvider(),
        ProviderCapabilitySet.Active | ProviderCapabilitySet.Suspending | ProviderCapabilitySet.DiskGuard | ProviderCapabilitySet.BaselineProvisioner => new MatrixActiveSuspendingDiskGuardBaselineProvisionerProvider(),
        ProviderCapabilitySet.Active | ProviderCapabilitySet.Suspending | ProviderCapabilitySet.BaselineResolver | ProviderCapabilitySet.BaselineProvisioner => new MatrixActiveSuspendingBaselineProvider(),
        ProviderCapabilitySet.Active | ProviderCapabilitySet.DiskGuard | ProviderCapabilitySet.BaselineResolver | ProviderCapabilitySet.BaselineProvisioner => new MatrixActiveDiskGuardBaselineProvider(),
        ProviderCapabilitySet.Suspending | ProviderCapabilitySet.DiskGuard | ProviderCapabilitySet.BaselineResolver | ProviderCapabilitySet.BaselineProvisioner => new MatrixSuspendingDiskGuardBaselineProvider(),
        ProviderCapabilitySet.Active | ProviderCapabilitySet.Suspending | ProviderCapabilitySet.DiskGuard | ProviderCapabilitySet.BaselineResolver | ProviderCapabilitySet.BaselineProvisioner => new MatrixActiveSuspendingDiskGuardBaselineProvider(),
        _ => throw new ArgumentOutOfRangeException(nameof(capabilities), capabilities, null),
    };

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
        private int _execCalls;
        private int _execSawCancellableToken;
        private int _screenshotCalls;
        private int _inputCalls;
        private int _accessibilityAtPointCalls;
        private int _accessibilityTreeCalls;
        private readonly List<string> _disposedLeaks = [];

        public int Active => Volatile.Read(ref _active);
        public int Created => Volatile.Read(ref _created);
        public int CreateAttempts => Volatile.Read(ref _createAttempts);
        public int ListManagedCalls => Volatile.Read(ref _listManagedCalls);
        public int PeakActive => Volatile.Read(ref _peakActive);
        public int ExecCalls => Volatile.Read(ref _execCalls);
        public bool ExecSawCancellableToken => Volatile.Read(ref _execSawCancellableToken) != 0;
        public int ScreenshotCalls => Volatile.Read(ref _screenshotCalls);
        public int InputCalls => Volatile.Read(ref _inputCalls);
        public int AccessibilityAtPointCalls => Volatile.Read(ref _accessibilityAtPointCalls);
        public int AccessibilityTreeCalls => Volatile.Read(ref _accessibilityTreeCalls);
        public IReadOnlyList<string> DisposedLeaks
        {
            get
            {
                lock (_disposedLeaks) return _disposedLeaks.ToArray();
            }
        }
        public IReadOnlyList<ManagedSandboxInfo> ManagedSandboxes { get; set; } = [];
        public bool FailNextCreate { get; set; }
        public string? FailNextCreateRetainedSandboxName { get; set; }
        public bool ThrowOnNextSandboxDispose { get; set; }
        public bool LeaveHostSandboxAfterNextDispose { get; set; }

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
            if (FailNextCreateRetainedSandboxName is { } retainedSandboxName)
            {
                FailNextCreateRetainedSandboxName = null;
                throw new SandboxProvisioningDeferredException(
                    "counting",
                    "create-cleanup",
                    "delete-failed",
                    "create failed and cleanup did not prove removal",
                    TimeSpan.FromSeconds(1),
                    retainedSandboxName: retainedSandboxName);
            }

            var active = Interlocked.Increment(ref _active);
            var created = Interlocked.Increment(ref _created);
            UpdatePeak(active);
            var disposeThrows = ThrowOnNextSandboxDispose;
            ThrowOnNextSandboxDispose = false;
            var leaveHostSandbox = LeaveHostSandboxAfterNextDispose;
            LeaveHostSandboxAfterNextDispose = false;
            return Task.FromResult<ISandbox>(new CountingSandbox(this, $"sandbox-{created}", disposeThrows, leaveHostSandbox));
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

        public void RecordExec(CancellationToken ct)
        {
            Interlocked.Increment(ref _execCalls);
            if (ct.CanBeCanceled)
                Interlocked.Exchange(ref _execSawCancellableToken, 1);
        }

        public void RecordScreenshot() => Interlocked.Increment(ref _screenshotCalls);

        public void RecordInput() => Interlocked.Increment(ref _inputCalls);

        public void RecordAccessibilityAtPoint() => Interlocked.Increment(ref _accessibilityAtPointCalls);

        public void RecordAccessibilityTree() => Interlocked.Increment(ref _accessibilityTreeCalls);

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

    [Flags]
    public enum ProviderCapabilitySet
    {
        None = 0,
        Active = 1,
        Suspending = 2,
        DiskGuard = 4,
        BaselineResolver = 8,
        BaselineProvisioner = 16,
    }

    [Flags]
    public enum SandboxCapabilitySet
    {
        None = 0,
        Preemptible = 1,
        Suspendable = 2,
        Shutdown = 4,
    }

    private sealed class SandboxCapabilityProvider(SandboxCapabilitySet capabilities) : ISandboxProvider
    {
        public CapabilitySandboxBase? LastSandbox { get; private set; }
        public string Name => "sandbox-capability";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            LastSandbox = capabilities switch
            {
                SandboxCapabilitySet.None => new MatrixPlainSandbox(),
                SandboxCapabilitySet.Preemptible => new MatrixPreemptibleSandbox(),
                SandboxCapabilitySet.Suspendable => new MatrixSuspendableSandbox(),
                SandboxCapabilitySet.Shutdown => new MatrixShutdownSandbox(),
                SandboxCapabilitySet.Preemptible | SandboxCapabilitySet.Suspendable => new MatrixPreemptibleSuspendableSandbox(),
                SandboxCapabilitySet.Preemptible | SandboxCapabilitySet.Shutdown => new MatrixPreemptibleShutdownSandbox(),
                SandboxCapabilitySet.Suspendable | SandboxCapabilitySet.Shutdown => new MatrixSuspendableShutdownSandbox(),
                SandboxCapabilitySet.Preemptible | SandboxCapabilitySet.Suspendable | SandboxCapabilitySet.Shutdown => new MatrixFullSandbox(),
                _ => throw new ArgumentOutOfRangeException(nameof(capabilities), capabilities, null),
            };
            return Task.FromResult<ISandbox>(LastSandbox);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private abstract class CapabilitySandboxBase : ISandbox
    {
        private bool _suspended;
        private bool _owned;

        public string Id { get; } = $"sandbox-{Guid.NewGuid():N}";
        public bool Disposed { get; private set; }
        public bool IsSuspended => _suspended;
        public bool IsOwnedByShutdownHandler => _owned || _suspended;
        public long? MemoryBytes => 1024;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, "", ""));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public Task StopAndPreserveAsync(CancellationToken ct = default)
        {
            _owned = true;
            return Task.CompletedTask;
        }

        public Task SuspendAsync(CancellationToken ct = default)
        {
            _suspended = true;
            return Task.CompletedTask;
        }

        public void MarkOwnedByShutdownHandler() => _owned = true;
    }

    private sealed class MatrixPlainSandbox : CapabilitySandboxBase { }
    private sealed class MatrixPreemptibleSandbox : CapabilitySandboxBase, IPreemptibleSandbox { }
    private sealed class MatrixSuspendableSandbox : CapabilitySandboxBase, ISuspendableSandbox { }
    private sealed class MatrixShutdownSandbox : CapabilitySandboxBase, IShutdownTeardownSandbox { }
    private sealed class MatrixPreemptibleSuspendableSandbox : CapabilitySandboxBase, IPreemptibleSandbox, ISuspendableSandbox { }
    private sealed class MatrixPreemptibleShutdownSandbox : CapabilitySandboxBase, IPreemptibleSandbox, IShutdownTeardownSandbox { }
    private sealed class MatrixSuspendableShutdownSandbox : CapabilitySandboxBase, ISuspendableSandbox, IShutdownTeardownSandbox { }
    private sealed class MatrixFullSandbox : CapabilitySandboxBase, IPreemptibleSandbox, ISuspendableSandbox, IShutdownTeardownSandbox { }

    private abstract class CapabilityProviderBase : PlainCountingProvider
    {
        private int _resumeCalls;
        private int _diskSampleCalls;
        private int _resolveBaselineCalls;
        private int _listBaselineCalls;
        private int _disposeBaselineCalls;
        private int _ensureBaselineCalls;

        public int ResumeCalls => Volatile.Read(ref _resumeCalls);
        public int DiskSampleCalls => Volatile.Read(ref _diskSampleCalls);
        public int ResolveBaselineCalls => Volatile.Read(ref _resolveBaselineCalls);
        public int ListBaselineCalls => Volatile.Read(ref _listBaselineCalls);
        public int DisposeBaselineCalls => Volatile.Read(ref _disposeBaselineCalls);
        public int EnsureBaselineCalls => Volatile.Read(ref _ensureBaselineCalls);

        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
            [];

        public Task ResumeSandboxAsync(string name, CancellationToken ct)
        {
            Interlocked.Increment(ref _resumeCalls);
            return Task.CompletedTask;
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

        public Task<string?> EnsureBaselineImageAsync(
            string profileName,
            SandboxProfileFlavor flavor,
            string? pinnedBaselineRef,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _ensureBaselineCalls);
            return Task.FromResult<string?>("baseline");
        }
    }

    private sealed class MatrixNoneProvider : CapabilityProviderBase { }
    private sealed class MatrixActiveProvider : CapabilityProviderBase, IActiveSandboxProvider { }
    private sealed class MatrixSuspendingProvider : CapabilityProviderBase, ISuspendingSandboxProvider { }
    private sealed class MatrixDiskGuardProvider : CapabilityProviderBase, IDiskGuardedSandboxProvider { }
    private sealed class MatrixBaselineResolverProvider : CapabilityProviderBase, IBaselineImageResolver { }
    private sealed class MatrixBaselineProvisionerProvider : CapabilityProviderBase, IBaselineImageProvisioner { }
    private sealed class MatrixActiveSuspendingProvider : CapabilityProviderBase, IActiveSandboxProvider, ISuspendingSandboxProvider { }
    private sealed class MatrixActiveDiskGuardProvider : CapabilityProviderBase, IActiveSandboxProvider, IDiskGuardedSandboxProvider { }
    private sealed class MatrixActiveBaselineResolverProvider : CapabilityProviderBase, IActiveSandboxProvider, IBaselineImageResolver { }
    private sealed class MatrixActiveBaselineProvisionerProvider : CapabilityProviderBase, IActiveSandboxProvider, IBaselineImageProvisioner { }
    private sealed class MatrixSuspendingDiskGuardProvider : CapabilityProviderBase, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider { }
    private sealed class MatrixSuspendingBaselineResolverProvider : CapabilityProviderBase, ISuspendingSandboxProvider, IBaselineImageResolver { }
    private sealed class MatrixSuspendingBaselineProvisionerProvider : CapabilityProviderBase, ISuspendingSandboxProvider, IBaselineImageProvisioner { }
    private sealed class MatrixDiskGuardBaselineResolverProvider : CapabilityProviderBase, IDiskGuardedSandboxProvider, IBaselineImageResolver { }
    private sealed class MatrixDiskGuardBaselineProvisionerProvider : CapabilityProviderBase, IDiskGuardedSandboxProvider, IBaselineImageProvisioner { }
    private sealed class MatrixBaselineProvider : CapabilityProviderBase, IBaselineImageResolver, IBaselineImageProvisioner { }
    private sealed class MatrixActiveSuspendingDiskGuardProvider : CapabilityProviderBase, IActiveSandboxProvider, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider { }
    private sealed class MatrixActiveSuspendingBaselineResolverProvider : CapabilityProviderBase, IActiveSandboxProvider, ISuspendingSandboxProvider, IBaselineImageResolver { }
    private sealed class MatrixActiveSuspendingBaselineProvisionerProvider : CapabilityProviderBase, IActiveSandboxProvider, ISuspendingSandboxProvider, IBaselineImageProvisioner { }
    private sealed class MatrixActiveDiskGuardBaselineResolverProvider : CapabilityProviderBase, IActiveSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver { }
    private sealed class MatrixActiveDiskGuardBaselineProvisionerProvider : CapabilityProviderBase, IActiveSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageProvisioner { }
    private sealed class MatrixActiveBaselineProvider : CapabilityProviderBase, IActiveSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner { }
    private sealed class MatrixSuspendingDiskGuardBaselineResolverProvider : CapabilityProviderBase, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver { }
    private sealed class MatrixSuspendingDiskGuardBaselineProvisionerProvider : CapabilityProviderBase, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageProvisioner { }
    private sealed class MatrixSuspendingBaselineProvider : CapabilityProviderBase, ISuspendingSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner { }
    private sealed class MatrixDiskGuardBaselineProvider : CapabilityProviderBase, IDiskGuardedSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner { }
    private sealed class MatrixActiveSuspendingDiskGuardBaselineResolverProvider : CapabilityProviderBase, IActiveSandboxProvider, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver { }
    private sealed class MatrixActiveSuspendingDiskGuardBaselineProvisionerProvider : CapabilityProviderBase, IActiveSandboxProvider, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageProvisioner { }
    private sealed class MatrixActiveSuspendingBaselineProvider : CapabilityProviderBase, IActiveSandboxProvider, ISuspendingSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner { }
    private sealed class MatrixActiveDiskGuardBaselineProvider : CapabilityProviderBase, IActiveSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner { }
    private sealed class MatrixSuspendingDiskGuardBaselineProvider : CapabilityProviderBase, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner { }
    private sealed class MatrixActiveSuspendingDiskGuardBaselineProvider : CapabilityProviderBase, IActiveSandboxProvider, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner { }

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
        public bool ThrowOnEnsureBaseline { get; init; }
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
            if (ThrowOnEnsureBaseline)
                throw new InvalidOperationException("ensure baseline failed");
            return "baseline";
        }
    }

    private sealed class CountingSandbox(
        PlainCountingProvider provider,
        string id,
        bool disposeThrows,
        bool leaveHostSandboxAfterDispose) :
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

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            provider.RecordExec(ct);
            return Task.FromResult(new SandboxExecResult(0, "stdout", "stderr"));
        }

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
        {
            provider.RecordScreenshot();
            return Task.FromResult<byte[]>([1, 2, 3]);
        }

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
        {
            provider.RecordInput();
            return Task.CompletedTask;
        }

        public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default)
        {
            provider.RecordAccessibilityAtPoint();
            return Task.FromResult<SandboxAccessibilitySnapshot?>(new SandboxAccessibilitySnapshot { Role = "button" });
        }

        public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default)
        {
            provider.RecordAccessibilityTree();
            return Task.FromResult<string?>("{}");
        }

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
                if (!leaveHostSandboxAfterDispose)
                    provider.Release();
                if (disposeThrows)
                    return ValueTask.FromException(new InvalidOperationException("dispose failed"));
            }
            return ValueTask.CompletedTask;
        }
    }
}
