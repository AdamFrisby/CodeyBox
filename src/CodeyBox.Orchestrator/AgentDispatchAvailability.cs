using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Dispatch-facing availability policy. It is the single place that combines
/// the master smoke switch, the in-VM smoke gate, and the availability registry
/// read semantics used by routing, pickup, and worker-health checks.
/// </summary>
public interface IAgentDispatchAvailability
{
    /// <summary>True when either availability storage or an in-VM gate is wired.</summary>
    bool IsWired { get; }

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

public sealed class AgentDispatchAvailability : IAgentDispatchAvailability
{
    private readonly IAgentAvailabilityRegistry? _availability;
    private readonly ISmokeAvailabilityRegistry? _smokeAvailability;
    private readonly IInVmSmokeGate? _inVmSmokeGate;
    private readonly SmokeOptionsSnapshot? _smokeOptions;

    public AgentDispatchAvailability(
        IAgentAvailabilityRegistry? availability = null,
        ISmokeAvailabilityRegistry? smokeAvailability = null,
        IInVmSmokeGate? inVmSmokeGate = null,
        SmokeOptionsSnapshot? smokeOptions = null)
    {
        _availability = availability;
        _smokeAvailability = smokeAvailability ?? availability as ISmokeAvailabilityRegistry;
        _inVmSmokeGate = inVmSmokeGate;
        _smokeOptions = smokeOptions;
    }

    public bool IsWired => _availability is not null || _inVmSmokeGate is not null;

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

        if (SmokeDisabled && _smokeAvailability is not null)
            return _smokeAvailability.GetAvailabilityWithoutSmokeGateExclusions(kind);

        return _availability.GetAvailability(kind);
    }

    public static IAgentDispatchAvailability? CreateIfConfigured(
        IAgentAvailabilityRegistry? availability = null,
        IInVmSmokeGate? inVmSmokeGate = null,
        SmokeOptionsSnapshot? smokeOptions = null)
    {
        return availability is null && inVmSmokeGate is null
            ? null
            : new AgentDispatchAvailability(
                availability,
                availability as ISmokeAvailabilityRegistry,
                inVmSmokeGate,
                smokeOptions);
    }

    private bool SmokeDisabled => _smokeOptions?.Enabled == false;
}
