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
        }
        return new ReadOnlyDictionary<string, string>(result);
    }

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
                Scopes = secret.Scopes,
            })
            .ToList()
            .AsReadOnly();
    }
}
