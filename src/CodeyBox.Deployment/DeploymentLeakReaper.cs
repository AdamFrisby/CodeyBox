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
/// <para>Naming criterion: a sandbox is a deployment-leak candidate when its
/// id is NOT present in the manager's active deployment set AND its
/// <see cref="ManagedSandboxInfo.CreatedAt"/> indicates it has been around
/// for at least <see cref="DeploymentLeakOptions.LeakAgeThreshold"/>. This
/// shares the underlying <see cref="ISandboxProvider.ListAllManagedAsync"/>
/// surface with <c>SandboxLeakReaper</c>; running both is safe because each
/// sweep disposes only the sandboxes it identifies as orphans of its own
/// concern. The two reapers may converge on the same orphan when a deployment
/// crashed between sandbox creation and the manager's bookkeeping write —
/// either reaper disposing it is the correct outcome.</para>
/// </summary>
public sealed class DeploymentLeakReaper : BackgroundService
{
    private readonly ISandboxProvider _provider;
    private readonly IDeploymentManager _manager;
    private readonly Func<DeploymentLeakOptions> _optsAccessor;
    private readonly ILogger<DeploymentLeakReaper> _log;
    private readonly Func<DateTimeOffset> _clock;
    private volatile IReadOnlyList<DeploymentLeakInfo> _latestLeaks = [];

    public DeploymentLeakReaper(
        ISandboxProvider provider,
        IDeploymentManager manager,
        DeploymentLeakOptions opts,
        ILogger<DeploymentLeakReaper> log)
        : this(provider, manager, () => opts, log, clock: null) { }

    public DeploymentLeakReaper(
        ISandboxProvider provider,
        IDeploymentManager manager,
        Func<DeploymentLeakOptions> optionsAccessor,
        ILogger<DeploymentLeakReaper> log,
        Func<DateTimeOffset>? clock = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _optsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<DeploymentLeakInfo> GetLatestLeaks() => _latestLeaks;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _optsAccessor();
        if (!opts.Enabled)
        {
            _log.LogInformation("DeploymentLeakReaper disabled via configuration; skipping");
            return;
        }
        var interval = opts.CheckInterval < TimeSpan.FromMinutes(1)
            ? TimeSpan.FromMinutes(1)
            : opts.CheckInterval;
        using var timer = new PeriodicTimer(interval);
        do
        {
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
            var now = _clock();

            var leaks = new List<DeploymentLeakInfo>();
            foreach (var info in managed)
            {
                if (info.IsTrackedActive) continue;
                if (activeSandboxIds.Contains(info.Name)) continue;
                var createdAt = info.CreatedAt ?? now - opts.LeakAgeThreshold;
                var age = now - createdAt;
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
    /// <summary>Enable or disable the deployment leak reaper. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often to run the leak scan. Minimum 1 minute. Default 15 minutes.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Minimum age before a non-active sandbox is declared a deployment leak. Default 30 minutes.</summary>
    public TimeSpan LeakAgeThreshold { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>When true, automatically dispose detected orphans. Default true.</summary>
    public bool AutoDispose { get; set; } = true;

    /// <summary>Per-sandbox dispose timeout. Default 5 minutes.</summary>
    public TimeSpan DisposeTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
