namespace CodeyBox.HetznerSandboxPlugin;

/// <summary>
/// Resolves Hetzner Cloud connection material from operator configuration
/// plus the host credential chain (process environment). The API token comes
/// <em>only</em> from an environment variable — never from configuration
/// files — using the standard <c>HCLOUD_TOKEN</c> name unless the options
/// rename it. The non-secret base URL is configuration first with an
/// environment fallback, so plain config-file and env-driven deployments both
/// work without hardcoding.
/// </summary>
public static class HetznerCredentialChain
{
    /// <summary>Standard environment variable for the Hetzner Cloud API token. Never log this value.</summary>
    public const string TokenEnvVarName = "HCLOUD_TOKEN";

    /// <summary>Environment variable overriding the API base URL (non-secret).</summary>
    public const string ApiBaseUrlEnvVarName = "HCLOUD_API_ENDPOINT";

    /// <summary>Default public Hetzner Cloud API endpoint.</summary>
    public const string DefaultApiBaseUrl = "https://api.hetzner.cloud/v1";
}

/// <summary>
/// Resolved Hetzner Cloud connection material for one operation. The token
/// rides the <c>Authorization: Bearer</c> header only; it is never logged,
/// never persisted, and never copied into exception messages — keep it in this
/// short-lived record.
/// </summary>
public sealed record HetznerCredentials(Uri ApiBaseUrl, string ApiToken, bool AllowUnsafeHttp)
{
    /// <summary>
    /// Redacted: the default positional-record <c>ToString</c> would print
    /// the API token. Never let a <c>$"{creds}"</c> in a log or exception
    /// message leak it.
    /// </summary>
    public override string ToString() =>
        $"HetznerCredentials {{ ApiBaseUrl = {ApiBaseUrl}, ApiToken = [redacted], " +
        $"AllowUnsafeHttp = {AllowUnsafeHttp} }}";

    /// <summary>
    /// Resolves credentials from options plus the host environment. A missing
    /// token fails loudly, naming the variable; the base URL must be https
    /// (http only for loopback test URLs under <c>AllowUnsafeHttp</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">A required value is missing or malformed.</exception>
    public static HetznerCredentials Resolve(
        HetznerSandboxOptions options, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var baseUrlRaw = string.IsNullOrWhiteSpace(options.ApiBaseUrl)
            ? (environment(HetznerCredentialChain.ApiBaseUrlEnvVarName) ?? string.Empty).Trim()
            : options.ApiBaseUrl.Trim();
        if (baseUrlRaw.Length == 0)
            baseUrlRaw = HetznerCredentialChain.DefaultApiBaseUrl;
        if (!Uri.TryCreate(baseUrlRaw, UriKind.Absolute, out var baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"Hetzner API base URL is not an absolute http(s) URL: '{baseUrlRaw}'. " +
                $"Set CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:ApiBaseUrl or the " +
                $"{HetznerCredentialChain.ApiBaseUrlEnvVarName} environment variable.");
        }
        if (baseUrl.Scheme == Uri.UriSchemeHttp
            && !HetznerApiClient.IsCleartextHttpPermitted(baseUrl, options.AllowUnsafeHttp))
        {
            throw new InvalidOperationException(
                "Hetzner API base URL must use https://. AllowUnsafeHttp=true permits http " +
                "only for loopback test URLs, never for remote hosts.");
        }

        var tokenVar = string.IsNullOrWhiteSpace(options.TokenEnvVar)
            ? HetznerCredentialChain.TokenEnvVarName
            : options.TokenEnvVar.Trim();
        var token = (environment(tokenVar) ?? string.Empty).Trim();
        if (token.Length == 0)
        {
            throw new InvalidOperationException(
                $"Hetzner API token environment variable '{tokenVar}' is not set. " +
                "Provision it via the host credential chain — never in configuration files. " +
                "The token stays on the host: it is never copied into guest user-data, logs, or prompts.");
        }

        return new HetznerCredentials(baseUrl, token, options.AllowUnsafeHttp);
    }
}
