using Microsoft.Extensions.Configuration;
using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.DopplerPlugin;

/// <summary>
/// One operator-declared mapping from a CodeyBox sandbox secret to a
/// Doppler secret. The mapping is what puts a secret group "against this
/// backend": <see cref="DopplerSecretProvider.CanIssue"/> is true only for
/// secrets carrying a <c>SandboxEnvVar</c> named here, so a group the
/// operator never mapped is never fetched, let alone injected.
/// <para>A Doppler config resolves <em>into</em> a group, never beside it:
/// the mapping names the group the secret belongs to plus the
/// project/config/secret triple that holds its value. Groups stay the unit
/// of authorisation; no parallel grouping concept is introduced.</para>
/// </summary>
public sealed record DopplerSecretMapping
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
    /// Doppler secret name (<c>GET /v3/configs/config/secret?name=…</c>).
    /// Empty defaults to <see cref="SandboxEnvVar"/> at issue time, so the
    /// common case (same name both sides) needs no repetition.
    /// </summary>
    public string SecretName { get; init; } = string.Empty;

    /// <summary>
    /// Doppler project slug or id. Empty inherits the provider-level
    /// <c>DefaultProject</c>.
    /// </summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>
    /// Doppler config name (for example <c>prd</c>). Empty inherits the
    /// provider-level <c>DefaultConfig</c>.
    /// </summary>
    public string Config { get; init; } = string.Empty;

    /// <summary>
    /// Name of the env var holding the restricted service token scoped to
    /// this mapping's project/config. Empty inherits the provider-level
    /// <c>ServiceTokenEnvVar</c>. A service token is bound to exactly one
    /// project/config, so mappings spanning configs each name their own
    /// least-privilege token here.
    /// </summary>
    public string TokenEnvVar { get; init; } = string.Empty;
}

