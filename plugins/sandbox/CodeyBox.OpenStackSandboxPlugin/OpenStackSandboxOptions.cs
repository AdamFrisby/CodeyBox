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
    /// Glance image used when the work spec's <c>ImageReference</c> is empty.
    /// Interpreted as an image id first, then as an exact (ordinal) image
    /// name which must match exactly one visible image. Sample:
    /// <c>Ubuntu 24.04</c>. Nothing cloud-specific is hardcoded: every
    /// deployment names its own baseline image here.
    /// </summary>
    public string ImageName { get; init; } = string.Empty;

    /// <summary>
    /// Nova flavor name each sandbox boots with (e.g. <c>standard-2-8</c>).
    /// Resolved by exact (ordinal) name to a flavor id; an ambiguous or
    /// missing name fails loudly. Required: empty refuses provisioning.
    /// </summary>
    public string FlavorName { get; init; } = string.Empty;

    /// <summary>
    /// Neutron network id the sandbox boots on. Required: empty refuses
    /// provisioning. Names are not accepted — ids are stable across renames.
    /// </summary>
    public string NetworkId { get; init; } = string.Empty;

    /// <summary>
    /// External (floating-IP pool) network id. Empty disables floating IPs:
    /// the sandbox is reached on its fixed address. Set to attach a floating
    /// IP at create time and SSH to it.
    /// </summary>
    public string FloatingNetworkId { get; init; } = string.Empty;

    /// <summary>
    /// Guest login user for SSH (the image's default user, e.g.
    /// <c>ubuntu</c> for Ubuntu cloud images — Nova injects the per-sandbox
    /// keypair into that user's authorized_keys).
    /// </summary>
    public string SshUser { get; init; } = "ubuntu";

    /// <summary>
    /// Owner host id stamped into server metadata/tags so leak reaping after
    /// a restart only touches this host's sandboxes. Empty falls back to the
    /// machine name at call time.
    /// </summary>
    public string OwnerId { get; init; } = string.Empty;

    /// <summary>
    /// Name prefix for every server this provider creates. Must start with
    /// <c>codeybox-</c> so leak reaping can identify managed servers without
    /// touching unrelated ones.
    /// </summary>
    public string ServerNamePrefix { get; init; } = "codeybox-";

    /// <summary>Filename prefix for temp SSH key material (no path separators).</summary>
    public string KeypairNamePrefix { get; init; } = "codeybox-";

    /// <summary>Neutron security-group name prefix (must start with <c>codeybox-</c>).</summary>
    public string SecurityGroupNamePrefix { get; init; } = "codeybox-sg-";

    /// <summary>
    /// CIDR list allowed to reach TCP/22 on sandbox security groups — the
    /// orchestrator egress addresses. Compared and stored verbatim; each entry
    /// must parse as a CIDR. Required non-empty: without it neither the
    /// provider nor any operator could SSH in, so provisioning refuses to run.
    /// </summary>
    public IReadOnlyList<string> OrchestratorSshCidrs { get; init; } = [];

    /// <summary>
    /// DNS server IPs permitted for UDP/TCP 53 egress (best-effort defence in
    /// depth; the kind stays NotEnforced). Empty means no DNS egress.
    /// </summary>
    public IReadOnlyList<string> DnsServerIps { get; init; } = ["1.1.1.1", "8.8.8.8"];

    /// <summary>
    /// NTP server IPs permitted for UDP 123 egress (best-effort defence in
    /// depth). Empty means no NTP egress.
    /// </summary>
    public IReadOnlyList<string> NtpServerIps { get; init; } = ["1.1.1.1", "8.8.8.8"];

    /// <summary>Upper bound on security-group rules created per sandbox (1–1024).</summary>
    public int MaxEgressRules { get; init; } = 128;

    /// <summary>Ceiling for the ACTIVE wait plus the SSH-readiness wait, in seconds (30–3600 each).</summary>
    public int SshReadyTimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// Base backoff (seconds, 5–3600) stamped on provisioning-deferred
    /// exceptions so the orchestrator rechecks quota/capacity after a bounded
    /// wait rather than hot-looping against a rejecting cloud.
    /// </summary>
    public int ProvisioningRecheckSeconds { get; init; } = 60;

    /// <summary>OpenSSH client binary for the data plane. Resolved via $PATH when bare.</summary>
    public string SshBinary { get; init; } = "ssh";

    /// <summary>ssh-keygen binary used for ephemeral per-sandbox keypairs. Resolved via $PATH when bare.</summary>
    public string SshKeygenBinary { get; init; } = "ssh-keygen";

    /// <summary>SSH port on the guest (1–65535).</summary>
    public int SshPort { get; init; } = 22;

    /// <summary>OpenSSH ConnectTimeout for data-plane connections, in seconds (1–300).</summary>
    public int SshConnectTimeoutSeconds { get; init; } = 10;

    /// <summary>Per-host DNS timeout when resolving AllowedHosts to egress IPs, in seconds (1–120).</summary>
    public int DnsTimeoutSeconds { get; init; } = 15;

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
            HttpTimeoutSeconds = ReadClampedInt(section, "HttpTimeoutSeconds", defaults.HttpTimeoutSeconds, 1, 600),
            TokenRefreshSkewSeconds = ReadClampedInt(section, "TokenRefreshSkewSeconds", defaults.TokenRefreshSkewSeconds, 0, 600),
            ReadyTimeoutSeconds = ReadClampedInt(section, "ReadyTimeoutSeconds", defaults.ReadyTimeoutSeconds, 30, 3600),
            PollIntervalMilliseconds = ReadClampedInt(section, "PollIntervalMilliseconds", defaults.PollIntervalMilliseconds, 200, 60_000),
            MaxPollIntervalMilliseconds = ReadClampedInt(section, "MaxPollIntervalMilliseconds", defaults.MaxPollIntervalMilliseconds, 1000, 120_000),
            MaxResponseBytes = ReadClampedInt(section, "MaxResponseBytes", defaults.MaxResponseBytes, 64 * 1024, 64 * 1024 * 1024),
            MaxListItems = ReadClampedInt(section, "MaxListItems", defaults.MaxListItems, 1, 100_000),
            MaxListPages = ReadClampedInt(section, "MaxListPages", defaults.MaxListPages, 1, 500),
            MaxUserDataBytes = ReadClampedInt(section, "MaxUserDataBytes", defaults.MaxUserDataBytes, 1024, 1024 * 1024),
            ImageName = (section["ImageName"] ?? string.Empty).Trim(),
            FlavorName = (section["FlavorName"] ?? string.Empty).Trim(),
            NetworkId = (section["NetworkId"] ?? string.Empty).Trim(),
            FloatingNetworkId = (section["FloatingNetworkId"] ?? string.Empty).Trim(),
            SshUser = PluginConfigReaders.ReadNonEmpty(section, "SshUser", defaults.SshUser),
            OwnerId = (section["OwnerId"] ?? string.Empty).Trim(),
            ServerNamePrefix = PluginConfigReaders.ReadNonEmpty(section, "ServerNamePrefix", defaults.ServerNamePrefix),
            KeypairNamePrefix = PluginConfigReaders.ReadNonEmpty(section, "KeypairNamePrefix", defaults.KeypairNamePrefix),
            SecurityGroupNamePrefix = PluginConfigReaders.ReadNonEmpty(
                section, "SecurityGroupNamePrefix", defaults.SecurityGroupNamePrefix),
            OrchestratorSshCidrs = PluginConfigReaders.ReadList(
                section.GetSection("OrchestratorSshCidrs"), defaults.OrchestratorSshCidrs),
            DnsServerIps = PluginConfigReaders.ReadList(section.GetSection("DnsServerIps"), defaults.DnsServerIps),
            NtpServerIps = PluginConfigReaders.ReadList(section.GetSection("NtpServerIps"), defaults.NtpServerIps),
            MaxEgressRules = ReadClampedInt(section, "MaxEgressRules", defaults.MaxEgressRules, 1, 1024),
            SshReadyTimeoutSeconds = ReadClampedInt(
                section, "SshReadyTimeoutSeconds", defaults.SshReadyTimeoutSeconds, 30, 3600),
            ProvisioningRecheckSeconds = ReadClampedInt(
                section, "ProvisioningRecheckSeconds", defaults.ProvisioningRecheckSeconds, 5, 3600),
            SshBinary = PluginConfigReaders.ReadNonEmpty(section, "SshBinary", defaults.SshBinary),
            SshKeygenBinary = PluginConfigReaders.ReadNonEmpty(section, "SshKeygenBinary", defaults.SshKeygenBinary),
            SshPort = ReadClampedInt(section, "SshPort", defaults.SshPort, 1, 65535),
            SshConnectTimeoutSeconds = ReadClampedInt(
                section, "SshConnectTimeoutSeconds", defaults.SshConnectTimeoutSeconds, 1, 300),
            DnsTimeoutSeconds = ReadClampedInt(
                section, "DnsTimeoutSeconds", defaults.DnsTimeoutSeconds, 1, 120),
            AllowUnsafeHttp = PluginConfigReaders.ReadBool(section, "AllowUnsafeHttp", defaults.AllowUnsafeHttp),
        };
    }

    private static int ReadClampedInt(
        IConfigurationSection section, string name, int defaultValue, int min, int max) =>
        Math.Clamp(PluginConfigReaders.ReadInt(section, name, defaultValue), min, max);

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
