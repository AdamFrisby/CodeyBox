using System.Collections.Concurrent;
using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

public sealed class AgentQuotaRecoveryProbeMonitor : BackgroundService
{
    private const int MaxDeniedProbeBackoffExponent = 6;
    private static readonly TimeSpan MaxDeniedProbeBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultNoResetTrackingTtl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<AgentQuotaMemberKey, TrackedRecoveryProbe> _tracked = new();
    private readonly IAgentQuotaAvailabilityObservationSource _observations;
    private readonly IAgentQuotaAvailabilityPublisher _publisher;
    private readonly IReadOnlyDictionary<AgentKind, IAgentQuotaProbe> _probesByKind;
    private readonly IAgentQuotaGate _quotaGate;
    private readonly QuotaRouterOptions _options;
    private readonly ILogger<AgentQuotaRecoveryProbeMonitor> _log;
    private readonly TimeProvider _time;
    private readonly IWorkItemStore? _store;
    private readonly IProjectRepository? _projects;
    private readonly IQuotaRetryAdmissionRouter? _admissionRouter;

    public AgentQuotaRecoveryProbeMonitor(
        IAgentQuotaAvailabilityObservationSource observations,
        IAgentQuotaAvailabilityPublisher publisher,
        IEnumerable<IAgentQuotaProbe> probes,
        IAgentQuotaGate quotaGate,
        QuotaRouterOptions options,
        ILogger<AgentQuotaRecoveryProbeMonitor> log,
        TimeProvider? timeProvider = null,
        IWorkItemStore? store = null,
        IProjectRepository? projects = null,
        IQuotaRetryAdmissionRouter? admissionRouter = null)
    {
        _observations = observations;
        _publisher = publisher;
        _probesByKind = AgentQuotaProbeCatalog.BuildSubscriptionProbeKindLookup(probes);
        _quotaGate = quotaGate;
        _options = options;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
        _store = store;
        _projects = projects;
        _admissionRouter = admissionRouter;

        _observations.QuotaUsabilityObserved += OnQuotaUsabilityObserved;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProbeTrackedMembersOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(ResolveProbeInterval(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Quota recovery probe monitor iteration failed; continuing");
                await Task.Delay(ResolveProbeInterval(), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task<int> ProbeTrackedMembersOnceAsync(CancellationToken ct = default)
    {
        if (_tracked.IsEmpty)
            return 0;

        var eligibleParkedWorkBuckets = await BuildEligibleParkedWorkBucketsAsync(ct).ConfigureAwait(false);
        var recovered = 0;
        foreach (var (key, tracked) in _tracked.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var member = tracked.Member;
            var nowUtc = _time.GetUtcNow();

            if (tracked.TrackUntil <= nowUtc)
            {
                _tracked.TryRemove(key, out _);
                continue;
            }

            if (tracked.NextProbeAt > nowUtc)
                continue;

            if (member.Billing != AgentBilling.Subscription
                || !_probesByKind.TryGetValue(member.Agent, out var probe))
            {
                _tracked.TryRemove(key, out _);
                continue;
            }

            if (eligibleParkedWorkBuckets is not null
                && !eligibleParkedWorkBuckets.Contains(QuotaRetryAdmissionPoolKey.FromMembership(member)))
            {
                _tracked.TryRemove(key, out _);
                continue;
            }

            AgentQuotaSnapshot snapshot;
            try
            {
                if (probe is IAgentQuotaRecoveryStateInvalidator recoveryInvalidator)
                    recoveryInvalidator.InvalidateRecoveryState(member);
                else if (probe is IAgentQuotaCacheInvalidator invalidator)
                    invalidator.InvalidateResponseCache();

                snapshot = await probe.GetAvailabilityAsync(member, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogDebug(
                    ex,
                    "Quota recovery probe for {Agent}/{Model} failed; member remains tracked",
                    member.Agent.Value,
                    member.ModelId ?? "(default)");
                continue;
            }

            if (!await _quotaGate.AllowsAsync(member, snapshot, nowUtc, ct).ConfigureAwait(false))
            {
                var resetAt = ResolveResetHint(snapshot, member);
                _publisher.RecordQuotaUsability(
                    member,
                    isUsable: false,
                    publishRecoverySignal: false,
                    resetAt);
                TrackDeniedProbe(key, tracked, resetAt, nowUtc);
                continue;
            }

            var published = _publisher.RecordQuotaUsability(
                member,
                isUsable: true,
                publishRecoverySignal: true);
            if (!published)
                continue;

            _tracked.TryRemove(key, out _);
            recovered++;
        }

        return recovered;
    }

    private void OnQuotaUsabilityObserved(AgentQuotaUsabilityObservation observation)
    {
        if (!observation.PublishRecoverySignal)
            return;

        var key = AgentQuotaMemberKey.From(observation.Member);
        if (observation.IsUsable)
        {
            _tracked.TryRemove(key, out _);
        }
        else
        {
            var nowUtc = _time.GetUtcNow();
            _tracked.AddOrUpdate(
                key,
                _ => TrackedRecoveryProbe.Create(observation.Member, nowUtc, ResolveTrackUntil(nowUtc, nowUtc, observation.ResetAt)),
                (_, existing) => existing with
                {
                    Member = observation.Member,
                    TrackUntil = observation.ResetAt is null && existing.TrackUntil > nowUtc
                        ? existing.TrackUntil
                        : ResolveTrackUntil(existing.FirstTrackedAt, nowUtc, observation.ResetAt),
                    NextProbeAt = existing.NextProbeAt <= nowUtc ? existing.NextProbeAt : nowUtc,
                });
        }
    }

    private TimeSpan ResolveProbeInterval()
    {
        var configured = _options.QuotaRecoveryProbeInterval;
        if (configured <= TimeSpan.Zero)
            return QuotaRouterDefaults.DefaultQuotaRecoveryProbeInterval;

        return configured;
    }

    private void TrackDeniedProbe(
        AgentQuotaMemberKey key,
        TrackedRecoveryProbe tracked,
        DateTimeOffset? resetAt,
        DateTimeOffset nowUtc)
    {
        var deniedCount = tracked.DeniedProbeCount + 1;
        var nextProbeAt = nowUtc + ResolveDeniedProbeBackoff(deniedCount);
        var trackUntil = resetAt is null && tracked.TrackUntil > nowUtc
            ? tracked.TrackUntil
            : ResolveTrackUntil(tracked.FirstTrackedAt, nowUtc, resetAt);
        if (nextProbeAt > trackUntil)
            nextProbeAt = trackUntil;

        var updated = tracked with
        {
            DeniedProbeCount = deniedCount,
            NextProbeAt = nextProbeAt,
            TrackUntil = trackUntil,
        };
        _tracked.TryUpdate(key, updated, tracked);
    }

    private TimeSpan ResolveDeniedProbeBackoff(int deniedProbeCount)
    {
        var baseInterval = ResolveProbeInterval();
        var configuredRecheck = _options.QuotaRecheckInterval > TimeSpan.Zero
            ? _options.QuotaRecheckInterval
            : MaxDeniedProbeBackoff;
        var cap = configuredRecheck < MaxDeniedProbeBackoff
            ? configuredRecheck
            : MaxDeniedProbeBackoff;
        if (cap < baseInterval)
            cap = baseInterval;
        var multiplier = 1L << Math.Min(Math.Max(deniedProbeCount - 1, 0), MaxDeniedProbeBackoffExponent);
        var backoff = MultiplyAndClamp(baseInterval, multiplier, cap);
        return backoff <= cap ? backoff : cap;
    }

    private static TimeSpan MultiplyAndClamp(TimeSpan interval, long multiplier, TimeSpan cap)
    {
        if (multiplier <= 1)
            return interval <= cap ? interval : cap;

        if (interval.Ticks >= cap.Ticks / multiplier)
            return cap;

        return TimeSpan.FromTicks(interval.Ticks * multiplier);
    }

    private DateTimeOffset ResolveTrackUntil(
        DateTimeOffset firstTrackedAt,
        DateTimeOffset nowUtc,
        DateTimeOffset? resetAt)
    {
        var maxLifetime = _options.RampWindow > TimeSpan.Zero
            ? _options.RampWindow
            : QuotaRouterDefaults.DefaultRampWindow;
        var maxUntil = firstTrackedAt + maxLifetime;

        if (resetAt is { } reset && reset > nowUtc)
            return Min(reset, maxUntil);

        var noResetTtl = _options.ObservedFailureRetention > TimeSpan.Zero
            ? _options.ObservedFailureRetention
            : DefaultNoResetTrackingTtl;
        return Min(firstTrackedAt + noResetTtl, maxUntil);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private static DateTimeOffset? ResolveResetHint(AgentQuotaSnapshot snapshot, AgentMembership member)
    {
        var quota = QuotaGatePolicy.ResolveMemberQuota(snapshot, member);
        return quota.ResetAt ?? snapshot.ResetAt;
    }

    private async Task<IReadOnlySet<QuotaRetryAdmissionPoolKey>?> BuildEligibleParkedWorkBucketsAsync(CancellationToken ct)
    {
        if (_store is null)
            return null;

        var buckets = new HashSet<QuotaRetryAdmissionPoolKey>();
        var projects = new Dictionary<ProjectId, Project?>();
        var scanLimit = ResolveEligibilityScanLimit();
        var scanned = 0;
        await foreach (var item in _store.ListWaitingForQuotaResetByPriorityAsync(scanLimit, ct: ct))
        {
            scanned++;
            var project = await GetProjectForEligibilityAsync(item.ProjectId, projects, ct).ConfigureAwait(false);
            if (!TryAddEligibleParkedWorkBuckets(item, project, buckets))
                return null;
        }

        return scanned < scanLimit ? buckets : null;
    }

    private async Task<Project?> GetProjectForEligibilityAsync(
        ProjectId projectId,
        Dictionary<ProjectId, Project?> projects,
        CancellationToken ct)
    {
        if (_projects is null)
            return null;

        if (projects.TryGetValue(projectId, out var cached))
            return cached;

        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        projects[projectId] = project;
        return project;
    }

    private bool TryAddEligibleParkedWorkBuckets(
        WorkItem item,
        Project? project,
        HashSet<QuotaRetryAdmissionPoolKey> buckets)
    {
        var requiredCapability = QuotaRetryPhasePolicy.RequiredCapabilityForQuotaRetryCandidate(item);
        if (DirectAgentMembership.IsDirectRoute(item, project))
        {
            if (DirectAgentMembership.TryCreate(item, project) is { } direct)
                buckets.Add(QuotaRetryAdmissionPoolKey.FromMembership(direct));
            return true;
        }

        if (_admissionRouter is null)
            return false;

        foreach (var poolKey in _admissionRouter.GetQuotaRetryAdmissionPool(item, project, requiredCapability))
            buckets.Add(poolKey);
        return true;
    }

    private int ResolveEligibilityScanLimit()
    {
        var configured = _options.MaxQuotaRecoveryProbeEligibilityScan;
        return configured > 0
            ? configured
            : QuotaRouterDefaults.DefaultQuotaRecoveryProbeEligibilityScanLimit;
    }

    public override void Dispose()
    {
        _observations.QuotaUsabilityObserved -= OnQuotaUsabilityObserved;
        base.Dispose();
    }

    private sealed record TrackedRecoveryProbe(
        AgentMembership Member,
        DateTimeOffset FirstTrackedAt,
        DateTimeOffset TrackUntil,
        DateTimeOffset NextProbeAt,
        int DeniedProbeCount)
    {
        public static TrackedRecoveryProbe Create(
            AgentMembership member,
            DateTimeOffset nowUtc,
            DateTimeOffset trackUntil) =>
            new(member, nowUtc, trackUntil, nowUtc, DeniedProbeCount: 0);
    }
}
