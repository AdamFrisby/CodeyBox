namespace CodeyBox.OpenStackSandboxPlugin;

/// <summary>
/// Resolves OpenStack connection material from operator configuration plus the
/// host credential chain (process environment). The application-credential id
/// and secret come <em>only</em> from environment variables — never from
/// configuration files — using the standard OpenStack names unless the options
/// rename them. Non-secret values (auth URL, region, interface) are
/// configuration first with the standard environment variables as fallback, so
/// plain config-file deployments and env-driven deployments both work without
/// hardcoding a cloud.
/// </summary>
public static class OpenStackCredentialChain
{
    /// <summary>Standard environment variable for the Keystone auth URL (non-secret).</summary>
    public const string AuthUrlEnvVarName = "OS_AUTH_URL";

    /// <summary>Standard environment variable for the application-credential id (non-secret).</summary>
    public const string CredentialIdEnvVarName = "OS_APPLICATION_CREDENTIAL_ID";

    /// <summary>Standard environment variable for the application-credential secret. Never log this value.</summary>
    public const string CredentialSecretEnvVarName = "OS_APPLICATION_CREDENTIAL_SECRET";

    /// <summary>Standard environment variable for the region (non-secret).</summary>
    public const string RegionEnvVarName = "OS_REGION_NAME";

    /// <summary>Standard environment variable for the catalog interface (non-secret).</summary>
    public const string InterfaceEnvVarName = "OS_INTERFACE";

    /// <summary>Default catalog interface.</summary>
    public const string DefaultInterface = "public";

    /// <summary>Exact-match allowlist of catalog interfaces. Never a substring match.</summary>
    public static readonly IReadOnlySet<string> KnownInterfaces =
        new HashSet<string>(StringComparer.Ordinal) { "public", "internal", "admin" };
}

/// <summary>
/// Resolved OpenStack connection material for one operation. The secret rides
/// Keystone auth requests only; it is never logged, never persisted, and never
/// copied into exception messages — keep it in this short-lived record.
/// </summary>
public sealed record OpenStackCredentials(
    Uri AuthUrl,
    string ApplicationCredentialId,
    string ApplicationCredentialSecret,
    string Region,
    string Interface,
    bool AllowUnsafeHttp)
{
    /// <summary>
    /// Resolves credentials from options plus the host environment. Empty
    /// option values fall back to the standard <c>OS_*</c> variables for the
    /// non-secret fields; the credential id and secret have no fallback —
    /// a missing variable fails loudly, naming the variable.
    /// </summary>
    /// <exception cref="InvalidOperationException">A required value is missing or malformed.</exception>
    public static OpenStackCredentials Resolve(
        OpenStackSandboxOptions options, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var authUrlRaw = string.IsNullOrWhiteSpace(options.AuthUrl)
            ? (environment(OpenStackCredentialChain.AuthUrlEnvVarName) ?? string.Empty).Trim()
            : options.AuthUrl.Trim();
        if (!Uri.TryCreate(authUrlRaw, UriKind.Absolute, out var authUrl)
            || (authUrl.Scheme != Uri.UriSchemeHttps && authUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"OpenStack auth URL is not an absolute http(s) URL: '{authUrlRaw}'. " +
                $"Set CodeyBox:Plugins:{OpenStackSandboxOptions.PluginId}:AuthUrl or the " +
                $"{OpenStackCredentialChain.AuthUrlEnvVarName} environment variable.");
        }
        if (authUrl.Scheme == Uri.UriSchemeHttp
            && !OpenStackApiClient.IsCleartextHttpPermitted(authUrl, options.AllowUnsafeHttp))
        {
            throw new InvalidOperationException(
                $"OpenStack auth URL must use https://. AllowUnsafeHttp=true permits http " +
                $"only for loopback test URLs, never for remote hosts.");
        }

        var idVar = string.IsNullOrWhiteSpace(options.CredentialIdEnvVar)
            ? OpenStackCredentialChain.CredentialIdEnvVarName
            : options.CredentialIdEnvVar.Trim();
        var secretVar = string.IsNullOrWhiteSpace(options.CredentialSecretEnvVar)
            ? OpenStackCredentialChain.CredentialSecretEnvVarName
            : options.CredentialSecretEnvVar.Trim();
        var credentialId = (environment(idVar) ?? string.Empty).Trim();
        if (credentialId.Length == 0)
        {
            throw new InvalidOperationException(
                $"OpenStack application-credential id environment variable '{idVar}' is not set. " +
                "Provision it via the host credential chain — never in configuration files.");
        }
        var credentialSecret = (environment(secretVar) ?? string.Empty).Trim();
        if (credentialSecret.Length == 0)
        {
            throw new InvalidOperationException(
                $"OpenStack application-credential secret environment variable '{secretVar}' is not set. " +
                "Provision it via the host credential chain — never in configuration files.");
        }

        var region = string.IsNullOrWhiteSpace(options.Region)
            ? (environment(OpenStackCredentialChain.RegionEnvVarName) ?? string.Empty).Trim()
            : options.Region.Trim();

        // Interface: an explicit non-default configuration value always wins;
        // otherwise the standard environment variable applies, defaulting to
        // "public". (The options default IS "public", so a stock config still
        // honours OS_INTERFACE; setting Interface explicitly takes precedence.)
        var configured = options.Interface.Trim();
        var envIface = (environment(OpenStackCredentialChain.InterfaceEnvVarName) ?? string.Empty).Trim();
        var iface = configured.Length != 0
            && (envIface.Length == 0
                || !string.Equals(configured, OpenStackCredentialChain.DefaultInterface, StringComparison.Ordinal))
            ? configured
            : envIface.Length != 0 ? envIface : OpenStackCredentialChain.DefaultInterface;
        if (!OpenStackCredentialChain.KnownInterfaces.Contains(iface))
        {
            throw new InvalidOperationException(
                $"OpenStack catalog interface '{iface}' is unknown. Use one of: public, internal, admin.");
        }

        return new OpenStackCredentials(authUrl, credentialId, credentialSecret, region, iface, options.AllowUnsafeHttp);
    }
}
