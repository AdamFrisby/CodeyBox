using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// B1: reference-counted garbage collector for content-hashed sandbox baseline
/// images. Periodically:
/// <list type="number">
///   <item>asks the work-item store for the live set of <see cref="WorkItem.BaselineImageRef"/>
///   values held by non-terminal items;</item>
///   <item>asks the sandbox provider for the host's current baseline images;</item>
///   <item>diff'd: any baseline on the host that is NOT in the live set is a
///   candidate for deletion;</item>
///   <item>each candidate is held for the <see cref="BaselineImageReaperOptions.GraceWindow"/>
///   before being purged. The reaper tracks <em>first observed orphan</em> per
///   name in memory so a brand-new baseline that has not yet been picked up by
///   any work item is not reaped on the same sweep it was baked.</item>
/// </list>
///
/// <para><b>Why this matters:</b> the B1 content-hash scheme produces a fresh
/// baseline name every time the operator edits MultipassExtraRuncmd /
/// ExtraCloudInit / the underlying cloud-init contents. Without GC the host
/// accumulates one stranded multi-GB VM per edit.</para>
///
/// <para><b>Why a grace window:</b> the reaper races the pickup-time stamp.
/// A work item picked up during the sweep transitions from
/// <c>BaselineImageRef = null</c> to non-null; if the reaper saw "no
/// references" between the bake and the stamp it must not delete the brand-
/// new baseline. The grace window (default 24h) is generous because baseline
/// disk pressure is the operator's existing pain point — the reaper is
/// conservative on purpose.</para>
///
/// <para><b>Capability gate:</b> only active when the registered sandbox
/// provider implements <see cref="IBaselineImageResolver"/>. The hosted
/// service registration must not throw when the provider lacks the
/// capability — the constructor accepts a nullable resolver and a null
/// resolver short-circuits <see cref="ExecuteAsync"/> with an info log.</para>
/// </summary>
public sealed class BaselineImageReaper : BackgroundService
{
    private readonly IBaselineImageResolver _resolver;
    private readonly IWorkItemStore _store;
    private readonly Func<BaselineImageReaperOptions> _optsAccessor;
    private readonly ILogger<BaselineImageReaper> _log;
    private readonly TimeProvider _time;

    // First time we observed each candidate orphan name on a sweep. Used to
    // apply the grace window: a baseline becomes eligible for deletion only
    // when (now - firstObservedAt) >= GraceWindow. Cleared when the name
    // shows up in the live-ref set again (e.g. a new work item pinned to it)
    // or when the name is successfully reaped.
    private readonly Dictionary<string, DateTimeOffset> _firstObservedOrphan = new(StringComparer.Ordinal);
    private readonly object _firstObservedGuard = new();

    // Latest sweep result, exposed to operators via the /baselines endpoint
    // so they can audit which baselines are live, orphaned-but-in-grace, or
    // about to be reaped on the next sweep. Replaced atomically per sweep.
    private volatile IReadOnlyList<BaselineImageReportEntry> _latestReport = [];

    public BaselineImageReaper(
        IBaselineImageResolver resolver,
        IWorkItemStore store,
        BaselineImageReaperOptions opts,
        ILogger<BaselineImageReaper> log,
        TimeProvider? time = null)
        : this(resolver, store, () => opts, log, time) { }

