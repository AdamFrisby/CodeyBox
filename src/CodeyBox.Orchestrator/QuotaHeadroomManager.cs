using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public interface IQuotaHeadroomManager
{
    Task<QuotaHeadroomGateResult> EvaluateAsync(
        QuotaHeadroomGateRequest request,
        CancellationToken ct = default);

    Task<QuotaHeadroomGateResult> TryReserveAsync(
        QuotaHeadroomGateRequest request,
        CancellationToken ct = default);

    double GetReservedHeadroomPct(ProjectId projectId, AgentKind agent);
}

public sealed record QuotaHeadroomGateRequest(
    ProjectId ProjectId,
    AgentMembership Member,
    double AvailablePct,
    DateTimeOffset? ResetAt,
    bool AuditOnRefusal = true,
    double? MinRemainingPct = null);

public sealed record QuotaHeadroomGateResult(
    bool Allow,
    string Reason,
    DateTimeOffset? RetryAt,
    bool InsufficientHeadroom = false,
    IQuotaReservationLease? Reservation = null,
    QuotaHeadroomEstimate? Estimate = null,
    double ReservedPct = 0,
    double? ProjectedAvailablePct = null);

public interface IQuotaReservationLease
{
    ProjectId ProjectId { get; }
    AgentKind Agent { get; }
    string? ModelId { get; }
    double ReservedPct { get; }

    /// <summary>
    /// Releases this reservation. Pass <paramref name="quotaMayHaveBeenConsumed"/>
    /// as true after an agent invocation reached the provider, so the lease can
    /// refresh provider quota or delay release when the snapshot could be stale.
    /// </summary>
    Task ReleaseAsync(bool quotaMayHaveBeenConsumed, CancellationToken ct = default);
}

public sealed class InProcessQuotaHeadroomManager : IQuotaHeadroomManager
{
    private readonly IQuotaHeadroomEstimator? _headroomEstimator;
    private readonly IReadOnlyDictionary<AgentKind, IAgentQuotaProbe> _probesByKind;
    private readonly QuotaRouterOptions _opts;
    private readonly ILogger<InProcessQuotaHeadroomManager> _log;
    private readonly ConcurrentDictionary<QuotaReservationKey, double> _reservedHeadroomPct = new();
    private readonly ConcurrentDictionary<QuotaReservationKey, object> _reservationLocks = new();
    private const double ReservationReleaseEpsilonPct = 0.000_001;

    public InProcessQuotaHeadroomManager(
        IQuotaHeadroomEstimator? headroomEstimator,
        IEnumerable<IAgentQuotaProbe> probes,
        QuotaRouterOptions opts,
        ILogger<InProcessQuotaHeadroomManager>? log = null)
    {
        _headroomEstimator = headroomEstimator;
        _probesByKind = probes
            .Where(p => p.SupportsHeadroom)
            .ToDictionary(p => p.Kind);
        _opts = opts;
        _log = log ?? NullLogger<InProcessQuotaHeadroomManager>.Instance;
    }

    public Task<QuotaHeadroomGateResult> EvaluateAsync(
        QuotaHeadroomGateRequest request,
        CancellationToken ct = default) =>
        EvaluateAsync(request, reserve: false, ct);

    public Task<QuotaHeadroomGateResult> TryReserveAsync(
        QuotaHeadroomGateRequest request,
        CancellationToken ct = default) =>
        EvaluateAsync(request, reserve: true, ct);

    public double GetReservedHeadroomPct(ProjectId projectId, AgentKind agent) =>
        GetReservedHeadroomPct(new QuotaReservationKey(projectId, agent));

