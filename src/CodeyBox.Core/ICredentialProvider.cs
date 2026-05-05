namespace CodeyBox.Core;

/// <summary>
/// Resolves the secret material an agent needs to authenticate. Designed to
/// return a short-lived value the orchestrator can mount into the sandbox as
/// an ephemeral tmpfs file. Implementations should not log the secret.
/// </summary>
public interface ICredentialProvider
{
    /// <summary>Returns the credential bundle for the given agent, or null if none is available.</summary>
    Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default);
}

/// <summary>
/// A credential bundle for a specific agent. <see cref="EnvironmentVariables"/>
/// are values the agent's CLI reads at startup; <see cref="Files"/> are
/// path → contents pairs to materialise as files inside the sandbox.
/// </summary>
public sealed record AgentCredential(
    AgentKind Agent,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    IReadOnlyDictionary<string, string> Files)
{
    /// <summary>
    /// Optional expiry for time-bound credentials issued by vault-style plugins.
    /// When set, the orchestrator caches the credential up to this instant and
    /// re-fetches afterward. When null (the default for all built-in providers)
    /// the credential is never cached — every pickup re-reads the underlying
    /// source (e.g. the OAuth JSON file) so live rotations propagate without
    /// an orchestrator restart.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
