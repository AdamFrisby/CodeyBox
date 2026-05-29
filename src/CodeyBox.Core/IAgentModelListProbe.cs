namespace CodeyBox.Core;

/// <summary>
/// Fetches the list of model identifiers available to the configured
/// credential for a given agent provider. Used at startup by the agent-class
/// config validator to flag <see cref="AgentMembership.ModelId"/> values that
/// would silently misroute (e.g. operator typo).
///
/// <para>Implementations MUST NOT throw — return a result with non-null
/// <see cref="AgentModelListResult.FailureReason"/> on any network, auth, or
/// parse issue. The validator interprets a failed result as "skip model-id
/// validation for this agent" (logs a warning, does not block startup).</para>
///
/// <para>Implementations should be cheap to invoke repeatedly (results are
/// only fetched once per process at startup, so caching is optional).</para>
///
/// <para>For per-call (hot-path) credential resolution — e.g. the text-only
/// runner mapping an undated alias to the canonical Messages-API id — provider
/// adapters expose a credential-scoped sub-interface (see
/// <c>IClaudeModelListProbe</c>) rather than reusing the ambient-credential
/// <see cref="GetModelListAsync(CancellationToken)"/>. That keeps both surfaces
/// behind a single DI-injectable, mockable abstraction owned by the provider
/// adapter — no static helpers on the concrete probe — and ensures hot-path
/// calls flow through the same <c>IHttpClientFactory</c> policy (timeout,
/// AllowAutoRedirect=false) as startup validation, instead of an ad-hoc
/// <c>HttpClient</c> in the runner.</para>
/// </summary>
public interface IAgentModelListProbe
{
    /// <summary>The agent kind this probe covers.</summary>
    AgentKind Kind { get; }

    /// <summary>
    /// Fetches the provider's model list. Must not throw.
    /// </summary>
    Task<AgentModelListResult> GetModelListAsync(CancellationToken ct);
}

/// <summary>
/// Outcome of an <see cref="IAgentModelListProbe"/> call.
/// </summary>
public sealed record AgentModelListResult
{
    /// <summary>Model identifiers returned by the provider, empty when the probe failed.</summary>
    public required IReadOnlyList<string> ModelIds { get; init; }

    /// <summary>
    /// Short human-readable reason when the probe could not produce a usable
    /// list (e.g. <c>"HTTP 401"</c>, <c>"network error"</c>). Null on success.
    /// </summary>
    public string? FailureReason { get; init; }

    public static AgentModelListResult Success(IReadOnlyList<string> ids) =>
        new() { ModelIds = ids };

    public static AgentModelListResult Failed(string reason) =>
        new() { ModelIds = Array.Empty<string>(), FailureReason = reason };
}
