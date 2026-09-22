using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.BitwardenPlugin;

/// <summary>
/// One operator-declared mapping from a CodeyBox sandbox secret to a
/// Bitwarden Secrets Manager secret. The mapping is what puts a secret
/// group "against this backend": <see cref="BitwardenSecretProvider.CanIssue"/>
/// is true only for secrets carrying a <c>SandboxEnvVar</c> named here, so
/// a group the operator never mapped is never fetched, let alone injected.
/// <para>A Secrets Manager secret resolves <em>into</em> a group, never
/// beside it: the mapping names the group the secret belongs to plus the
/// secret that holds its value. Groups stay the unit of authorisation; no
/// parallel grouping concept is introduced.</para>
/// </summary>
public sealed record BitwardenSecretMapping
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
    /// Secrets Manager secret UUID (<c>GET /secrets/{id}</c>).
    /// Exactly one of <see cref="SecretId"/> and <see cref="SecretKey"/>
    /// must be set; the UUID form is preferred because it survives renames.
    /// </summary>
    public string SecretId { get; init; } = string.Empty;

    /// <summary>
    /// Secrets Manager secret key, resolved with an exact (ordinal) match
    /// over the organisation's secret listing. Empty selects the
    /// <see cref="SecretId"/> path. Requires an effective organisation id
    /// (mapping <c>OrganizationId</c> or provider <c>OrganizationId</c>).
    /// </summary>
    public string SecretKey { get; init; } = string.Empty;

    /// <summary>
    /// Secrets Manager project UUID constraining key resolution (and
    /// documenting where the secret lives). Empty means any project in the
    /// organisation; combined with <see cref="SecretId"/> it is a checked
    /// expectation — a fetched secret from another project fails loudly.
    /// </summary>
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>
    /// Organisation UUID owning the secret. Empty inherits the
    /// provider-level <c>OrganizationId</c>.
    /// </summary>
    public string OrganizationId { get; init; } = string.Empty;

    /// <summary>
    /// Machine-account client id used for this mapping. Empty inherits the
    /// provider-level <c>ClientId</c>. An id is configuration, not a secret.
    /// </summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Name of the env var holding the machine-account client secret scoped
    /// to this mapping. Empty inherits the provider-level
    /// <c>ClientSecretEnvVar</c>. A machine account's project grants are the
    /// natural mapping onto secret groups: one machine account (or account
    /// set) per group, each naming its own least-privilege credential here.
    /// </summary>
    public string ClientSecretEnvVar { get; init; } = string.Empty;
}

