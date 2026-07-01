using System.Collections.Concurrent;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

internal interface ISandboxAdmissionSnapshot
{
    int CurrentAdmittedSandboxes { get; }
    int MaxConcurrentSandboxes { get; }
}

/// <summary>
/// Decorates an <see cref="ISandboxProvider"/> with a process-wide live-sandbox
/// admission gate. The token is acquired before the inner provider starts
/// provisioning and is released exactly once when the returned sandbox handle is
/// disposed, so worker, audit, merge, smoke, and verifier call sites all share
/// the same VM budget without each call site knowing about the policy.
/// </summary>
public class SandboxAdmissionControlledProvider : ISandboxProvider, ISandboxAdmissionSnapshot, IActiveSandboxProgressProvider, IResourceMetricsCapturingProvider
{
    private readonly ISandboxProvider _inner;
    private readonly SandboxAdmissionGate _gate;
    private readonly ILogger _log;
    private readonly ActiveSandboxTracker? _active;
    private readonly NamedAdmissionTracker? _resumeAdmissions;
    private readonly NamedAdmissionTracker _disposedSandboxAdmissions = new();
    private readonly NamedAdmissionTracker _disposedBaselineAdmissions = new();
    private readonly ConcurrentDictionary<SandboxAdmissionIdentity, AdmissionControlledSandbox> _preservedLiveSandboxes = new();
    private readonly ISuspendingSandboxProvider? _suspendingProvider;
    private readonly IDiskGuardedSandboxProvider? _diskGuardedProvider;
    private readonly IBaselineImageResolver? _baselineResolver;
    private readonly IBaselineImageProvisioner? _baselineProvisioner;
    private readonly IActiveSandboxProgressProvider? _progressProvider;
    private readonly ISandboxHostPoolSnapshot? _hostPoolSnapshot;

    private SandboxAdmissionControlledProvider(
        ISandboxProvider inner,
        SandboxAdmissionGate gate,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(log);

        _inner = inner;
        _gate = gate;
        _log = log;
        _active = inner is IActiveSandboxProvider ? new ActiveSandboxTracker() : null;
        _suspendingProvider = inner as ISuspendingSandboxProvider;
        _resumeAdmissions = _suspendingProvider is null ? null : new NamedAdmissionTracker();
        _diskGuardedProvider = inner as IDiskGuardedSandboxProvider;
        _baselineResolver = inner as IBaselineImageResolver;
        _baselineProvisioner = inner as IBaselineImageProvisioner;
        _progressProvider = inner as IActiveSandboxProgressProvider;
        _hostPoolSnapshot = inner as ISandboxHostPoolSnapshot;
    }

