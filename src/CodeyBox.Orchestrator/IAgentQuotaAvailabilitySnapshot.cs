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

    /// <summary>
    /// Instance-aware variant of <see cref="SnapshotQuotaAvailability"/>.
    /// Implementations that do not track instances return the legacy kind as
    /// the instance id.
    /// </summary>
    IReadOnlyList<(string InstanceId, AgentKind Agent, string? ModelId, double AvailablePct)> SnapshotQuotaAvailabilityByInstance() =>
        SnapshotQuotaAvailability()
            .Select(row => (row.Agent.Value, row.Agent, row.ModelId, row.AvailablePct))
            .ToList();
}

/// <summary>
/// Emits a wake-up signal when quota availability observations cross from
/// unroutable to routable. Consumers can react without depending on the
/// concrete transition tracker.
/// </summary>
public interface IAgentQuotaAvailabilitySignal
{
    event Action? QuotaUsableThresholdCrossed;

    /// <summary>
    /// Instance-aware quota recovery signal. The legacy no-argument event is
    /// retained for broad wake-ups; subscribers that can act on a recovered
    /// member should use this event to avoid being limited by a global priority
    /// prefix.
    /// </summary>
    event Action<AgentQuotaMemberKey>? QuotaMemberUsableThresholdCrossed;
}
