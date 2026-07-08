using System.Collections.Concurrent;
using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

public sealed class AgentQuotaRecoveryProbeMonitor : BackgroundService
{
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
    private readonly AgentClassRouter? _router;

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
        AgentClassRouter? router = null)
    {
        _observations = observations;
        _publisher = publisher;
        _probesByKind = AgentQuotaProbeCatalog.BuildKindLookup(probes);
        _quotaGate = quotaGate;
        _options = options;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
        _store = store;
        _projects = projects;
        _router = router;

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

            if (!await HasEligibleParkedWorkAsync(member, ct).ConfigureAwait(false))
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

        _tracked[key] = tracked with
        {
            DeniedProbeCount = deniedCount,
            NextProbeAt = nextProbeAt,
            TrackUntil = trackUntil,
        };
    }

    private TimeSpan ResolveDeniedProbeBackoff(int deniedProbeCount)
    {
        var baseInterval = ResolveProbeInterval();
        var multiplier = 1 << Math.Min(Math.Max(deniedProbeCount - 1, 0), 6);
        var backoff = TimeSpan.FromTicks(baseInterval.Ticks * multiplier);
        var configuredRecheck = _options.QuotaRecheckInterval > TimeSpan.Zero
            ? _options.QuotaRecheckInterval
            : MaxDeniedProbeBackoff;
        var cap = configuredRecheck < MaxDeniedProbeBackoff
            ? configuredRecheck
            : MaxDeniedProbeBackoff;
        if (cap < baseInterval)
            cap = baseInterval;
        return backoff <= cap ? backoff : cap;
    }

    private DateTimeOffset ResolveTrackUntil(
        DateTimeOffset firstTrackedAt,
        DateTimeOffset nowUtc,
        DateTimeOffset? resetAt)
    {
        var maxLifetime = _options.RampWindow > TimeSpan.Zero
            ? _options.RampWindow
            : TimeSpan.FromDays(7);
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

    private async Task<bool> HasEligibleParkedWorkAsync(AgentMembership member, CancellationToken ct)
    {
        if (_store is null)
            return true;

        await foreach (var item in _store.ListByStateAsync(WorkItemState.WaitingForQuotaReset, ct))
        {
            var project = _projects is null
                ? null
                : await _projects.GetAsync(item.ProjectId, ct).ConfigureAwait(false);
            if (IsEligibleParkedWorkForMember(item, project, member))
                return true;
        }

        return false;
    }

    private bool IsEligibleParkedWorkForMember(WorkItem item, Project? project, AgentMembership member)
    {
        var requiredCapability = QuotaRetryWorkItem.RequiredCapabilityForRetry(item);
        if (DirectAgentMembership.IsDirectRoute(item, project))
            return DirectAgentMembership.TryCreate(item, project) is { } direct
                && DirectAgentMembership.SameQuotaBucket(direct, member);

        if (_router is null)
            return true;

        return _router.IsEligibleClassMemberWithCapability(item, project, member, requiredCapability);
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
