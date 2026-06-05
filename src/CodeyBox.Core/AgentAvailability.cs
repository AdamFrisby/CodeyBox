namespace CodeyBox.Core;

/// <summary>
/// Result of an availability lookup for an agent. <see cref="Available"/> is
/// false when the agent is currently benched by any signal (host/in-VM smoke,
/// fast-fail breaker, missing probe); <see cref="Reason"/> carries the
/// operator-facing explanation in that case.
///
/// <para>Lives in <c>CodeyBox.Core</c> (not the orchestrator) so the
/// dispatch-gate port <see cref="IInVmSmokeGate"/> can return it directly —
/// letting routing consumers obtain a verdict from a single
/// <see cref="IInVmSmokeGate.EnsureAvailableAsync"/> call rather than binding
/// to both the gate and the concrete availability registry.</para>
/// </summary>
public sealed record AgentAvailability(
    bool Available,
    string? Reason,
    DateTimeOffset? LastSmokePassedAt,
    AgentAvailabilityCause Cause = AgentAvailabilityCause.None);

public enum AgentAvailabilityCause
{
    None = 0,
    SmokeGate = 1,
    FastFail = 2,
    MissingProbe = 3,
    OperatorPaused = 4,
}
