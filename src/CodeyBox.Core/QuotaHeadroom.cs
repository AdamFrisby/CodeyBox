namespace CodeyBox.Core;

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

public interface IQuotaHeadroomEstimator
{
    Task<QuotaHeadroomEstimate?> EstimateAsync(
        QuotaHeadroomRequest request,
        CancellationToken ct = default);
}

public sealed record QuotaHeadroomRequest(
    ProjectId ProjectId,
    AgentKind Agent,
    string? ModelId);

public sealed record QuotaHeadroomEstimate(
    double EstimatedIterPctCost,
    double AverageTokensPerIteration,
    int SampledItemCount,
    string Source,
    bool TrustedForEnforcement = false);

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
