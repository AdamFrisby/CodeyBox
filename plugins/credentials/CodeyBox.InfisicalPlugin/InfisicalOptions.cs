using Microsoft.Extensions.Configuration;
using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.InfisicalPlugin;

/// <summary>
/// One operator-declared mapping from a CodeyBox sandbox secret to an
/// Infisical secret. The mapping is what puts a secret group "against this
/// backend": <see cref="InfisicalSecretProvider.CanIssue"/> is true only for
/// secrets carrying a <c>SandboxEnvVar</c> named here, so a group the
/// operator never mapped is never fetched, let alone injected.
/// </summary>
public sealed record InfisicalSecretMapping
{
    /// <summary>
    /// CodeyBox sandbox variable this mapping serves. Matched exactly
    /// (ordinal) against <c>ProjectSandboxSecret.SandboxEnvVar</c>.
    /// </summary>
    public string SandboxEnvVar { get; init; } = string.Empty;

    /// <summary>
    /// Optional group guard: when set, the project secret must additionally
    /// belong to this group (ordinal exact match). The grant check stays
    /// with the manager; this is defence in depth against mapping a var
    /// that another group reuses.
    /// </summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>
    /// Infisical static secret key (<c>GET /api/v3/secrets/raw/{key}</c>).
    /// Exactly one of this and <see cref="DynamicSecretName"/> must be set.
    /// </summary>
    public string SecretKey { get; init; } = string.Empty;

    /// <summary>
    /// Infisical dynamic-secret name (<c>POST /api/v1/dynamic-secrets/leases</c>).
    /// Exactly one of this and <see cref="SecretKey"/> must be set.
    /// </summary>
    public string DynamicSecretName { get; init; } = string.Empty;

    /// <summary>
    /// Field selected out of a dynamic lease's <c>data</c> object (for
    /// example <c>password</c>). Required for dynamic mappings; unused for
    /// static ones. The lease <c>data</c> shape varies by dynamic-secret
    /// type, so the operator names the field that holds the credential.
    /// </summary>
    public string DataField { get; init; } = string.Empty;

    /// <summary>
    /// Infisical secret path (folder), default <c>/</c>. Applies to both
    /// static fetches (<c>secretPath</c> query) and dynamic leases
    /// (<c>path</c> body/query).
    /// </summary>
    public string SecretPath { get; init; } = "/";

    /// <summary>
    /// TTL handed to Infisical when creating a dynamic lease (Infisical
    /// duration syntax, for example <c>1h</c>). Empty omits the field and
    /// the server default applies. Unused for static mappings.
    /// </summary>
    public string Ttl { get; init; } = string.Empty;

    /// <summary>
    /// When true the value never enters the guest: the sandbox receives
    /// only the broker endpoint URL and the proxy attaches the credential
    /// server-side. Requires <c>BrokerEnabled</c> plus
    /// <see cref="BrokerUpstreamBaseUrl"/>.
    /// </summary>
    public bool Brokered { get; init; }

    /// <summary>
    /// Upstream origin the broker forwards to for this mapping (scheme +
    /// host, for example <c>https://api.example.com</c>). The broker
    /// refuses to forward anywhere else.
    /// </summary>
    public string BrokerUpstreamBaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Header the broker injects the credential as (for example
    /// <c>Authorization</c>). Required for brokered mappings.
    /// </summary>
    public string BrokerInjectHeader { get; init; } = "Authorization";

    /// <summary>
    /// Scheme prefix for the injected header value (<c>Bearer</c>,
    /// <c>Token</c>, or empty for the raw value).
    /// </summary>
    public string BrokerInjectScheme { get; init; } = "Bearer";

    /// <summary>
    /// Exact upstream paths the broker forwards for this mapping
    /// (ordinal match, query excluded). Empty allows any path under
    /// <see cref="BrokerUpstreamBaseUrl"/>.
    /// </summary>
    public IReadOnlyList<string> BrokerAllowedPaths { get; init; } = [];
}

/// <summary>
/// Operator knobs for the Infisical credential plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.infisical</c>. Every operational value lives
/// here — never as a literal in source — and the section is re-read on every
/// issue/renew/revoke so edits take effect without a host restart. The one
/// exception is <c>TimeoutSeconds</c>: it is baked into the HTTP client when
/// that client is first built, so changing it takes effect on the next host
/// restart.
/// <para>Secrets never appear here: <see cref="ClientIdEnvVar"/>,
/// <see cref="ClientSecretEnvVar"/> and <see cref="AccessTokenEnvVar"/> name
/// environment variables whose values the operator provisions from the host
/// credential chain (vault agent, systemd credentials, container secrets).
/// Only the names are configured.</para>
/// </summary>
public sealed record InfisicalOptions : ICredentialOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.infisical";

