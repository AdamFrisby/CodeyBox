using System.Collections.Concurrent;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

internal interface ISandboxAdmissionSnapshot
{
    int CurrentAdmittedSandboxes { get; }
    int MaxConcurrentSandboxes { get; }
}

internal interface ISandboxResumeAdmissionTracker
{
    void ReleaseResumeAdmission(string name);
}

/// <summary>
/// Decorates an <see cref="ISandboxProvider"/> with a process-wide live-sandbox
/// admission gate. The token is acquired before the inner provider starts
/// provisioning and is released exactly once when the returned sandbox handle is
/// disposed, so worker, audit, merge, smoke, and verifier call sites all share
/// the same VM budget without each call site knowing about the policy.
/// </summary>
public class SandboxAdmissionControlledProvider : ISandboxProvider, ISandboxAdmissionSnapshot
{
    private readonly SandboxAdmissionGate _gate;
    private readonly ILogger _log;

    private SandboxAdmissionControlledProvider(
        ISandboxProvider inner,
        SandboxAdmissionGate gate,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(log);
        Inner = inner;
        _gate = gate;
        _log = log;
    }

    public static ISandboxProvider Wrap(ISandboxProvider inner, int maxConcurrentSandboxes, ILogger log)
    {
        var gate = new SandboxAdmissionGate(maxConcurrentSandboxes);

        if (inner is IActiveSandboxProvider
            && inner is ISuspendingSandboxProvider suspendingProvider
            && inner is IDiskGuardedSandboxProvider diskGuardedProvider
            && inner is IBaselineImageResolver baselineResolver
            && inner is IBaselineImageProvisioner baselineProvisioner)
        {
            return new MultipassCapabilitySandboxAdmissionControlledProvider(
                inner,
                suspendingProvider,
                diskGuardedProvider,
                baselineResolver,
                baselineProvisioner,
                gate,
                log);
        }

        if (inner is IActiveSandboxProvider
            && inner is ISuspendingSandboxProvider activeSuspending)
        {
            return new ActiveSuspendingSandboxAdmissionControlledProvider(
                inner,
                activeSuspending,
                gate,
                log);
        }

        if (inner is IActiveSandboxProvider)
            return new ActiveSandboxAdmissionControlledProvider(inner, gate, log);

        if (inner is ISuspendingSandboxProvider suspending)
            return new SuspendingSandboxAdmissionControlledProvider(inner, suspending, gate, log);

        if (inner is IDiskGuardedSandboxProvider diskGuarded)
            return new DiskGuardedSandboxAdmissionControlledProvider(inner, diskGuarded, gate, log);

        return new SandboxAdmissionControlledProvider(inner, gate, log);
    }

    protected ISandboxProvider Inner { get; }

    public int CurrentAdmittedSandboxes => _gate.CurrentAdmitted;

    public int MaxConcurrentSandboxes => _gate.MaxConcurrent;

    public string Name => Inner.Name;

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        var lease = await _gate.AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            var sandbox = await Inner.CreateAsync(spec, ct).ConfigureAwait(false);
            var controlled = WrapSandbox(sandbox, lease);
            TrackActive(spec, controlled);
            return controlled;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
        Inner.ListAllManagedAsync(ct);

    public Task DisposeLeakedAsync(string name, CancellationToken ct) =>
        Inner.DisposeLeakedAsync(name, ct);

    protected virtual void TrackActive(SandboxSpec spec, ISandbox sandbox) { }

    protected virtual void OnSandboxDisposed(ISandbox sandbox) { }

    private protected ValueTask<SandboxAdmissionLease> AcquireAdmissionAsync(CancellationToken ct = default) =>
        _gate.AcquireAsync(ct);

    private ISandbox WrapSandbox(ISandbox sandbox, SandboxAdmissionLease lease)
    {
        if (sandbox is IPreemptibleSandbox preemptible
            && sandbox is ISuspendableSandbox suspendable
            && sandbox is IShutdownTeardownSandbox shutdown)
        {
            return new AdmissionControlledFullSandbox(
                sandbox,
                preemptible,
                suspendable,
                shutdown,
                lease,
                OnSandboxDisposed,
                _log);
        }

        if (sandbox is IPreemptibleSandbox preemptibleOnly)
        {
            return new AdmissionControlledPreemptibleSandbox(
                sandbox,
                preemptibleOnly,
                lease,
                OnSandboxDisposed,
                _log);
        }

        if (sandbox is IShutdownTeardownSandbox shutdownOnly)
        {
            return new AdmissionControlledShutdownSandbox(
                sandbox,
                shutdownOnly,
                lease,
                OnSandboxDisposed,
                _log);
        }

        if (sandbox is ISuspendableSandbox suspendableOnly)
        {
            return new AdmissionControlledSuspendableSandbox(
                sandbox,
                suspendableOnly,
                lease,
                OnSandboxDisposed,
                _log);
        }

        return new AdmissionControlledSandbox(sandbox, lease, OnSandboxDisposed, _log);
    }

