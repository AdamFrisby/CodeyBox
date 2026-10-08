namespace CodeyBox.GceSandboxPlugin;

/// <summary>
/// Mockable seam for the workload/machine identity behind every Compute Engine call.
/// The production implementation reads a pre-provisioned OAuth2 access token from the
/// host process environment (variable name from
/// <see cref="GceSandboxOptions.AccessTokenEnvVar"/>). There is deliberately no
/// Application Default Credentials fallback: personal <c>gcloud auth</c> sessions must
/// never silently become the identity that provisions project infrastructure.
/// </summary>
public interface IGceCredentialSource
{
    /// <summary>Returns the bearer token for Compute Engine calls. Throws when absent.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken ct);
}

/// <summary>
/// Host-owned credential chain for the GCE provider: an explicit workload/machine
/// identity injected behind <see cref="IGceCredentialSource"/>, read from the host
/// environment at call time so rotation propagates without a restart. The token value
/// is never written to configuration, guest metadata, user-data, logs, or prompts.
/// </summary>
public static class GceCredentialChain
{
    /// <summary>Default environment variable holding the workload identity's access token.</summary>
    public const string AccessTokenEnvVarName = "GCE_ACCESS_TOKEN";

    /// <summary>
    /// Resolves the bearer token from the configured environment variable. Throws
    /// <see cref="InvalidOperationException"/> (never returns a fallback identity) when
    /// the variable is missing or blank.
    /// </summary>
    public static string Resolve(GceSandboxOptions options, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        var variable = string.IsNullOrWhiteSpace(options.AccessTokenEnvVar)
            ? AccessTokenEnvVarName
            : options.AccessTokenEnvVar.Trim();
        var token = environment(variable);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"GCE workload identity is not configured: environment variable '{variable}' is missing or blank. " +
                $"Provision an explicit workload/machine identity and export its access token there; " +
                $"personal ADC credentials are never used as a fallback.");
        }
        return token.Trim();
    }
}

/// <summary>Production <see cref="IGceCredentialSource"/> over the host process environment.</summary>
public sealed class EnvironmentGceCredentialSource(
    GceSandboxOptions options,
    Func<string, string?> environment) : IGceCredentialSource
{
    private readonly GceSandboxOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly Func<string, string?> _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    public Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        _ = ct;
        return Task.FromResult(GceCredentialChain.Resolve(_options, _environment));
    }
}
