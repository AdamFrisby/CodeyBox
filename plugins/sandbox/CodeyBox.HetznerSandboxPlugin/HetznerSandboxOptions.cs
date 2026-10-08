using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.HetznerSandboxPlugin;

/// <summary>
/// Operator knobs for the Hetzner Cloud sandbox-provider plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.hetzner-sandbox</c>. Every operational value
/// lives here — never as a literal in source — and the section is re-read on
/// every operation so edits take effect without a host restart.
///
/// <para>Secrets never appear here: the API token is read from the host
/// credential chain (process environment) at call time, so rotation propagates
/// without a restart and a config file can never carry secret material. Only
/// the variable <em>name</em> is configured; it defaults to the standard
/// <c>HCLOUD_TOKEN</c>.</para>
///
/// <para>Disabled by default: the provider refuses to operate until
/// <c>Enabled</c> is set and the plugin is allowlisted. The provider kind
/// contributed by this plugin is <c>hetzner</c>. No baseline bake/snapshot
/// capability is advertised: acquisitions always boot the configured approved
/// image.</para>
/// </summary>
public sealed record HetznerSandboxOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.hetzner-sandbox";

    /// <summary>Sandbox provider kind this plugin contributes.</summary>
    public const string ProviderKind = "hetzner";

    /// <summary>Master switch. Default false: the provider refuses to operate until enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Hetzner Cloud API base URL (default <c>https://api.hetzner.cloud/v1</c>).
    /// Empty falls back to the <c>HCLOUD_API_ENDPOINT</c> environment variable,
    /// then to the default. Must be https (http only for loopback test URLs
    /// under <see cref="AllowUnsafeHttp"/>).
    /// </summary>
    public string ApiBaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Name of the environment variable holding the Hetzner Cloud API token.
    /// The value is never read from configuration files, never logged, and
    /// never persisted — and never copied into guest user-data.
    /// </summary>
    public string TokenEnvVar { get; init; } = HetznerCredentialChain.TokenEnvVarName;

    /// <summary>Per-request HTTP timeout for Hetzner Cloud API calls, in seconds (1–600).</summary>
    public int HttpTimeoutSeconds { get; init; } = HetznerClientLimits.DefaultHttpTimeoutSeconds;

    /// <summary>Default ceiling for a server-status wait, in seconds (30–3600).</summary>
    public int ReadyTimeoutSeconds { get; init; } = 600;

    /// <summary>Base delay between server-status polls, in milliseconds (200–60000). Backs off exponentially.</summary>
    public int PollIntervalMilliseconds { get; init; } = HetznerClientLimits.DefaultPollIntervalMilliseconds;

    /// <summary>Ceiling for the exponential poll backoff, in milliseconds (1000–120000).</summary>
    public int MaxPollIntervalMilliseconds { get; init; } = HetznerClientLimits.DefaultMaxPollIntervalMilliseconds;

    /// <summary>Upper bound on a single decoded API response body, in bytes (64 KiB–64 MiB).</summary>
    public int MaxResponseBytes { get; init; } = HetznerClientLimits.DefaultMaxResponseBytes;

    /// <summary>Upper bound on items collected from one list operation, across pages (1–100000).</summary>
    public int MaxListItems { get; init; } = HetznerClientLimits.DefaultMaxListItems;

    /// <summary>Maximum list pages walked by a paged listing (1–500). Hitting the cap fails loudly.</summary>
    public int MaxListPages { get; init; } = HetznerClientLimits.DefaultMaxListPages;

    /// <summary>Upper bound on cloud-config user_data bytes sent on server create (1 KiB–1 MiB).</summary>
    public int MaxUserDataBytes { get; init; } = HetznerClientLimits.DefaultMaxUserDataBytes;

    /// <summary>
    /// Approved image each sandbox boots with, as a numeric image id or an
    /// exact (ordinal) image name which must match exactly one available,
    /// non-deprecated system image (e.g. <c>ubuntu-24.04</c>). Required: empty
    /// refuses provisioning. A spec <c>ImageReference</c> overrides this per
    /// acquisition and resolves with the same rules. Deprecated images are
    /// refused loudly — the approval pins a supported image, not a rotting one.
    /// </summary>
    public string Image { get; init; } = string.Empty;

    /// <summary>
    /// Server type each sandbox boots with (e.g. <c>cx23</c>). Resolved by
    /// exact (ordinal) name; a missing or ambiguous name fails loudly.
    /// Required: empty refuses provisioning.
    /// </summary>
    public string ServerType { get; init; } = string.Empty;

    /// <summary>
    /// Location each sandbox boots in (e.g. <c>fsn1</c>). Resolved by exact
    /// (ordinal) location name; a missing name fails loudly. Required: empty
    /// refuses provisioning. Pins the architecture/location placement
    /// explicitly instead of letting the vendor choose.
    /// </summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>
    /// Private network id attached at create time. Zero (default) means no
    /// private network: the sandbox is reached on its public address. Names
    /// are not accepted — ids are stable across renames.
    /// </summary>
    public long NetworkId { get; init; }

    /// <summary>Whether the server keeps its public IPv4 address (SSH reachability). Default true.</summary>
    public bool EnablePublicIpv4 { get; init; } = true;

    /// <summary>Whether the server keeps its public IPv6 address. Default false.</summary>
    public bool EnablePublicIpv6 { get; init; }

    /// <summary>
    /// Home location for an optional floating IPv4 address (e.g.
    /// <c>fsn1</c>). Empty (default) disables floating IPs: the sandbox is
    /// reached on its server public address. Set to allocate one floating IP
    /// per sandbox, created already assigned to the server, and SSH to it.
    /// </summary>
    public string FloatingIpHomeLocation { get; init; } = string.Empty;

    /// <summary>
    /// Guest login user for SSH (Hetzner cloud images default to
    /// <c>root</c>; the per-sandbox key is injected into that user's
    /// authorized_keys via cloud-init).
    /// </summary>
    public string SshUser { get; init; } = "root";

    /// <summary>
    /// Owner host id stamped into server/SSH-key/firewall/floating-IP labels
    /// so leak reaping after a restart only touches this host's sandboxes.
    /// Empty falls back to the machine name at call time.
    /// </summary>
    public string OwnerId { get; init; } = string.Empty;

    /// <summary>
    /// Name prefix for every server this provider creates. Must start with
    /// <c>codeybox-</c> so leak reaping can scope candidates — though a prefix
    /// alone never authorizes deletion: labels are re-verified every time.
    /// </summary>
    public string ServerNamePrefix { get; init; } = "codeybox-";

    /// <summary>Name prefix for per-sandbox SSH keys (must start with <c>codeybox-</c>).</summary>
    public string SshKeyNamePrefix { get; init; } = "codeybox-";

    /// <summary>Name prefix for per-sandbox firewalls (must start with <c>codeybox-</c>).</summary>
    public string FirewallNamePrefix { get; init; } = "codeybox-fw-";

    /// <summary>Name prefix for per-sandbox floating IPs (must start with <c>codeybox-</c>).</summary>
    public string FloatingIpNamePrefix { get; init; } = "codeybox-fip-";

    /// <summary>
    /// CIDR list allowed to reach TCP/22 on sandbox firewalls — the
    /// orchestrator egress addresses. Compared and stored verbatim; each entry
    /// must parse as a CIDR. Required non-empty: without it neither the
    /// provider nor any operator could SSH in, so provisioning refuses to run.
    /// </summary>
    public IReadOnlyList<string> OrchestratorSshCidrs { get; init; } = [];

    /// <summary>
    /// DNS server IPs permitted for outbound UDP/TCP 53 (best-effort defence
    /// in depth; the kind stays NotEnforced). Empty means no DNS egress rule.
    /// </summary>
    public IReadOnlyList<string> DnsServerIps { get; init; } = ["1.1.1.1", "8.8.8.8"];

    /// <summary>
    /// NTP server IPs permitted for outbound UDP 123 (best-effort defence in
    /// depth). Empty means no NTP egress rule.
    /// </summary>
    public IReadOnlyList<string> NtpServerIps { get; init; } = ["1.1.1.1", "8.8.8.8"];

    /// <summary>Upper bound on firewall rules created per sandbox (1–1024).</summary>
    public int MaxFirewallRules { get; init; } = 128;

    /// <summary>Ceiling for the SSH-readiness wait, in seconds (30–3600).</summary>
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
    /// Test hook: allow plain-http API URLs, but only for loopback hosts
    /// (localhost / 127.0.0.1 / ::1). Remote http URLs are refused even with
    /// this set — the API token rides every request. Never set in production.
    /// </summary>
    public bool AllowUnsafeHttp { get; init; }

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid
    /// values fall back to safe defaults via <see cref="PluginConfigReaders"/>
    /// so a bad hot-reload never crashes an operation.
    /// </summary>
    public static HetznerSandboxOptions FromConfiguration(IConfigurationSection? section)
    {
        var defaults = new HetznerSandboxOptions();
        if (section is null)
            return defaults;

        return new HetznerSandboxOptions
        {
            Enabled = PluginConfigReaders.ReadBool(section, "Enabled", defaults.Enabled),
            ApiBaseUrl = (section["ApiBaseUrl"] ?? string.Empty).Trim(),
            TokenEnvVar = PluginConfigReaders.ReadNonEmpty(
                section, "TokenEnvVar", defaults.TokenEnvVar),
            HttpTimeoutSeconds = ReadClampedInt(section, "HttpTimeoutSeconds", defaults.HttpTimeoutSeconds, 1, 600),
            ReadyTimeoutSeconds = ReadClampedInt(section, "ReadyTimeoutSeconds", defaults.ReadyTimeoutSeconds, 30, 3600),
            PollIntervalMilliseconds = ReadClampedInt(section, "PollIntervalMilliseconds", defaults.PollIntervalMilliseconds, 200, 60_000),
            MaxPollIntervalMilliseconds = ReadClampedInt(section, "MaxPollIntervalMilliseconds", defaults.MaxPollIntervalMilliseconds, 1000, 120_000),
            MaxResponseBytes = ReadClampedInt(section, "MaxResponseBytes", defaults.MaxResponseBytes, 64 * 1024, 64 * 1024 * 1024),
            MaxListItems = ReadClampedInt(section, "MaxListItems", defaults.MaxListItems, 1, 100_000),
            MaxListPages = ReadClampedInt(section, "MaxListPages", defaults.MaxListPages, 1, 500),
            MaxUserDataBytes = ReadClampedInt(section, "MaxUserDataBytes", defaults.MaxUserDataBytes, 1024, 1024 * 1024),
            Image = (section["Image"] ?? string.Empty).Trim(),
            ServerType = (section["ServerType"] ?? string.Empty).Trim(),
            Location = (section["Location"] ?? string.Empty).Trim(),
            NetworkId = ReadClampedLong(section, "NetworkId", defaults.NetworkId, 0, long.MaxValue),
            EnablePublicIpv4 = PluginConfigReaders.ReadBool(section, "EnablePublicIpv4", defaults.EnablePublicIpv4),
            EnablePublicIpv6 = PluginConfigReaders.ReadBool(section, "EnablePublicIpv6", defaults.EnablePublicIpv6),
            FloatingIpHomeLocation = (section["FloatingIpHomeLocation"] ?? string.Empty).Trim(),
            SshUser = PluginConfigReaders.ReadNonEmpty(section, "SshUser", defaults.SshUser),
            OwnerId = (section["OwnerId"] ?? string.Empty).Trim(),
            ServerNamePrefix = PluginConfigReaders.ReadNonEmpty(section, "ServerNamePrefix", defaults.ServerNamePrefix),
            SshKeyNamePrefix = PluginConfigReaders.ReadNonEmpty(section, "SshKeyNamePrefix", defaults.SshKeyNamePrefix),
            FirewallNamePrefix = PluginConfigReaders.ReadNonEmpty(
                section, "FirewallNamePrefix", defaults.FirewallNamePrefix),
            FloatingIpNamePrefix = PluginConfigReaders.ReadNonEmpty(
                section, "FloatingIpNamePrefix", defaults.FloatingIpNamePrefix),
            OrchestratorSshCidrs = PluginConfigReaders.ReadList(
                section.GetSection("OrchestratorSshCidrs"), defaults.OrchestratorSshCidrs),
            DnsServerIps = PluginConfigReaders.ReadList(section.GetSection("DnsServerIps"), defaults.DnsServerIps),
            NtpServerIps = PluginConfigReaders.ReadList(section.GetSection("NtpServerIps"), defaults.NtpServerIps),
            MaxFirewallRules = ReadClampedInt(section, "MaxFirewallRules", defaults.MaxFirewallRules, 8, 1024),
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

    private static long ReadClampedLong(
        IConfigurationSection section, string name, long defaultValue, long min, long max)
    {
        var raw = section[name];
        if (!string.IsNullOrWhiteSpace(raw)
            && long.TryParse(
                raw.Trim(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            return Math.Clamp(parsed, min, max);
        }
        return defaultValue;
    }

    private static int ReadClampedInt(
        IConfigurationSection section, string name, int defaultValue, int min, int max) =>
        Math.Clamp(PluginConfigReaders.ReadInt(section, name, defaultValue), min, max);

    /// <summary>Projects the size/timeout knobs onto the REST client's bounds record.</summary>
    public HetznerClientLimits ToClientLimits() => new()
    {
        HttpTimeout = TimeSpan.FromSeconds(HttpTimeoutSeconds),
        PollInterval = TimeSpan.FromMilliseconds(PollIntervalMilliseconds),
        MaxPollInterval = TimeSpan.FromMilliseconds(MaxPollIntervalMilliseconds),
        MaxResponseBytes = MaxResponseBytes,
        MaxListItems = MaxListItems,
        MaxListPages = MaxListPages,
        MaxUserDataBytes = MaxUserDataBytes,
        AllowUnsafeHttp = AllowUnsafeHttp,
    };
}
