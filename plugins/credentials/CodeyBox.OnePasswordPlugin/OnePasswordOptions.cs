using Microsoft.Extensions.Configuration;
using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.OnePasswordPlugin;

/// <summary>
/// One operator-declared mapping from a CodeyBox sandbox secret to a
/// 1Password item field. The mapping is what puts a secret group "against
/// this backend": <see cref="OnePasswordSecretProvider.CanIssue"/> is true
/// only for secrets carrying a <c>SandboxEnvVar</c> named here, so a group
/// the operator never mapped is never fetched, let alone injected.
/// <para>A vault resolves <em>into</em> a group, never beside it: the
/// mapping names the group the secret belongs to plus the vault/item/field
/// triple that holds its value. Groups stay the unit of authorisation; no
/// parallel grouping concept is introduced.</para>
/// </summary>
public sealed record OnePasswordSecretMapping
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
    /// Vault UUID for the Connect path (<c>GET /v1/vaults/{id}/…</c>), or
    /// the vault name/title for the service-account path
    /// (<c>op://vault/…</c>). Exactly one of this and <see cref="VaultName"/>
    /// must be set.
    /// </summary>
    public string VaultId { get; init; } = string.Empty;

    /// <summary>
    /// Vault name, resolved to a UUID with an exact-match lookup for the
    /// Connect path and passed through for the service-account path.
    /// Exactly one of this and <see cref="VaultId"/> must be set.
    /// </summary>
    public string VaultName { get; init; } = string.Empty;

    /// <summary>
    /// Item UUID for the Connect path, or the item title for the
    /// service-account path. Exactly one of this and <see cref="ItemTitle"/>
    /// must be set.
    /// </summary>
    public string ItemId { get; init; } = string.Empty;

    /// <summary>
    /// Item title, resolved to a UUID with an exact-match lookup for the
    /// Connect path and passed through for the service-account path.
    /// Exactly one of this and <see cref="ItemId"/> must be set.
    /// </summary>
    public string ItemTitle { get; init; } = string.Empty;

    /// <summary>
    /// Item field holding the secret, matched exactly (ordinal) against the
    /// field label first, then the field id. Defaults to
    /// <c>password</c> — the conventional label 1Password assigns the
    /// concealed credential on login, password, and API-credential items.
    /// </summary>
    public string Field { get; init; } = "password";

    /// <summary>
    /// When true this mapping reads with the <c>op</c> CLI under a
    /// service-account token (<c>op read op://vault/item/field</c>) instead
    /// of the Connect server. The CLI, not the network, is then the
    /// transport; a per-mapping <see cref="TokenEnvVar"/> is meaningless
    /// there and is rejected by validation.
    /// </summary>
    public bool UseServiceAccount { get; init; }

    /// <summary>
    /// Name of the env var holding the Connect token scoped to this
    /// mapping's vault. Empty inherits the provider-level
    /// <c>ConnectTokenEnvVar</c>. A Connect token reaches exactly the
    /// vaults it was granted, so mappings spanning vaults each name their
    /// own least-privilege token here. Unused (and rejected) for
    /// service-account mappings.
    /// </summary>
    public string TokenEnvVar { get; init; } = string.Empty;
}

