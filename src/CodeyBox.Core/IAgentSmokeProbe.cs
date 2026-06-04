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

/// <summary>
/// Classification of a smoke-test failure for routing decisions and operator
/// alerting. A persistent failure is one the orchestrator cannot recover from
/// on its own — auth/credential rejection, missing binary, malformed bundle —
/// and benching the agent until an operator acts is correct. A transient
/// failure is a network blip, 5xx, or timeout where retrying later is the
/// right move. <c>None</c> is set when the probe passed; <c>Unknown</c> is
/// the default for failures whose nature cannot be determined (e.g. an HTTP
/// status outside the buckets we classify) so consumers can still treat them
/// as the more conservative "retry later".
///
/// <para>The distinction matters because a persistent failure dressed up as
/// transient leaves the agent benched indefinitely with no operator-visible
/// signal — the periodic sweep keeps retrying, the router keeps falling
/// through, and the throughput collapse is silent. The webhook /
/// <c>AuditLog.AgentSmokePersistentlyFailed</c> path raises the alarm only
/// when the category is <see cref="Persistent"/>, so the noise floor stays low.</para>
/// </summary>
public enum SmokeFailureCategory
{
    /// <summary>Probe passed — no failure to classify.</summary>
    None = 0,
    /// <summary>Network blip, timeout, 5xx, or other server-side error worth retrying.</summary>
    Transient = 1,
    /// <summary>
    /// Auth/credential rejection, missing binary, malformed credential bundle,
    /// or any failure that will keep failing until an operator re-authorizes
    /// the agent. Surfaces to operators via <c>agent.smoke_failed</c> with
    /// <c>Category=persistent</c> so dashboards and runbooks can distinguish
    /// "fix this now" from "wait it out".
    /// </summary>
    Persistent = 2,
    /// <summary>Failure shape we cannot confidently bucket — treated as transient by retries.</summary>
    Unknown = 3,
}

/// <summary>
/// Result of a single credential smoke test run. <see cref="Category"/>
/// distinguishes transient (worth retrying) from persistent (operator action
/// required) failures so the orchestrator does not silently bench a healthy-
/// quota agent because of a credential expiry. The default of
/// <see cref="SmokeFailureCategory.None"/> is correct for the <c>Ok=true</c>
/// path; producers must set <see cref="SmokeFailureCategory.Transient"/> or
/// <see cref="SmokeFailureCategory.Persistent"/> on failure.
/// </summary>
public sealed record AgentSmokeResult(
    bool Ok,
    string? FailureReason,
    TimeSpan Duration,
    SmokeFailureCategory Category = SmokeFailureCategory.None);

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
    /// registered for the kind, or when the master smoke switch is disabled.
    /// Callers that need to distinguish those cases should inspect the smoke
    /// configuration before treating <c>null</c> as "this layer does not know
    /// the agent".
    /// </summary>
    Task<AgentSmokeResult?> ProbeAsync(AgentKind kind, CancellationToken ct);
}
