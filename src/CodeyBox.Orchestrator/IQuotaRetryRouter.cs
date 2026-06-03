using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Focused routing port used by quota auto-retry re-evaluation. It exposes only
/// the router operations needed to decide whether a parked quota item can run
/// now and when exhausted class members are expected to refill.
/// </summary>
public interface IQuotaRetryRouter
{
    Task<QuotaRetryRoutingDecision> ResolveQuotaRetryAsync(
        WorkItem item,
        Project? project,
        CancellationToken ct);

    Task<DateTimeOffset?> ComputeEarliestExhaustedResetAsync(
        WorkItem item,
        Project? project,
        CancellationToken ct);
}

public sealed record QuotaRetryRoutingDecision(
    bool ShouldWait,
    bool NoEligibleMembers,
    string? Reason);
