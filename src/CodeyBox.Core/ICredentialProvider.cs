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
/// path → contents pairs to materialise as files inside the sandbox;
/// <see cref="Mounts"/> are bind-mounts the orchestrator merges into the
/// sandbox spec for non-secret credential adjuncts that must come from the
/// host filesystem. Providers must not mount writable host credential
/// directories into untrusted agent sandboxes.
/// </summary>
public sealed record AgentCredential
{
    private IReadOnlyList<SandboxMount> _mounts = [];

    public AgentCredential(
        AgentKind Agent,
        IReadOnlyDictionary<string, string> EnvironmentVariables,
        IReadOnlyDictionary<string, string> Files)
    {
        this.Agent = Agent;
        AgentCredentialMaterializationPolicy.SnapshotBundle(
            EnvironmentVariables,
            Files,
            out var environmentSnapshot,
            out var fileSnapshot);
        this.EnvironmentVariables = environmentSnapshot;
        this.Files = fileSnapshot;
    }

    public AgentKind Agent { get; init; }

    /// <summary>Bounded immutable snapshot of sandbox credential environment values.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

    /// <summary>Bounded immutable snapshot of canonical relative file paths and payloads.</summary>
    public IReadOnlyDictionary<string, string> Files { get; }

    /// <summary>
    /// Optional expiry for time-bound credentials issued by vault-style plugins.
    /// When set, the orchestrator caches the credential up to this instant and
    /// re-fetches afterward. When null (the default for all built-in providers)
    /// the credential is never cached — every pickup re-reads the underlying
    /// source (e.g. the OAuth JSON file) so live rotations propagate without
    /// an orchestrator restart.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Optional bind-mounts the credential provider wants applied to any
    /// sandbox running this agent. The orchestrator merges these into
    /// <c>SandboxSpec.Mounts</c> when the credential is in scope. Do not use
    /// this to expose writable host credential directories to untrusted agent
    /// sandboxes.
    /// </summary>
    public IReadOnlyList<SandboxMount> Mounts
    {
        get => _mounts;
        init => _mounts = AgentCredentialMaterializationPolicy.SnapshotMounts(value, nameof(Mounts));
    }

    public void Deconstruct(
        out AgentKind Agent,
        out IReadOnlyDictionary<string, string> EnvironmentVariables,
        out IReadOnlyDictionary<string, string> Files)
    {
        Agent = this.Agent;
        EnvironmentVariables = this.EnvironmentVariables;
        Files = this.Files;
    }
}