    /// <summary>Master switch. Default false: off unless an operator enables it.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Infisical site origin (scheme + host, no trailing path). Defaults to
    /// Infisical Cloud; self-hosted instances set their own origin. Must be
    /// http(s). Self-hosted over plain http is accepted only for loopback
    /// hosts (local development); anything else requires https.
    /// </summary>
    public string SiteUrl { get; init; } = "https://app.infisical.com";

    /// <summary>Name of the env var holding the machine-identity client ID (universal auth).</summary>
    public string ClientIdEnvVar { get; init; } = "INFISICAL_CLIENT_ID";

    /// <summary>Name of the env var holding the machine-identity client secret (universal auth).</summary>
    public string ClientSecretEnvVar { get; init; } = "INFISICAL_CLIENT_SECRET";

    /// <summary>
    /// Name of the env var holding a ready-made Infisical access token.
    /// Empty disables this path. When both universal-auth credentials and a
    /// token are present, universal auth wins so short-lived machine
    /// credentials stay the primary path.
    /// </summary>
    public string AccessTokenEnvVar { get; init; } = string.Empty;

    /// <summary>Infisical project slug for dynamic-lease endpoints (they address the project by slug).</summary>
    public string ProjectSlug { get; init; } = string.Empty;

    /// <summary>Infisical project ID owning the secrets. Required.</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>Infisical environment slug (for example <c>prod</c>). Required.</summary>
    public string Environment { get; init; } = "prod";

    /// <summary>
    /// Client-side validity window for a static-secret lease. Renewal
    /// re-fetches, so rotation propagates within one window. Kept in step
    /// with the host <c>SecretLeasing:DefaultLeaseTtl</c> default (20 min).
    /// Range 1–1440 minutes.
    /// </summary>
    public int StaticLeaseTtlMinutes { get; init; } = 20;

    /// <summary>Per-request timeout, in seconds (1–300, default 30).</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Hard cap on an Infisical response body, in bytes. Enforced while
    /// buffering, before parsing. Default 256 KiB.
    /// </summary>
    public int MaxResponseBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// Seconds of clock skew tolerated when deciding an access token needs
    /// refresh before it actually expires. Default 60.
    /// </summary>
    public int TokenRefreshSkewSeconds { get; init; } = 60;

    /// <summary>
    /// Operator-declared secret mappings: the only secrets this backend
    /// serves. Empty means the provider issues nothing.
    /// </summary>
    public IReadOnlyList<InfisicalSecretMapping> Mappings { get; init; } = [];

    /// <summary>
    /// Master switch for the brokered (Agent Proxy) path. Default false so
    /// retrieval and brokering stay separable: either can be used alone.
    /// </summary>
    public bool BrokerEnabled { get; init; }

    /// <summary>Interface the loopback broker binds (default loopback only).</summary>
    public string BrokerBindHost { get; init; } = "127.0.0.1";

    /// <summary>Broker bind port; 0 selects an ephemeral port.</summary>
    public int BrokerBindPort { get; init; }

    /// <summary>
    /// Host name advertised in broker endpoint URLs. Defaults to the bind
    /// host. Set to the host-gateway address when guests run in VMs without
    /// shared loopback (and move <see cref="BrokerBindHost"/> to the
    /// matching interface — never 0.0.0.0 without a firewall).
    /// </summary>
    public string BrokerAdvertiseHost { get; init; } = string.Empty;