    private async Task<QuotaHeadroomGateResult> EvaluateAsync(
        QuotaHeadroomGateRequest request,
        bool reserve,
        CancellationToken ct)
    {
        var member = request.Member;
        if (member.Billing == AgentBilling.PayPerApi)
            return new QuotaHeadroomGateResult(true, "pay-per-api member is never quota-gated", request.ResetAt);

        var key = new QuotaReservationKey(request.ProjectId, member.Agent);
        var reservedPct = GetReservedHeadroomPct(key);
        if (request.AvailablePct < 0)
        {
            return new QuotaHeadroomGateResult(
                true,
                "quota unavailable for headroom projection",
                request.ResetAt,
                ReservedPct: reservedPct);
        }

        QuotaHeadroomEstimate? estimate;
        try
        {
            estimate = await EstimateHeadroomAsync(request.ProjectId, member, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Quota headroom estimation failed for project {ProjectId} agent {Agent} model {Model}; refusing dispatch as a fail-closed safety measure",
                request.ProjectId.Value,
                member.Agent.Value,
                member.ModelId ?? "(default)");
            return new QuotaHeadroomGateResult(
                false,
                "headroom estimation unavailable",
                request.ResetAt,
                InsufficientHeadroom: true,
                ReservedPct: reservedPct,
                ProjectedAvailablePct: request.AvailablePct - reservedPct);
        }

        var estimatedCost = estimate?.EstimatedIterPctCost;
        if (estimatedCost is not { } cost || cost <= 0)
        {
            return new QuotaHeadroomGateResult(
                true,
                "quota available",
                request.ResetAt,
                Estimate: estimate,
                ReservedPct: reservedPct,
                ProjectedAvailablePct: request.AvailablePct - reservedPct);
        }

        if (!estimate!.TrustedForEnforcement)
        {
            return new QuotaHeadroomGateResult(
                true,
                $"quota available (untrusted headroom estimate {cost:F1}% skipped for enforcement)",
                request.ResetAt,
                Estimate: estimate,
                ReservedPct: reservedPct,
                ProjectedAvailablePct: request.AvailablePct - reservedPct - cost);
        }

        var minRemainingPct = Math.Max(0, request.MinRemainingPct ?? 0);
        IQuotaReservationLease? reservation = null;

        if (reserve)
        {
            var sync = _reservationLocks.GetOrAdd(key, _ => new object());
            lock (sync)
            {
                reservedPct = GetReservedHeadroomPct(key);
                if (request.AvailablePct - reservedPct - cost >= minRemainingPct)
                {
                    _reservedHeadroomPct.AddOrUpdate(key, cost, (_, existing) => existing + cost);
                    reservation = new InProcessQuotaReservationLease(this, key, member.ModelId, cost);
                }
            }
        }

        var projected = request.AvailablePct - reservedPct - cost;
        if (reservation is null && projected < minRemainingPct)
        {
            const string reason = "insufficient headroom";
            if (request.AuditOnRefusal)
            {
                AuditLog.QuotaDispatchRefused(
                    member.Agent,
                    request.ProjectId,
                    request.AvailablePct,
                    cost,
                    reason);
            }

            return new QuotaHeadroomGateResult(
                false,
                $"insufficient headroom (available={request.AvailablePct:F1}%, reserved={reservedPct:F1}%, estimatedCost={cost:F1}%, projected={projected:F1}% < min={minRemainingPct:F1}%)",
                request.ResetAt,
                InsufficientHeadroom: true,
                Estimate: estimate,
                ReservedPct: reservedPct,
                ProjectedAvailablePct: projected);
        }

        return new QuotaHeadroomGateResult(
            true,
            $"quota available; projected headroom {projected:F1}% after estimated {cost:F1}% iteration",
            request.ResetAt,
            Reservation: reservation,
            Estimate: estimate,
            ReservedPct: reservedPct,
            ProjectedAvailablePct: projected);
    }

    private async Task<QuotaHeadroomEstimate?> EstimateHeadroomAsync(
        ProjectId projectId,
        AgentMembership member,
        CancellationToken ct)
    {
        if (_headroomEstimator is null)
            return null;

        return await _headroomEstimator.EstimateAsync(
            new QuotaHeadroomRequest(projectId, member.Agent, member.ModelId),
            ct);
    }

