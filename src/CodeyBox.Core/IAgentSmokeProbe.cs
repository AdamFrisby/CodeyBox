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
