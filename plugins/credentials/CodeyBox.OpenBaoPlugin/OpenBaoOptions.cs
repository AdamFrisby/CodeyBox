using Microsoft.Extensions.Configuration;
using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.OpenBaoPlugin;

/// <summary>
/// One operator-declared mapping from a CodeyBox sandbox secret to an
/// OpenBao read path. The mapping is what puts a secret group "against this
/// backend": <see cref="OpenBaoSecretProvider.CanIssue"/> is true only for
/// secrets carrying a <c>SandboxEnvVar</c> named here, so a group the
/// operator never mapped is never fetched, let alone injected.
/// <para>Exactly one of <see cref="SecretPath"/> (static KV read — the
/// response must not carry a lease) and <see cref="DynamicPath"/> (dynamic
/// secrets engine — the response must carry a real server lease) is set,
/// so the declared intent is checked against what the server actually
/// returned rather than silently downgraded.</para>
/// </summary>
public sealed record OpenBaoSecretMapping
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
    /// Static KV read path relative to <c>/v1/</c> — the full API path,
    /// including the <c>data/</c> segment for KV v2 mounts (for example
    /// <c>secret/data/myapp</c>) or the bare path for KV v1 (for example
    /// <c>kv/myapp</c>). Exactly one of this and <see cref="DynamicPath"/>.
    /// </summary>
    public string SecretPath { get; init; } = string.Empty;

    /// <summary>
    /// Dynamic secrets-engine credential path relative to <c>/v1/</c>,
    /// read with GET (for example <c>database/creds/readonly</c> or
    /// <c>aws/creds/deploy</c>). The response must carry a real
    /// <c>lease_id</c>; an unleased response is a backend failure, not a
    /// silent downgrade to a static window.
    /// </summary>
    public string DynamicPath { get; init; } = string.Empty;

    /// <summary>
    /// Field inside the response's data object holding the credential
    /// value (for example <c>password</c> for <c>database/creds</c>, or the
    /// key name inside a KV entry). Required for every mapping.
    /// </summary>
    public string DataField { get; init; } = string.Empty;

    /// <summary>
    /// KV engine version for <see cref="SecretPath"/> mappings: 1 reads the
    /// field straight from <c>data</c>, 2 descends into <c>data.data</c>
    /// (the wrapped KV v2 shape). Ignored for dynamic mappings.
    /// </summary>
    public int KvVersion { get; init; } = 1;
}

