using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Dispatch-facing availability policy. It is the single place that combines
/// operator pause state, the master smoke switch, the in-VM smoke gate, and
/// the availability registry read semantics used by routing, pickup, and
/// worker-health checks.
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
    public const string PausedReasonPrefix = "paused by operator";

    private readonly IAgentEffectiveAvailabilityReader? _availability;
    private readonly IInVmSmokeGate? _inVmSmokeGate;
    private readonly SmokeOptionsSnapshot? _smokeOptions;
    private readonly IAgentPauseController? _pauses;

    public AgentDispatchAvailability(
        IAgentEffectiveAvailabilityReader? availability = null,
        IInVmSmokeGate? inVmSmokeGate = null,
        SmokeOptionsSnapshot? smokeOptions = null,
        IAgentPauseController? pauses = null)
    {
        _availability = availability;
        _inVmSmokeGate = inVmSmokeGate;
        _smokeOptions = smokeOptions;
        _pauses = pauses;
    }

    public async Task<AgentAvailability?> EnsureAvailableAsync(
        AgentKind kind,
        InVmSmokeSandboxTarget target,
        CancellationToken ct)
    {
        if (await GetPausedAvailabilityAsync(kind, ct) is { } paused)
            return paused;

        if (SmokeDisabled)
            return GetAvailability(kind);

        if (_inVmSmokeGate is not null)
            return await _inVmSmokeGate.EnsureAvailableAsync(kind, target, ct);

        return _availability?.GetAvailability(kind);
    }

    public AgentAvailability? GetAvailability(AgentKind kind)
    {
        if (GetPausedAvailability(kind) is { } paused)
            return paused;

        if (_availability is null)
            return null;

        if (SmokeDisabled)
            return _availability.GetAvailabilityWithoutSmokeGateExclusions(kind);

        return _availability.GetAvailability(kind);
    }

    private bool SmokeDisabled => _smokeOptions?.Enabled == false;

    public static bool IsPausedVerdict(AgentAvailability? availability) =>
        availability is { Available: false, Reason: { } reason }
        && reason.StartsWith(PausedReasonPrefix, StringComparison.Ordinal);

    private async Task<AgentAvailability?> GetPausedAvailabilityAsync(
        AgentKind kind,
        CancellationToken ct)
    {
        if (_pauses is null)
            return null;

        var pause = await _pauses.GetAgentStateAsync(kind, ct);
        return pause is null ? null : ToPausedAvailability(pause);
    }

    private AgentAvailability? GetPausedAvailability(AgentKind kind)
    {
        if (_pauses is null)
            return null;

        var pause = _pauses.GetAgentStateAsync(kind, CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
        return pause is null ? null : ToPausedAvailability(pause);
    }

    private static AgentAvailability ToPausedAvailability(AgentPauseState pause) =>
        new(false, FormatPausedReason(pause), null);

    private static string FormatPausedReason(AgentPauseState pause)
    {
        var reason = string.IsNullOrWhiteSpace(pause.PausedReason)
            ? PausedReasonPrefix
            : $"{PausedReasonPrefix}: {pause.PausedReason}";
        return pause.ExpiresAt is { } expiresAt
            ? $"{reason} until {expiresAt:O}"
            : reason;
    }
}
