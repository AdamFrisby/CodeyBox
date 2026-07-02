using System.Collections.Concurrent;
using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Deployment;

/// <summary>
/// Periodic background sweep that detects — and optionally disposes —
/// provider-managed deployment resources whose deployment owner no longer exists in
/// <see cref="IDeploymentManager.GetActive"/>. Follows the same pattern as
/// <c>SandboxLeakReaper</c>: enumerate the substrate provider's managed resources,
/// filter by an age threshold, and dispose the orphans.
///
/// <para>The reaper is the safety net for orchestrator restarts and aborted
/// deploy lifecycles. The happy path is already covered by
/// <see cref="IDeploymentHandle.DisposeAsync"/>; this reaper only acts when
/// the normal path was interrupted before disposal could run.</para>
///
/// <para>The cleanup provider is deployment-scoped: the built-in sandbox
/// adapter filters to deployment-tagged sandboxes, while future cloud-VM
/// substrates can expose their own deployment resource inventory without
/// referencing sandbox APIs. The deployment reaper still honours preserve
/// skip-gates for deployment VMs:</para>
/// <list type="bullet">
///   <item><b>HasPreemptMarker</b> — graceful-shutdown-preserved VMs are
///   exempt for <see cref="DeploymentLeakOptions.PreemptRetention"/>
///   (default 24h).</item>
///   <item><b>IsSuspendLifecycleOrFrozen</b> — suspended VMs (multipass
///   <c>Suspending</c>/<c>Suspended</c>) get a dedicated
///   <see cref="DeploymentLeakOptions.SuspendOrphanGrace"/> measured from
///   first observation in a suspend state before disposal.</item>
///   <item>Optional <c>suspendedNameProvider</c> — the composition root
///   wires this to the work-item store's SuspendedVmName index so the
///   startup resume handler can multipass-start them back to Running.</item>
/// </list>
/// <para>Running both reapers is safe because each uses provider-reported
/// ownership tags to operate on its own lifecycle class.</para>
/// </summary>
public sealed class DeploymentLeakReaper : BackgroundService
{
    private readonly IDeploymentCleanupProvider _cleanupProvider;
    private readonly IDeploymentManager _manager;
    private readonly Func<DeploymentLeakOptions> _optsAccessor;
    private readonly ILogger<DeploymentLeakReaper> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<CancellationToken, Task<IReadOnlySet<string>>>? _suspendedNameProvider;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _suspendOrphanFirstSeen =
        new(StringComparer.Ordinal);
    private volatile IReadOnlyList<DeploymentLeakInfo> _latestLeaks = [];

    public DeploymentLeakReaper(
        IDeploymentCleanupProvider cleanupProvider,
        IDeploymentManager manager,
        Func<DeploymentLeakOptions> optionsAccessor,
        ILogger<DeploymentLeakReaper> log,
        Func<DateTimeOffset>? clock = null,
        Func<CancellationToken, Task<IReadOnlySet<string>>>? suspendedNameProvider = null)
    {
        _cleanupProvider = cleanupProvider ?? throw new ArgumentNullException(nameof(cleanupProvider));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _optsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _suspendedNameProvider = suspendedNameProvider;
    }

