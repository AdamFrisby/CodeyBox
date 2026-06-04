using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Dispatch-facing availability policy. It is the single place that combines
/// the master smoke switch, the in-VM smoke gate, and the availability registry
/// read semantics used by routing, pickup, and worker-health checks.
/// </summary>
public interface IAgentDispatchAvailability
{
    /// <summary>
    /// Returns the effective availability for dispatch. When smoke is enabled,
    /// the in-VM gate owns the read/probe/re-read sequence if present. When the
    /// master smoke switch is disabled, smoke-gate exclusions are ignored and
    /// the in-VM gate is not invoked.
    /// </summary>
    Task<AgentAvailability?> EnsureAvailableAsync(
        AgentKind kind,
        InVmSmokeSandboxTarget target,
        CancellationToken ct);

    /// <summary>
    /// Returns the current effective availability without running a probe. Used
    /// by readiness/health checks that only need the cached verdict.
    /// </summary>
    AgentAvailability? GetAvailability(AgentKind kind);
}

/// <summary>
/// Read-only availability view used by dispatch policy. The default read
/// includes every exclusion; the smoke-disabled read keeps non-smoke
/// exclusions such as the fast-fail breaker while ignoring smoke-gate sources.
/// </summary>
public interface IAgentEffectiveAvailabilityReader
{
    AgentAvailability GetAvailability(AgentKind kind);

    AgentAvailability GetAvailabilityWithoutSmokeGateExclusions(AgentKind kind);
}

public sealed class AgentDispatchAvailability : IAgentDispatchAvailability
{
    private readonly IAgentEffectiveAvailabilityReader? _availability;
    private readonly IInVmSmokeGate? _inVmSmokeGate;
    private readonly SmokeOptionsSnapshot? _smokeOptions;

    public AgentDispatchAvailability(
        IAgentEffectiveAvailabilityReader? availability = null,
        IInVmSmokeGate? inVmSmokeGate = null,
        SmokeOptionsSnapshot? smokeOptions = null)
    {
        _availability = availability;
        _inVmSmokeGate = inVmSmokeGate;
        _smokeOptions = smokeOptions;
    }

    public async Task<AgentAvailability?> EnsureAvailableAsync(
        AgentKind kind,
        InVmSmokeSandboxTarget target,
        CancellationToken ct)
    {
        if (SmokeDisabled)
            return GetAvailability(kind);

        if (_inVmSmokeGate is not null)
            return await _inVmSmokeGate.EnsureAvailableAsync(kind, target, ct);

        return _availability?.GetAvailability(kind);
    }

    public AgentAvailability? GetAvailability(AgentKind kind)
    {
        if (_availability is null)
            return null;

        if (SmokeDisabled)
            return _availability.GetAvailabilityWithoutSmokeGateExclusions(kind);

        return _availability.GetAvailability(kind);
    }

    private bool SmokeDisabled => _smokeOptions?.Enabled == false;
}
