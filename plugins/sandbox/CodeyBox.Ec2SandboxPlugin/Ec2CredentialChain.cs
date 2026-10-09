namespace CodeyBox.Ec2SandboxPlugin;

/// <summary>
/// Resolves AWS connection material from operator configuration plus the
/// host credential chain (process environment). The access key id and secret
/// access key come <em>only</em> from environment variables — never from
/// configuration files — using the standard <c>AWS_ACCESS_KEY_ID</c> /
/// <c>AWS_SECRET_ACCESS_KEY</c> / <c>AWS_SESSION_TOKEN</c> names unless the
/// options rename them. There is deliberately no shared-config file, ECS/EKS
/// role, or instance-metadata fallback: ambient personal or machine identity
/// must never silently become the identity that provisions infrastructure.
/// </summary>
public static class Ec2CredentialChain
{
    /// <summary>Standard environment variable for the AWS access key id. Never log its value.</summary>
    public const string AccessKeyEnvVarName = "AWS_ACCESS_KEY_ID";

    /// <summary>Standard environment variable for the AWS secret access key. Never log this value.</summary>
    public const string SecretKeyEnvVarName = "AWS_SECRET_ACCESS_KEY";

    /// <summary>Standard environment variable for the optional AWS session token. Never log this value.</summary>
    public const string SessionTokenEnvVarName = "AWS_SESSION_TOKEN";
}

/// <summary>
/// Resolved AWS signing identity for one operation. The secret rides the
/// SigV4 HMAC chain only; it is never logged, never persisted, never placed
/// in a query string, and never copied into exception messages, guest
/// user-data, logs, or prompts — keep it in this short-lived record.
/// </summary>
public sealed record Ec2Credentials(
    string AccessKeyId,
    string SecretAccessKey,
    string? SessionToken,
    string Region,
    Uri ServiceUrl,
    bool AllowUnsafeHttp)
{
    /// <summary>
    /// Redacted: the default positional-record <c>ToString</c> would print
    /// the secret. Never let a <c>$"{creds}"</c> in a log or exception
    /// message leak it.
    /// </summary>
    public override string ToString() =>
        $"Ec2Credentials {{ AccessKeyId = {RedactTail(AccessKeyId)}, " +
        $"SecretAccessKey = [redacted], SessionToken = {(SessionToken is null ? "(absent)" : "[redacted]")}, " +
        $"Region = {Region}, ServiceUrl = {ServiceUrl}, AllowUnsafeHttp = {AllowUnsafeHttp} }}";

    internal static string RedactTail(string value) =>
        value.Length <= 4 ? "****" : "****" + value[^4..];

    /// <summary>
    /// Resolves credentials from options plus the host environment. A missing
    /// access key or secret fails loudly, naming the variable; the region
    /// must look like an AWS region and the endpoint must be https (http
    /// only for loopback test URLs under <c>AllowUnsafeHttp</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">A required value is missing or malformed.</exception>
    public static Ec2Credentials Resolve(
        Ec2SandboxOptions options, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var region = options.Region.Trim();
        if (!Ec2Placement.IsValidRegion(region))
        {
            throw new InvalidOperationException(
                $"EC2 region '{region}' is not a valid AWS region name (expected e.g. us-east-1). " +
                $"Set CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:Region explicitly.");
        }
        if (!string.IsNullOrWhiteSpace(options.Zone)
            && !Ec2Placement.IsValidZone(options.Zone.Trim(), region))
        {
            throw new InvalidOperationException(
                $"EC2 zone '{options.Zone.Trim()}' does not belong to region '{region}'. " +
                $"Set CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:Zone to a zone in that region, or leave it empty.");
        }

        var serviceUrlRaw = string.IsNullOrWhiteSpace(options.ServiceUrl)
            ? $"https://ec2.{region}.amazonaws.com"
            : options.ServiceUrl.Trim();
        if (!Uri.TryCreate(serviceUrlRaw, UriKind.Absolute, out var serviceUrl)
            || (serviceUrl.Scheme != Uri.UriSchemeHttps && serviceUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"EC2 service URL is not an absolute http(s) URL: '{serviceUrlRaw}'. " +
                $"Set CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:ServiceUrl to an https endpoint.");
        }
        if (serviceUrl.Scheme == Uri.UriSchemeHttp
            && !Ec2ApiClient.IsCleartextHttpPermitted(serviceUrl, options.AllowUnsafeHttp))
        {
            throw new InvalidOperationException(
                "EC2 service URL must use https://. AllowUnsafeHttp=true permits http " +
                "only for loopback test URLs, never for remote hosts.");
        }

        var accessVar = string.IsNullOrWhiteSpace(options.AccessKeyEnvVar)
            ? Ec2CredentialChain.AccessKeyEnvVarName
            : options.AccessKeyEnvVar.Trim();
        var secretVar = string.IsNullOrWhiteSpace(options.SecretKeyEnvVar)
            ? Ec2CredentialChain.SecretKeyEnvVarName
            : options.SecretKeyEnvVar.Trim();
        var accessKey = (environment(accessVar) ?? string.Empty).Trim();
        var secretKey = (environment(secretVar) ?? string.Empty).Trim();
        if (accessKey.Length == 0 || secretKey.Length == 0)
        {
            var missing = accessKey.Length == 0 ? accessVar : secretVar;
            throw new InvalidOperationException(
                $"EC2 signing identity environment variable '{missing}' is not set. " +
                "Provision an explicit IAM identity and export its key pair there — " +
                "shared-config files, container roles, and instance metadata are never used as a fallback. " +
                "The secret stays on the host: it is never copied into guest user-data, logs, or prompts.");
        }

        string? sessionToken = null;
        var sessionVar = string.IsNullOrWhiteSpace(options.SessionTokenEnvVar)
            ? Ec2CredentialChain.SessionTokenEnvVarName
            : options.SessionTokenEnvVar.Trim();
        if (!string.IsNullOrWhiteSpace(sessionVar))
        {
            var rawToken = (environment(sessionVar) ?? string.Empty).Trim();
            if (rawToken.Length > 0)
                sessionToken = rawToken;
        }

        return new Ec2Credentials(accessKey, secretKey, sessionToken, region, serviceUrl, options.AllowUnsafeHttp);
    }
}