    public static ISandboxProvider Wrap(ISandboxProvider inner, int maxConcurrentSandboxes, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(log);

        var gate = new SandboxAdmissionGate(maxConcurrentSandboxes);
        var capabilities = ProviderCapabilities.None;
        if (inner is IActiveSandboxProvider) capabilities |= ProviderCapabilities.Active;
        if (inner is ISuspendingSandboxProvider) capabilities |= ProviderCapabilities.Suspending;
        if (inner is IDiskGuardedSandboxProvider) capabilities |= ProviderCapabilities.DiskGuard;
        if (inner is IBaselineImageResolver) capabilities |= ProviderCapabilities.BaselineResolver;
        if (inner is IBaselineImageProvisioner) capabilities |= ProviderCapabilities.BaselineProvisioner;
        if (inner is ISandboxHostPoolSnapshot) capabilities |= ProviderCapabilities.HostPool;

        var exposesHostPool = capabilities.HasFlag(ProviderCapabilities.HostPool);
        var providerCapabilities = capabilities & ~ProviderCapabilities.HostPool;
        if (exposesHostPool)
        {
            return providerCapabilities switch
            {
                ProviderCapabilities.None => new HostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active => new ActiveHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Suspending => new SuspendingHostPoolProvider(inner, gate, log),
                ProviderCapabilities.DiskGuard => new DiskGuardHostPoolProvider(inner, gate, log),
                ProviderCapabilities.BaselineResolver => new BaselineResolverHostPoolProvider(inner, gate, log),
                ProviderCapabilities.BaselineProvisioner => new BaselineProvisionerHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active | ProviderCapabilities.Suspending => new ActiveSuspendingHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active | ProviderCapabilities.DiskGuard => new ActiveDiskGuardHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active | ProviderCapabilities.BaselineResolver => new ActiveBaselineResolverHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active | ProviderCapabilities.BaselineProvisioner => new ActiveBaselineProvisionerHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard => new SuspendingDiskGuardHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Suspending | ProviderCapabilities.BaselineResolver => new SuspendingBaselineResolverHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Suspending | ProviderCapabilities.BaselineProvisioner => new SuspendingBaselineProvisionerHostPoolProvider(inner, gate, log),
                ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver => new DiskGuardBaselineResolverHostPoolProvider(inner, gate, log),
                ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineProvisioner => new DiskGuardBaselineProvisionerHostPoolProvider(inner, gate, log),
                ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new BaselineHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active | ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard => new ActiveSuspendingDiskGuardHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active | ProviderCapabilities.Suspending | ProviderCapabilities.BaselineResolver => new ActiveSuspendingBaselineResolverHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active | ProviderCapabilities.Suspending | ProviderCapabilities.BaselineProvisioner => new ActiveSuspendingBaselineProvisionerHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver => new ActiveDiskGuardBaselineResolverHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineProvisioner => new ActiveDiskGuardBaselineProvisionerHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active | ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new ActiveBaselineHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver => new SuspendingDiskGuardBaselineResolverHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineProvisioner => new SuspendingDiskGuardBaselineProvisionerHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Suspending | ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new SuspendingBaselineHostPoolProvider(inner, gate, log),
                ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new DiskGuardBaselineHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active | ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver => new ActiveSuspendingDiskGuardBaselineResolverHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active | ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineProvisioner => new ActiveSuspendingDiskGuardBaselineProvisionerHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active | ProviderCapabilities.Suspending | ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new ActiveSuspendingBaselineHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new ActiveDiskGuardBaselineHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new SuspendingDiskGuardBaselineHostPoolProvider(inner, gate, log),
                ProviderCapabilities.Active | ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new ActiveSuspendingDiskGuardBaselineHostPoolProvider(inner, gate, log),
                _ => throw new InvalidOperationException($"Unhandled sandbox provider capability set: {capabilities}"),
            };
        }

        return providerCapabilities switch
        {
            ProviderCapabilities.None => new SandboxAdmissionControlledProvider(inner, gate, log),
            ProviderCapabilities.Active => new ActiveProvider(inner, gate, log),
            ProviderCapabilities.Suspending => new SuspendingProvider(inner, gate, log),
            ProviderCapabilities.DiskGuard => new DiskGuardProvider(inner, gate, log),
            ProviderCapabilities.BaselineResolver => new BaselineResolverProvider(inner, gate, log),
            ProviderCapabilities.BaselineProvisioner => new BaselineProvisionerProvider(inner, gate, log),
            ProviderCapabilities.Active | ProviderCapabilities.Suspending => new ActiveSuspendingProvider(inner, gate, log),
            ProviderCapabilities.Active | ProviderCapabilities.DiskGuard => new ActiveDiskGuardProvider(inner, gate, log),
            ProviderCapabilities.Active | ProviderCapabilities.BaselineResolver => new ActiveBaselineResolverProvider(inner, gate, log),
            ProviderCapabilities.Active | ProviderCapabilities.BaselineProvisioner => new ActiveBaselineProvisionerProvider(inner, gate, log),
            ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard => new SuspendingDiskGuardProvider(inner, gate, log),
            ProviderCapabilities.Suspending | ProviderCapabilities.BaselineResolver => new SuspendingBaselineResolverProvider(inner, gate, log),
            ProviderCapabilities.Suspending | ProviderCapabilities.BaselineProvisioner => new SuspendingBaselineProvisionerProvider(inner, gate, log),
            ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver => new DiskGuardBaselineResolverProvider(inner, gate, log),
            ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineProvisioner => new DiskGuardBaselineProvisionerProvider(inner, gate, log),
            ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new BaselineProvider(inner, gate, log),
            ProviderCapabilities.Active | ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard => new ActiveSuspendingDiskGuardProvider(inner, gate, log),
            ProviderCapabilities.Active | ProviderCapabilities.Suspending | ProviderCapabilities.BaselineResolver => new ActiveSuspendingBaselineResolverProvider(inner, gate, log),
            ProviderCapabilities.Active | ProviderCapabilities.Suspending | ProviderCapabilities.BaselineProvisioner => new ActiveSuspendingBaselineProvisionerProvider(inner, gate, log),
            ProviderCapabilities.Active | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver => new ActiveDiskGuardBaselineResolverProvider(inner, gate, log),
            ProviderCapabilities.Active | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineProvisioner => new ActiveDiskGuardBaselineProvisionerProvider(inner, gate, log),
            ProviderCapabilities.Active | ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new ActiveBaselineProvider(inner, gate, log),
            ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver => new SuspendingDiskGuardBaselineResolverProvider(inner, gate, log),
            ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineProvisioner => new SuspendingDiskGuardBaselineProvisionerProvider(inner, gate, log),
            ProviderCapabilities.Suspending | ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new SuspendingBaselineProvider(inner, gate, log),
            ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new DiskGuardBaselineProvider(inner, gate, log),
            ProviderCapabilities.Active | ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver => new ActiveSuspendingDiskGuardBaselineResolverProvider(inner, gate, log),
            ProviderCapabilities.Active | ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineProvisioner => new ActiveSuspendingDiskGuardBaselineProvisionerProvider(inner, gate, log),
            ProviderCapabilities.Active | ProviderCapabilities.Suspending | ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new ActiveSuspendingBaselineProvider(inner, gate, log),
            ProviderCapabilities.Active | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new ActiveDiskGuardBaselineProvider(inner, gate, log),
            ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new SuspendingDiskGuardBaselineProvider(inner, gate, log),
            ProviderCapabilities.Active | ProviderCapabilities.Suspending | ProviderCapabilities.DiskGuard | ProviderCapabilities.BaselineResolver | ProviderCapabilities.BaselineProvisioner => new ActiveSuspendingDiskGuardBaselineProvider(inner, gate, log),
            _ => throw new InvalidOperationException($"Unhandled sandbox provider capability set: {capabilities}"),
        };
    }

    public int CurrentAdmittedSandboxes => _gate.CurrentAdmitted;

    public int MaxConcurrentSandboxes => _gate.MaxConcurrent;

    // Forward the wrapped provider's resource-metrics capture capability so
    // WorkSandboxContext (which only ever sees this decorator) can gate its
    // per-phase VM isolation on the live toggle.
    public bool CapturesResourceMetrics =>
        _inner is IResourceMetricsCapturingProvider capturing && capturing.CapturesResourceMetrics;

