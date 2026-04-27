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
    private readonly Dictionary<AgentKind, AgentCredentialMapping> _map;

    public EnvironmentCredentialProvider(IEnumerable<AgentCredentialMapping> mappings)
    {
        _map = mappings.ToDictionary(m => m.Agent);
    }

    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (!_map.TryGetValue(agent, out var mapping))
            return Task.FromResult<AgentCredential?>(null);

        var value = Environment.GetEnvironmentVariable(mapping.HostEnvironmentVariable);
        if (string.IsNullOrEmpty(value))
            return Task.FromResult<AgentCredential?>(null);

        var env = new Dictionary<string, string> { [mapping.SandboxEnvironmentVariable] = value };
        return Task.FromResult<AgentCredential?>(new AgentCredential(agent, env, new Dictionary<string, string>()));
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
