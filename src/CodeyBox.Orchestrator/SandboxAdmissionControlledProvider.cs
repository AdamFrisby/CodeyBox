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
public class SandboxAdmissionControlledProvider : ISandboxProvider, ISandboxAdmissionSnapshot, IActiveSandboxProgressProvider
{
    private readonly ISandboxProvider _inner;
    private readonly SandboxAdmissionGate _gate;
    private readonly ILogger _log;
    private readonly ActiveSandboxTracker? _active;
    private readonly NamedAdmissionTracker? _resumeAdmissions;
    private readonly NamedAdmissionTracker _disposedSandboxAdmissions = new();
    private readonly NamedAdmissionTracker _disposedBaselineAdmissions = new();
    private readonly ConcurrentDictionary<string, AdmissionControlledSandbox> _preservedLiveSandboxes = new(StringComparer.Ordinal);
    private readonly ISuspendingSandboxProvider? _suspendingProvider;
    private readonly IDiskGuardedSandboxProvider? _diskGuardedProvider;
    private readonly IBaselineImageResolver? _baselineResolver;
    private readonly IBaselineImageProvisioner? _baselineProvisioner;
    private readonly IActiveSandboxProgressProvider? _progressProvider;

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

        return capabilities switch
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

    public string Name => _inner.Name;

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
        var managedNames = managed.Select(static info => info.Name).ToArray();
        _resumeAdmissions?.ReleaseMissing(managedNames);
        _disposedSandboxAdmissions.ReleaseMissing(managedNames);
        ReleaseMissingPreservedLiveSandboxes(managedNames);
        return managed;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        await _inner.DisposeLeakedAsync(name, ct).ConfigureAwait(false);
        _preservedLiveSandboxes.TryRemove(name, out _);
        _resumeAdmissions?.Release(name);
        _disposedSandboxAdmissions.Release(name);
    }

    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
        (_active ?? throw new NotSupportedException("The wrapped sandbox provider does not expose active sandboxes.")).Snapshot();

    public IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress() =>
        _progressProvider?.SnapshotActiveSandboxProgress() ?? [];

    public async Task ResumeSandboxAsync(string name, CancellationToken ct)
    {
        var suspendingProvider = _suspendingProvider
            ?? throw new NotSupportedException("The wrapped sandbox provider does not support suspend/resume.");
        var resumeAdmissions = _resumeAdmissions
            ?? throw new NotSupportedException("The wrapped sandbox provider does not track resume admission.");

        resumeAdmissions.Begin(name);
        SandboxAdmissionLease? lease = null;
        var retained = false;
        try
        {
            lease = await _gate.AcquireAsync(ct).ConfigureAwait(false);
            await suspendingProvider.ResumeSandboxAsync(name, ct).ConfigureAwait(false);
            if (TryAdoptResumeAdmission(name, lease))
                resumeAdmissions.CancelPending(name);
            else
                resumeAdmissions.Retain(name, lease);
            retained = true;
        }
        finally
        {
            if (!retained)
            {
                lease?.Dispose();
                resumeAdmissions.CancelPending(name);
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
        _disposedBaselineAdmissions.ReleaseMissing(baselines.Select(static info => info.Name));
        return baselines;
    }

    public async Task DisposeBaselineImageAsync(string name, CancellationToken ct)
    {
        var baselineResolver = _baselineResolver
            ?? throw new NotSupportedException("The wrapped sandbox provider does not resolve baseline images.");
        await baselineResolver.DisposeBaselineImageAsync(name, ct).ConfigureAwait(false);
        _disposedBaselineAdmissions.Release(name);
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
            _disposedBaselineAdmissions.Retain(ex.RetainedSandboxName!, lease);
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
        _preservedLiveSandboxes.TryRemove(sandbox.Id, out _);
        _resumeAdmissions?.Release(sandbox.Id);
        if (!admissionHeld)
            return;

        var releaseAdmission = false;
        if (innerDisposeSucceeded)
            releaseAdmission = !await IsManagedSandboxStillPresentAsync(sandbox.Id).ConfigureAwait(false);

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
            _disposedSandboxAdmissions.Retain(sandbox.Id, lease);
        }
    }

    private void OnSandboxPreserved(AdmissionControlledSandbox sandbox)
    {
        _preservedLiveSandboxes[sandbox.Id] = sandbox;
        _resumeAdmissions?.Release(sandbox.Id);
    }

    private void ReleaseMissingPreservedLiveSandboxes(IReadOnlyCollection<string> managedNames)
    {
        if (_preservedLiveSandboxes.IsEmpty)
            return;

        var present = new HashSet<string>(managedNames, StringComparer.Ordinal);
        foreach (var name in _preservedLiveSandboxes.Keys)
        {
            if (!present.Contains(name))
                _preservedLiveSandboxes.TryRemove(name, out _);
        }
    }

    private bool TryAdoptResumeAdmission(string name, SandboxAdmissionLease lease)
    {
        if (!_preservedLiveSandboxes.TryRemove(name, out var sandbox))
            return false;

        return sandbox.TryAdoptAdmissionLease(lease);
    }

    private async Task<bool> IsManagedSandboxStillPresentAsync(string name)
    {
        try
        {
            var managed = await _inner.ListAllManagedAsync(CancellationToken.None).ConfigureAwait(false);
            return managed.Any(info => string.Equals(info.Name, name, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Could not verify whether sandbox {SandboxId} still exists; retaining sandbox admission token",
                name);
            return true;
        }
    }

    private void RetainDeferredProvisioningAdmission(
        SandboxProvisioningDeferredException ex,
        SandboxAdmissionLease lease)
    {
        if (IsRetainedBaselineProvisioning(ex) && _baselineResolver is not null)
        {
            _disposedBaselineAdmissions.Retain(ex.RetainedSandboxName!, lease);
            return;
        }

        _disposedSandboxAdmissions.Retain(ex.RetainedSandboxName!, lease);
    }

    private static bool IsRetainedBaselineProvisioning(SandboxProvisioningDeferredException ex) =>
        ex.Operation.StartsWith("baseline-", StringComparison.Ordinal);

    [Flags]
    private enum ProviderCapabilities
    {
        None = 0,
        Active = 1,
        Suspending = 2,
        DiskGuard = 4,
        BaselineResolver = 8,
        BaselineProvisioner = 16,
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
        private readonly ConcurrentDictionary<string, (WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> _active = new(StringComparer.Ordinal);

        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> Snapshot() =>
            _active.Values.ToList();

        public void Track(SandboxSpec spec, ISandbox sandbox)
        {
            if (spec.TimingWorkItemId is not { } workItemId
                || sandbox is not IShutdownTeardownSandbox teardown)
                return;
            _active[sandbox.Id] = (workItemId, teardown);
        }

        public void Remove(ISandbox sandbox)
        {
            _active.TryRemove(sandbox.Id, out _);
        }
    }

    private sealed class NamedAdmissionTracker
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, SandboxAdmissionLease> _leases = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
        private readonly HashSet<string> _releaseRequested = new(StringComparer.Ordinal);

        public void Begin(string name)
        {
            lock (_sync)
            {
                _pending.Add(name);
            }
        }

        public void CancelPending(string name)
        {
            lock (_sync)
            {
                _pending.Remove(name);
                _releaseRequested.Remove(name);
            }
        }

        public void Retain(string name, SandboxAdmissionLease lease)
        {
            SandboxAdmissionLease? prior = null;
            var releaseNow = false;
            lock (_sync)
            {
                _pending.Remove(name);
                if (_releaseRequested.Remove(name))
                {
                    releaseNow = true;
                }
                else
                {
                    if (_leases.Remove(name, out var existing))
                        prior = existing;
                    _leases[name] = lease;
                }
            }
            prior?.Dispose();
            if (releaseNow)
                lease.Dispose();
        }

        public void Release(string name)
        {
            SandboxAdmissionLease? lease = null;
            lock (_sync)
            {
                if (_leases.Remove(name, out var existing))
                    lease = existing;
                else if (_pending.Contains(name))
                    _releaseRequested.Add(name);
            }
            lease?.Dispose();
        }

        public void ReleaseMissing(IEnumerable<string> managedNames)
        {
            HashSet<string>? present = null;
            List<SandboxAdmissionLease>? toRelease = null;
            lock (_sync)
            {
                if (_leases.Count == 0)
                    return;

                present = new HashSet<string>(managedNames, StringComparer.Ordinal);
                foreach (var (name, lease) in _leases.ToArray())
                {
                    if (present.Contains(name))
                        continue;
                    _leases.Remove(name);
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

internal class AdmissionControlledSandbox : ISandbox, IPreserveOnDisposeSandbox
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
    }

    public string Id => _inner.Id;

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
        _inner.ExecAsync(exec, ct);

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