    public string Name => _inner.Name;
    public SandboxAgentOutputTransportKind AgentOutputTransportKind => _inner.AgentOutputTransportKind;
    public SandboxBatchLaunchMode BatchLaunchMode => _inner.BatchLaunchMode;

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        var lease = await _gate.AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            var sandbox = await _inner.CreateAsync(spec, ct).ConfigureAwait(false);
            var controlled = WrapSandbox(sandbox, lease);
            _active?.Track(spec, controlled);
            return controlled;
        }
        catch (SandboxProvisioningDeferredException ex)
            when (!string.IsNullOrWhiteSpace(ex.RetainedSandboxName))
        {
            _log.LogWarning(
                ex,
                "Retaining sandbox admission token after create failure because sandbox {SandboxName} may still exist",
                ex.RetainedSandboxName);
            RetainDeferredProvisioningAdmission(ex, lease);
            throw;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var managed = await _inner.ListAllManagedAsync(ct).ConfigureAwait(false);
        var managedIds = managed.Select(SandboxAdmissionIdentity.FromManaged).ToArray();
        var inventory = SandboxInventoryScope.From(managed);
        _resumeAdmissions?.ReleaseMissing(managedIds, inventory.CanTreatMissingAsAbsent);
        _disposedSandboxAdmissions.ReleaseMissing(managedIds, inventory.CanTreatMissingAsAbsent);
        ReleaseMissingPreservedLiveSandboxes(managedIds, inventory);
        return managed;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        await _inner.DisposeLeakedAsync(name, ct).ConfigureAwait(false);
        ReleasePreservedLiveSandboxesByName(name);
        _resumeAdmissions?.ReleaseName(name);
        _disposedSandboxAdmissions.ReleaseName(name);
    }

    public async Task DisposeLeakedAsync(ManagedSandboxInfo sandbox, CancellationToken ct)
    {
        await _inner.DisposeLeakedAsync(sandbox, ct).ConfigureAwait(false);
        var identity = SandboxAdmissionIdentity.FromManaged(sandbox);
        _preservedLiveSandboxes.TryRemove(identity, out _);
        _resumeAdmissions?.Release(identity);
        _disposedSandboxAdmissions.Release(identity);
    }

    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
        (_active ?? throw new NotSupportedException("The wrapped sandbox provider does not expose active sandboxes.")).Snapshot();

    public IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress() =>
        _progressProvider?.SnapshotActiveSandboxProgress() ?? [];

    public IReadOnlyList<SandboxHostPoolEntry> SnapshotHostPool() =>
        _hostPoolSnapshot?.SnapshotHostPool() ?? [];

    public async Task ResumeSandboxAsync(string name, CancellationToken ct)
    {
        var suspendingProvider = _suspendingProvider
            ?? throw new NotSupportedException("The wrapped sandbox provider does not support suspend/resume.");
        var resumeAdmissions = _resumeAdmissions
            ?? throw new NotSupportedException("The wrapped sandbox provider does not track resume admission.");

        var identity = SandboxAdmissionIdentity.FromName(name);
        resumeAdmissions.Begin(identity);
        SandboxAdmissionLease? lease = null;
        var retained = false;
        try
        {
            lease = await _gate.AcquireAsync(ct).ConfigureAwait(false);
            await suspendingProvider.ResumeSandboxAsync(name, ct).ConfigureAwait(false);
            if (TryAdoptResumeAdmission(identity, lease))
                resumeAdmissions.CancelPending(identity);
            else
                resumeAdmissions.Retain(identity, lease);
            retained = true;
        }
        finally
        {
            if (!retained)
            {
                lease?.Dispose();
                resumeAdmissions.CancelPending(identity);
            }
        }
    }

    public Task<int?> WaitForAdoptedAgentCompletionAsync(
        string vmName,
        string agentLogPath,
        Action<string>? logSink,
        TimeSpan? deadline,
        CancellationToken ct)
    {
        var suspendingProvider = _suspendingProvider
            ?? throw new NotSupportedException("The wrapped sandbox provider does not support suspend/resume.");
        return suspendingProvider.WaitForAdoptedAgentCompletionAsync(vmName, agentLogPath, logSink, deadline, ct);
    }

    public Task<bool> PushSuspendedVmCheckpointRefAsync(
        string vmName,
        string workingDir,
        string refName,
        string commitMessage,
        CancellationToken ct)
    {
        var suspendingProvider = _suspendingProvider
            ?? throw new NotSupportedException("The wrapped sandbox provider does not support suspend/resume.");
        return suspendingProvider.PushSuspendedVmCheckpointRefAsync(vmName, workingDir, refName, commitMessage, ct);
    }

    public Task<IReadOnlyList<string>> ReconcileStuckSandboxesAsync(
        IReadOnlySet<string> liveSuspendedNames,
        CancellationToken ct)
    {
        var suspendingProvider = _suspendingProvider
            ?? throw new NotSupportedException("The wrapped sandbox provider does not support suspend/resume.");
        return suspendingProvider.ReconcileStuckSandboxesAsync(liveSuspendedNames, ct);
    }

    public IReadOnlyList<DiskGuardSample> SampleDiskGuardState()
    {
        var diskGuardedProvider = _diskGuardedProvider
            ?? throw new NotSupportedException("The wrapped sandbox provider does not expose disk guard state.");
        return diskGuardedProvider.SampleDiskGuardState();
    }

    public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
    {
        var baselineResolver = _baselineResolver
            ?? throw new NotSupportedException("The wrapped sandbox provider does not resolve baseline images.");
        return baselineResolver.ResolveBaselineRef(profileName, flavor);
    }

    public async Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct)
    {
        var baselineResolver = _baselineResolver
            ?? throw new NotSupportedException("The wrapped sandbox provider does not resolve baseline images.");
        var baselines = await baselineResolver.ListBaselineImagesAsync(ct).ConfigureAwait(false);
        _disposedBaselineAdmissions.ReleaseMissing(baselines.Select(static info => SandboxAdmissionIdentity.FromName(info.Name)));
        return baselines;
    }

    public async Task DisposeBaselineImageAsync(string name, CancellationToken ct)
    {
        var baselineResolver = _baselineResolver
            ?? throw new NotSupportedException("The wrapped sandbox provider does not resolve baseline images.");
        await baselineResolver.DisposeBaselineImageAsync(name, ct).ConfigureAwait(false);
        _disposedBaselineAdmissions.Release(SandboxAdmissionIdentity.FromName(name));
    }

    public async Task<string?> EnsureBaselineImageAsync(
        string profileName,
        SandboxProfileFlavor flavor,
        string? pinnedBaselineRef,
        CancellationToken ct)
    {
        var baselineProvisioner = _baselineProvisioner
            ?? throw new NotSupportedException("The wrapped sandbox provider does not provision baseline images.");
        var lease = await _gate.AcquireAsync(ct).ConfigureAwait(false);
        var releaseLease = true;
        try
        {
            var result = await baselineProvisioner.EnsureBaselineImageAsync(
                profileName,
                flavor,
                pinnedBaselineRef,
                ct).ConfigureAwait(false);
            return result;
        }
        catch (SandboxProvisioningDeferredException ex)
            when (!string.IsNullOrWhiteSpace(ex.RetainedSandboxName))
        {
            _log.LogWarning(
                ex,
                "Retaining sandbox admission token after baseline bake failure because baseline {BaselineName} may still exist",
                ex.RetainedSandboxName);
            _disposedBaselineAdmissions.Retain(SandboxAdmissionIdentity.FromException(ex), lease);
            releaseLease = false;
            throw;
        }
        finally
        {
            if (releaseLease)
                lease.Dispose();
        }
    }

    private ISandbox WrapSandbox(ISandbox sandbox, SandboxAdmissionLease lease)
    {
        var capabilities = SandboxCapabilities.None;
        var preemptible = sandbox as IPreemptibleSandbox;
        var suspendable = sandbox as ISuspendableSandbox;
        var shutdown = sandbox as IShutdownTeardownSandbox;
        if (preemptible is not null) capabilities |= SandboxCapabilities.Preemptible;
        if (suspendable is not null) capabilities |= SandboxCapabilities.Suspendable;
        if (shutdown is not null) capabilities |= SandboxCapabilities.Shutdown;

        return capabilities switch
        {
            SandboxCapabilities.None => new AdmissionControlledSandbox(sandbox, lease, OnSandboxDisposedAsync, OnSandboxPreserved, _log),
            SandboxCapabilities.Preemptible => new AdmissionControlledPreemptibleSandbox(sandbox, preemptible!, lease, OnSandboxDisposedAsync, OnSandboxPreserved, _log),
            SandboxCapabilities.Suspendable => new AdmissionControlledSuspendableSandbox(sandbox, suspendable!, lease, OnSandboxDisposedAsync, OnSandboxPreserved, _log),
            SandboxCapabilities.Shutdown => new AdmissionControlledShutdownSandbox(sandbox, shutdown!, lease, OnSandboxDisposedAsync, OnSandboxPreserved, _log),
            SandboxCapabilities.Preemptible | SandboxCapabilities.Suspendable => new AdmissionControlledPreemptibleSuspendableSandbox(sandbox, preemptible!, suspendable!, lease, OnSandboxDisposedAsync, OnSandboxPreserved, _log),
            SandboxCapabilities.Preemptible | SandboxCapabilities.Shutdown => new AdmissionControlledPreemptibleShutdownSandbox(sandbox, preemptible!, shutdown!, lease, OnSandboxDisposedAsync, OnSandboxPreserved, _log),
            SandboxCapabilities.Suspendable | SandboxCapabilities.Shutdown => new AdmissionControlledSuspendableShutdownSandbox(sandbox, suspendable!, shutdown!, lease, OnSandboxDisposedAsync, OnSandboxPreserved, _log),
            SandboxCapabilities.Preemptible | SandboxCapabilities.Suspendable | SandboxCapabilities.Shutdown => new AdmissionControlledFullSandbox(sandbox, preemptible!, suspendable!, shutdown!, lease, OnSandboxDisposedAsync, OnSandboxPreserved, _log),
            _ => throw new InvalidOperationException($"Unhandled sandbox capability set: {capabilities}"),
        };
    }

    private async ValueTask OnSandboxDisposedAsync(
        AdmissionControlledSandbox sandbox,
        SandboxAdmissionLease lease,
        bool innerDisposeSucceeded,
        bool admissionHeld)
    {
        _active?.Remove(sandbox);
        var identity = SandboxAdmissionIdentity.FromSandbox(sandbox);
        _preservedLiveSandboxes.TryRemove(identity, out _);
        _resumeAdmissions?.Release(identity);
        if (!admissionHeld)
            return;

        var releaseAdmission = false;
        if (innerDisposeSucceeded)
            releaseAdmission = !await IsManagedSandboxStillPresentAsync(identity).ConfigureAwait(false);

        if (releaseAdmission)
            lease.Dispose();
        else
        {
            if (innerDisposeSucceeded)
            {
                _log.LogWarning(
                    "Retaining sandbox admission token after dispose for sandbox {SandboxId} because provider inventory still lists it or could not prove it absent",
                    sandbox.Id);
            }
            _disposedSandboxAdmissions.Retain(identity, lease);
        }
    }

    private void OnSandboxPreserved(AdmissionControlledSandbox sandbox)
    {
        var identity = SandboxAdmissionIdentity.FromSandbox(sandbox);
        _preservedLiveSandboxes[identity] = sandbox;
        _resumeAdmissions?.Release(identity);
    }

    private void ReleasePreservedLiveSandboxesByName(string name)
    {
        foreach (var key in _preservedLiveSandboxes.Keys)
        {
            if (string.Equals(key.Name, name, StringComparison.Ordinal))
                _preservedLiveSandboxes.TryRemove(key, out _);
        }
    }

    private void ReleaseMissingPreservedLiveSandboxes(
        IReadOnlyCollection<SandboxAdmissionIdentity> managedIds,
        SandboxInventoryScope inventory)
    {
        if (_preservedLiveSandboxes.IsEmpty)
            return;

        var present = managedIds.ToHashSet();
        foreach (var identity in _preservedLiveSandboxes.Keys)
        {
            if (!present.Contains(identity) && inventory.CanTreatMissingAsAbsent(identity))
                _preservedLiveSandboxes.TryRemove(identity, out _);
        }
    }

    private bool TryAdoptResumeAdmission(SandboxAdmissionIdentity identity, SandboxAdmissionLease lease)
    {
        if (!_preservedLiveSandboxes.TryRemove(identity, out var sandbox))
            return false;

        return sandbox.TryAdoptAdmissionLease(lease);
    }

    private async Task<bool> IsManagedSandboxStillPresentAsync(SandboxAdmissionIdentity identity)
    {
        try
        {
            var managed = await _inner.ListAllManagedAsync(CancellationToken.None).ConfigureAwait(false);
            if (managed.Any(info => SandboxAdmissionIdentity.FromManaged(info) == identity))
                return true;
            var inventory = SandboxInventoryScope.From(managed);
            return !inventory.CanTreatMissingAsAbsent(identity);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Could not verify whether sandbox {SandboxId} still exists; retaining sandbox admission token",
                identity.Name);
            return true;
        }
    }

    private void RetainDeferredProvisioningAdmission(
        SandboxProvisioningDeferredException ex,
        SandboxAdmissionLease lease)
    {
        if (IsRetainedBaselineProvisioning(ex) && _baselineResolver is not null)
        {
            _disposedBaselineAdmissions.Retain(SandboxAdmissionIdentity.FromException(ex), lease);
            return;
        }

        _disposedSandboxAdmissions.Retain(SandboxAdmissionIdentity.FromException(ex), lease);
    }

    private static bool IsRetainedBaselineProvisioning(SandboxProvisioningDeferredException ex) =>
        ex.Operation.StartsWith("baseline-", StringComparison.Ordinal);

    private readonly record struct SandboxAdmissionIdentity(string Name, string? HostId)
    {
        public static SandboxAdmissionIdentity FromName(string name) =>
            new(name, HostId: null);

        public static SandboxAdmissionIdentity FromManaged(ManagedSandboxInfo info) =>
            new(info.Name, NormalizeHostId(info.HostId));

        public static SandboxAdmissionIdentity FromException(SandboxProvisioningDeferredException ex) =>
            new(ex.RetainedSandboxName!, NormalizeHostId(ex.RetainedSandboxHostId));

        public static SandboxAdmissionIdentity FromSandbox(ISandbox sandbox)
        {
            var hostId = sandbox is IHostQualifiedSandbox hostQualified
                ? hostQualified.HostId
                : null;
            return new SandboxAdmissionIdentity(sandbox.Id, NormalizeHostId(hostId));
        }

        private static string? NormalizeHostId(string? hostId) =>
            string.IsNullOrWhiteSpace(hostId) ? null : hostId;
    }

    private readonly record struct SandboxInventoryScope(
        bool IsComplete,
        IReadOnlySet<string> InventoriedHostIds)
    {
        public static SandboxInventoryScope From(IReadOnlyList<ManagedSandboxInfo> managed)
        {
            if (managed is IManagedSandboxInventoryResult inventory)
                return new SandboxInventoryScope(inventory.IsComplete, inventory.InventoriedHostIds);

            return new SandboxInventoryScope(
                IsComplete: true,
                InventoriedHostIds: new HashSet<string>(StringComparer.Ordinal));
        }

        public bool CanTreatMissingAsAbsent(SandboxAdmissionIdentity identity) =>
            IsComplete
            || (identity.HostId is { } hostId && InventoriedHostIds.Contains(hostId));
    }

    [Flags]
    private enum ProviderCapabilities
    {
        None = 0,
        Active = 1,
        Suspending = 2,
        DiskGuard = 4,
        BaselineResolver = 8,
        BaselineProvisioner = 16,
        HostPool = 32,
    }

    [Flags]
    private enum SandboxCapabilities
    {
        None = 0,
        Preemptible = 1,
        Suspendable = 2,
        Shutdown = 4,
    }

    private sealed class ActiveProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider
    { }

    private sealed class HostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISandboxHostPoolSnapshot
    { }

    private sealed class SuspendingHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, ISandboxHostPoolSnapshot
    { }

    private sealed class DiskGuardHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IDiskGuardedSandboxProvider, ISandboxHostPoolSnapshot
    { }

    private sealed class BaselineResolverHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IBaselineImageResolver, ISandboxHostPoolSnapshot
    { }

    private sealed class BaselineProvisionerHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveSuspendingHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider, ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveDiskGuardHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, IDiskGuardedSandboxProvider, ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveBaselineResolverHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, IBaselineImageResolver, ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveBaselineProvisionerHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class SuspendingDiskGuardHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, ISandboxHostPoolSnapshot
    { }

    private sealed class SuspendingBaselineResolverHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, IBaselineImageResolver, ISandboxHostPoolSnapshot
    { }

    private sealed class SuspendingBaselineProvisionerHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class DiskGuardBaselineResolverHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IDiskGuardedSandboxProvider, IBaselineImageResolver, ISandboxHostPoolSnapshot
    { }

    private sealed class DiskGuardBaselineProvisionerHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IDiskGuardedSandboxProvider, IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class BaselineHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IBaselineImageResolver, IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveSuspendingDiskGuardHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveSuspendingBaselineResolverHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider, IBaselineImageResolver, ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveSuspendingBaselineProvisionerHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider, IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveDiskGuardBaselineResolverHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver, ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveDiskGuardBaselineProvisionerHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveBaselineHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class SuspendingDiskGuardBaselineResolverHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver, ISandboxHostPoolSnapshot
    { }

    private sealed class SuspendingDiskGuardBaselineProvisionerHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class SuspendingBaselineHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class DiskGuardBaselineHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IDiskGuardedSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveSuspendingDiskGuardBaselineResolverHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver, ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveSuspendingDiskGuardBaselineProvisionerHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveSuspendingBaselineHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveDiskGuardBaselineHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class SuspendingDiskGuardBaselineHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class ActiveSuspendingDiskGuardBaselineHostPoolProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner, ISandboxHostPoolSnapshot
    { }

    private sealed class SuspendingProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider
    { }

    private sealed class DiskGuardProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IDiskGuardedSandboxProvider
    { }

    private sealed class BaselineResolverProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IBaselineImageResolver
    { }

    private sealed class BaselineProvisionerProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IBaselineImageProvisioner
    { }

    private sealed class ActiveSuspendingProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider
    { }

    private sealed class ActiveDiskGuardProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, IDiskGuardedSandboxProvider
    { }

    private sealed class ActiveBaselineResolverProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, IBaselineImageResolver
    { }

    private sealed class ActiveBaselineProvisionerProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, IBaselineImageProvisioner
    { }

    private sealed class SuspendingDiskGuardProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, IDiskGuardedSandboxProvider
    { }

    private sealed class SuspendingBaselineResolverProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, IBaselineImageResolver
    { }

    private sealed class SuspendingBaselineProvisionerProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, IBaselineImageProvisioner
    { }

    private sealed class DiskGuardBaselineResolverProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IDiskGuardedSandboxProvider, IBaselineImageResolver
    { }

    private sealed class DiskGuardBaselineProvisionerProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IDiskGuardedSandboxProvider, IBaselineImageProvisioner
    { }

    private sealed class BaselineProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IBaselineImageResolver, IBaselineImageProvisioner
    { }

    private sealed class ActiveSuspendingDiskGuardProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider
    { }

    private sealed class ActiveSuspendingBaselineResolverProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider, IBaselineImageResolver
    { }

    private sealed class ActiveSuspendingBaselineProvisionerProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider, IBaselineImageProvisioner
    { }

    private sealed class ActiveDiskGuardBaselineResolverProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver
    { }

    private sealed class ActiveDiskGuardBaselineProvisionerProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageProvisioner
    { }

    private sealed class ActiveBaselineProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner
    { }

    private sealed class SuspendingDiskGuardBaselineResolverProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver
    { }

    private sealed class SuspendingDiskGuardBaselineProvisionerProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageProvisioner
    { }

    private sealed class SuspendingBaselineProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner
    { }

    private sealed class DiskGuardBaselineProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IDiskGuardedSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner
    { }

    private sealed class ActiveSuspendingDiskGuardBaselineResolverProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver
    { }

    private sealed class ActiveSuspendingDiskGuardBaselineProvisionerProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageProvisioner
    { }

    private sealed class ActiveSuspendingBaselineProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner
    { }

    private sealed class ActiveDiskGuardBaselineProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner
    { }

    private sealed class SuspendingDiskGuardBaselineProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner
    { }

    private sealed class ActiveSuspendingDiskGuardBaselineProvider(ISandboxProvider inner, SandboxAdmissionGate gate, ILogger log)
        : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider, ISuspendingSandboxProvider, IDiskGuardedSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner
    { }

    private sealed class ActiveSandboxTracker
    {
        private readonly ConcurrentDictionary<SandboxAdmissionIdentity, (WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> _active = new();

        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> Snapshot() =>
            _active.Values.ToList();

        public void Track(SandboxSpec spec, ISandbox sandbox)
        {
            if (spec.TimingWorkItemId is not { } workItemId
                || sandbox is not IShutdownTeardownSandbox teardown)
                return;
            _active[SandboxAdmissionIdentity.FromSandbox(sandbox)] = (workItemId, teardown);
        }

        public void Remove(ISandbox sandbox)
        {
            _active.TryRemove(SandboxAdmissionIdentity.FromSandbox(sandbox), out _);
        }
    }

    private sealed class NamedAdmissionTracker
    {
        private readonly object _sync = new();
        private readonly Dictionary<SandboxAdmissionIdentity, SandboxAdmissionLease> _leases = new();
        private readonly HashSet<SandboxAdmissionIdentity> _pending = new();
        private readonly HashSet<SandboxAdmissionIdentity> _releaseRequested = new();

        public void Begin(SandboxAdmissionIdentity identity)
        {
            lock (_sync)
            {
                _pending.Add(identity);
            }
        }

        public void CancelPending(SandboxAdmissionIdentity identity)
        {
            lock (_sync)
            {
                _pending.Remove(identity);
                _releaseRequested.Remove(identity);
            }
        }

        public void Retain(SandboxAdmissionIdentity identity, SandboxAdmissionLease lease)
        {
            SandboxAdmissionLease? prior = null;
            var releaseNow = false;
            lock (_sync)
            {
                _pending.Remove(identity);
                if (_releaseRequested.Remove(identity))
                {
                    releaseNow = true;
                }
                else
                {
                    if (_leases.Remove(identity, out var existing))
                        prior = existing;
                    _leases[identity] = lease;
                }
            }
            prior?.Dispose();
            if (releaseNow)
                lease.Dispose();
        }

        public void Release(SandboxAdmissionIdentity identity)
        {
            SandboxAdmissionLease? lease = null;
            lock (_sync)
            {
                if (_leases.Remove(identity, out var existing))
                    lease = existing;
                else if (_pending.Contains(identity))
                    _releaseRequested.Add(identity);
            }
            lease?.Dispose();
        }

        public void ReleaseName(string name)
        {
            List<SandboxAdmissionLease>? leases = null;
            lock (_sync)
            {
                foreach (var (identity, lease) in _leases.ToArray())
                {
                    if (!string.Equals(identity.Name, name, StringComparison.Ordinal))
                        continue;
                    _leases.Remove(identity);
                    (leases ??= []).Add(lease);
                }

                foreach (var identity in _pending.ToArray())
                {
                    if (string.Equals(identity.Name, name, StringComparison.Ordinal))
                        _releaseRequested.Add(identity);
                }
            }

            if (leases is null)
                return;
            foreach (var lease in leases)
                lease.Dispose();
        }

        public void ReleaseMissing(IEnumerable<SandboxAdmissionIdentity> managedIds)
            => ReleaseMissing(managedIds, static _ => true);

        public void ReleaseMissing(
            IEnumerable<SandboxAdmissionIdentity> managedIds,
            Func<SandboxAdmissionIdentity, bool> canTreatMissingAsAbsent)
        {
            HashSet<SandboxAdmissionIdentity>? present = null;
            List<SandboxAdmissionLease>? toRelease = null;
            lock (_sync)
            {
                if (_leases.Count == 0)
                    return;

                present = managedIds.ToHashSet();
                foreach (var (identity, lease) in _leases.ToArray())
                {
                    if (present.Contains(identity) || !canTreatMissingAsAbsent(identity))
                        continue;
                    _leases.Remove(identity);
                    (toRelease ??= []).Add(lease);
                }
            }

            if (toRelease is null)
                return;
            foreach (var lease in toRelease)
                lease.Dispose();
        }
    }
}

