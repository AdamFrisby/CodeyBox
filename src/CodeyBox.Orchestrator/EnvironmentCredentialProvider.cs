using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Default ICredentialProvider: reads each agent's secret from a configured
/// environment variable and exposes it under the canonical env name the
/// agent's CLI expects. Suitable for single-tenant deployments.
///
/// For multi-tenant or rotating-secret deployments, replace this with an
/// implementation that calls into a secrets manager. The interface is the
/// stable surface — orchestrator and agents do not change.
/// </summary>
public sealed class EnvironmentCredentialProvider : ICredentialProvider
{
    private readonly Dictionary<AgentKind, List<AgentCredentialMapping>> _map;

    /// <param name="mappings">
    /// One or more mappings per agent. Several are legitimate when a CLI reads more than one credential
    /// from the environment — GitHub Copilot takes a GitHub token for subscription mode and, when the
    /// operator has configured a bring-your-own-key endpoint, a separate provider key. Two mappings for
    /// the same agent must not target the same sandbox variable; that is a wiring mistake, not a
    /// fallback, so it throws rather than letting one silently win.
    /// </param>
    public EnvironmentCredentialProvider(IEnumerable<AgentCredentialMapping> mappings)
    {
        _map = [];
        foreach (var mapping in mappings)
        {
            if (!_map.TryGetValue(mapping.Agent, out var forAgent))
            {
                forAgent = [];
                _map[mapping.Agent] = forAgent;
            }

            if (forAgent.Any(m => string.Equals(
                    m.SandboxEnvironmentVariable,
                    mapping.SandboxEnvironmentVariable,
                    StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"Duplicate credential mapping for agent '{mapping.Agent.Value}' targeting sandbox "
                    + $"variable '{mapping.SandboxEnvironmentVariable}'.",
                    nameof(mappings));
            }

            forAgent.Add(mapping);
        }
    }

    /// <summary>
    /// The agent's environment credentials: every mapped host variable that is actually set, exposed
    /// under the name its CLI expects. Null when the agent has no mappings or none of them are
    /// populated — an absent optional credential (a BYOK key for a local server that needs none) simply
    /// contributes nothing rather than failing the lookup.
    /// </summary>
    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (!_map.TryGetValue(agent, out var mappings))
            return Task.FromResult<AgentCredential?>(null);

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            var value = Environment.GetEnvironmentVariable(mapping.HostEnvironmentVariable);
            if (!string.IsNullOrEmpty(value))
                env[mapping.SandboxEnvironmentVariable] = value;
        }

        if (env.Count == 0)
            return Task.FromResult<AgentCredential?>(null);

        return Task.FromResult<AgentCredential?>(
            new AgentCredential(agent, env, new Dictionary<string, string>()));
    }
}

/// <summary>
/// Names the host env var holding the secret and the env var the sandbox
/// agent reads. Kept separate so the host can use namespaced names
/// (CODEYBOX_CLAUDE_API_KEY) while the sandbox sees what the CLI expects
/// (ANTHROPIC_API_KEY).
/// </summary>
public sealed record AgentCredentialMapping(
    AgentKind Agent,
    string HostEnvironmentVariable,
    string SandboxEnvironmentVariable);
