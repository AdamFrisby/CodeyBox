using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.OpenStackSandboxPlugin;

/// <summary>
/// Operator knobs for the OpenStack sandbox-provider plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.openstack-sandbox</c>. Every operational value
/// lives here — never as a literal in source — and the section is re-read on
/// every operation so edits take effect without a host restart.
///
/// <para>Secrets never appear here: the application-credential id and secret
/// are read from the host credential chain (process environment) at call
/// time, so rotation propagates without a restart and a config file can never
/// carry secret material. Only the variable <em>names</em> are configured;
/// they default to the standard OpenStack names.</para>
///
/// <para>Disabled by default: the provider refuses to operate until
/// <c>Enabled</c> is set and the plugin is allowlisted. The provider kind
/// contributed by this plugin is <c>openstack</c>.</para>
/// </summary>
public sealed record OpenStackSandboxOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.openstack-sandbox";

    /// <summary>Sandbox provider kind this plugin contributes.</summary>
    public const string ProviderKind = "openstack";

    /// <summary>Master switch. Default false: the provider refuses to operate until enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Keystone authentication URL (e.g. <c>https://api.pub1.infomaniak.cloud:5000/v3</c>).
    /// Empty falls back to the <c>OS_AUTH_URL</c> environment variable. Must be https
    /// (http only for loopback test URLs under <see cref="AllowUnsafeHttp"/>).
    /// </summary>
    public string AuthUrl { get; init; } = string.Empty;

    /// <summary>
    /// OpenStack region selecting the service-catalog endpoints (e.g. <c>dc4-a</c>).
    /// Empty falls back to the <c>OS_REGION_NAME</c> environment variable; empty there
    /// too means the first endpoint matching the interface is used.
    /// </summary>
    public string Region { get; init; } = string.Empty;

    /// <summary>
    /// Service-catalog interface selecting the endpoints: one of
    /// <c>public</c>, <c>internal</c>, <c>admin</c>. Compared by exact match.
    /// Empty falls back to <c>OS_INTERFACE</c>, defaulting to <c>public</c>.
    /// </summary>
    public string Interface { get; init; } = OpenStackCredentialChain.DefaultInterface;

    /// <summary>
    /// Name of the environment variable holding the application-credential id.
    /// The value is never read from configuration files.
    /// </summary>
    public string CredentialIdEnvVar { get; init; } = OpenStackCredentialChain.CredentialIdEnvVarName;

    /// <summary>
    /// Name of the environment variable holding the application-credential secret.
    /// The value is never read from configuration files, never logged, and never persisted.
    /// </summary>
    public string CredentialSecretEnvVar { get; init; } = OpenStackCredentialChain.CredentialSecretEnvVarName;

    /// <summary>Per-request HTTP timeout for Keystone/Nova/Neutron/Glance calls, in seconds (1–600).</summary>
    public int HttpTimeoutSeconds { get; init; } = OpenStackClientLimits.DefaultHttpTimeoutSeconds;

    /// <summary>
    /// Seconds before token expiry at which a fresh Keystone token is fetched (0–600).
    /// The cached token is refreshed ahead of expiry so in-flight polls never race it.
    /// </summary>
    public int TokenRefreshSkewSeconds { get; init; } = OpenStackClientLimits.DefaultTokenRefreshSkewSeconds;

    /// <summary>Default ceiling for a server-status wait, in seconds (30–3600).</summary>
    public int ReadyTimeoutSeconds { get; init; } = 600;

    /// <summary>Base delay between server-status polls, in milliseconds (200–60000). Backs off exponentially.</summary>
    public int PollIntervalMilliseconds { get; init; } = OpenStackClientLimits.DefaultPollIntervalMilliseconds;

    /// <summary>Ceiling for the exponential poll backoff, in milliseconds (1000–120000).</summary>
    public int MaxPollIntervalMilliseconds { get; init; } = OpenStackClientLimits.DefaultMaxPollIntervalMilliseconds;

    /// <summary>Upper bound on a single decoded API response body, in bytes (64 KiB–64 MiB).</summary>
    public int MaxResponseBytes { get; init; } = OpenStackClientLimits.DefaultMaxResponseBytes;

    /// <summary>Upper bound on items collected from one list operation, across pages (1–100000).</summary>
    public int MaxListItems { get; init; } = OpenStackClientLimits.DefaultMaxListItems;

    /// <summary>Maximum list pages walked by a paged listing (1–500). Hitting the cap fails loudly.</summary>
    public int MaxListPages { get; init; } = OpenStackClientLimits.DefaultMaxListPages;

    /// <summary>Upper bound on cloud-config user_data bytes sent on server create (1 KiB–1 MiB).</summary>
    public int MaxUserDataBytes { get; init; } = OpenStackClientLimits.DefaultMaxUserDataBytes;

    /// <summary>
    /// Test hook: allow plain-http Keystone and service URLs, but only
    /// for loopback hosts (localhost / 127.0.0.1 / ::1). Remote http URLs are
    /// refused even with this set — the credential secret rides every request.
    /// Never set in production.
    /// </summary>
    public bool AllowUnsafeHttp { get; init; }

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid
    /// values fall back to safe defaults via <see cref="PluginConfigReaders"/>
    /// so a bad hot-reload never crashes an operation.
    /// </summary>
    public static OpenStackSandboxOptions FromConfiguration(IConfigurationSection? section)
    {
        var defaults = new OpenStackSandboxOptions();
        if (section is null)
            return defaults;

        return new OpenStackSandboxOptions
        {
            Enabled = PluginConfigReaders.ReadBool(section, "Enabled", defaults.Enabled),
            AuthUrl = (section["AuthUrl"] ?? string.Empty).Trim(),
            Region = (section["Region"] ?? string.Empty).Trim(),
            Interface = PluginConfigReaders.ReadNonEmpty(section, "Interface", defaults.Interface),
            CredentialIdEnvVar = PluginConfigReaders.ReadNonEmpty(
                section, "CredentialIdEnvVar", defaults.CredentialIdEnvVar),
            CredentialSecretEnvVar = PluginConfigReaders.ReadNonEmpty(
                section, "CredentialSecretEnvVar", defaults.CredentialSecretEnvVar),
            HttpTimeoutSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "HttpTimeoutSeconds", defaults.HttpTimeoutSeconds), 1, 600),
            TokenRefreshSkewSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "TokenRefreshSkewSeconds", defaults.TokenRefreshSkewSeconds), 0, 600),
            ReadyTimeoutSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "ReadyTimeoutSeconds", defaults.ReadyTimeoutSeconds), 30, 3600),
            PollIntervalMilliseconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "PollIntervalMilliseconds", defaults.PollIntervalMilliseconds), 200, 60_000),
            MaxPollIntervalMilliseconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxPollIntervalMilliseconds", defaults.MaxPollIntervalMilliseconds), 1000, 120_000),
            MaxResponseBytes = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxResponseBytes", defaults.MaxResponseBytes), 64 * 1024, 64 * 1024 * 1024),
            MaxListItems = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxListItems", defaults.MaxListItems), 1, 100_000),
            MaxListPages = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxListPages", defaults.MaxListPages), 1, 500),
            MaxUserDataBytes = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxUserDataBytes", defaults.MaxUserDataBytes), 1024, 1024 * 1024),
            AllowUnsafeHttp = PluginConfigReaders.ReadBool(section, "AllowUnsafeHttp", defaults.AllowUnsafeHttp),
        };
    }

    /// <summary>Projects the size/timeout knobs onto the REST client's bounds record.</summary>
    public OpenStackClientLimits ToClientLimits() => new()
    {
        HttpTimeout = TimeSpan.FromSeconds(HttpTimeoutSeconds),
        TokenRefreshSkew = TimeSpan.FromSeconds(TokenRefreshSkewSeconds),
        PollInterval = TimeSpan.FromMilliseconds(PollIntervalMilliseconds),
        MaxPollInterval = TimeSpan.FromMilliseconds(MaxPollIntervalMilliseconds),
        MaxResponseBytes = MaxResponseBytes,
        MaxListItems = MaxListItems,
        MaxListPages = MaxListPages,
        MaxUserDataBytes = MaxUserDataBytes,
        AllowUnsafeHttp = AllowUnsafeHttp,
    };
}