internal sealed class SandboxAdmissionGate
{
    private readonly object _sync = new();
    private readonly Queue<Waiter> _waiters = new();
    private int _available;

    public SandboxAdmissionGate(int maxConcurrent)
    {
        if (maxConcurrent < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrent), "Max concurrent sandboxes must be >= 1");
        MaxConcurrent = maxConcurrent;
        _available = maxConcurrent;
    }

    public int MaxConcurrent { get; }

    public int CurrentAdmitted
    {
        get
        {
            lock (_sync)
                return MaxConcurrent - _available;
        }
    }

    public ValueTask<SandboxAdmissionLease> AcquireAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (ct.IsCancellationRequested)
                return new ValueTask<SandboxAdmissionLease>(Task.FromCanceled<SandboxAdmissionLease>(ct));

            if (_available > 0 && _waiters.Count == 0)
            {
                _available--;
                return ValueTask.FromResult(new SandboxAdmissionLease(this));
            }

            var waiter = new Waiter(this, ct);
            _waiters.Enqueue(waiter);
            waiter.RegisterCancellation();
            return new ValueTask<SandboxAdmissionLease>(waiter.Task);
        }
    }

    internal void Release()
    {
        while (true)
        {
            Waiter? waiter;
            lock (_sync)
            {
                if (_waiters.Count == 0)
                {
                    if (_available >= MaxConcurrent)
                        throw new InvalidOperationException("Sandbox admission token released more than once");
                    _available++;
                    return;
                }

                waiter = _waiters.Dequeue();
            }

            if (waiter.TryGrant())
                return;
        }
    }

    private sealed class Waiter
    {
        private const int Waiting = 0;
        private const int Granted = 1;
        private const int Canceled = 2;

        private readonly SandboxAdmissionGate _gate;
        private readonly CancellationToken _ct;
        private readonly TaskCompletionSource<SandboxAdmissionLease> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        private int _state;

        public Waiter(SandboxAdmissionGate gate, CancellationToken ct)
        {
            _gate = gate;
            _ct = ct;
        }

        public Task<SandboxAdmissionLease> Task => _tcs.Task;

        public void RegisterCancellation()
        {
            if (_ct.CanBeCanceled)
                _registration = _ct.Register(static state => ((Waiter)state!).TryCancel(), this);
        }

        public bool TryGrant()
        {
            if (Interlocked.CompareExchange(ref _state, Granted, Waiting) != Waiting)
                return false;
            _registration.Dispose();
            _tcs.TrySetResult(new SandboxAdmissionLease(_gate));
            return true;
        }

        private void TryCancel()
        {
            if (Interlocked.CompareExchange(ref _state, Canceled, Waiting) == Waiting)
                _tcs.TrySetCanceled(_ct);
        }
    }
}

