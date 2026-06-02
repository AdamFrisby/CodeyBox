using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Exposes the most-recent subscription quota-availability percentage observed
/// per (agent, model) during routing, without coupling the consumer to the
/// concrete router. Implemented by <see cref="AgentClassRouter"/> and consumed
/// by the OpenTelemetry quota-headroom observable gauge so telemetry depends on
/// this focused contract rather than the full routing type.
/// </summary>
public interface IAgentQuotaAvailabilitySnapshot
{
    /// <summary>
    /// Synchronous snapshot of the most-recent quota-availability percentage
    /// observed per (agent, model). A percentage of <c>-1</c> indicates the
    /// probe could not determine availability. Returns an empty list before any
    /// probe has run.
    /// </summary>
    IReadOnlyList<(AgentKind Agent, string? ModelId, double AvailablePct)> SnapshotQuotaAvailability();
}
