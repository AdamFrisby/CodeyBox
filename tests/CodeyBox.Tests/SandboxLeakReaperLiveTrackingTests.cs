using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Regression coverage for the live-VM leak-reaper incident: sandboxes created
/// through the placement path (the provider registry's kind instance) were
/// inventoried by a SECOND provider instance behind the reloadable router —
/// same backend name, separate in-memory active-sandbox registry — so the
/// sweep reported live mid-phase sandboxes as untracked and destroyed them.
/// These tests exercise the real composition (<see cref="Program.BuildReloadableSandboxProvider"/>,
/// <see cref="Program.BuildManagedSandboxLifecycleProvider"/>,
/// <see cref="CompositeManagedSandboxProvider"/>, <see cref="SandboxLeakReaper"/>)
/// against an in-memory provider with Incus-like tracking semantics.
/// </summary>
public sealed class SandboxLeakReaperLiveTrackingTests
{
    [Fact]
    public async Task RunSweep_MidPhaseSandboxOlderThanThreshold_IsNotDisposed()
    {
        // The incident shape: a sandbox created through the placement path is
        // 90 minutes old (past the 30-minute sweep threshold) but still owned
        // by a live work phase. With a shared provider instance the lifecycle
        // inventory reports it tracked-active; the leaked-instance topology
        // reported it untracked and the reaper deleted the live VM.
        var incus = new TrackingSandboxProvider(SandboxProviderKinds.Incus);
        using var wiring = BuildWiring(incus);

        var placedProvider = wiring.Registry.EnsureKind(SandboxProviderKinds.Incus);
        incus.NextCreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(90);
        await using var sandbox = await placedProvider.CreateAsync(new SandboxSpec
        {
            ImageReference = "test-image",
            TimingWorkItemId = WorkItemId.New(),
            TimingPhase = "work",
        });

        var lifecycle = Program.BuildManagedSandboxLifecycleProvider(wiring.Services);
        var reaper = new SandboxLeakReaper(
            lifecycle,
            new NullWebhookDispatcher(),
            new SandboxLeakOptions
            {
                Enabled = true,
                CheckInterval = TimeSpan.FromHours(1),
                LeakAgeThreshold = TimeSpan.FromMinutes(30),
                AutoDispose = true,
            },
            NullLogger<SandboxLeakReaper>.Instance);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(incus.DisposedNames);
        Assert.Empty(reaper.GetLatestLeaks());
        var managed = await lifecycle.ListAllManagedAsync(CancellationToken.None);
        Assert.Contains(managed, info => info.Name == sandbox.Id && info.IsTrackedActive);
    }

    [Fact]
    public async Task WorkToAuditPhaseHandoff_OneSandboxStaysTrackedActive()
    {
        // WorkSandboxContext's reuse path keeps ONE sandbox across a
        // work -> audit phase handoff. Its tracked-active flag must survive
        // the handoff in the lifecycle view the reaper sweeps.
        var incus = new TrackingSandboxProvider(SandboxProviderKinds.Incus);
        using var wiring = BuildWiring(incus);
        var lifecycle = Program.BuildManagedSandboxLifecycleProvider(wiring.Services);

        var itemId = WorkItemId.New();
        var context = new WorkSandboxContext(
            wiring.SingletonProvider,
            new PipelineTuningSnapshot(new PipelineTuningOptions()),
            NullLogger.Instance);
        // Placement path: the pipeline acquires through the registry-resolved
        // member provider, not the singleton.
        Task<ISandbox> AcquireAsync(SandboxSpec spec, CancellationToken ct) =>
            wiring.Registry.EnsureKind(SandboxProviderKinds.Incus).CreateAsync(spec, ct);

        await using var workSandbox = await context.GetOrCreateSandboxAsync(
            new SandboxSpec
            {
                ImageReference = "test-image",
                TimingWorkItemId = itemId,
                TimingPhase = "work",
            },
            CancellationToken.None,
            AcquireAsync);

        var duringWork = await lifecycle.ListAllManagedAsync(CancellationToken.None);
        var workEntry = Assert.Single(duringWork, info => info.Name == workSandbox.Id);
        Assert.True(workEntry.IsTrackedActive);

        await using var auditSandbox = await context.GetOrCreateSandboxAsync(
            new SandboxSpec
            {
                ImageReference = "test-image",
                TimingWorkItemId = itemId,
                TimingPhase = "audit",
            },
            CancellationToken.None,
            AcquireAsync);

        Assert.Equal(1, incus.CreateCalls);
        var duringAudit = await lifecycle.ListAllManagedAsync(CancellationToken.None);
        var auditEntry = Assert.Single(duringAudit, info => info.Name == workSandbox.Id);
        Assert.True(auditEntry.IsTrackedActive);
        Assert.Equal(workSandbox.Id, auditSandbox.Id);
    }