internal sealed class SandboxAdmissionLease : IDisposable
{
    private SandboxAdmissionGate? _gate;

    public SandboxAdmissionLease(SandboxAdmissionGate gate)
    {
        _gate = gate;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

internal class AdmissionControlledSandbox : ISandbox, IPreserveOnDisposeSandbox, IHostQualifiedSandbox, ISandboxDecorator
{
    private readonly ISandbox _inner;
    private readonly IPreserveOnDisposeSandbox? _preserveOnDispose;
    private readonly Func<AdmissionControlledSandbox, SandboxAdmissionLease, bool, bool, ValueTask> _onDisposed;
    private readonly Action<AdmissionControlledSandbox> _onPreserved;
    private readonly ILogger _log;
    private readonly object _admissionSync = new();
    private SandboxAdmissionLease _lease;
    private bool _admissionReleasedForPreserve;
    private int _disposed;

    public AdmissionControlledSandbox(
        ISandbox inner,
        SandboxAdmissionLease lease,
        Func<AdmissionControlledSandbox, SandboxAdmissionLease, bool, bool, ValueTask> onDisposed,
        Action<AdmissionControlledSandbox> onPreserved,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(onDisposed);
        ArgumentNullException.ThrowIfNull(onPreserved);
        ArgumentNullException.ThrowIfNull(log);
        _inner = inner;
        _preserveOnDispose = inner as IPreserveOnDisposeSandbox;
        _lease = lease;
        _onDisposed = onDisposed;
        _onPreserved = onPreserved;
        _log = log;
        HostId = (inner as IHostQualifiedSandbox)?.HostId ?? "";
    }

    public string Id => _inner.Id;
    public string HostId { get; }
    public ISandbox InnerSandbox => _inner;
    public SandboxAgentOutputTransportKind AgentOutputTransportKind => _inner.AgentOutputTransportKind;
    public SandboxBatchLaunchMode BatchLaunchMode => _inner.BatchLaunchMode;
    public SandboxResourceMetrics? ResourceMetrics => _inner.ResourceMetrics;

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
        _inner.ExecAsync(exec, ct);

    public Task SyncStateToHostAsync(CancellationToken ct = default) =>
        _inner.SyncStateToHostAsync(ct);

    public Task KillActiveExecsAsync(CancellationToken ct = default) =>
        _inner.KillActiveExecsAsync(ct);

    public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default) =>
        _inner.GetScreenshotAsync(ct);

    public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default) =>
        _inner.SynthesizeInputAsync(events, ct);