    private async Task ReleaseQuotaReservationAsync(
        InProcessQuotaReservationLease reservation,
        bool quotaMayHaveBeenConsumed,
        CancellationToken ct = default)
    {
        if (!quotaMayHaveBeenConsumed)
        {
            reservation.ReleaseReservedHeadroom();
            return;
        }

        var refreshed = await TryRefreshReservationQuotaAsync(reservation, ct);
        if (refreshed || _opts.QuotaCacheTtl <= TimeSpan.Zero)
        {
            reservation.ReleaseReservedHeadroom();
            return;
        }

        _log.LogWarning(
            "Could not force-refresh quota before releasing {Agent}/{Model} reservation for project {ProjectId}; retaining {ReservedPct:F1}% reserved headroom for {Delay}",
            reservation.Agent.Value,
            reservation.ModelId ?? "(default)",
            reservation.ProjectId.Value,
            reservation.ReservedPct,
            _opts.QuotaCacheTtl);
        _ = ReleaseReservationAfterDelayAsync(reservation, _opts.QuotaCacheTtl);
    }

    private async Task<bool> TryRefreshReservationQuotaAsync(IQuotaReservationLease reservation, CancellationToken ct)
    {
        if (!_probesByKind.TryGetValue(reservation.Agent, out var probe))
            return false;

        try
        {
            var member = new AgentMembership
            {
                Agent = reservation.Agent,
                Billing = AgentBilling.Subscription,
                ModelId = reservation.ModelId,
                QualityScore = 0,
            };
            var snapshot = await probe.RefreshAvailabilityAsync(member, ct);
            var quota = AgentQuotaResolver.ResolveMemberQuota(snapshot, member);
            if (quota.AvailablePct < 0)
            {
                _log.LogWarning(
                    "Quota refresh returned unknown availability before releasing {Agent}/{Model} reservation for project {ProjectId}; retaining reserved headroom",
                    reservation.Agent.Value,
                    reservation.ModelId ?? "(default)",
                    reservation.ProjectId.Value);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(
                ex,
                "Quota refresh failed before releasing {Agent}/{Model} reservation for project {ProjectId}",
                reservation.Agent.Value,
                reservation.ModelId ?? "(default)",
                reservation.ProjectId.Value);
            return false;
        }
    }

    private static async Task ReleaseReservationAfterDelayAsync(InProcessQuotaReservationLease reservation, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
        }
        finally
        {
            reservation.ReleaseReservedHeadroom();
        }
    }

    private double GetReservedHeadroomPct(QuotaReservationKey key) =>
        _reservedHeadroomPct.TryGetValue(key, out var reserved) ? reserved : 0;

    private void ReleaseReservation(QuotaReservationKey key, double amount)
    {
        var sync = _reservationLocks.GetOrAdd(key, _ => new object());
        lock (sync)
        {
            if (!_reservedHeadroomPct.TryGetValue(key, out var existing))
                return;

            var next = existing - amount;
            if (next <= ReservationReleaseEpsilonPct)
                _reservedHeadroomPct.TryRemove(key, out _);
            else
                _reservedHeadroomPct[key] = next;
        }
    }

    private sealed record QuotaReservationKey(ProjectId ProjectId, AgentKind Agent);

    private sealed class InProcessQuotaReservationLease : IQuotaReservationLease
    {
        private readonly InProcessQuotaHeadroomManager _manager;
        private readonly QuotaReservationKey _key;
        private readonly string? _modelId;
        private int _disposed;
        private int _releaseStarted;

        public InProcessQuotaReservationLease(
            InProcessQuotaHeadroomManager manager,
            QuotaReservationKey key,
            string? modelId,
            double reservedPct)
        {
            _manager = manager;
            _key = key;
            _modelId = modelId;
            ReservedPct = reservedPct;
        }

        public ProjectId ProjectId => _key.ProjectId;
        public AgentKind Agent => _key.Agent;
        public string? ModelId => string.IsNullOrEmpty(_modelId) ? null : _modelId;
        public double ReservedPct { get; }

        public Task ReleaseAsync(bool quotaMayHaveBeenConsumed, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _releaseStarted, 1) != 0)
                return Task.CompletedTask;

            return _manager.ReleaseQuotaReservationAsync(this, quotaMayHaveBeenConsumed, ct);
        }

        public void ReleaseReservedHeadroom()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _manager.ReleaseReservation(_key, ReservedPct);
        }
    }
}