/// <summary>
/// Operator knobs for the Bitwarden credential plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.bitwarden</c>. Every operational value lives
/// here — never as a literal in source — and the section is re-read on every
/// issue/renew/revoke so edits take effect without a host restart.
/// <para>Secrets never appear here: <see cref="ClientSecretEnvVar"/> and
/// per-mapping overrides name environment variables whose values the
/// operator provisions from the host credential chain (vault agent, systemd
/// credentials, container secrets). Only the names are configured. The
/// machine-account client id is not a secret and is configured directly.
/// </para>
/// </summary>
public sealed record BitwardenOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.bitwarden";

    /// <summary>Master switch. Default false: off unless an operator enables it.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Secrets Manager API origin (scheme + host, no trailing path).
    /// Defaults to the US SaaS API; use <c>https://api.bitwarden.eu</c> for
    /// EU tenants or the self-hosted origin. Must be http(s); plain http is
    /// accepted only for loopback hosts (local development and tests) —
    /// anything else requires https so bearer tokens never cross the
    /// network in clear.
    /// </summary>
    public string ApiUrl { get; init; } = "https://api.bitwarden.com";

    /// <summary>
    /// Identity origin serving <c>/connect/token</c> for the
    /// machine-account client-credentials grant. Defaults to the US SaaS
    /// identity; use <c>https://identity.bitwarden.eu</c> for EU tenants or
    /// the self-hosted origin. Same http(s)/loopback rule as
    /// <see cref="ApiUrl"/>.
    /// </summary>
    public string IdentityUrl { get; init; } = "https://identity.bitwarden.com";

    /// <summary>
    /// Default machine-account client id, inherited by mappings that set no
    /// <c>ClientId</c>. An id is configuration, not a secret.
    /// </summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Name of the env var holding the machine-account client secret used
    /// when a mapping names no <c>ClientSecretEnvVar</c> override.
    /// </summary>
    public string ClientSecretEnvVar { get; init; } = "BITWARDEN_CLIENT_SECRET";

    /// <summary>
    /// Default organisation UUID, inherited by mappings that set no
    /// <c>OrganizationId</c>. Required for key-based resolution; the id
    /// form carries no org requirement.
    /// </summary>
    public string OrganizationId { get; init; } = string.Empty;

    /// <summary>
    /// Client-side validity window for a lease. Renewal re-authenticates
    /// and re-fetches, so rotation propagates within one window; the minted
    /// expiry additionally never exceeds the access token's own server
    /// lifetime. Kept in step with the host
    /// <c>SecretLeasing:DefaultLeaseTtl</c> default (20 min). Range 1–1440
    /// minutes.
    /// </summary>
    public int StaticLeaseTtlMinutes { get; init; } = 20;

    /// <summary>
    /// Seconds of clock skew subtracted from a minted access token's server
    /// <c>expires_in</c> before it is trusted. Default 60.
    /// </summary>
    public int TokenRefreshSkewSeconds { get; init; } = 60;

    /// <summary>Per-request timeout, in seconds (1–300, default 30).</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Hard cap on a Bitwarden response body, in bytes. Enforced while
    /// buffering, before parsing. Default 256 KiB.
    /// </summary>
    public int MaxResponseBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// Operator-declared secret mappings: the only secrets this backend
    /// serves. Empty means the provider issues nothing.
    /// </summary>
    public IReadOnlyList<BitwardenSecretMapping> Mappings { get; init; } = [];

    /// <summary>
    /// Binds the plugin configuration section to options. Invalid values
    /// fall back to safe defaults (disabled features, bounded numbers) and
    /// are surfaced via <paramref name="warnings"/> instead of throwing, so
    /// a bad hot-reload never breaks issuance.
    /// </summary>
    public static BitwardenOptions FromConfiguration(
        IConfigurationSection section, List<string>? warnings = null)
    {
        var defaults = new BitwardenOptions();
        if (section is null)
            return defaults;

        return new BitwardenOptions
        {
            Enabled = ReadBool(section, "Enabled", defaults.Enabled),
            ApiUrl = ReadNonEmpty(section, "ApiUrl", defaults.ApiUrl).TrimEnd('/'),
            IdentityUrl = ReadNonEmpty(section, "IdentityUrl", defaults.IdentityUrl).TrimEnd('/'),
            ClientId = (section["ClientId"] ?? string.Empty).Trim(),
            ClientSecretEnvVar = ReadNonEmpty(section, "ClientSecretEnvVar", defaults.ClientSecretEnvVar),
            OrganizationId = (section["OrganizationId"] ?? string.Empty).Trim(),
            StaticLeaseTtlMinutes = Math.Clamp(
                ReadInt(section, "StaticLeaseTtlMinutes", defaults.StaticLeaseTtlMinutes, warnings), 1, 1440),
            TokenRefreshSkewSeconds = Math.Clamp(
                ReadInt(section, "TokenRefreshSkewSeconds", defaults.TokenRefreshSkewSeconds, warnings), 0, 3600),
            TimeoutSeconds = Math.Clamp(
                ReadInt(section, "TimeoutSeconds", defaults.TimeoutSeconds, warnings), 1, 300),
            MaxResponseBytes = Math.Max(
                ReadInt(section, "MaxResponseBytes", defaults.MaxResponseBytes, warnings), 1024),
            Mappings = ReadMappings(section.GetSection("Mappings")),
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
        if (!Uri.TryCreate(ApiUrl, UriKind.Absolute, out var api)
            || (api.Scheme != Uri.UriSchemeHttps && api.Scheme != Uri.UriSchemeHttp))
        {
            errors.Add($"ApiUrl '{ApiUrl}' must be an absolute http(s) URL.");
        }
        else if (api.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(api.Host))
        {
            errors.Add($"ApiUrl '{ApiUrl}' uses plain http against a non-loopback host; use https.");
        }
        if (!Uri.TryCreate(IdentityUrl, UriKind.Absolute, out var identity)
            || (identity.Scheme != Uri.UriSchemeHttps && identity.Scheme != Uri.UriSchemeHttp))
        {
            errors.Add($"IdentityUrl '{IdentityUrl}' must be an absolute http(s) URL.");
        }
        else if (identity.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(identity.Host))
        {
            errors.Add($"IdentityUrl '{IdentityUrl}' uses plain http against a non-loopback host; use https.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in Mappings)
        {
            var where = string.IsNullOrWhiteSpace(mapping.SandboxEnvVar)
                ? "mapping with an empty SandboxEnvVar"
                : $"mapping for '{mapping.SandboxEnvVar}'";
            if (string.IsNullOrWhiteSpace(mapping.SandboxEnvVar))
            {
                errors.Add("A mapping has an empty SandboxEnvVar; every mapping must name its sandbox variable.");
                continue;
            }
            if (mapping.SandboxEnvVar.Length > MaxSandboxEnvVarChars)
                errors.Add($"{where}: SandboxEnvVar exceeds {MaxSandboxEnvVarChars} characters (lease handles embed it).");
            if (!IsEnvVarName(mapping.SandboxEnvVar))
                errors.Add($"{where}: SandboxEnvVar must be a POSIX identifier ([A-Za-z_][A-Za-z0-9_]*).");
            if (!seen.Add(mapping.SandboxEnvVar))
                errors.Add($"{where}: duplicate SandboxEnvVar; each sandbox variable maps once.");
            var hasId = !string.IsNullOrWhiteSpace(mapping.SecretId);
            var hasKey = !string.IsNullOrWhiteSpace(mapping.SecretKey);
            if (hasId == hasKey)
                errors.Add($"{where}: set exactly one of SecretId and SecretKey (UUID form preferred; it survives renames).");
            if (hasId && !Guid.TryParse(mapping.SecretId.Trim(), out _))
                errors.Add($"{where}: SecretId '{mapping.SecretId.Trim()}' is not a UUID.");
            if (hasKey && mapping.SecretKey.Trim().Length > MaxSecretKeyChars)
                errors.Add($"{where}: SecretKey exceeds {MaxSecretKeyChars} characters.");
            if (!string.IsNullOrWhiteSpace(mapping.ProjectId) && !Guid.TryParse(mapping.ProjectId.Trim(), out _))
                errors.Add($"{where}: ProjectId '{mapping.ProjectId.Trim()}' is not a UUID.");
            var org = string.IsNullOrWhiteSpace(mapping.OrganizationId) ? OrganizationId : mapping.OrganizationId;
            if (!string.IsNullOrWhiteSpace(org) && !Guid.TryParse(org.Trim(), out _))
                errors.Add($"{where}: organisation id '{org.Trim()}' is not a UUID.");
            if (hasKey && string.IsNullOrWhiteSpace(org))
                errors.Add($"{where}: key-based resolution needs an organisation (set mapping OrganizationId or provider OrganizationId).");
            var secretEnv = string.IsNullOrWhiteSpace(mapping.ClientSecretEnvVar)
                ? ClientSecretEnvVar
                : mapping.ClientSecretEnvVar;
            if (string.IsNullOrWhiteSpace(secretEnv))
                errors.Add($"{where}: no client-secret source (set mapping ClientSecretEnvVar or provider ClientSecretEnvVar).");
        }

        return errors.AsReadOnly();
    }

    /// <summary>
    /// Maximum sandbox-variable length accepted in a mapping. Lease handles
    /// embed the variable name, and handles are capped at
    /// <c>SecretLeasingOptions.MaxLeaseIdLength</c> (256).
    /// </summary>
    internal const int MaxSandboxEnvVarChars = 64;

    internal const int MaxSecretKeyChars = 128;

    private static IReadOnlyList<BitwardenSecretMapping> ReadMappings(IConfigurationSection section)
    {
        var mappings = new List<BitwardenSecretMapping>();
        foreach (var child in section.GetChildren())
        {
            mappings.Add(new BitwardenSecretMapping
            {
                SandboxEnvVar = (child["SandboxEnvVar"] ?? string.Empty).Trim(),
                Group = (child["Group"] ?? string.Empty).Trim(),
                SecretId = (child["SecretId"] ?? string.Empty).Trim(),
                SecretKey = (child["SecretKey"] ?? string.Empty).Trim(),
                ProjectId = (child["ProjectId"] ?? string.Empty).Trim(),
                OrganizationId = (child["OrganizationId"] ?? string.Empty).Trim(),
                ClientId = (child["ClientId"] ?? string.Empty).Trim(),
                ClientSecretEnvVar = (child["ClientSecretEnvVar"] ?? string.Empty).Trim(),
            });
        }
        return mappings.AsReadOnly();
    }

    private static bool ReadBool(IConfigurationSection section, string key, bool fallback)
    {
        var raw = section[key];
        return string.IsNullOrWhiteSpace(raw) || !bool.TryParse(raw.Trim(), out var parsed) ? fallback : parsed;
    }

    private static int ReadInt(
        IConfigurationSection section, string key, int fallback, List<string>? warnings)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            warnings?.Add($"'{key}' value '{raw.Trim()}' is not an integer; using {fallback}.");
            return fallback;
        }
        return parsed;
    }

    private static string ReadNonEmpty(IConfigurationSection section, string key, string fallback)
    {
        var raw = section[key];
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
    }

    internal static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "::1", StringComparison.Ordinal)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnvVarName(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        if (!(value[0] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_'))
            return false;
        foreach (var c in value.AsSpan(1))
        {
            if (!(c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
                return false;
        }
        return true;
    }
}