    public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default) =>
        _inner.GetAccessibilityAtPointAsync(x, y, ct);

    public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default) =>
        _inner.GetAccessibilityTreeJsonAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Exception? disposeFailure = null;
        var innerDisposeSucceeded = false;
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            innerDisposeSucceeded = true;
        }
        catch (Exception ex)
        {
            disposeFailure = ex;
            _log.LogWarning(
                ex,
                "Retaining sandbox admission token after dispose failed for sandbox {SandboxId}",
                Id);
            throw;
        }
        finally
        {
            try
            {
                var (lease, admissionHeld) = SnapshotAdmissionForDispose();
                await _onDisposed(this, lease, innerDisposeSucceeded, admissionHeld).ConfigureAwait(false);
            }
            catch (Exception releaseEx)
            {
                if (disposeFailure is null)
                    throw;
                _log.LogError(
                    releaseEx,
                    "Failed to retain sandbox admission token after dispose failure for sandbox {SandboxId}",
                    Id);
            }
        }
    }

    public void DisablePreserveOnDispose() => _preserveOnDispose?.DisablePreserveOnDispose();

    internal bool TryAdoptAdmissionLease(SandboxAdmissionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_admissionSync)
        {
            if (_disposed != 0 || !_admissionReleasedForPreserve)
                return false;

            _lease = lease;
            _admissionReleasedForPreserve = false;
            return true;
        }
    }

    protected async Task StopAndReleaseAdmissionAsync(
        IPreemptibleSandbox preemptible,
        CancellationToken ct)
    {
        await preemptible.StopAndPreserveAsync(ct).ConfigureAwait(false);
        ReleaseAdmissionAfterPreserve();
    }

    private void ReleaseAdmissionAfterPreserve()
    {
        SandboxAdmissionLease? release = null;
        var notify = false;
        lock (_admissionSync)
        {
            if (!_admissionReleasedForPreserve)
            {
                _admissionReleasedForPreserve = true;
                release = _lease;
                notify = true;
            }
        }

        release?.Dispose();
        if (notify)
            _onPreserved(this);
    }

    private (SandboxAdmissionLease Lease, bool AdmissionHeld) SnapshotAdmissionForDispose()
    {
        lock (_admissionSync)
            return (_lease, !_admissionReleasedForPreserve);
    }
}

