using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Periodic background sweep that detects — and optionally disposes — Multipass
/// VMs (or other managed sandboxes) that outlived their work item. These "leaked"
/// sandboxes accumulate when the orchestrator crashes mid-disposal, slowly eating
/// disk and memory on the host.
///
/// <para><b>What counts as a leak:</b> a sandbox whose name starts with
/// <c>codeybox-*</c>, that is not in the current process's in-memory active set
/// (meaning the current orchestrator did not create it, or the creating instance
/// crashed before calling DisposeAsync), and whose creation timestamp is older
/// than <see cref="SandboxLeakOptions.LeakAgeThreshold"/>. The age threshold
/// guards against mistaking a sandbox that is mid-way through work-phase clone
/// — typically less than 30 minutes — for a genuine leak.</para>
///
/// <para><b>Auto-dispose:</b> off by default. Operators should review leaks
/// before enabling. When <see cref="SandboxLeakOptions.AutoDispose"/> is true,
/// each leaked sandbox is purged with a per-sandbox timeout; one failure never
/// blocks the rest of the sweep.</para>
///
/// <para>Detection-only results (when auto-dispose is off) are still emitted as
/// audit-tier <c>sandbox.leak_detected</c> events and returned by
/// <c>GET /sandboxes/leaked</c> so the operator can act.</para>
/// </summary>
public sealed class SandboxLeakReaper : BackgroundService
{
    private readonly ISandboxProvider _provider;
    private readonly IWebhookDispatcher _webhooks;
    private readonly SandboxLeakOptions _opts;
    private readonly ILogger<SandboxLeakReaper> _log;

    // Latest detected leaks, snapshotted after each sweep. Thread-safe via
    // Interlocked-style replace; the API endpoint reads this without locking.
    private volatile IReadOnlyList<LeakedSandboxInfo> _latestLeaks = [];

    public SandboxLeakReaper(
        ISandboxProvider provider,
        IWebhookDispatcher webhooks,
        SandboxLeakOptions opts,
        ILogger<SandboxLeakReaper> log)
    {
        _provider = provider;
        _webhooks = webhooks;
        _opts = opts;
        _log = log;
    }

    /// <summary>Returns the most recently detected leaked sandboxes.</summary>
    public IReadOnlyList<LeakedSandboxInfo> GetLatestLeaks() => _latestLeaks;

    /// <summary>
    /// Removes a single entry from the latest leak list. Called by the operator-dispose
    /// endpoint immediately after a successful disposal so that a second POST call for
    /// the same name returns 404 rather than attempting a redundant multipass delete.
    /// </summary>
    public void RemoveFromLatestLeaks(string name)
    {
        _latestLeaks = _latestLeaks.Where(l => l.Name != name).ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("SandboxLeakReaper disabled via configuration; skipping");
            return;
        }

        // Guard against misconfigured intervals that would cause ArgumentOutOfRangeException
        // (TimeSpan.Zero) or a tight multipass-hammering loop (very small values).
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
            var now = DateTimeOffset.UtcNow;

