namespace CodeyBox.Core;

/// <summary>
/// Verifies that an agent's credentials are valid by issuing a minimal
/// direct API call (not via the CLI or a sandbox). Implementations must be
/// fast (≤5 s happy path) and must not log the authorization header or
/// the credential values.
///
/// Not every runner has a sensible probe. This interface is kept separate from
/// <see cref="IAgentRunner"/> so agents without a probe (e.g. Copilot)
/// simply have no registered implementation, and the smoke gate skips them.
/// </summary>
public interface IAgentSmokeProbe
{
    AgentKind Kind { get; }

    /// <summary>
    /// Runs a lightweight credential check. Never throws — callers treat any
    /// exception from the implementation as a transient failure. Returns
    /// <c>Ok=false</c> with a human-readable <c>FailureReason</c> on
    /// credential or network problems.
    /// </summary>
    Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct);
}

/// <summary>Result of a single credential smoke test run.</summary>
public sealed record AgentSmokeResult(bool Ok, string? FailureReason, TimeSpan Duration);

/// <summary>
/// Runs a single host-side credential smoke probe on demand. This is the
/// core-owned port the <c>/admin/agent/{name}/smoke</c> endpoint depends on so
/// the presentation layer never binds to the concrete periodic-sweep
/// background service. The implementation runs the registered
/// <see cref="IAgentSmokeProbe"/> for the kind and feeds the result back into
/// the availability registry, exactly as the periodic sweep does.
/// </summary>
public interface IHostSmokeProbeRunner
{
    /// <summary>
    /// Runs the host credential probe registered for <paramref name="kind"/>,
    /// if any, and returns its result. Returns <c>null</c> when no probe is
    /// registered for the kind (the caller treats that as "this layer does not
    /// know the agent").
    /// </summary>
    Task<AgentSmokeResult?> ProbeAsync(AgentKind kind, CancellationToken ct);
}
