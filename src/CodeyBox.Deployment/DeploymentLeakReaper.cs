using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Deployment;

/// <summary>
/// Periodic background sweep that detects — and optionally disposes —
/// provider-managed sandboxes whose deployment owner no longer exists in
/// <see cref="IDeploymentManager.GetActive"/>. Follows the same pattern as
/// <c>SandboxLeakReaper</c>: enumerate the provider's managed sandboxes,
/// filter by an age threshold, and dispose the orphans.
///
/// <para>The reaper is the safety net for orchestrator restarts and aborted
/// deploy lifecycles. The happy path is already covered by
/// <see cref="IDeploymentHandle.DisposeAsync"/>; this reaper only acts when
/// the normal path was interrupted before disposal could run.</para>
///
/// <para>The provider's <see cref="ISandboxProvider.ListAllManagedAsync"/>
/// surface lists EVERY codeybox-* sandbox — work-item phase VMs, suspended
/// VMs, preempt-marked VMs, and deployment VMs alike — without a kind
/// discriminator. To avoid destroying VMs the work pipeline intentionally
/// preserved, this reaper honours the same skip-gates as
/// <c>SandboxLeakReaper</c>:</para>
/// <list type="bullet">
///   <item><b>HasPreemptMarker</b> — graceful-shutdown-preserved VMs are
///   exempt for <see cref="DeploymentLeakOptions.PreemptRetention"/>
///   (default 24h).</item>
///   <item><b>IsSuspendLifecycleOrFrozen</b> — suspended VMs (multipass
///   <c>Suspending</c>/<c>Suspended</c>) are exempt; they belong to the
///   Claude session worker's stop/resume contract.</item>
///   <item>Optional <c>suspendedNameProvider</c> — the composition root
///   wires this to the work-item store's SuspendedVmName index so the
///   startup resume handler can multipass-start them back to Running.</item>
/// </list>
/// <para>Running both reapers is safe because any sandbox this reaper
/// disposes is also a SandboxLeakReaper orphan (same preserve gates, same
/// LeakAgeThreshold floor). The two reapers converge on the same orphans;
/// either one disposing is the correct outcome.</para>
/// </summary>
public sealed class DeploymentLeakReaper : BackgroundService
{
    private readonly ISandboxProvider _provider;
    private readonly IDeploymentManager _manager;
    private readonly Func<DeploymentLeakOptions> _optsAccessor;
    private readonly ILogger<DeploymentLeakReaper> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<CancellationToken, Task<IReadOnlySet<string>>>? _suspendedNameProvider;
    private volatile IReadOnlyList<DeploymentLeakInfo> _latestLeaks = [];

    public DeploymentLeakReaper(
        ISandboxProvider provider,
        IDeploymentManager manager,
        Func<DeploymentLeakOptions> optionsAccessor,
        ILogger<DeploymentLeakReaper> log,
        Func<DateTimeOffset>? clock = null,
        Func<CancellationToken, Task<IReadOnlySet<string>>>? suspendedNameProvider = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
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
        // short-circuits before allocating the timer.
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
        try
        {
            var managed = await _provider.ListAllManagedAsync(ct).ConfigureAwait(false);
            var active = _manager.GetActive();
            var activeSandboxIds = new HashSet<string>(
                active.Where(a => a.SandboxId is not null).Select(a => a.SandboxId!),
                StringComparer.Ordinal);
            var suspendedNames = _suspendedNameProvider is null
                ? (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal)
                : await _suspendedNameProvider(ct).ConfigureAwait(false);
            var now = _clock();

            var leaks = new List<DeploymentLeakInfo>();
            foreach (var info in managed)
            {
                // Tracked-active: the current orchestrator process owns this
                // sandbox via a live phase or active deployment handle. Never
                // a leak.
                if (info.IsTrackedActive) continue;

                // Currently held by a deployment we know about — the manager's
                // active set is authoritative for the in-process case.
                if (activeSandboxIds.Contains(info.Name)) continue;

                // Honour the work-item suspend index. The startup resume
                // handler reattaches these on the next orchestrator start;
                // purging them mid-restart strands the work item.
                if (suspendedNames.Contains(info.Name)) continue;

                // A VM in a suspend lifecycle state (freezing/frozen) without
                // a live mapping belongs to the Claude session worker's
                // stop/resume contract or is mid-snapshot. SandboxLeakReaper
                // applies a dedicated SuspendOrphanGrace here; we conservatively
                // skip them entirely so we never race the sibling reaper's
                // grace window — if they're true suspend orphans, the sibling
                // will dispose them under its dedicated gate.
                if (info.IsSuspendLifecycleOrFrozen) continue;

                // Unknown CreatedAt is conservative: we treat it as "too young
                // to know" and skip rather than risk reaping a sandbox whose
                // staging metadata is just temporarily missing.
                if (info.CreatedAt is not { } createdAt) continue;
                var age = now - createdAt;

                // Preempt-marked (graceful-shutdown-preserved) VMs are exempt
                // for the longer PreemptRetention window. Once the operator's
                // retention window elapses they're treated like any other leak.
                if (info.HasPreemptMarker && age < opts.PreemptRetention) continue;

                if (age < opts.LeakAgeThreshold) continue;

                leaks.Add(new DeploymentLeakInfo(info.Name, createdAt, age, info.DiskBytes));
            }
            _latestLeaks = leaks;

            if (leaks.Count == 0) return;
            _log.LogWarning("DeploymentLeakReaper: {Count} candidate orphan sandbox(es)", leaks.Count);

            if (!opts.AutoDispose) return;

            foreach (var leak in leaks)
            {
                ct.ThrowIfCancellationRequested();
                using var timeoutCts = new CancellationTokenSource(opts.DisposeTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                try
                {
                    await _provider.DisposeLeakedAsync(leak.Name, linked.Token).ConfigureAwait(false);
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
                    _log.LogWarning(ex,
                        "DeploymentLeakReaper: failed to dispose orphan sandbox {Name}",
                        leak.Name);
                }
            }
        }
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

    /// <summary>When true, automatically dispose detected orphans. Default true.</summary>
    public bool AutoDispose { get; set; } = true;

    /// <summary>Per-sandbox dispose timeout. Default 5 minutes.</summary>
    public TimeSpan DisposeTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