            var leaks = new List<LeakedSandboxInfo>();
            foreach (var info in allManaged)
            {
                if (info.IsTrackedActive)
                    continue;

                // No creation timestamp → can't determine age → skip (conservative).
                if (!info.CreatedAt.HasValue)
                    continue;

                var age = now - info.CreatedAt.Value;
                if (age < _opts.LeakAgeThreshold)
                    continue;

                var diskMb = info.DiskBytes.HasValue ? info.DiskBytes.Value / (1024 * 1024) : (long?)null;
                leaks.Add(new LeakedSandboxInfo(info.Name, info.CreatedAt.Value, age, info.DiskBytes));
                AuditLog.SandboxLeakDetected(info.Name, age.TotalMinutes, diskMb);
                _ = _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "sandbox.leak_detected",
                    Details = new SandboxLeakDetails
                    {
                        Name = info.Name,
                        AgeMinutes = Math.Round(age.TotalMinutes, 1),
                        DiskMb = diskMb,
                    },
                }, ct);
            }

            _latestLeaks = leaks;

            if (leaks.Count == 0) return;

            _log.LogWarning("SandboxLeakReaper: {Count} leaked sandbox(es) detected", leaks.Count);

            if (!_opts.AutoDispose)
            {
                _log.LogWarning(
                    "SandboxLeakReaper: AutoDispose=false — review leaked sandboxes via GET /sandboxes/leaked and dispose manually via POST /sandboxes/leaked/{{name}}/dispose");
                return;
            }

            // Dispose all leaks concurrently, each with an independent timeout.
            // A single failed disposal must never block the rest of the batch.
            var disposeTasks = leaks.Select(leak => DisposeSingleAsync(leak, ct));
            await Task.WhenAll(disposeTasks);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SandboxLeakReaper: sweep failed");
        }
    }

    private async Task DisposeSingleAsync(LeakedSandboxInfo leak, CancellationToken stoppingToken)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

        var diskMb = leak.DiskBytes.HasValue ? leak.DiskBytes.Value / (1024 * 1024) : (long?)null;
        try
        {
            await _provider.DisposeLeakedAsync(leak.Name, linkedCts.Token);
            var disposedAt = DateTimeOffset.UtcNow;
            AuditLog.SandboxLeakDisposed(leak.Name, leak.Age.TotalMinutes, diskMb, disposedAt);
            _ = _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "sandbox.leak_disposed",
                Details = new SandboxLeakDetails
                {
                    Name = leak.Name,
                    AgeMinutes = Math.Round(leak.Age.TotalMinutes, 1),
                    DiskMb = diskMb,
                    DisposedAt = disposedAt,
                },
            }, stoppingToken);
            _log.LogInformation("SandboxLeakReaper: disposed leaked sandbox {Name}", leak.Name);
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
            AuditLog.SandboxLeakDisposeFailed(leak.Name, leak.Age.TotalMinutes, diskMb, "timeout");
            _ = _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "sandbox.leak_dispose_failed",
                Details = new SandboxLeakDetails
                {
                    Name = leak.Name,
                    AgeMinutes = Math.Round(leak.Age.TotalMinutes, 1),
                    DiskMb = diskMb,
                    Error = "timeout",
                },
            }, stoppingToken);
            _log.LogWarning("SandboxLeakReaper: timed out disposing leaked sandbox {Name}", leak.Name);
        }
        catch (Exception ex)
        {
            AuditLog.SandboxLeakDisposeFailed(leak.Name, leak.Age.TotalMinutes, diskMb, ex.Message);
            _ = _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "sandbox.leak_dispose_failed",
                Details = new SandboxLeakDetails
                {
                    Name = leak.Name,
                    AgeMinutes = Math.Round(leak.Age.TotalMinutes, 1),
                    DiskMb = diskMb,
                    Error = ex.Message,
                },
            }, stoppingToken);
            _log.LogWarning(ex, "SandboxLeakReaper: failed to dispose leaked sandbox {Name}", leak.Name);
        }
    }
}

/// <summary>A leaked sandbox detected by <see cref="SandboxLeakReaper"/>.</summary>
public sealed record LeakedSandboxInfo(
    string Name,
    DateTimeOffset CreatedAt,
    TimeSpan Age,
    long? DiskBytes);

/// <summary>
/// Configuration for <see cref="SandboxLeakReaper"/>. Bound from
/// <c>CodeyBox:SandboxLeak</c>.
/// </summary>
public sealed class SandboxLeakOptions
{
    /// <summary>Enable or disable the leak reaper. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often to run the leak scan. Default 15 minutes.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Minimum age before a non-active sandbox is declared leaked.
    /// Default 30 minutes — conservative enough to not mistake a sandbox
    /// that is mid-way through work-phase clone.
    /// </summary>
    public TimeSpan LeakAgeThreshold { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// When true, automatically dispose each detected leak after logging it.
    /// Default false — start with detection only so operators can review
    /// before enabling automatic cleanup.
    /// </summary>
    public bool AutoDispose { get; set; } = false;
}