/// <summary>
/// Operator knobs for the Doppler credential plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.doppler</c>. Every operational value lives
/// here — never as a literal in source — and the section is re-read on every
/// issue/renew/revoke so edits take effect without a host restart.
/// <para>Secrets never appear here: <see cref="ServiceTokenEnvVar"/>,
/// per-mapping <c>TokenEnvVar</c> overrides, and
/// <see cref="OidcTokenEnvVar"/> name environment variables whose values
/// the operator provisions from the host credential chain (vault agent,
/// systemd credentials, container secrets). Only the names are
/// configured.</para>
/// </summary>
public sealed record DopplerOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.doppler";

    /// <summary>Master switch. Default false: off unless an operator enables it.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Doppler API origin (scheme + host, no trailing path). Defaults to
    /// Doppler's SaaS API. Must be http(s); plain http is accepted only for
    /// loopback hosts (local development and tests) — anything else
    /// requires https so bearer tokens never cross the network in clear.
    /// </summary>
    public string ApiUrl { get; init; } = "https://api.doppler.com";

    /// <summary>Default Doppler project slug or id, inherited by mappings that set no Project.</summary>
    public string DefaultProject { get; init; } = string.Empty;

    /// <summary>Default Doppler config name (for example <c>prd</c>), inherited by mappings that set no Config.</summary>
    public string DefaultConfig { get; init; } = string.Empty;

    /// <summary>
    /// Name of the env var holding the restricted service token used when a
    /// mapping names no <c>TokenEnvVar</c> override. Restricted service
    /// tokens are scoped to one project/config with read-only access and
    /// are the documented default credential.
    /// </summary>
    public string ServiceTokenEnvVar { get; init; } = "DOPPLER_TOKEN";

    /// <summary>
    /// Doppler service-account identity id for the short-lived
    /// lease-shaped path (<c>POST /v3/auth/oidc</c>). Empty disables the
    /// identity path and every mapping uses static service tokens.
    /// An identity id is configuration, not a secret.
    /// </summary>
    public string IdentityId { get; init; } = string.Empty;

    /// <summary>
    /// Name of the env var holding a fresh OIDC token minted by the
    /// operator's identity provider (for example GitHub Actions'
    /// <c>ACTIONS_ID_TOKEN_REQUEST_TOKEN</c> flow output staged by a host
    /// helper). Empty disables the identity path. The OIDC token itself is
    /// short-lived; renewal past its lifetime fails loudly as
    /// infrastructure (see the README's honest lease statement).
    /// </summary>
    public string OidcTokenEnvVar { get; init; } = string.Empty;

    /// <summary>
    /// Client-side validity window for a static-token lease. Renewal
    /// re-fetches, so rotation propagates within one window. Kept in step
    /// with the host <c>SecretLeasing:DefaultLeaseTtl</c> default (20 min).
    /// Range 1–1440 minutes.
    /// </summary>
    public int StaticLeaseTtlMinutes { get; init; } = 20;

    /// <summary>
    /// Seconds of clock skew subtracted from a minted identity token's
    /// server <c>expires_at</c> before it is trusted. Default 60.
    /// </summary>
    public int TokenRefreshSkewSeconds { get; init; } = 60;

    /// <summary>Per-request timeout, in seconds (1–300, default 30).</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Hard cap on a Doppler response body, in bytes. Enforced while
    /// buffering, before parsing. Default 256 KiB.
    /// </summary>
    public int MaxResponseBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// Operator-declared secret mappings: the only secrets this backend
    /// serves. Empty means the provider issues nothing.
    /// </summary>
    public IReadOnlyList<DopplerSecretMapping> Mappings { get; init; } = [];

    /// <summary>True when the short-lived identity path is configured (both id and OIDC token source set).</summary>
    public bool IdentityConfigured =>
        !string.IsNullOrWhiteSpace(IdentityId) && !string.IsNullOrWhiteSpace(OidcTokenEnvVar);

    /// <summary>
    /// Binds the plugin configuration section to options. Invalid values
    /// fall back to safe defaults (disabled features, bounded numbers) and
    /// are surfaced via <paramref name="warnings"/> instead of throwing, so
    /// a bad hot-reload never breaks issuance.
    /// </summary>
    public static DopplerOptions FromConfiguration(
        IConfigurationSection section, List<string>? warnings = null)
    {
        var defaults = new DopplerOptions();
        if (section is null)
            return defaults;

        return new DopplerOptions
        {
            Enabled = ReadBool(section, "Enabled", defaults.Enabled),
            ApiUrl = ReadNonEmpty(section, "ApiUrl", defaults.ApiUrl).TrimEnd('/'),
            DefaultProject = (section["DefaultProject"] ?? string.Empty).Trim(),
            DefaultConfig = (section["DefaultConfig"] ?? string.Empty).Trim(),
            ServiceTokenEnvVar = ReadNonEmpty(section, "ServiceTokenEnvVar", defaults.ServiceTokenEnvVar),
            IdentityId = (section["IdentityId"] ?? string.Empty).Trim(),
            OidcTokenEnvVar = (section["OidcTokenEnvVar"] ?? string.Empty).Trim(),
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

        var identityIdSet = !string.IsNullOrWhiteSpace(IdentityId);
        var oidcEnvSet = !string.IsNullOrWhiteSpace(OidcTokenEnvVar);
        if (identityIdSet != oidcEnvSet)
            errors.Add("IdentityId and OidcTokenEnvVar must be set together (the short-lived identity path needs both); " +
                "leave both empty for static service tokens only.");

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
            var secretName = string.IsNullOrWhiteSpace(mapping.SecretName) ? mapping.SandboxEnvVar : mapping.SecretName;
            if (secretName.Length > MaxSecretNameChars)
                errors.Add($"{where}: secret name exceeds {MaxSecretNameChars} characters.");
            var project = string.IsNullOrWhiteSpace(mapping.Project) ? DefaultProject : mapping.Project;
            var config = string.IsNullOrWhiteSpace(mapping.Config) ? DefaultConfig : mapping.Config;
            if (string.IsNullOrWhiteSpace(project))
                errors.Add($"{where}: no Doppler project (set mapping Project or DefaultProject).");
            if (string.IsNullOrWhiteSpace(config))
                errors.Add($"{where}: no Doppler config (set mapping Config or DefaultConfig).");
        }

        return errors.AsReadOnly();
    }

    /// <summary>
    /// Maximum sandbox-variable length accepted in a mapping. Lease handles
    /// embed the variable name, and handles are capped at
    /// <c>SecretLeasingOptions.MaxLeaseIdLength</c> (256).
    /// </summary>
    internal const int MaxSandboxEnvVarChars = 64;

    internal const int MaxSecretNameChars = 128;

    private static IReadOnlyList<DopplerSecretMapping> ReadMappings(IConfigurationSection section)
    {
        var mappings = new List<DopplerSecretMapping>();
        foreach (var child in section.GetChildren())
        {
            mappings.Add(new DopplerSecretMapping
            {
                SandboxEnvVar = (child["SandboxEnvVar"] ?? string.Empty).Trim(),
                Group = (child["Group"] ?? string.Empty).Trim(),
                SecretName = (child["SecretName"] ?? string.Empty).Trim(),
                Project = (child["Project"] ?? string.Empty).Trim(),
                Config = (child["Config"] ?? string.Empty).Trim(),
                TokenEnvVar = (child["TokenEnvVar"] ?? string.Empty).Trim(),
            });
        }
        return mappings.AsReadOnly();
    }

    private static bool ReadBool(IConfigurationSection section, string key, bool fallback)
        => CredentialOptions.ReadBool(section, key, fallback);

    private static int ReadInt(
        IConfigurationSection section, string key, int fallback, List<string>? warnings)
        => CredentialOptions.ReadInt(section, key, fallback, warnings);

    private static string ReadNonEmpty(IConfigurationSection section, string key, string fallback)
        => CredentialOptions.ReadNonEmpty(section, key, fallback);

    internal static bool IsLoopbackHost(string host)
        => CredentialOptions.IsLoopbackHost(host);

    private static bool IsEnvVarName(string value)
        => CredentialOptions.IsEnvVarName(value);
}