/// <summary>
/// Operator knobs for the OpenBao credential plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.openbao</c>. Every operational value lives
/// here — never as a literal in source — and the section is re-read on every
/// issue/renew/revoke so edits take effect without a host restart. The one
/// exception is <c>TimeoutSeconds</c>: it is baked into the HTTP client when
/// that client is first built, so changing it takes effect on the next host
/// restart.
/// <para>Secrets never appear here: <see cref="TokenEnvVar"/>,
/// <see cref="AppRoleIdEnvVar"/> and <see cref="AppRoleSecretIdEnvVar"/>
/// name environment variables whose values the operator provisions from the
/// host credential chain (vault agent, systemd credentials, container
/// secrets). Only the names are configured.</para>
/// </summary>
public sealed record OpenBaoOptions : ICredentialOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.openbao";

    /// <summary>Master switch. Default false: off unless an operator enables it.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// OpenBao server origin (scheme + host, no trailing path; for example
    /// <c>https://bao.internal.example.com:8200</c>). Must be http(s); plain
    /// http is accepted only for loopback hosts (dev servers and tests) —
    /// anything else requires https so tokens never cross the network in
    /// clear.
    /// </summary>
    public string Address { get; init; } = "http://127.0.0.1:8200";

    /// <summary>
    /// Name of the env var holding a ready-made OpenBao token. The value is
    /// re-read on every call so an externally rotated token propagates
    /// without a restart. Ignored when the AppRole pair is fully set —
    /// AppRole wins so short-lived machine credentials stay the primary
    /// path.
    /// </summary>
    public string TokenEnvVar { get; init; } = "BAO_TOKEN";

    /// <summary>Name of the env var holding the AppRole role ID (not itself a secret).</summary>
    public string AppRoleIdEnvVar { get; init; } = "OPENBAO_ROLE_ID";

    /// <summary>Name of the env var holding the AppRole secret ID.</summary>
    public string AppRoleSecretIdEnvVar { get; init; } = "OPENBAO_SECRET_ID";

    /// <summary>
    /// Mount path of the AppRole auth method relative to <c>/v1/auth/</c>
    /// (default <c>approle</c>). Only used when the AppRole pair is set.
    /// </summary>
    public string AuthMount { get; init; } = "approle";

    /// <summary>
    /// Client-side validity window for a static-KV lease. Renewal
    /// re-fetches, so rotation propagates within one window. Kept in step
    /// with the host <c>SecretLeasing:DefaultLeaseTtl</c> default (20 min).
    /// Range 1–1440 minutes.
    /// </summary>
    public int StaticLeaseTtlMinutes { get; init; } = 20;

    /// <summary>
    /// Seconds of clock skew subtracted from server-reported lifetimes
    /// (AppRole tokens, dynamic leases) before they are trusted. Default 60.
    /// </summary>
    public int TokenRefreshSkewSeconds { get; init; } = 60;

    /// <summary>
    /// Seconds requested as the <c>increment</c> on each
    /// <c>sys/leases/renew</c> call. The server clamps to the role's max
    /// TTL; this is the ask, not the grant. Range 60–86400, default 1200.
    /// </summary>
    public int RenewIncrementSeconds { get; init; } = 1200;

    /// <summary>
    /// When true (default) revocations pass <c>sync=true</c> so
    /// <c>sys/leases/revoke</c> returns only after the credential is
    /// genuinely dead — revocation at teardown is immediate and verified,
    /// not queued.
    /// </summary>
    public bool RevokeSync { get; init; } = true;

    /// <summary>Per-request timeout, in seconds (1–300, default 30).</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Hard cap on an OpenBao response body, in bytes. Enforced while
    /// buffering, before parsing. Default 256 KiB.
    /// </summary>
    public int MaxResponseBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// Operator-declared secret mappings: the only secrets this backend
    /// serves. Empty means the provider issues nothing.
    /// </summary>
    public IReadOnlyList<OpenBaoSecretMapping> Mappings { get; init; } = [];

    /// <summary>True when both AppRole env-var names are configured.</summary>
    public bool AppRoleConfigured =>
        !string.IsNullOrWhiteSpace(AppRoleIdEnvVar) && !string.IsNullOrWhiteSpace(AppRoleSecretIdEnvVar);

    /// <summary>
    /// Binds the plugin configuration section to options. Invalid values
    /// fall back to safe defaults (disabled features, bounded numbers) and
    /// are surfaced via <paramref name="warnings"/> instead of throwing, so
    /// a bad hot-reload never breaks issuance.
    /// </summary>
    public static OpenBaoOptions FromConfiguration(
        IConfigurationSection section, List<string>? warnings = null)
    {
        var defaults = new OpenBaoOptions();
        if (section is null)
            return defaults;

        return new OpenBaoOptions
        {
            Enabled = CredentialOptions.ReadBool(section, "Enabled", defaults.Enabled, warnings),
            Address = CredentialOptions.ReadNonEmpty(section, "Address", defaults.Address).TrimEnd('/'),
            TokenEnvVar = CredentialOptions.ReadNonEmpty(section, "TokenEnvVar", defaults.TokenEnvVar),
            AppRoleIdEnvVar = CredentialOptions.ReadNonEmpty(section, "AppRoleIdEnvVar", defaults.AppRoleIdEnvVar),
            AppRoleSecretIdEnvVar = CredentialOptions.ReadNonEmpty(
                section, "AppRoleSecretIdEnvVar", defaults.AppRoleSecretIdEnvVar),
            AuthMount = CredentialOptions.ReadNonEmpty(section, "AuthMount", defaults.AuthMount).Trim('/'),
            StaticLeaseTtlMinutes = CredentialOptions.ReadStaticLeaseTtlMinutes(
                section, defaults.StaticLeaseTtlMinutes, warnings),
            TokenRefreshSkewSeconds = CredentialOptions.ReadTokenRefreshSkewSeconds(
                section, defaults.TokenRefreshSkewSeconds, warnings),
            RenewIncrementSeconds = Math.Clamp(
                CredentialOptions.ReadInt(
                    section, "RenewIncrementSeconds", defaults.RenewIncrementSeconds, warnings),
                MinRenewIncrementSeconds, MaxRenewIncrementSeconds),
            RevokeSync = CredentialOptions.ReadBool(section, "RevokeSync", defaults.RevokeSync, warnings),
            TimeoutSeconds = CredentialOptions.ReadTimeoutSeconds(
                section, "TimeoutSeconds", defaults.TimeoutSeconds, warnings),
            MaxResponseBytes = CredentialOptions.ReadByteCap(
                section, "MaxResponseBytes", defaults.MaxResponseBytes, warnings),
            Mappings = ReadMappings(section.GetSection("Mappings"), warnings),
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
        CredentialOptions.ValidateEndpointUrl("Address", Address, errors);
        if (!OpenBaoPaths.IsValid(AuthMount))
            errors.Add("AuthMount must be a valid OpenBao mount path (non-empty segments, no '..', no whitespace or control characters).");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in Mappings)
        {
            if (!CredentialOptions.ValidateSandboxEnvVar(mapping.SandboxEnvVar, seen, errors, out var where))
                continue;
            var hasStatic = !string.IsNullOrWhiteSpace(mapping.SecretPath);
            var hasDynamic = !string.IsNullOrWhiteSpace(mapping.DynamicPath);
            if (hasStatic == hasDynamic)
                errors.Add($"{where}: set exactly one of SecretPath (static KV) and DynamicPath (dynamic engine).");
            if (hasStatic && !OpenBaoPaths.IsValid(mapping.SecretPath))
                errors.Add($"{where}: SecretPath is not a valid OpenBao path.");
            if (hasDynamic && !OpenBaoPaths.IsValid(mapping.DynamicPath))
                errors.Add($"{where}: DynamicPath is not a valid OpenBao path.");
            if (string.IsNullOrWhiteSpace(mapping.DataField))
                errors.Add($"{where}: DataField is required (the credential field inside the response data).");
            else if (mapping.DataField.Length > MaxDataFieldChars)
                errors.Add($"{where}: DataField exceeds {MaxDataFieldChars} characters.");
            if (hasStatic && mapping.KvVersion is not (1 or 2))
                errors.Add($"{where}: KvVersion must be 1 or 2.");
        }

        return errors.AsReadOnly();
    }

    internal const int MaxPathChars = 512;
    internal const int MaxDataFieldChars = 128;
    internal const int MinRenewIncrementSeconds = 60;
    internal const int MaxRenewIncrementSeconds = 86400;

    private static IReadOnlyList<OpenBaoSecretMapping> ReadMappings(
        IConfigurationSection section, List<string>? warnings)
    {
        var mappings = new List<OpenBaoSecretMapping>();
        foreach (var child in section.GetChildren())
        {
            mappings.Add(new OpenBaoSecretMapping
            {
                SandboxEnvVar = (child["SandboxEnvVar"] ?? string.Empty).Trim(),
                Group = (child["Group"] ?? string.Empty).Trim(),
                SecretPath = (child["SecretPath"] ?? string.Empty).Trim().Trim('/'),
                DynamicPath = (child["DynamicPath"] ?? string.Empty).Trim().Trim('/'),
                DataField = (child["DataField"] ?? string.Empty).Trim(),
                // Read raw so Validate() flags an out-of-range value instead
                // of silently clamping it.
                KvVersion = CredentialOptions.ReadInt(child, "KvVersion", 1, warnings),
            });
        }
        return mappings.AsReadOnly();
    }
}

/// <summary>
/// Path policy for operator-declared OpenBao read paths and auth mounts.
/// Paths are configuration, but they land in a URL sink, so the rule is
/// enforced once here and re-checked at the request site: relative path
/// segments only — no leading or trailing slash, no empty or dot segments,
/// no whitespace, control characters, or query/fragment delimiters.
/// </summary>
internal static class OpenBaoPaths
{
    /// <summary>Characters that can never appear in a configured path.</summary>
    private static readonly char[] InvalidChars = ['?', '#', '\\'];

    internal static bool IsValid(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > OpenBaoOptions.MaxPathChars)
            return false;
        if (path[0] == '/' || path[^1] == '/')
            return false;
        if (path.IndexOfAny(InvalidChars) >= 0)
            return false;
        if (path.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
                return false;
        }
        return true;
    }
}
