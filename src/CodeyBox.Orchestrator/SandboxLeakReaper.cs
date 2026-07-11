using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Periodic background sweep that detects — and optionally disposes — persistent
/// managed sandboxes that outlived their work item. These "leaked" sandboxes
/// accumulate when the orchestrator crashes mid-disposal, slowly consuming
/// provider disk and memory.
///
/// <para><b>What counts as a leak:</b> a provider-owned sandbox reported by the
/// managed lifecycle inventory, that is not in the current process's active
/// ownership snapshot (meaning the creating process crashed, or normal phase
/// disposal failed and released ownership), and whose creation timestamp is
/// either older than <see cref="SandboxLeakOptions.LeakAgeThreshold"/> or missing.
/// Each provider enforces its own ownership metadata and configurable namespace;
/// the age threshold guards against mistaking an in-progress provision for a
/// genuine leak.</para>
///
/// <para><b>Auto-dispose:</b> on by default. Operators can set
/// <see cref="SandboxLeakOptions.AutoDispose"/> to false for detection-only
/// operation. When enabled, each leaked sandbox is purged with a per-sandbox
/// timeout; one failure never blocks the rest of the sweep.</para>
///
/// <para>Detection-only results (when auto-dispose is off) are still emitted as
/// audit-tier <c>sandbox.leak_detected</c> events and returned by
/// <c>GET /sandboxes/leaked</c> so the operator can act.</para>
/// </summary>
public sealed class SandboxLeakReaper : BackgroundService
{
    private readonly IManagedSandboxLifecycle _provider;
    private readonly IWebhookDispatcher _webhooks;
    private readonly Func<SandboxLeakOptions> _optsAccessor;
    private readonly ILogger<SandboxLeakReaper> _log;
    private readonly IWorkItemStore? _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly LeakDetectionSink? _leakSink;

    // First time THIS reaper observed each sandbox in a suspend-lifecycle state with no
    // live mapping. The suspend-orphan grace is measured from this timestamp, NOT
    // from CreatedAt: a long-running in-flight VM has an old CreatedAt, so a
    // CreatedAt-based age gate would purge it on the first sweep after it enters
    // Suspending — mid-snapshot, while the provider may still be writing the RAM
    // image. Pruned each sweep to the set of identities still observed as orphans,
    // so it does not grow without bound. ConcurrentDictionary because the
    // operator-dispose endpoint and the sweep can touch reaper state from
    // different threads.
    private readonly ConcurrentDictionary<LeakIdentity, DateTimeOffset> _suspendOrphanFirstSeen = new();

    // Latest detected leaks, snapshotted after each sweep. Thread-safe via
    // Interlocked-style replace; the API endpoint reads this without locking.
    private volatile IReadOnlyList<LeakedSandboxInfo> _latestLeaks = [];

    // Resolves the current SandboxLeakOptions on every read so threshold/policy
    // edits applied via IOptionsMonitor (LeakAgeThreshold, PreemptRetention,
    // AutoDispose, MaxConcurrentAutoDispose) take effect on the next sweep
    // without restarting CodeyBox. CheckInterval and Enabled are sampled at
    // PeriodicTimer construction so changes to those fields require a restart —
    // limitation documented on the fields themselves.
    private SandboxLeakOptions _opts => _optsAccessor();

    public SandboxLeakReaper(
        IManagedSandboxLifecycle provider,
        IWebhookDispatcher webhooks,
        SandboxLeakOptions opts,
        ILogger<SandboxLeakReaper> log)
        : this(provider, webhooks, () => opts, log, store: null) { }

    public SandboxLeakReaper(
        IManagedSandboxLifecycle provider,
        IWebhookDispatcher webhooks,
        Func<SandboxLeakOptions> optionsAccessor,
        ILogger<SandboxLeakReaper> log)
        : this(provider, webhooks, optionsAccessor, log, store: null) { }

    public SandboxLeakReaper(
        IManagedSandboxLifecycle provider,
        IWebhookDispatcher webhooks,
        Func<SandboxLeakOptions> optionsAccessor,
        ILogger<SandboxLeakReaper> log,
        IWorkItemStore? store,
        Func<DateTimeOffset>? clock = null,
        LeakDetectionSink? leakSink = null)
    {
        _provider = provider;
        _webhooks = webhooks;
        _optsAccessor = optionsAccessor;
        _log = log;
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _leakSink = leakSink;
    }

