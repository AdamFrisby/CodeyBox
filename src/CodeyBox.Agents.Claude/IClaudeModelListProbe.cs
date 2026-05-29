using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Claude-specific extension of <see cref="IAgentModelListProbe"/> that exposes
/// a credential-scoped overload for hot-path callers. The ambient-credential
/// <see cref="IAgentModelListProbe.GetModelListAsync(CancellationToken)"/>
/// remains the entry point for startup validation; this credential-scoped
/// overload is the entry point for per-call resolution (e.g. the text-only
/// runner mapping an undated CLI alias like <c>claude-opus-4-8</c> to the
/// canonical Messages-API id <c>claude-opus-4-8-YYYYMMDD</c> before posting).
///
/// <para>Routing the hot path through the same DI-registered adapter as
/// startup validation keeps a single class in charge of the Anthropic
/// <c>GET /v1/models</c> surface (URL, auth headers, version pin, response
/// cap, JSON parsing) and ensures both surfaces share the
/// <c>IHttpClientFactory</c>-configured policy (5 s timeout,
/// <c>AllowAutoRedirect=false</c>) — production cannot otherwise mock or swap
/// model-list behavior without falling back to a runner-internal HTTP seam,
/// and the runner-internal default <see cref="HttpClient"/> diverges from the
/// startup policy under slow or redirecting upstreams.</para>
///
/// <para>Like the base interface, implementations MUST NOT throw — return a
/// result with non-null <see cref="AgentModelListResult.FailureReason"/> on
/// any network, auth, or parse issue.</para>
/// </summary>
public interface IClaudeModelListProbe : IAgentModelListProbe
{
    /// <summary>
    /// Fetches the model list using the supplied per-call credentials, rather
    /// than the ambient credential provider the parameterless overload uses.
    /// Exactly one of <paramref name="oauthToken"/> / <paramref name="apiKey"/>
    /// is consumed (OAuth wins); both null returns a failed result.
    /// </summary>
    Task<AgentModelListResult> GetModelListAsync(
        string? oauthToken, string? apiKey, CancellationToken ct);
}
