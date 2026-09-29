using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Shared fetch policy for <see cref="IAgentBurnEstimator"/> used by quota
/// math: "no estimator wired" and "estimator threw" both mean the configured
/// percentage chain applies unchanged, and cancellation is never swallowed.
/// One home so the dispatch path and advisory surfaces cannot drift.
/// </summary>
internal static class AgentBurnEstimatorExtensions
{
    /// <summary>
    /// Returns the measured per-item burn for <paramref name="agent"/>, or
    /// null when no estimator is wired or it throws. Validity (enough
    /// samples, positive burn) is decided by the pure consumers
    /// (<see cref="AgentBurnEstimate.HasMeasuredBurn"/>), not here, so every
    /// caller shares one threshold. Cancellation propagates. The estimator
    /// caches per agent, so one fetch per member per dispatch pass is cheap.
    /// </summary>
    public static async Task<AgentBurnEstimate?> GetEstimateOrNullAsync(
        this IAgentBurnEstimator? estimator,
        AgentKind agent,
        ILogger? log,
        CancellationToken ct)
    {
        if (estimator is null) return null;
        try
        {
            return await estimator.GetEstimateAsync(agent, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex,
                "Quota burn: estimator threw for {Agent}; using configured estimates",
                agent.Value);
            return null;
        }
    }
}