    /// <summary>Maximum proxied request/response body, in bytes (default 1 MiB).</summary>
    public int BrokerMaxBodyBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum concurrent broker forwards (default 16).</summary>
    public int BrokerMaxConcurrency { get; init; } = 16;

    /// <summary>Maximum broker endpoint path length accepted (default 512 chars).</summary>
    public int BrokerMaxPathChars { get; init; } = 512;

    /// <summary>
    /// Binds the plugin configuration section to options. Invalid values
    /// fall back to safe defaults (disabled features, bounded numbers) and
    /// are surfaced via <paramref name="warnings"/> instead of throwing, so
    /// a bad hot-reload never breaks issuance.
    /// </summary>
    public static InfisicalOptions FromConfiguration(
        IConfigurationSection section, List<string>? warnings = null)
    {
        var defaults = new InfisicalOptions();
        if (section is null)
            return defaults;

        return new InfisicalOptions
        {
            Enabled = CredentialOptions.ReadBool(section, "Enabled", defaults.Enabled, warnings),
            SiteUrl = CredentialOptions.ReadNonEmpty(section, "SiteUrl", defaults.SiteUrl).TrimEnd('/'),
            ClientIdEnvVar = CredentialOptions.ReadNonEmpty(section, "ClientIdEnvVar", defaults.ClientIdEnvVar),
            ClientSecretEnvVar = CredentialOptions.ReadNonEmpty(section, "ClientSecretEnvVar", defaults.ClientSecretEnvVar),
            AccessTokenEnvVar = (section["AccessTokenEnvVar"] ?? string.Empty).Trim(),
            WorkspaceId = (section["WorkspaceId"] ?? string.Empty).Trim(),
            ProjectSlug = (section["ProjectSlug"] ?? string.Empty).Trim(),
            Environment = CredentialOptions.ReadNonEmpty(section, "Environment", defaults.Environment),
            StaticLeaseTtlMinutes = CredentialOptions.ReadStaticLeaseTtlMinutes(
                section, defaults.StaticLeaseTtlMinutes, warnings),
            TimeoutSeconds = CredentialOptions.ReadTimeoutSeconds(
                section, "TimeoutSeconds", defaults.TimeoutSeconds, warnings),
            MaxResponseBytes = CredentialOptions.ReadByteCap(
                section, "MaxResponseBytes", defaults.MaxResponseBytes, warnings),
            TokenRefreshSkewSeconds = CredentialOptions.ReadTokenRefreshSkewSeconds(
                section, defaults.TokenRefreshSkewSeconds, warnings),
            Mappings = ReadMappings(section.GetSection("Mappings"), warnings),
            BrokerEnabled = CredentialOptions.ReadBool(section, "BrokerEnabled", defaults.BrokerEnabled, warnings),
            BrokerBindHost = CredentialOptions.ReadNonEmpty(section, "BrokerBindHost", defaults.BrokerBindHost),
            BrokerBindPort = Math.Clamp(
                CredentialOptions.ReadInt(section, "BrokerBindPort", defaults.BrokerBindPort, warnings), 0, 65535),
            BrokerAdvertiseHost = (section["BrokerAdvertiseHost"] ?? string.Empty).Trim(),
            BrokerMaxBodyBytes = CredentialOptions.ReadByteCap(
                section, "BrokerMaxBodyBytes", defaults.BrokerMaxBodyBytes, warnings),
            BrokerMaxConcurrency = Math.Clamp(
                CredentialOptions.ReadInt(section, "BrokerMaxConcurrency", defaults.BrokerMaxConcurrency, warnings), 1, 256),
            BrokerMaxPathChars = Math.Clamp(
                CredentialOptions.ReadInt(section, "BrokerMaxPathChars", defaults.BrokerMaxPathChars, warnings), 64, 4096),
        };
    }