internal sealed class AdmissionControlledPreemptibleSandbox(
    ISandbox inner,
    IPreemptibleSandbox preemptible,
    SandboxAdmissionLease lease,
    Func<AdmissionControlledSandbox, SandboxAdmissionLease, bool, bool, ValueTask> onDisposed,
    Action<AdmissionControlledSandbox> onPreserved,
    ILogger log) : AdmissionControlledSandbox(inner, lease, onDisposed, onPreserved, log), IPreemptibleSandbox
{
    public Task StopAndPreserveAsync(CancellationToken ct = default) =>
        StopAndReleaseAdmissionAsync(preemptible, ct);
}

internal sealed class AdmissionControlledShutdownSandbox(
    ISandbox inner,
    IShutdownTeardownSandbox shutdown,
    SandboxAdmissionLease lease,
    Func<AdmissionControlledSandbox, SandboxAdmissionLease, bool, bool, ValueTask> onDisposed,
    Action<AdmissionControlledSandbox> onPreserved,
    ILogger log) : AdmissionControlledSandbox(inner, lease, onDisposed, onPreserved, log), IShutdownTeardownSandbox
{
    public bool IsOwnedByShutdownHandler => shutdown.IsOwnedByShutdownHandler;

    public void MarkOwnedByShutdownHandler() => shutdown.MarkOwnedByShutdownHandler();
}

