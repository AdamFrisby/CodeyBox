namespace CodeyBox.Core;

/// <summary>
/// Canonical sandbox-phase scopes a project secret may be injected into.
/// Mirrors the per-phase network-profile vocabulary (<c>Work</c>,
/// <c>Rework</c>, <c>AuditAgent</c>, <c>AuditTool</c>, <c>Merge</c>) so the
/// operator reasons about one phase list in both places.
/// </summary>
public static class ProjectSandboxSecretScopes
{
    public const string Work = "work";
    public const string Rework = "rework";

    /// <summary>Matches both audit-agent and audit-tool sandboxes.</summary>
    public const string Audit = "audit";
    public const string AuditAgent = "audit-agent";
    public const string AuditTool = "audit-tool";
    public const string Merge = "merge";

    /// <summary>
    /// Delegation turns reuse the work-phase runner path; they receive
    /// rework-scoped secrets.
    /// </summary>
    public const string Delegation = "delegation";

    /// <summary>Scopes applied when a declaration omits its phase list.</summary>
    public static readonly IReadOnlyList<string> Defaults = [Work, Rework];

    /// <summary>Every scope name accepted in project configuration.</summary>
    public static readonly IReadOnlyList<string> All = [Work, Rework, Audit, AuditAgent, AuditTool, Merge];

    /// <summary>
    /// Normalises a configured scope token to its canonical lowercase form,
    /// or null when it is not a known scope.
    /// </summary>
    public static string? TryNormalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim().ToLowerInvariant();
        foreach (var known in All)
        {
            if (string.Equals(normalized, known, StringComparison.Ordinal))
                return known;
        }
        return null;
    }

    /// <summary>
    /// Returns true when a secret declared with <paramref name="declaredScopes"/>
    /// is in scope for a sandbox running <paramref name="sandboxScope"/>.
    /// The <c>audit</c> declaration matches both audit-agent and audit-tool
    /// sandboxes; a <c>delegation</c> sandbox is treated as rework.
    /// </summary>
    public static bool MatchesScope(IReadOnlyList<string> declaredScopes, string sandboxScope)
    {
        ArgumentNullException.ThrowIfNull(declaredScopes);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxScope);
        var normalizedSandbox = string.Equals(sandboxScope, Delegation, StringComparison.OrdinalIgnoreCase)
            ? Rework
            : sandboxScope.Trim().ToLowerInvariant();
        foreach (var declared in declaredScopes)
        {
            var normalized = declared.Trim().ToLowerInvariant();
            if (string.Equals(normalized, normalizedSandbox, StringComparison.Ordinal))
                return true;
            if (string.Equals(normalized, Audit, StringComparison.Ordinal)
                && (string.Equals(normalizedSandbox, AuditAgent, StringComparison.Ordinal)
                    || string.Equals(normalizedSandbox, AuditTool, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }
}

/// <summary>Bounds for project-scoped sandbox-secret declarations.</summary>
public static class ProjectSandboxSecretLimits
{
    /// <summary>Maximum SandboxSecrets entries accepted per project.</summary>
    public const int MaxSecretsPerProject = 32;

    /// <summary>Maximum UTF-8 bytes accepted in one resolved secret value.</summary>
    public const int MaxSecretValueUtf8Bytes = 64 * 1024;
}

/// <summary>
/// One project-scoped test secret, declared by reference: the host environment
/// variable naming the secret and the sandbox environment variable receiving
/// it. The literal value never appears in project configuration.
/// </summary>
public sealed record ProjectSandboxSecret
{
    /// <summary>Name of the host environment variable holding the secret value. Read at sandbox-provisioning time.</summary>
    public required string HostEnvVar { get; init; }

    /// <summary>Name of the sandbox environment variable the value is injected as.</summary>
    public required string SandboxEnvVar { get; init; }

    /// <summary>
    /// Canonical lowercase scopes this secret is injected into. Empty means
    /// the declaration default (<c>work</c> + <c>rework</c>); the repository
    /// always resolves to an explicit non-empty list.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; init; } = ProjectSandboxSecretScopes.Defaults;

    /// <summary>True when this secret is injected into a sandbox running <paramref name="sandboxScope"/>.</summary>
    public bool AppliesTo(string sandboxScope)
        => ProjectSandboxSecretScopes.MatchesScope(Scopes, sandboxScope);
}

/// <summary>
/// Names-only view of a project's injected secrets for operator audit
/// surfaces. Carries no value by construction — there is no value field.
/// </summary>
public sealed record ProjectSandboxSecretDescriptor
{
    public required string HostEnvVar { get; init; }
    public required string SandboxEnvVar { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
}