    [Fact]
    public void DefaultLeakAgeThreshold_ExceedsWorkTimeoutCeiling()
    {
        // A work phase may legitimately run DefaultWorkTimeoutMinutes
        // (240 shipped; up to MaxMinutes configured) times the phase
        // absolute-timeout multiplier before the phase cap fires. The leak
        // threshold default must sit above that bound or mid-phase sandboxes
        // get declared abandoned.
        var shipped = new SandboxLeakOptions().LeakAgeThreshold;
        Assert.True(
            shipped > TimeSpan.FromMinutes(WorkTimeoutPolicy.DefaultMinutes),
            $"LeakAgeThreshold {shipped} must exceed DefaultWorkTimeoutMinutes {WorkTimeoutPolicy.DefaultMinutes}m");
        var maxPhase = TimeSpan.FromMinutes(
            WorkTimeoutPolicy.MaxMinutes * PipelineOptions.DefaultPhaseAbsoluteTimeoutMultiplier);
        Assert.True(
            shipped > maxPhase,
            $"LeakAgeThreshold {shipped} must exceed the maximum legitimate phase duration {maxPhase}");
    }

    [Fact]
    public async Task DisposeLeaked_RefusesWhenLivePhaseOwnsSandboxEvenIfTrackingDictionaryLostIt()
    {
        // The provider's tracked-active dictionary is allowed to be WRONG —
        // that is the bug. Before deleting, the composite must re-verify
        // against live phase/worker state, and a live binding vetoes the
        // delete even when the in-memory listing says untracked.
        var provider = new TrackingSandboxProvider("incus");
        provider.SeedUntracked("codeybox-live-vm", DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
        provider.SeedLivePhaseBinding("codeybox-live-vm", WorkItemId.New());
        var composite = new CompositeManagedSandboxProvider([provider]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => composite.DisposeLeakedAsync(
                new ManagedSandboxInfo(
                    "codeybox-live-vm",
                    DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
                    null,
                    IsTrackedActive: false,
                    LifecycleProviderId: "incus"),
                CancellationToken.None));

        Assert.Contains("live work phase", ex.Message);
        Assert.Empty(provider.DisposedNames);
    }

    [Fact]
    public async Task DisposeLeaked_StillReclaimsGenuineOrphan()
    {
        // Positive control: the live-state veto must not block reclamation of
        // a sandbox nothing owns — no tracked-active report, no live phase
        // binding anywhere.
        var provider = new TrackingSandboxProvider("incus");
        provider.SeedUntracked("codeybox-orphan", DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
        var composite = new CompositeManagedSandboxProvider([provider]);

        await composite.ListAllManagedAsync(CancellationToken.None);
        await composite.DisposeLeakedAsync(
            new ManagedSandboxInfo(
                "codeybox-orphan",
                DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
                null,
                IsTrackedActive: false,
                LifecycleProviderId: "incus"),
            CancellationToken.None);

        Assert.Contains("codeybox-orphan", provider.DisposedNames);
        Assert.DoesNotContain(
            "codeybox-orphan",
            (await composite.ListAllManagedAsync(CancellationToken.None)).Select(info => info.Name));
    }

    [Fact]
    public async Task AdmissionWrapper_SnapshotActiveSandboxes_UnionCoversInnerTrackedSandboxes()
    {
        // The outer singleton wrapper's own tracker only sees sandboxes
        // admitted through it. Shutdown teardown and disposal re-verification
        // need the union with the inner provider's live set, or
        // placement-admitted sandboxes stay invisible to live-state checks.
        var incus = new TrackingSandboxProvider(SandboxProviderKinds.Incus);
        using var wiring = BuildWiring(incus);
        var inner = wiring.Registry.EnsureKind(SandboxProviderKinds.Incus);
        await using var innerCreated = await inner.CreateAsync(new SandboxSpec
        {
            ImageReference = "test-image",
            TimingWorkItemId = WorkItemId.New(),
            TimingPhase = "work",
        });

        var wrapped = SandboxAdmissionControlledProvider.Wrap(
            wiring.SingletonProvider,
            maxConcurrentSandboxes: 4,
            NullLogger<SandboxAdmissionControlledProvider>.Instance);

        var snapshot = Assert.IsAssignableFrom<IActiveSandboxProvider>(wrapped).SnapshotActiveSandboxes();
        Assert.Contains(snapshot, entry => entry.Sandbox.Id == innerCreated.Id);
    }

    private sealed record Wiring(
        ServiceProvider Services,
        SandboxProviderRegistry Registry,
        ISandboxProvider SingletonProvider) : IDisposable
    {
        public void Dispose() => Services.Dispose();
    }

    private static Wiring BuildWiring(TrackingSandboxProvider incus)
    {
        var multipass = new TrackingSandboxProvider(SandboxProviderKinds.Multipass);
        var registry = new SandboxProviderRegistry(
            kind => kind switch
            {
                "incus" => incus,
                "multipass" => multipass,
                _ => throw new InvalidOperationException($"Unexpected provider kind '{kind}'"),
            });
        var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton<IOptionsMonitor<CodeyBoxOptions>>(
                new StaticOptionsMonitor<CodeyBoxOptions>(new CodeyBoxOptions { SandboxProvider = SandboxProviderKinds.Incus }))
            .AddSingleton<ISandboxProviderRegistry>(registry)
            .AddSingleton<ISandboxProvider>(sp =>
                Program.BuildReloadableSandboxProvider(sp, NullLoggerFactory.Instance))
            .AddSingleton(new SandboxClassesSnapshot([]))
            .AddSingleton<IE2eExecutionPool>(new StubE2eExecutionPool())
            .BuildServiceProvider();
        return new Wiring(
            services,
            registry,
            services.GetRequiredService<ISandboxProvider>());
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class StubE2eExecutionPool : IE2eExecutionPool
    {
        public string Name => "stub-e2e";
        public int MaxConcurrent => 0;
        public int InFlight => 0;
        public Task<IE2eExecutionSlot> LeaseAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("Test stub pool issues no leases");
    }

    /// <summary>
    /// In-memory provider with Incus-like lifecycle semantics: creations mark
    /// the instance's live registry, handle disposal releases it, inventory
    /// reports tracked-active only for live-registry entries, and disposal of
    /// a tracked name is refused at the sink.
    /// </summary>
    private sealed class TrackingSandboxProvider
        : ISandboxProvider, IActiveSandboxProvider, IActiveSandboxProgressProvider,
          IDiskGuardedSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner,
          IResourceMetricsCapturingProvider
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _backend = new(StringComparer.Ordinal);
        private readonly HashSet<string> _liveRegistry = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WorkItemId> _livePhaseOwners = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TrackingSandbox> _liveHandles = new(StringComparer.Ordinal);
        private readonly List<string> _disposedNames = [];

        private sealed record Entry(ManagedSandboxInfo Info, TrackingSandbox? Handle);

        public TrackingSandboxProvider(string name) => Name = name;

        public string Name { get; }
        public int CreateCalls { get; private set; }
        public DateTimeOffset? NextCreatedAt { get; set; }

        public IReadOnlyList<string> DisposedNames
        {
            get { lock (_gate) return _disposedNames.ToList(); }
        }

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            var name = $"codeybox-{Guid.NewGuid():N}"[..20];
            var sandbox = new TrackingSandbox(name, Name, ReleaseTracking);
            lock (_gate)
            {
                CreateCalls++;
                _liveRegistry.Add(name);
                _liveHandles[name] = sandbox;
                if (spec.TimingWorkItemId is { } workItemId)
                    _livePhaseOwners[name] = workItemId;
                _backend[name] = new Entry(
                    new ManagedSandboxInfo(
                        name,
                        NextCreatedAt ?? DateTimeOffset.UtcNow,
                        DiskBytes: null,
                        IsTrackedActive: false),
                    sandbox);
                NextCreatedAt = null;
            }
            return Task.FromResult<ISandbox>(sandbox);
        }

        /// <summary>A VM visible on the backend with no live registry entry —
        /// e.g. created by a crashed process.</summary>
        public void SeedUntracked(string name, DateTimeOffset createdAt)
        {
            lock (_gate)
            {
                _backend[name] = new Entry(
                    new ManagedSandboxInfo(name, createdAt, null, IsTrackedActive: false),
                    Handle: null);
            }
        }

        /// <summary>A live work-phase binding for a sandbox the live registry
        /// no longer reports — the registry-lost-an-entry failure mode.</summary>
        public void SeedLivePhaseBinding(string name, WorkItemId workItemId)
        {
            lock (_gate)
            {
                _livePhaseOwners[name] = workItemId;
                if (_backend.TryGetValue(name, out var entry) && entry.Handle is not null)
                    _liveHandles[name] = entry.Handle;
            }
        }

        private void ReleaseTracking(string name)
        {
            lock (_gate)
            {
                _liveRegistry.Remove(name);
                _livePhaseOwners.Remove(name);
                _liveHandles.Remove(name);
                _backend.Remove(name);
            }
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(
                    _backend.Values
                        .Select(e => e.Info with { IsTrackedActive = _liveRegistry.Contains(e.Info.Name) })
                        .ToList());
            }
        }

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_liveRegistry.Contains(name))
                    throw new InvalidOperationException(
                        $"Refusing to dispose managed sandbox '{name}' because it is still tracked as active.");
                _backend.Remove(name);
                _disposedNames.Add(name);
            }
            return Task.CompletedTask;
        }

        public Task DisposeLeakedAsync(ManagedSandboxInfo sandbox, CancellationToken ct)
        {
            if (sandbox.LifecycleProviderId is not null
                && !string.Equals(sandbox.LifecycleProviderId, Name, StringComparison.Ordinal))
                throw new NotSupportedException("Sandbox lifecycle scope does not match this provider.");
            return DisposeLeakedAsync(sandbox.Name, ct);
        }

        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes()
        {
            lock (_gate)
            {
                return _livePhaseOwners
                    .Where(kvp => _liveHandles.ContainsKey(kvp.Key))
                    .Select(kvp => (kvp.Value, (IShutdownTeardownSandbox)_liveHandles[kvp.Key]))
                    .ToList();
            }
        }

        public IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress()
        {
            lock (_gate)
            {
                return _livePhaseOwners
                    .Select(kvp => new ActiveSandboxProgress(kvp.Value, kvp.Key))
                    .ToList();
            }
        }

        public IReadOnlyList<DiskGuardSample> SampleDiskGuardState() => [];
        public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor) => null;
        public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BaselineImageInfo>>([]);
        public Task DisposeBaselineImageAsync(string name, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> EnsureBaselineImageAsync(
            string profileName, SandboxProfileFlavor flavor, string? pinnedBaselineRef, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public bool CapturesResourceMetrics => false;
    }

    private sealed class TrackingSandbox(string id, string providerId, Action<string> onDisposed)
        : IProviderOwnedSandbox, IShutdownTeardownSandbox
    {
        private bool _disposed;
        public string Id => id;
        public string ProviderId => providerId;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, "", ""));

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                onDisposed(Id);
            }
            return ValueTask.CompletedTask;
        }
    }
}