/// <summary>
/// Operator knobs for the 1Password credential plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.onepassword</c>. Every operational value
/// lives here — never as a literal in source — and the section is re-read
/// on every issue/renew/revoke so edits take effect without a host restart.
/// The one exception is <c>TimeoutSeconds</c>: it is baked into the HTTP
/// client when that client is first built, so changing it takes effect on
/// the next host restart.
/// <para>Secrets never appear here: <see cref="ConnectTokenEnvVar"/> and
/// <see cref="ServiceAccountTokenEnvVar"/> name environment variables
/// whose values the operator provisions from the host credential chain
/// (vault agent, systemd credentials, container secrets). Only the names
/// are configured.</para>
/// </summary>
public sealed record OnePasswordOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.onepassword";

    /// <summary>Master switch. Default false: off unless an operator enables it.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// 1Password Connect server origin (scheme + host, no trailing path).
    /// Connect is self-hosted: the operator deploys it (see the plugin
    /// README) and points the deployment at it here. Must be http(s);
    /// plain http is accepted only for loopback hosts (local development
    /// and the default docker-compose topology) — anything else requires
    /// https so bearer tokens never cross the network in clear.
    /// </summary>
    public string ServerUrl { get; init; } = "http://localhost:8080";

    /// <summary>
    /// Name of the env var holding the Connect server access token used
    /// when a mapping names no <c>TokenEnvVar</c> override. Connect tokens
    /// are vault-scoped at creation (read-only where possible) — one token
    /// per vault (or per vault set) is the documented least-privilege
    /// layout, with per-mapping <c>TokenEnvVar</c> overrides selecting
    /// them.
    /// </summary>
    public string ConnectTokenEnvVar { get; init; } = "OP_CONNECT_TOKEN";

    /// <summary>
    /// Name of the env var holding the 1Password service-account token
    /// (<c>ops_…</c>) used by service-account mappings
    /// (<c>UseServiceAccount: true</c>). Empty disables the service-account
    /// path. Service-account vault grants are immutable: to change what a
    /// service account reaches, create a new one.
    /// </summary>
    public string ServiceAccountTokenEnvVar { get; init; } = string.Empty;

    /// <summary>
    /// Path to, or name of, the 1Password CLI binary used by
    /// service-account mappings. Resolved without a shell. Default
    /// <c>op</c> (first match on <c>PATH</c>).
    /// </summary>
    public string OpBinaryPath { get; init; } = "op";

    /// <summary>
    /// Client-side validity window for a lease. Renewal re-fetches, so an
    /// item edit or rotation propagates within one window. Kept in step
    /// with the host <c>SecretLeasing:DefaultLeaseTtl</c> default (20 min).
    /// Range 1–1440 minutes.
    /// </summary>
    public int StaticLeaseTtlMinutes { get; init; } = 20;

    /// <summary>Per-request timeout for Connect HTTP calls, in seconds (1–300, default 30).</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Timeout for one <c>op read</c> invocation, in seconds (1–300,
    /// default 30). The child is killed on expiry.
    /// </summary>
    public int OpTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Hard cap on a Connect response body, in bytes. Enforced while
    /// buffering, before parsing. Default 256 KiB.
    /// </summary>
    public int MaxResponseBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// Operator-declared secret mappings: the only secrets this backend
    /// serves. Empty means the provider issues nothing.
    /// </summary>
    public IReadOnlyList<OnePasswordSecretMapping> Mappings { get; init; } = [];

    /// <summary>True when at least one mapping reads via the service-account CLI path.</summary>
    public bool ServiceAccountConfigured =>
        Mappings.Any(m => m.UseServiceAccount) && !string.IsNullOrWhiteSpace(ServiceAccountTokenEnvVar);

    /// <summary>
    /// Binds the plugin configuration section to options. Invalid values
    /// fall back to safe defaults (disabled features, bounded numbers) and
    /// are surfaced via <paramref name="warnings"/> instead of throwing, so
    /// a bad hot-reload never breaks issuance.
    /// </summary>
    public static OnePasswordOptions FromConfiguration(
        IConfigurationSection section, List<string>? warnings = null)
    {
        var defaults = new OnePasswordOptions();
        if (section is null)
            return defaults;

        return new OnePasswordOptions
        {
            Enabled = ReadBool(section, "Enabled", defaults.Enabled),
            ServerUrl = ReadNonEmpty(section, "ServerUrl", defaults.ServerUrl).TrimEnd('/'),
            ConnectTokenEnvVar = ReadNonEmpty(section, "ConnectTokenEnvVar", defaults.ConnectTokenEnvVar),
            ServiceAccountTokenEnvVar = (section["ServiceAccountTokenEnvVar"] ?? string.Empty).Trim(),
            OpBinaryPath = ReadNonEmpty(section, "OpBinaryPath", defaults.OpBinaryPath),
            StaticLeaseTtlMinutes = Math.Clamp(
                ReadInt(section, "StaticLeaseTtlMinutes", defaults.StaticLeaseTtlMinutes, warnings), 1, 1440),
            TimeoutSeconds = Math.Clamp(
                ReadInt(section, "TimeoutSeconds", defaults.TimeoutSeconds, warnings), 1, 300),
            OpTimeoutSeconds = Math.Clamp(
                ReadInt(section, "OpTimeoutSeconds", defaults.OpTimeoutSeconds, warnings), 1, 300),
            MaxResponseBytes = Math.Max(
                ReadInt(section, "MaxResponseBytes", defaults.MaxResponseBytes, warnings), 1024),
            Mappings = ReadMappings(section.GetSection("Mappings")),
        };
    }

    /// <summary>
    /// Pure validation: returns every operator-facing problem with the
    /// options, or an empty list when the configuration is usable. Never
    /// touches the network, never spawns a process, and never reads secret
    /// values — only names.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var server)
            || (server.Scheme != Uri.UriSchemeHttps && server.Scheme != Uri.UriSchemeHttp))
        {
            errors.Add($"ServerUrl '{ServerUrl}' must be an absolute http(s) URL.");
        }
        else if (server.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(server.Host))
        {
            errors.Add($"ServerUrl '{ServerUrl}' uses plain http against a non-loopback host; use https.");
        }

        if (string.IsNullOrWhiteSpace(OpBinaryPath))
            errors.Add("OpBinaryPath must not be empty (service-account mappings spawn the 1Password CLI).");

        if (Mappings.Any(m => m.UseServiceAccount) && string.IsNullOrWhiteSpace(ServiceAccountTokenEnvVar))
            errors.Add("A mapping sets UseServiceAccount but ServiceAccountTokenEnvVar is empty; " +
                "name the env var holding the service-account token or drop UseServiceAccount.");

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

            var hasVaultId = !string.IsNullOrWhiteSpace(mapping.VaultId);
            var hasVaultName = !string.IsNullOrWhiteSpace(mapping.VaultName);
            if (hasVaultId == hasVaultName)
                errors.Add($"{where}: set exactly one of VaultId and VaultName.");
            if (hasVaultId && mapping.VaultId.Length > MaxVaultChars)
                errors.Add($"{where}: VaultId exceeds {MaxVaultChars} characters.");
            if (hasVaultName && mapping.VaultName.Length > MaxVaultChars)
                errors.Add($"{where}: VaultName exceeds {MaxVaultChars} characters.");

            var hasItemId = !string.IsNullOrWhiteSpace(mapping.ItemId);
            var hasItemTitle = !string.IsNullOrWhiteSpace(mapping.ItemTitle);
            if (hasItemId == hasItemTitle)
                errors.Add($"{where}: set exactly one of ItemId and ItemTitle.");
            if (hasItemId && mapping.ItemId.Length > MaxItemChars)
                errors.Add($"{where}: ItemId exceeds {MaxItemChars} characters.");
            if (hasItemTitle && mapping.ItemTitle.Length > MaxItemChars)
                errors.Add($"{where}: ItemTitle exceeds {MaxItemChars} characters.");

            var field = string.IsNullOrWhiteSpace(mapping.Field) ? DefaultField : mapping.Field;
            if (field.Length > MaxFieldChars)
                errors.Add($"{where}: Field exceeds {MaxFieldChars} characters.");

            if (mapping.UseServiceAccount && !string.IsNullOrWhiteSpace(mapping.TokenEnvVar))
                errors.Add($"{where}: TokenEnvVar is meaningless for service-account mappings " +
                    "(the service-account token comes from ServiceAccountTokenEnvVar); leave it empty.");
        }

        return errors.AsReadOnly();
    }

    /// <summary>Default item field read when a mapping names none.</summary>
    internal const string DefaultField = "password";

    /// <summary>
    /// Maximum sandbox-variable length accepted in a mapping. Lease handles
    /// embed the variable name, and handles are capped at
    /// <c>SecretLeasingOptions.MaxLeaseIdLength</c> (256).
    /// </summary>
    internal const int MaxSandboxEnvVarChars = 64;

    internal const int MaxVaultChars = 128;
    internal const int MaxItemChars = 256;
    internal const int MaxFieldChars = 128;

    private static IReadOnlyList<OnePasswordSecretMapping> ReadMappings(IConfigurationSection section)
    {
        var mappings = new List<OnePasswordSecretMapping>();
        foreach (var child in section.GetChildren())
        {
            mappings.Add(new OnePasswordSecretMapping
            {
                SandboxEnvVar = (child["SandboxEnvVar"] ?? string.Empty).Trim(),
                Group = (child["Group"] ?? string.Empty).Trim(),
                VaultId = (child["VaultId"] ?? string.Empty).Trim(),
                VaultName = (child["VaultName"] ?? string.Empty).Trim(),
                ItemId = (child["ItemId"] ?? string.Empty).Trim(),
                ItemTitle = (child["ItemTitle"] ?? string.Empty).Trim(),
                Field = ReadNonEmpty(child, "Field", DefaultField),
                UseServiceAccount = ReadBool(child, "UseServiceAccount", false),
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
