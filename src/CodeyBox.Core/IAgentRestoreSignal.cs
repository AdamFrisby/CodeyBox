namespace CodeyBox.Core;

/// <summary>
/// Fired when an agent's availability flips from excluded to routable —
/// either because a smoke probe transitioned FAIL -> PASS or because an
/// operator <c>POST /admin/agent/{name}/reset</c> cleared an existing
/// exclusion. Resetting an already-routable agent does not publish this event.
/// Consumers use the signal to re-evaluate Failed/parked work items whose
/// last attempt landed on the now-restored agent during the outage window.
///
/// <para>The signal mirrors <see cref="IAgentPauseSignal"/> in shape: a
/// lightweight wake notification, intentionally narrow so any subscriber
/// can be added without coupling to the registry's internal exclusion
/// taxonomy. The payload carries the restored agent plus the outage start
/// timestamp the consumer needs to scope the sweep (so the consumer never
/// has to read the registry snapshot a second time and race against a
/// follow-up failure that already pushed <c>LastSmokeFailedAt</c>
/// forward).</para>
/// </summary>
public interface IAgentRestoreSignal
{
    event Action<AgentRestoredEvent>? AgentRestored;
}

/// <summary>
/// Payload for <see cref="IAgentRestoreSignal.AgentRestored"/>. Carries the
/// agent that just recovered plus the window the consumer should consider
/// when scoping its sweep.
/// </summary>
/// <param name="Agent">The agent whose availability just transitioned to routable.</param>
/// <param name="OutageStartedAt">
/// UTC timestamp of the FIRST exclusion in the outage streak that just ended
/// — anchored to the moment the agent transitioned healthy → excluded under
/// ANY source (smoke probe, fast-fail breaker, no-changes breaker,
/// missing-probe, runtime auth-required), and pinned across follow-up
/// failures so a long multi-hour outage keeps the outage start, not the
    /// most recent failure. Null only when a publisher cannot reconstruct an
    /// outage window for a restore it did emit. Consumers should treat null as
    /// "no outage window known" and skip retroactive retries while still
    /// producing zero-count telemetry where applicable.
/// </param>
/// <param name="RestoredAt">UTC timestamp of the transition.</param>
public sealed record AgentRestoredEvent(
    AgentKind Agent,
    DateTimeOffset? OutageStartedAt,
    DateTimeOffset RestoredAt);
