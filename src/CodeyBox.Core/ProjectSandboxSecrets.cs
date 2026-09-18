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
/// Naming rules for project sandbox-secret groups. A group is the unit of
/// authorisation: a secret is injected only when an operator grant authorises
/// its group for the requesting work item. The word "group" is deliberately
/// distinct from <see cref="ProjectSandboxSecretScopes"/> ("scopes" = phases);
/// the two axes compose — a grant decides whether a group applies to an item
/// at all, phase scoping decides which sandboxes within it receive the value.
/// </summary>
public static class ProjectSandboxSecretGroups
{
    /// <summary>
    /// Implicit group for declarations that name no group. The repository maps
    /// today's flat declarations here and synthesises an implicit
    /// project-wide grant for them (see
    /// <see cref="ProjectSandboxSecretGrant.IsImplicit"/>), so pre-grant
    /// configurations keep working. The implicit grant is explicit in the
    /// resolved model and in load logs — never silent.
    /// </summary>
    public const string Default = "default";

    /// <summary>Maximum characters accepted in a group name.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// True when <paramref name="value"/> is a usable group name: 1–64 chars
    /// of letters, digits, <c>-</c>, <c>_</c> or <c>.</c>, starting with a
    /// letter or digit. Comparison elsewhere is ordinal and case-sensitive.
    /// </summary>
    public static bool IsValidName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
            return false;
        if (!char.IsLetterOrDigit(value[0]))
            return false;
        foreach (var ch in value)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Normalises a configured group token (trims whitespace); empty resolves
    /// to <see cref="Default"/>. Throws <see cref="ArgumentException"/> when
    /// the result is not a valid name.
    /// </summary>
    public static string NormalizeOrDefault(string? value, string where)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return Default;
        if (!IsValidName(trimmed))
            throw new ArgumentException(
                $"{where} names an invalid secret group '{trimmed}': use 1–64 letters, digits, '-', '_' or '.', " +
                $"starting with a letter or digit.",
                nameof(value));
        return trimmed;
    }
}

/// <summary>
/// Operator-declared authorisation for one secret group. A grant names a
/// group and applies to the whole project, optionally narrowed to a single
/// work item. Matching uses project identity (grants live on the project)
/// and exact work-item identity only: nothing a work item contains — prompt,
/// title, capabilities, knobs, external ids — can cause a grant to match.
/// </summary>
public sealed record ProjectSandboxSecretGrant
{
    /// <summary>Secret group this grant authorises. Case-sensitive, ordinal match.</summary>
    public required string Group { get; init; }

    /// <summary>
    /// When set, this grant authorises only the work item with this identity
    /// (exact <see cref="Guid"/> equality). Null means project-wide: every
    /// item in the project.
    /// </summary>
    public Guid? WorkItemId { get; init; }

    /// <summary>True when this grant applies to every item in the project.</summary>
    public bool IsProjectWide => WorkItemId is null;

    /// <summary>
    /// True for the repository-synthesised migration grant that keeps
    /// pre-grant flat declarations working. Operator-declared grants are
    /// never implicit. Recorded on every injection so the audit trail shows
    /// whether authorisation came from an operator or from migration.
    /// </summary>
    public bool IsImplicit { get; init; }

    /// <summary>
    /// True when this grant authorises <paramref name="group"/> for
    /// <paramref name="workItemId"/>. Pure and deterministic: project config,
    /// work-item identity, done. Null <paramref name="workItemId"/> matches
    /// project-wide grants only.
    /// </summary>
    public bool Authorizes(string group, WorkItemId? workItemId)
    {
        if (!string.Equals(Group, group, StringComparison.Ordinal))
            return false;
        if (WorkItemId is null)
            return true;
        return workItemId is not null && WorkItemId.Value == workItemId.Value.Value;
    }
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
    /// Secret group this secret is declared into. The group is the unit of
    /// authorisation: injection requires an operator grant for this group.
    /// Defaults to <see cref="ProjectSandboxSecretGroups.Default"/>; the
    /// repository maps declarations without an explicit group there.
    /// </summary>
    public string Group { get; init; } = ProjectSandboxSecretGroups.Default;

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
    public required string Group { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
}

/// <summary>
/// Names-only attribution for one injected secret: which group it came from
/// and which grant authorised it, so "why did this item hold that credential"
/// is answerable afterwards from the audit log. Carries no value by
/// construction — there is no value field.
/// </summary>
public sealed record ProjectSandboxSecretInjection
{
    /// <summary>Name of the sandbox environment variable the value was injected as.</summary>
    public required string SandboxEnvVar { get; init; }

    /// <summary>Secret group the injected secret was declared into.</summary>
    public required string Group { get; init; }

    /// <summary>True when authorisation came from a project-wide grant; false for a work-item grant.</summary>
    public required bool GrantedProjectWide { get; init; }

    /// <summary>
    /// True when authorisation came from the repository-synthesised migration
    /// grant rather than an operator-declared grant.
    /// </summary>
    public required bool GrantedByImplicitMigration { get; init; }
}