internal sealed class AdmissionControlledSuspendableSandbox(
    ISandbox inner,
    ISuspendableSandbox suspendable,
    SandboxAdmissionLease lease,
    Func<AdmissionControlledSandbox, SandboxAdmissionLease, bool, bool, ValueTask> onDisposed,
    Action<AdmissionControlledSandbox> onPreserved,
    ILogger log) : AdmissionControlledSandbox(inner, lease, onDisposed, onPreserved, log), ISuspendableSandbox
{
    public bool IsSuspended => suspendable.IsSuspended;

    public long? MemoryBytes => suspendable.MemoryBytes;

    public Task SuspendAsync(CancellationToken ct = default) =>
        suspendable.SuspendAsync(ct);
}

internal sealed class AdmissionControlledPreemptibleSuspendableSandbox(
    ISandbox inner,
    IPreemptibleSandbox preemptible,
    ISuspendableSandbox suspendable,
    SandboxAdmissionLease lease,
    Func<AdmissionControlledSandbox, SandboxAdmissionLease, bool, bool, ValueTask> onDisposed,
    Action<AdmissionControlledSandbox> onPreserved,
    ILogger log)
    : AdmissionControlledSandbox(inner, lease, onDisposed, onPreserved, log),
        IPreemptibleSandbox,
        ISuspendableSandbox
{
    public bool IsSuspended => suspendable.IsSuspended;

    public long? MemoryBytes => suspendable.MemoryBytes;

    public Task StopAndPreserveAsync(CancellationToken ct = default) =>
        StopAndReleaseAdmissionAsync(preemptible, ct);

    public Task SuspendAsync(CancellationToken ct = default) =>
        suspendable.SuspendAsync(ct);
}

internal sealed class AdmissionControlledPreemptibleShutdownSandbox(
    ISandbox inner,
    IPreemptibleSandbox preemptible,
    IShutdownTeardownSandbox shutdown,
    SandboxAdmissionLease lease,
    Func<AdmissionControlledSandbox, SandboxAdmissionLease, bool, bool, ValueTask> onDisposed,
    Action<AdmissionControlledSandbox> onPreserved,
    ILogger log)
    : AdmissionControlledSandbox(inner, lease, onDisposed, onPreserved, log),
        IPreemptibleSandbox,
        IShutdownTeardownSandbox
{
    public bool IsOwnedByShutdownHandler => shutdown.IsOwnedByShutdownHandler;

    public Task StopAndPreserveAsync(CancellationToken ct = default) =>
        StopAndReleaseAdmissionAsync(preemptible, ct);

    public void MarkOwnedByShutdownHandler() => shutdown.MarkOwnedByShutdownHandler();
}

internal sealed class AdmissionControlledSuspendableShutdownSandbox(
    ISandbox inner,
    ISuspendableSandbox suspendable,
    IShutdownTeardownSandbox shutdown,
    SandboxAdmissionLease lease,
    Func<AdmissionControlledSandbox, SandboxAdmissionLease, bool, bool, ValueTask> onDisposed,
    Action<AdmissionControlledSandbox> onPreserved,
    ILogger log)
    : AdmissionControlledSandbox(inner, lease, onDisposed, onPreserved, log),
        ISuspendableSandbox,
        IShutdownTeardownSandbox
{
    public bool IsSuspended => suspendable.IsSuspended;

    public bool IsOwnedByShutdownHandler => shutdown.IsOwnedByShutdownHandler;

    public long? MemoryBytes => suspendable.MemoryBytes;

    public Task SuspendAsync(CancellationToken ct = default) =>
        suspendable.SuspendAsync(ct);

    public void MarkOwnedByShutdownHandler() => shutdown.MarkOwnedByShutdownHandler();
}

internal sealed class AdmissionControlledFullSandbox(
    ISandbox inner,
    IPreemptibleSandbox preemptible,
    ISuspendableSandbox suspendable,
    IShutdownTeardownSandbox shutdown,
    SandboxAdmissionLease lease,
    Func<AdmissionControlledSandbox, SandboxAdmissionLease, bool, bool, ValueTask> onDisposed,
    Action<AdmissionControlledSandbox> onPreserved,
    ILogger log)
    : AdmissionControlledSandbox(inner, lease, onDisposed, onPreserved, log),
        IPreemptibleSandbox,
        ISuspendableSandbox,
        IShutdownTeardownSandbox
{
    public bool IsSuspended => suspendable.IsSuspended;

    public bool IsOwnedByShutdownHandler => shutdown.IsOwnedByShutdownHandler;

    public long? MemoryBytes => suspendable.MemoryBytes;

    public Task StopAndPreserveAsync(CancellationToken ct = default) =>
        StopAndReleaseAdmissionAsync(preemptible, ct);

    public Task SuspendAsync(CancellationToken ct = default) =>
        suspendable.SuspendAsync(ct);

    public void MarkOwnedByShutdownHandler() => shutdown.MarkOwnedByShutdownHandler();
}