    private sealed class ActiveSandboxAdmissionControlledProvider(
        ISandboxProvider inner,
        SandboxAdmissionGate gate,
        ILogger log) : SandboxAdmissionControlledProvider(inner, gate, log), IActiveSandboxProvider
    {
        private readonly ActiveSandboxTracker _active = new();

        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
            _active.Snapshot();

        protected override void TrackActive(SandboxSpec spec, ISandbox sandbox) =>
            _active.Track(spec, sandbox);

        protected override void OnSandboxDisposed(ISandbox sandbox) =>
            _active.Remove(sandbox);
    }

    private abstract class SuspendingSandboxAdmissionControlledProviderBase(
        ISandboxProvider inner,
        ISuspendingSandboxProvider suspendingProvider,
        SandboxAdmissionGate gate,
        ILogger log) : SandboxAdmissionControlledProvider(inner, gate, log), ISuspendingSandboxProvider, ISandboxResumeAdmissionTracker
    {
        private readonly object _resumeLeaseSync = new();
        private readonly Dictionary<string, SandboxAdmissionLease> _resumeLeases = new(StringComparer.Ordinal);
        private readonly HashSet<string> _releasedResumeNames = new(StringComparer.Ordinal);

        public async Task ResumeSandboxAsync(string name, CancellationToken ct)
        {
            var lease = await AcquireAdmissionAsync(ct).ConfigureAwait(false);
            var leaseTransferred = false;
            try
            {
                await suspendingProvider.ResumeSandboxAsync(name, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                SandboxAdmissionLease? prior = null;
                lock (_resumeLeaseSync)
                {
                    if (_releasedResumeNames.Remove(name))
                    {
                        leaseTransferred = true;
                    }
                    else
                    {
                        if (_resumeLeases.Remove(name, out var existing))
                            prior = existing;
                        _resumeLeases[name] = lease;
                        leaseTransferred = true;
                        lease = null!;
                    }
                }

                prior?.Dispose();
            }
            finally
            {
                if (!leaseTransferred)
                    lease.Dispose();
                else
                    lease?.Dispose();
            }
        }

        public void ReleaseResumeAdmission(string name)
        {
            SandboxAdmissionLease? lease = null;
            lock (_resumeLeaseSync)
            {
                if (_resumeLeases.Remove(name, out var existing))
                    lease = existing;
                else
                    _releasedResumeNames.Add(name);
            }
            lease?.Dispose();
        }

        public Task<int?> WaitForAdoptedAgentCompletionAsync(
            string vmName,
            string agentLogPath,
            Action<string>? logSink,
            TimeSpan? deadline,
            CancellationToken ct) =>
            suspendingProvider.WaitForAdoptedAgentCompletionAsync(vmName, agentLogPath, logSink, deadline, ct);

        public Task<bool> PushSuspendedVmCheckpointRefAsync(
            string vmName,
            string workingDir,
            string refName,
            string commitMessage,
            CancellationToken ct) =>
            suspendingProvider.PushSuspendedVmCheckpointRefAsync(vmName, workingDir, refName, commitMessage, ct);

        public Task<IReadOnlyList<string>> ReconcileStuckSandboxesAsync(
            IReadOnlySet<string> liveSuspendedNames,
            CancellationToken ct) =>
            suspendingProvider.ReconcileStuckSandboxesAsync(liveSuspendedNames, ct);
    }

    private sealed class SuspendingSandboxAdmissionControlledProvider(
        ISandboxProvider inner,
        ISuspendingSandboxProvider suspendingProvider,
        SandboxAdmissionGate gate,
        ILogger log) : SuspendingSandboxAdmissionControlledProviderBase(inner, suspendingProvider, gate, log)
    {
    }

    private class ActiveSuspendingSandboxAdmissionControlledProvider(
        ISandboxProvider inner,
        ISuspendingSandboxProvider suspendingProvider,
        SandboxAdmissionGate gate,
        ILogger log) : SuspendingSandboxAdmissionControlledProviderBase(inner, suspendingProvider, gate, log), IActiveSandboxProvider
    {
        private readonly ActiveSandboxTracker _active = new();

        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
            _active.Snapshot();

        protected override void TrackActive(SandboxSpec spec, ISandbox sandbox) =>
            _active.Track(spec, sandbox);

        protected override void OnSandboxDisposed(ISandbox sandbox) =>
            _active.Remove(sandbox);
    }

    private sealed class DiskGuardedSandboxAdmissionControlledProvider(
        ISandboxProvider inner,
        IDiskGuardedSandboxProvider diskGuardedProvider,
        SandboxAdmissionGate gate,
        ILogger log) : SandboxAdmissionControlledProvider(inner, gate, log), IDiskGuardedSandboxProvider
    {
        public IReadOnlyList<DiskGuardSample> SampleDiskGuardState() =>
            diskGuardedProvider.SampleDiskGuardState();
    }

    private sealed class MultipassCapabilitySandboxAdmissionControlledProvider(
        ISandboxProvider inner,
        ISuspendingSandboxProvider suspendingProvider,
        IDiskGuardedSandboxProvider diskGuardedProvider,
        IBaselineImageResolver baselineResolver,
        IBaselineImageProvisioner baselineProvisioner,
        SandboxAdmissionGate gate,
        ILogger log)
        : ActiveSuspendingSandboxAdmissionControlledProvider(inner, suspendingProvider, gate, log),
            IDiskGuardedSandboxProvider,
            IBaselineImageResolver,
            IBaselineImageProvisioner
    {
        public IReadOnlyList<DiskGuardSample> SampleDiskGuardState() =>
            diskGuardedProvider.SampleDiskGuardState();

        public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor) =>
            baselineResolver.ResolveBaselineRef(profileName, flavor);

        public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
            baselineResolver.ListBaselineImagesAsync(ct);

        public Task DisposeBaselineImageAsync(string name, CancellationToken ct) =>
            baselineResolver.DisposeBaselineImageAsync(name, ct);

        public async Task<string?> EnsureBaselineImageAsync(
            string profileName,
            SandboxProfileFlavor flavor,
            string? pinnedBaselineRef,
            CancellationToken ct)
        {
            using var lease = await AcquireAdmissionAsync(ct).ConfigureAwait(false);
            return await baselineProvisioner.EnsureBaselineImageAsync(
                profileName,
                flavor,
                pinnedBaselineRef,
                ct).ConfigureAwait(false);
        }
    }

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

internal class AdmissionControlledSandbox : ISandbox
{
    private readonly ISandbox _inner;
    private readonly SandboxAdmissionLease _lease;
    private readonly Action<ISandbox> _onDisposed;
    private readonly ILogger _log;
    private int _disposed;