    public IReadOnlyList<DeploymentLeakInfo> GetLatestLeaks() => _latestLeaks;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Cadence is sampled at PeriodicTimer construction (PeriodicTimer cannot
        // be retuned without reconstruction), but Enabled is re-checked each tick
        // so an operator hot-flipping Enabled=false stops the sweep at the next
        // tick rather than the next process restart. Initial Enabled=false still
        // allocates the timer so a later Enabled=true can take effect.
        var initial = _optsAccessor();
        if (!initial.Enabled)
        {
            _log.LogInformation("DeploymentLeakReaper disabled via configuration; skipping initial sweep");
            // Fall through to the per-tick loop so a later Enabled=true takes effect.
        }
        var interval = initial.CheckInterval < TimeSpan.FromMinutes(1)
            ? TimeSpan.FromMinutes(1)
            : initial.CheckInterval;
        using var timer = new PeriodicTimer(interval);
        do
        {
            var current = _optsAccessor();
            if (current.Enabled)
                await RunSweepAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task RunSweepAsync(CancellationToken ct)
    {
        var opts = _optsAccessor();
        if (!opts.Enabled)
        {
            _latestLeaks = [];
            return;
        }
        try
        {
            var managed = await _cleanupProvider.ListAllManagedAsync(ct).ConfigureAwait(false);
            var active = _manager.GetActive();
            var activeSubstrateIds = new HashSet<string>(
                active.Where(a => a.SubstrateId is not null).Select(a => a.SubstrateId!),
                StringComparer.Ordinal);
            var suspendedNames = _suspendedNameProvider is null
                ? (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal)
                : await _suspendedNameProvider(ct).ConfigureAwait(false);
            var now = _clock();
            var observedSuspendOrphans = new HashSet<string>(StringComparer.Ordinal);

            var leaks = new List<DeploymentLeakInfo>();
            foreach (var info in managed)
            {
                // Tracked-active: the current orchestrator process owns this
                // resource via a live phase or active deployment handle. Never
                // a leak.
                if (info.IsTrackedActive) continue;

                // Currently held by a deployment we know about — the manager's
                // active set is authoritative for the in-process case.
                if (activeSubstrateIds.Contains(info.Name)) continue;

                // Honour the work-item suspend index. The startup resume
                // handler reattaches these on the next orchestrator start;
                // purging them mid-restart strands the work item.
                if (suspendedNames.Contains(info.Name)) continue;

                DateTimeOffset createdAt;
                TimeSpan age;
                if (info.IsSuspendLifecycleOrFrozen)
                {
                    // The general SandboxLeakReaper no longer owns deployment
                    // sandboxes, so deployment suspend/frozen states need their
                    // own grace path here. Measure from first observation in
                    // suspend state, not VM creation time, to avoid purging a
                    // long-lived deployment that has only just begun freezing.
                    observedSuspendOrphans.Add(info.Name);
                    var firstSeen = _suspendOrphanFirstSeen.GetOrAdd(info.Name, now);
                    if (now - firstSeen < opts.SuspendOrphanGrace) continue;
                    createdAt = info.CreatedAt ?? firstSeen;
                    age = now - createdAt;
                }
                else
                {
                    // Missing creation metadata is itself an orphan signal.
                    // Remote providers cannot always recover CreatedAt, so
                    // treat an unknown timestamp as old enough to report and
                    // sweep, matching the general sandbox reaper's safety-net
                    // behavior.
                    createdAt = info.CreatedAt ?? now - opts.LeakAgeThreshold;
                    age = now - createdAt;

                    // Preempt-marked (graceful-shutdown-preserved) VMs are
                    // exempt for the longer PreemptRetention window. Once the
                    // operator's retention window elapses they're treated like
                    // any other leak.
                    if (info.HasPreemptMarker && age < opts.PreemptRetention) continue;

                    if (age < opts.LeakAgeThreshold) continue;
                }

                leaks.Add(new DeploymentLeakInfo(info.Name, createdAt, age, info.DiskBytes));
            }

            foreach (var name in _suspendOrphanFirstSeen.Keys)
                if (!observedSuspendOrphans.Contains(name))
                    _suspendOrphanFirstSeen.TryRemove(name, out _);

            _latestLeaks = leaks;

            if (leaks.Count == 0) return;
            _log.LogWarning("DeploymentLeakReaper: {Count} candidate orphan sandbox(es)", leaks.Count);

            if (!opts.AutoDispose) return;

            var failedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var leak in leaks)
            {
                ct.ThrowIfCancellationRequested();
                using var timeoutCts = new CancellationTokenSource(opts.DisposeTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                try
                {
                    await _cleanupProvider.DisposeLeakedAsync(leak.Name, linked.Token).ConfigureAwait(false);
                    _log.LogInformation(
                        "DeploymentLeakReaper: disposed orphan sandbox {Name} age={AgeMinutes:F1}min",
                        leak.Name, leak.Age.TotalMinutes);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedNames.Add(leak.Name);
                    _log.LogWarning(ex,
                        "DeploymentLeakReaper: failed to dispose orphan sandbox {Name}",
                        leak.Name);
                }
            }
            _latestLeaks = leaks.Where(leak => failedNames.Contains(leak.Name)).ToList();
        }
        // Only swallow OCEs that are NOT the outer shutdown-cancellation. Without
        // the `when` clause, the inner `catch (...) when (ct.IsCancellationRequested) { throw; }`
        // above is dead code because this outer handler re-catches the rethrown
        // exception. Narrowing here lets the inner rethrow propagate up to
        // ExecuteAsync (which expects OCE on shutdown) and still absorb any
        // transient per-leak timeout OCEs that escape the inner handler.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DeploymentLeakReaper: sweep failed");
        }
    }
}

public sealed record DeploymentLeakInfo(
    string Name,
    DateTimeOffset CreatedAt,
    TimeSpan Age,
    long? DiskBytes);

/// <summary>
/// Configuration for <see cref="DeploymentLeakReaper"/>. Bound from
/// <c>CodeyBox:DeploymentLeak</c>.
/// </summary>
public sealed class DeploymentLeakOptions
{
    /// <summary>Enable or disable the deployment leak reaper. Default true. Hot-reloadable.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often to run the leak scan. Minimum 1 minute. Default 15 minutes. Startup-only.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Minimum age before a non-active sandbox is declared a deployment leak. Default 30 minutes.</summary>
    public TimeSpan LeakAgeThreshold { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a sandbox with a graceful-shutdown preempt marker is preserved
    /// before being treated like a regular leak. Default 24 hours — matches
    /// <c>SandboxLeakOptions.PreemptRetention</c> so the two reapers do not
    /// disagree about when a preserved VM is fair game.
    /// </summary>
    public TimeSpan PreemptRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Dedicated grace before a deployment VM in a suspend lifecycle state is
    /// treated as an orphan. Measured from first observation by this process,
    /// not from VM creation time, so a deployment just entering Suspended is not
    /// purged mid-snapshot. Default matches the general sandbox reaper.
    /// </summary>
    public TimeSpan SuspendOrphanGrace { get; set; } =
        SuspendTimeoutPolicy.For(SandboxResourceLimits.Default.MemoryBytes);

    /// <summary>When true, automatically dispose detected orphans. Default true.</summary>
    public bool AutoDispose { get; set; } = true;

    /// <summary>Per-sandbox dispose timeout. Default 5 minutes.</summary>
    public TimeSpan DisposeTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
