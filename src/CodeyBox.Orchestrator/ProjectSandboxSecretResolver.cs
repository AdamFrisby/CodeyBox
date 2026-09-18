using System.Collections.ObjectModel;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Resolves project-scoped test secrets into sandbox environment values at
/// provisioning time. This is a separate channel from
/// <see cref="AgentCredential"/>: values are read from host environment
/// variables named by the project declaration and are never selectable
/// through
/// <see cref="Sandbox.SandboxEnvironmentVariablePolicy.SelectDirectCredentialEnvironment"/>.
/// Guards (reserved-name rejection, NUL rejection, byte budgets) sit here,
/// adjacent to the <c>SandboxSpec.Environment</c> sink, so the check survives
/// future callers.
/// </summary>
public static class ProjectSandboxSecretResolver
{
    /// <summary>
    /// Returns the sandbox environment entries for <paramref name="scope"/>
    /// (for example <c>work</c>, <c>rework</c>, <c>audit-agent</c>,
    /// <c>audit-tool</c>, <c>merge</c>). Declarations whose host variable is
    /// unset or empty are skipped with a names-only warning; nothing logged
    /// or thrown here ever carries a secret value.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ResolveForScope(
        Project project,
        string scope,
        Func<string, string?> readHostEnvironment,
        ILogger? log = null)
        => ResolveForScope(project, workItemId: null, scope, readHostEnvironment, log);

    /// <summary>
    /// Returns the sandbox environment entries for <paramref name="scope"/>
    /// authorised for <paramref name="workItemId"/>. A secret is injected
    /// only when its group has a grant authorising this work item (default
    /// deny): a group with no matching grant injects nothing, and a missing
    /// grant is never an error — the item simply runs without the secret.
    /// Matching uses project identity (grants live on the project) and exact
    /// work-item identity only; no work-item content field is consulted.
    /// Phase scoping applies unchanged within a granted group. Every granted
    /// injection is logged names-only with its group and grant kind, so the
    /// audit log answers "why did this item hold that credential".
    /// </summary>
    public static IReadOnlyDictionary<string, string> ResolveForScope(
        Project project,
        WorkItemId? workItemId,
        string scope,
        Func<string, string?> readHostEnvironment,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(readHostEnvironment);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(scope) || project.SandboxSecrets.Count == 0)
            return new ReadOnlyDictionary<string, string>(result);

        long aggregateBytes = 0;
        foreach (var secret in project.SandboxSecrets)
        {
            if (!secret.AppliesTo(scope))
                continue;
            var grant = FindAuthorizingGrant(project.SandboxSecretGrants, secret.Group, workItemId);
            if (grant is null)
                continue;
            SandboxEnvironmentVariablePolicy.ValidateCredentialEnvironmentVariable(
                secret.SandboxEnvVar, nameof(project));
            var value = readHostEnvironment(secret.HostEnvVar);
            if (string.IsNullOrEmpty(value))
            {
                log?.LogWarning(
                    "Project {ProjectId} sandbox secret '{SandboxEnvVar}' skipped for scope '{Scope}': " +
                    "host environment variable '{HostEnvVar}' is not set or empty.",
                    project.Id.Value, secret.SandboxEnvVar, scope, secret.HostEnvVar);
                continue;
            }
            if (value.Contains('\0'))
                throw new ArgumentException(
                    $"Project '{project.Id.Value}' sandbox secret '{secret.SandboxEnvVar}' holds a NUL byte.",
                    nameof(project));
            var valueBytes = Encoding.UTF8.GetByteCount(value);
            if (valueBytes > ProjectSandboxSecretLimits.MaxSecretValueUtf8Bytes)
                throw new ArgumentException(
                    $"Project '{project.Id.Value}' sandbox secret '{secret.SandboxEnvVar}' exceeds the " +
                    $"per-value size limit.",
                    nameof(project));
            if (valueBytes > AgentCredentialMaterializationPolicy.MaterializationBudgetBytes - aggregateBytes)
                throw new ArgumentException(
                    $"Project '{project.Id.Value}' sandbox secrets exceed the sandbox credential budget.",
                    nameof(project));
            aggregateBytes += valueBytes;
            result[secret.SandboxEnvVar] = value;
            log?.LogInformation(
                "Project {ProjectId} injected sandbox secret '{SandboxEnvVar}' for scope '{Scope}' " +
                "from secret group '{Group}' via {GrantKind} grant.",
                project.Id.Value, secret.SandboxEnvVar, scope, secret.Group,
                DescribeGrantKind(grant));
        }
        return new ReadOnlyDictionary<string, string>(result);
    }

    /// <summary>
    /// Pure grant decision: the first grant authorising <paramref name="group"/>
    /// for <paramref name="workItemId"/>, or null when no grant matches
    /// (default deny). Group comparison is ordinal and case-sensitive; a
    /// work-item grant matches only on exact work-item identity. Null
    /// <paramref name="workItemId"/> matches project-wide grants only.
    /// Deterministic in (project config, work-item identity, group).
    /// </summary>
    public static ProjectSandboxSecretGrant? FindAuthorizingGrant(
        IReadOnlyList<ProjectSandboxSecretGrant> grants,
        string group,
        WorkItemId? workItemId)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        foreach (var grant in grants)
        {
            if (grant.Authorizes(group, workItemId))
                return grant;
        }
        return null;
    }

    /// <summary>
    /// Names-only attribution for every secret that would be injected for
    /// <paramref name="scope"/> and <paramref name="workItemId"/>: which
    /// group it came from and which grant authorised it. Carries no values
    /// by construction. A missing grant is not an error — the item simply
    /// has no attribution entries.
    /// </summary>
    public static IReadOnlyList<ProjectSandboxSecretInjection> DescribeInjections(
        Project project,
        WorkItemId? workItemId,
        string scope)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(scope))
            return [];
        var injections = new List<ProjectSandboxSecretInjection>();
        foreach (var secret in project.SandboxSecrets)
        {
            if (!secret.AppliesTo(scope))
                continue;
            var grant = FindAuthorizingGrant(project.SandboxSecretGrants, secret.Group, workItemId);
            if (grant is null)
                continue;
            injections.Add(new ProjectSandboxSecretInjection
            {
                SandboxEnvVar = secret.SandboxEnvVar,
                Group = secret.Group,
                GrantedProjectWide = grant.IsProjectWide,
                GrantedByImplicitMigration = grant.IsImplicit,
            });
        }
        return injections.AsReadOnly();
    }

    private static string DescribeGrantKind(ProjectSandboxSecretGrant grant)
        => grant.IsImplicit ? "implicit-migration project-wide"
            : grant.IsProjectWide ? "project-wide"
            : "work-item";

    /// <summary>
    /// Names-only descriptors for operator audit surfaces. Carries no values
    /// by construction.
    /// </summary>
    public static IReadOnlyList<ProjectSandboxSecretDescriptor> Describe(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.SandboxSecrets
            .Select(secret => new ProjectSandboxSecretDescriptor
            {
                HostEnvVar = secret.HostEnvVar,
                SandboxEnvVar = secret.SandboxEnvVar,
                Group = secret.Group,
                Scopes = secret.Scopes,
            })
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Operator audit view of the project's secret-group grants. Carries no
    /// values by construction — grants name groups and work-item ids only.
    /// </summary>
    public static IReadOnlyList<ProjectSandboxSecretGrant> DescribeGrants(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.SandboxSecretGrants.ToList().AsReadOnly();
    }
}