    /// <summary>
    /// Pure validation: returns every operator-facing problem with the
    /// options, or an empty list when the configuration is usable. Never
    /// touches the network and never reads secret values — only names.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        CredentialOptions.ValidateEndpointUrl("SiteUrl", SiteUrl, errors);

        if (string.IsNullOrWhiteSpace(WorkspaceId))
            errors.Add("WorkspaceId is required (the Infisical project ID).");
        if (string.IsNullOrWhiteSpace(Environment))
            errors.Add("Environment is required (for example 'prod').");
        if (Mappings.Any(m => !string.IsNullOrWhiteSpace(m.DynamicSecretName))
            && string.IsNullOrWhiteSpace(ProjectSlug))
            errors.Add("ProjectSlug is required when a dynamic-secret mapping exists (dynamic-lease endpoints address the project by slug).");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in Mappings)
        {
            if (!CredentialOptions.ValidateSandboxEnvVar(mapping.SandboxEnvVar, seen, errors, out var where))
                continue;
            var hasStatic = !string.IsNullOrWhiteSpace(mapping.SecretKey);
            var hasDynamic = !string.IsNullOrWhiteSpace(mapping.DynamicSecretName);
            if (hasStatic == hasDynamic)
                errors.Add($"{where}: set exactly one of SecretKey and DynamicSecretName.");
            if (hasDynamic && string.IsNullOrWhiteSpace(mapping.DataField))
                errors.Add($"{where}: dynamic mappings require DataField (the credential field inside the lease data).");
            if (mapping.Brokered)
            {
                if (!BrokerEnabled)
                    errors.Add($"{where}: brokered but BrokerEnabled is false; enable the broker or drop Brokered.");
                // The broker attaches the credential upstream-side: the
                // same https-everywhere rule as SiteUrl applies here, so an
                // injected secret never crosses the network in clear.
                CredentialOptions.ValidateEndpointUrl(
                    "BrokerUpstreamBaseUrl", mapping.BrokerUpstreamBaseUrl, errors, where);
                if (string.IsNullOrWhiteSpace(mapping.BrokerInjectHeader))
                    errors.Add($"{where}: brokered mappings require BrokerInjectHeader.");
            }
        }

        return errors.AsReadOnly();
    }

    private static IReadOnlyList<InfisicalSecretMapping> ReadMappings(
        IConfigurationSection section, List<string>? warnings)
    {
        var mappings = new List<InfisicalSecretMapping>();
        foreach (var child in section.GetChildren())
        {
            mappings.Add(new InfisicalSecretMapping
            {
                SandboxEnvVar = (child["SandboxEnvVar"] ?? string.Empty).Trim(),
                Group = (child["Group"] ?? string.Empty).Trim(),
                SecretKey = (child["SecretKey"] ?? string.Empty).Trim(),
                DynamicSecretName = (child["DynamicSecretName"] ?? string.Empty).Trim(),
                DataField = (child["DataField"] ?? string.Empty).Trim(),
                SecretPath = CredentialOptions.ReadNonEmpty(child, "SecretPath", "/"),
                Ttl = (child["Ttl"] ?? string.Empty).Trim(),
                Brokered = CredentialOptions.ReadBool(child, "Brokered", false, warnings),
                BrokerUpstreamBaseUrl = (child["BrokerUpstreamBaseUrl"] ?? string.Empty).Trim().TrimEnd('/'),
                BrokerInjectHeader = CredentialOptions.ReadNonEmpty(child, "BrokerInjectHeader", "Authorization"),
                BrokerInjectScheme = (child["BrokerInjectScheme"] ?? "Bearer").Trim(),
                BrokerAllowedPaths = ReadList(child.GetSection("BrokerAllowedPaths")),
            });
        }
        return mappings.AsReadOnly();
    }

    private static IReadOnlyList<string> ReadList(IConfigurationSection section)
    {
        var values = new List<string>();
        foreach (var child in section.GetChildren())
        {
            var value = child.Value?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);
        }
        return values.AsReadOnly();
    }
}