    /// <summary>
    /// Returns leaked sandboxes from the latest sweep that have not yet been
    /// successfully auto-disposed or operator-disposed.
    /// </summary>
    public IReadOnlyList<LeakedSandboxInfo> GetLatestLeaks() => _latestLeaks;

    /// <summary>
    /// Removes a single entry from the latest leak list. Called by the operator-dispose
    /// endpoint immediately after a successful disposal so that a second POST call for
    /// the same snapshot returns 404 rather than attempting a redundant provider delete.
    /// </summary>
    public void RemoveFromLatestLeaks(string name, string? hostId = null)
    {
        _latestLeaks = _latestLeaks
            .Where(l => l.Name != name || !string.Equals(l.HostId, hostId, StringComparison.Ordinal))
            .ToList();
    }

    public void RemoveFromLatestLeaks(LeakedSandboxInfo leak)
    {
        _latestLeaks = _latestLeaks
            .Where(candidate => !SameLeak(candidate, leak))
            .ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("SandboxLeakReaper disabled via configuration; skipping");
            return;
        }

        // Guard against misconfigured intervals that would cause ArgumentOutOfRangeException
        // (TimeSpan.Zero) or a tight provider-hammering loop (very small values).
        var interval = _opts.CheckInterval < TimeSpan.FromMinutes(1)
            ? TimeSpan.FromMinutes(1)
            : _opts.CheckInterval;
        if (_opts.CheckInterval < TimeSpan.FromMinutes(1))
            _log.LogWarning("SandboxLeakReaper: CheckInterval {Configured} is below the 1-minute minimum; clamped to 1 minute", _opts.CheckInterval);

        // Guard against a threshold so small that every untracked sandbox is disposed
        // on the first sweep after a restart (when _activeSandboxNames is empty). Mirrors
        // the floor applied to CheckInterval.
        if (_opts.LeakAgeThreshold < TimeSpan.FromMinutes(5))
            _log.LogWarning(
                "SandboxLeakReaper: LeakAgeThreshold {Configured} is below the recommended 5-minute minimum; sandboxes from a prior orchestrator instance that crashed recently may be disposed prematurely",
                _opts.LeakAgeThreshold);