    public BaselineImageReaper(
        IBaselineImageResolver resolver,
        IWorkItemStore store,
        Func<BaselineImageReaperOptions> optsAccessor,
        ILogger<BaselineImageReaper> log,
        TimeProvider? time = null)
    {
        _resolver = resolver ?? NullBaselineImageResolver.Instance;
        _store = store;
        _optsAccessor = optsAccessor;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Latest sweep snapshot — what the most recent
    /// <see cref="RunSweepAsync"/> observed and decided. Consumed by the
    /// <c>/baselines</c> operator endpoint.
    /// </summary>
    public IReadOnlyList<BaselineImageReportEntry> GetLatestReport() => _latestReport;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _optsAccessor();
        if (!opts.Enabled)
        {
            _log.LogInformation("BaselineImageReaper disabled via configuration; skipping");
            return;
        }
        if (_resolver is NullBaselineImageResolver)
        {
            _log.LogInformation(
                "BaselineImageReaper: registered sandbox provider does not implement IBaselineImageResolver; skipping");
            return;
        }

        // Clamp to a 15-minute floor: more frequent than that would just hammer
        // multipass list without giving the grace window time to apply.
        var interval = opts.CheckInterval < TimeSpan.FromMinutes(15)
            ? TimeSpan.FromMinutes(15)
            : opts.CheckInterval;
        if (opts.CheckInterval < TimeSpan.FromMinutes(15))
            _log.LogWarning(
                "BaselineImageReaper: CheckInterval {Configured} is below the 15-minute minimum; clamped",
                opts.CheckInterval);

        using var timer = new PeriodicTimer(interval);
        do
        {
            await RunSweepAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunSweepAsync(CancellationToken ct)
    {
        var opts = _optsAccessor();
        try
        {
            var live = await _store.GetActiveBaselineImageRefsAsync(ct);
            var onHost = await _resolver.ListBaselineImagesAsync(ct);
            var now = _time.GetUtcNow();
            var graceWindow = opts.GraceWindow < TimeSpan.Zero ? TimeSpan.Zero : opts.GraceWindow;

            var report = new List<BaselineImageReportEntry>(onHost.Count);
            var toReap = new List<string>();
            var observedNamesThisSweep = new HashSet<string>(StringComparer.Ordinal);

            foreach (var image in onHost)
            {
                observedNamesThisSweep.Add(image.Name);
                if (live.Contains(image.Name))
                {
                    // Live reference: clear any first-observed entry so a
                    // baseline that becomes orphaned later starts a fresh
                    // grace window.
                    lock (_firstObservedGuard) { _firstObservedOrphan.Remove(image.Name); }
                    report.Add(new BaselineImageReportEntry(image.Name, IsLive: true, FirstObservedOrphanAt: null, AgeInGrace: null));
                    continue;
                }

                // Orphan candidate — apply / refresh the first-observed clock.
                DateTimeOffset firstObservedAt;
                lock (_firstObservedGuard)
                {
                    if (!_firstObservedOrphan.TryGetValue(image.Name, out firstObservedAt))
                    {
                        firstObservedAt = now;
                        _firstObservedOrphan[image.Name] = firstObservedAt;
                    }
                }

                var ageInGrace = now - firstObservedAt;
                report.Add(new BaselineImageReportEntry(image.Name, IsLive: false, FirstObservedOrphanAt: firstObservedAt, AgeInGrace: ageInGrace));

                if (ageInGrace >= graceWindow)
                    toReap.Add(image.Name);
            }

            // Forget any tracked orphans that disappeared from the host between
            // sweeps (e.g. operator-purged manually) so the dictionary doesn't
            // grow unbounded over a long uptime.
            lock (_firstObservedGuard)
            {
                var stale = _firstObservedOrphan.Keys.Where(k => !observedNamesThisSweep.Contains(k)).ToList();
                foreach (var k in stale) _firstObservedOrphan.Remove(k);
            }

            _latestReport = report;

            if (toReap.Count == 0)
            {
                _log.LogDebug(
                    "BaselineImageReaper: sweep complete — {Live} live, {Orphan} in grace, 0 to reap",
                    report.Count(r => r.IsLive),
                    report.Count(r => !r.IsLive));
                return;
            }

            foreach (var name in toReap)
            {
                try
                {
                    await _resolver.DisposeBaselineImageAsync(name, ct);
                    lock (_firstObservedGuard) { _firstObservedOrphan.Remove(name); }
                    _log.LogInformation(
                        "BaselineImageReaper: reaped orphaned baseline {Name} (grace window {Grace} elapsed)",
                        name, graceWindow);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Best-effort: a single failure does not block the rest of the batch.
                    _log.LogWarning(ex, "BaselineImageReaper: failed to reap baseline {Name}", name);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "BaselineImageReaper: sweep failed");
        }
    }
}

/// <summary>
/// One row of <see cref="BaselineImageReaper.GetLatestReport"/>.
/// <see cref="IsLive"/> is true when at least one non-terminal work item
/// holds <see cref="WorkItem.BaselineImageRef"/> equal to <see cref="Name"/>.
/// <see cref="FirstObservedOrphanAt"/> is the timestamp the reaper first saw
/// the baseline as an orphan (null for live entries); <see cref="AgeInGrace"/>
/// is how long it has been in the grace window since first observation.
/// </summary>
public sealed record BaselineImageReportEntry(
    string Name,
    bool IsLive,
    DateTimeOffset? FirstObservedOrphanAt,
    TimeSpan? AgeInGrace);

/// <summary>
/// Configuration for <see cref="BaselineImageReaper"/>. Bound from
/// <c>CodeyBox:BaselineImageReaper</c>.
/// </summary>
public sealed class BaselineImageReaperOptions
{
    /// <summary>Enable or disable the reaper. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Sweep cadence. Default 6 hours. Floor 15 minutes — sweeping more
    /// frequently than that just hammers multipass list without giving the
    /// grace window a chance to apply.
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How long an orphan must remain orphaned before the reaper deletes it.
    /// Default 24 hours. Defends against a sweep that runs in the gap between
    /// "baseline baked" and "first work item pickup stamps a ref pointing at
    /// it" — without the window we would purge brand-new baselines.
    /// </summary>
    public TimeSpan GraceWindow { get; set; } = TimeSpan.FromHours(24);
}