    public AdmissionControlledSandbox(
        ISandbox inner,
        SandboxAdmissionLease lease,
        Action<ISandbox> onDisposed,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(onDisposed);
        ArgumentNullException.ThrowIfNull(log);
        _inner = inner;
        _lease = lease;
        _onDisposed = onDisposed;
        _log = log;
    }

    protected ISandbox Inner => _inner;

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
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            disposeFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                _onDisposed(this);
                _lease.Dispose();
            }
            catch (Exception releaseEx)
            {
                if (disposeFailure is null)
                    throw;
                _log.LogError(
                    releaseEx,
                    "Failed to release sandbox admission token after dispose failure for sandbox {SandboxId}",
                    Id);
            }
        }
    }
}

internal sealed class AdmissionControlledPreemptibleSandbox(
    ISandbox inner,
    IPreemptibleSandbox preemptible,
    SandboxAdmissionLease lease,
    Action<ISandbox> onDisposed,
    ILogger log) : AdmissionControlledSandbox(inner, lease, onDisposed, log), IPreemptibleSandbox
{
    public Task StopAndPreserveAsync(CancellationToken ct = default) =>
        preemptible.StopAndPreserveAsync(ct);
}

internal sealed class AdmissionControlledShutdownSandbox(
    ISandbox inner,
    IShutdownTeardownSandbox shutdown,
    SandboxAdmissionLease lease,
    Action<ISandbox> onDisposed,
    ILogger log) : AdmissionControlledSandbox(inner, lease, onDisposed, log), IShutdownTeardownSandbox
{
    public bool IsOwnedByShutdownHandler => shutdown.IsOwnedByShutdownHandler;

    public void MarkOwnedByShutdownHandler() => shutdown.MarkOwnedByShutdownHandler();
}

internal sealed class AdmissionControlledSuspendableSandbox(
    ISandbox inner,
    ISuspendableSandbox suspendable,
    SandboxAdmissionLease lease,
    Action<ISandbox> onDisposed,
    ILogger log) : AdmissionControlledSandbox(inner, lease, onDisposed, log), ISuspendableSandbox
{
    public bool IsSuspended => suspendable.IsSuspended;

    public long? MemoryBytes => suspendable.MemoryBytes;

    public Task SuspendAsync(CancellationToken ct = default) =>
        suspendable.SuspendAsync(ct);
}

internal sealed class AdmissionControlledFullSandbox(
    ISandbox inner,
    IPreemptibleSandbox preemptible,
    ISuspendableSandbox suspendable,
    IShutdownTeardownSandbox shutdown,
    SandboxAdmissionLease lease,
    Action<ISandbox> onDisposed,
    ILogger log)
    : AdmissionControlledSandbox(inner, lease, onDisposed, log),
        IPreemptibleSandbox,
        ISuspendableSandbox,
        IShutdownTeardownSandbox
{
    public bool IsSuspended => suspendable.IsSuspended;

    public bool IsOwnedByShutdownHandler => shutdown.IsOwnedByShutdownHandler;

    public long? MemoryBytes => suspendable.MemoryBytes;

    public Task StopAndPreserveAsync(CancellationToken ct = default) =>
        preemptible.StopAndPreserveAsync(ct);

    public Task SuspendAsync(CancellationToken ct = default) =>
        suspendable.SuspendAsync(ct);

    public void MarkOwnedByShutdownHandler() => shutdown.MarkOwnedByShutdownHandler();
}