        using var timer = new PeriodicTimer(interval);
        // Run immediately at startup, then on the configured interval.
        do
        {
            await RunSweepAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunSweepAsync(CancellationToken ct)
    {
        try
        {
            var allManaged = await _provider.ListAllManagedAsync(ct);
            var now = _clock();
            var observedSuspendOrphans = new HashSet<LeakIdentity>();
            var duplicateNamesWithActiveSnapshot = allManaged
                .GroupBy(static info => info.Name, StringComparer.Ordinal)
                .Where(static group => group.Count() > 1 && group.Any(static info => info.IsTrackedActive))
                .Select(static group => group.Key)
                .ToHashSet(StringComparer.Ordinal);

            // R8-core: any VM named in a work item's SuspendedVmName is being
            // held across an orchestrator restart and MUST NOT be reaped — the
            // startup resume handler will start it through its owning lifecycle provider
            // (or clear the bookkeeping if the resume fails). Built fresh per
            // sweep so a resume that completes between sweeps takes effect on
            // the next pass without an explicit invalidation hook.
            var suspendedNames = await BuildSuspendedVmNameSetAsync(ct);

            var leaks = new List<LeakedSandboxInfo>();
            foreach (var info in allManaged)
            {
                if (info.IsTrackedActive)
                    continue;

                if (duplicateNamesWithActiveSnapshot.Contains(info.Name))
                    continue;

                if (suspendedNames.Contains(info.Name))
                    continue;

                var missingCreationMetadata = !info.CreatedAt.HasValue;
                var createdAt = info.CreatedAt ?? now - _opts.LeakAgeThreshold;
                var age = now - createdAt;

                // A VM in a suspend lifecycle state (freezing to disk or already
                // frozen) that reached this point has no live SuspendedVmName
                // mapping (those are filtered above), so the startup resume handler
                // will never reattach it — it is an orphan from a crash between
                // suspend and the bookkeeping write, or from a cleared/expired
                // mapping. SuspendAsync drops a .codeybox-preempt marker, so without
                // a dedicated branch such a VM would inherit the 24h PreemptRetention
                // grace and leak for a day. The provider abstracts its own lifecycle
                // vocabulary into IsSuspendLifecycleOrFrozen so the reaper does not
                // depend on any backend's state strings.
                var isSuspendOrphan = info.IsSuspendLifecycleOrFrozen;
                var leakIdentity = LeakIdentity.From(info);

                if (isSuspendOrphan)
                {
                    // Dedicated suspend grace, NOT the CreatedAt-based age gate. A
                    // VM that just entered Suspending may still be writing its RAM
                    // image to disk — providers may take many minutes for a loaded
                    // VM. Measure the grace from when this reaper FIRST saw
                    // the VM in a suspend state (a long-running VM's old CreatedAt
                    // would otherwise clear LeakAgeThreshold immediately and we'd
                    // purge mid-snapshot). Only once the grace elapses with still no
                    // live mapping is it a true orphan eligible for delete --purge.
                    observedSuspendOrphans.Add(leakIdentity);
                    var firstSeen = _suspendOrphanFirstSeen.GetOrAdd(leakIdentity, now);
                    if (now - firstSeen < _opts.SuspendOrphanGrace)
                        continue;
                }
                else
                {
                    if (info.HasPreemptMarker && age < _opts.PreemptRetention)
                        continue;

                    if (age < _opts.LeakAgeThreshold)
                        continue;
                }

                var diskMb = info.DiskBytes.HasValue ? info.DiskBytes.Value / (1024 * 1024) : (long?)null;
                // Suspend orphans are gated on the dedicated grace above, not on
                // CreatedAt, so classify them first — a suspend orphan whose staging
                // metadata is also missing is still a suspend orphan, not a generic
                // missing-metadata leak.
                var reason = isSuspendOrphan
                    ? SandboxLeakReasons.OrphanedSuspendingVm
                    : (missingCreationMetadata
                        ? SandboxLeakReasons.UntrackedSandboxMissingCreationMetadata
                        : (info.HasPreemptMarker
                            ? SandboxLeakReasons.ExpiredPreemptRetention
                            : SandboxLeakReasons.UntrackedSandbox));
                leaks.Add(new LeakedSandboxInfo(
                    info.Name,
                    createdAt,
                    age,
                    info.DiskBytes,
                    reason,
                    info.LifecycleProviderId,
                    info.HostId));
                AuditLog.SandboxLeakDetected(info.Name, age.TotalMinutes, diskMb, reason);
                _ = _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "sandbox.leak_detected",
                    Details = new SandboxLeakDetails
                    {
                        Name = info.Name,
                        AgeMinutes = Math.Round(age.TotalMinutes, 1),
                        DiskMb = diskMb,
                        Reason = reason,
                    },
                }, ct);
                _leakSink?.Increment();
            }

            // Drop first-seen entries for VMs that are no longer suspend orphans
            // (resumed, reaped, or gone) so the map tracks only currently-orphaned
            // VMs and cannot grow without bound across the process lifetime.
            foreach (var identity in _suspendOrphanFirstSeen.Keys)
                if (!observedSuspendOrphans.Contains(identity))
                    _suspendOrphanFirstSeen.TryRemove(identity, out _);

            _latestLeaks = leaks;

            if (leaks.Count == 0) return;

            _log.LogWarning("SandboxLeakReaper: {Count} leaked sandbox(es) detected", leaks.Count);

            if (!_opts.AutoDispose)
            {
                _log.LogWarning(
                    "SandboxLeakReaper: AutoDispose=false — review leaked sandboxes via GET /sandboxes/leaked and dispose manually via POST /sandboxes/leaked/{{name}}/dispose");
                return;
            }

            var maxConcurrentDisposes = Math.Max(1, _opts.MaxConcurrentAutoDispose);
            if (_opts.MaxConcurrentAutoDispose < 1)
                _log.LogWarning(
                    "SandboxLeakReaper: MaxConcurrentAutoDispose {Configured} is below the 1-dispose minimum; clamped to 1",
                    _opts.MaxConcurrentAutoDispose);

            // Dispose leaks with bounded host-side pressure; each sandbox still
            // gets its independent timeout and one failure never blocks the batch.
            var failedLeaks = new ConcurrentBag<LeakIdentity>();
            await Parallel.ForEachAsync(leaks, new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = maxConcurrentDisposes,
            }, async (leak, token) =>
            {
                var failedLeak = await DisposeSingleAsync(leak, token);
                if (failedLeak is not null)
                    failedLeaks.Add(failedLeak.Value);
            });
            var failedLeakSet = failedLeaks.ToHashSet();
            _latestLeaks = leaks.Where(leak => failedLeakSet.Contains(LeakIdentity.From(leak))).ToList();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SandboxLeakReaper: sweep failed");
        }
    }

    private async Task<HashSet<string>> BuildSuspendedVmNameSetAsync(CancellationToken ct)
    {
        if (_store is null) return new HashSet<string>(StringComparer.Ordinal);
        var set = new HashSet<string>(StringComparer.Ordinal);
        // ListSuspendedAsync hits the partial index idx_work_items_suspended_vm
        // (suspended_vm_name WHERE NOT NULL), so this loop's cost scales with
        // the in-flight suspend count rather than the full work_items table.
        await foreach (var item in _store.ListSuspendedAsync(ct))
        {
            // Honour SuspendedVmName for any non-terminal item. The suspend-on-
            // shutdown handler persists a mapping for every entry from
            // SnapshotActiveSandboxes — Working/Auditing/Reworking/Merging but
            // also ReworkingForConflict and any other mid-flight phase that can
            // hold a live sandbox — so the exemption must track "not terminal"
            // rather than an explicit allow-list that silently drops a state and
            // lets the reaper purge a VM the startup resume handler is about to
            // reattach. A terminal item with a stale SuspendedVmName (e.g.
            // cancelled by the operator between suspend and the next sweep) is
            // intentionally NOT exempt, so its VM does not leak forever.
            if (string.IsNullOrWhiteSpace(item.SuspendedVmName)) continue;
            if (!WorkItemDependencies.TerminalStates.Contains(item.State))
            {
                set.Add(item.SuspendedVmName!);
            }
        }
        return set;
    }

    private async Task<LeakIdentity?> DisposeSingleAsync(LeakedSandboxInfo leak, CancellationToken stoppingToken)
    {
        using var timeoutCts = new CancellationTokenSource(_opts.DisposeTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

        var diskMb = leak.DiskBytes.HasValue ? leak.DiskBytes.Value / (1024 * 1024) : (long?)null;
        try
        {
            await _provider.DisposeLeakedAsync(ToManagedSandboxInfo(leak), linkedCts.Token);
            var disposedAt = DateTimeOffset.UtcNow;
            AuditLog.SandboxLeakDisposed(leak.Name, leak.Age.TotalMinutes, diskMb, disposedAt, leak.Reason);
            _ = _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "sandbox.leak_disposed",
                Details = new SandboxLeakDetails
                {
                    Name = leak.Name,
                    AgeMinutes = Math.Round(leak.Age.TotalMinutes, 1),
                    DiskMb = diskMb,
                    DisposedAt = disposedAt,
                    Reason = leak.Reason,
                },
            }, stoppingToken);
            _log.LogInformation(
                "SandboxLeakReaper: disposed leaked sandbox {Name} age={AgeMinutes:F1}min reason={Reason}",
                leak.Name,
                leak.Age.TotalMinutes,
                leak.Reason);
            return null;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service is shutting down — do not emit a spurious "timeout" audit event for
            // a planned restart. Rethrow so Task.WhenAll in RunSweepAsync surfaces the
            // cancellation, which is swallowed by RunSweepAsync's OperationCanceledException handler.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Per-disposal 5-minute timeout fired.
            AuditLog.SandboxLeakDisposeFailed(leak.Name, leak.Age.TotalMinutes, diskMb, "timeout", leak.Reason);
            _ = _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "sandbox.leak_dispose_failed",
                Details = new SandboxLeakDetails
                {
                    Name = leak.Name,
                    AgeMinutes = Math.Round(leak.Age.TotalMinutes, 1),
                    DiskMb = diskMb,
                    Error = "timeout",
                    Reason = leak.Reason,
                },
            }, stoppingToken);
            _log.LogWarning("SandboxLeakReaper: timed out disposing leaked sandbox {Name}", leak.Name);
            return LeakIdentity.From(leak);
        }
        catch (Exception ex)
        {
            AuditLog.SandboxLeakDisposeFailed(leak.Name, leak.Age.TotalMinutes, diskMb, ex.Message, leak.Reason);
            _ = _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "sandbox.leak_dispose_failed",
                Details = new SandboxLeakDetails
                {
                    Name = leak.Name,
                    AgeMinutes = Math.Round(leak.Age.TotalMinutes, 1),
                    DiskMb = diskMb,
                    Error = ex.Message,
                    Reason = leak.Reason,
                },
            }, stoppingToken);
            _log.LogWarning(ex, "SandboxLeakReaper: failed to dispose leaked sandbox {Name}", leak.Name);
            return LeakIdentity.From(leak);
        }
    }

    private static bool SameLeak(LeakedSandboxInfo left, LeakedSandboxInfo right) =>
        LeakIdentity.From(left).Equals(LeakIdentity.From(right));

    private static ManagedSandboxInfo ToManagedSandboxInfo(LeakedSandboxInfo leak)
        => new(
            leak.Name,
            leak.CreatedAt,
            leak.DiskBytes,
            IsTrackedActive: false,
            LifecycleProviderId: leak.LifecycleProviderId,
            HostId: leak.HostId);

    private readonly record struct LeakIdentity(string Name, string? LifecycleProviderId, string? HostId)
    {
        public static LeakIdentity From(ManagedSandboxInfo info) =>
            new(info.Name, NormalizeId(info.LifecycleProviderId), NormalizeId(info.HostId));

        public static LeakIdentity From(LeakedSandboxInfo info) =>
            new(info.Name, NormalizeId(info.LifecycleProviderId), NormalizeId(info.HostId));

        private static string? NormalizeId(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

/// <summary>
/// Configuration for <see cref="SandboxLeakReaper"/>. Bound from
/// <c>CodeyBox:SandboxLeak</c>.
/// </summary>
public sealed class SandboxLeakOptions
{
    /// <summary>
    /// Enable or disable the leak reaper. Default true.
    /// <para><b>Startup-only:</b> sampled at PeriodicTimer construction.
    /// Toggling at runtime requires a CodeyBox restart.</para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often to run the leak scan. Default 15 minutes.
    /// <para><b>Startup-only:</b> sampled at PeriodicTimer construction so
    /// edits applied at runtime do not change the sweep cadence until restart.</para>
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Minimum age before a non-active sandbox is declared leaked.
    /// Default 30 minutes — conservative enough to not mistake a sandbox
    /// that is mid-way through work-phase clone.
    /// <para><b>Hot-reloadable:</b> read on each sweep.</para>
    /// </summary>
    public TimeSpan LeakAgeThreshold { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Maximum time to exempt gracefully preempted sandboxes from leak reporting
    /// and auto-disposal. After this bound they are treated like ordinary leaks.
    /// Default 24 hours.
    /// <para><b>Hot-reloadable:</b> read on each sweep.</para>
    /// </summary>
    public TimeSpan PreemptRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Dedicated grace before a suspend-lifecycle VM (as abstracted by
    /// <see cref="ManagedSandboxInfo.IsSuspendLifecycleOrFrozen"/>) with no live <c>SuspendedVmName</c>
    /// mapping is treated as a leak and purged. Measured from when the reaper first
    /// observes the VM in a suspend state — NOT from the VM's creation time — so a
    /// long-running in-flight VM that has just begun freezing is not purged while
    /// its provider may still be writing the RAM image. Sized to the worst-case
    /// RAM-snapshot budget for the default VM profile so a slow snapshot completes
    /// (or the orchestrator restarts and reclaims the mapping) before the reaper
    /// acts. Default ~30 minutes (<see cref="SuspendTimeoutPolicy.For(long?, System.TimeSpan?, System.TimeSpan?)"/>
    /// of the default profile RAM).
    /// <para><b>Hot-reloadable:</b> read on each sweep.</para>
    /// </summary>
    public TimeSpan SuspendOrphanGrace { get; set; } =
        SuspendTimeoutPolicy.For(SandboxResourceLimits.Default.MemoryBytes);

    /// <summary>
    /// When true, automatically dispose each detected leak after logging it.
    /// Default true because sandbox VMs are phase-scoped and old untracked
    /// instances consume host memory until purged.
    /// <para><b>Hot-reloadable:</b> read on each sweep.</para>
    /// </summary>
    public bool AutoDispose { get; set; } = true;

    /// <summary>
    /// Maximum number of leaked sandboxes to dispose concurrently in one sweep.
    /// Default 4 to limit pressure on lifecycle backends during restart cleanup.
    /// <para><b>Hot-reloadable:</b> read on each sweep.</para>
    /// </summary>
    public int MaxConcurrentAutoDispose { get; set; } = 4;

    /// <summary>
    /// Per-sandbox timeout for one managed-provider disposal operation.
    /// Default 5 minutes. Raising this allows a slow backend purge to complete
    /// instead of being abandoned mid-operation.
    /// <para><b>Hot-reloadable:</b> read from the Func accessor on each sweep.</para>
    /// </summary>
    public TimeSpan DisposeTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
