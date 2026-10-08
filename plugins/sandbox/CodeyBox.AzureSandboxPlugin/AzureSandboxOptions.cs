using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.AzureSandboxPlugin;

/// <summary>
/// Operator knobs for the Azure Virtual Machines sandbox-provider plugin,
/// bound from <c>CodeyBox:Plugins:codeybox.azure-sandbox</c>. Every
/// operational value lives here — never as a literal in source — and the
/// section is re-read on every operation so edits take effect without a host
/// restart.
///
/// <para>Secrets never appear here: the ARM bearer token is read from the
/// host credential chain (process environment) at call time, so rotation
/// propagates without a restart and a config file can never carry secret
/// material. Only the variable <em>name</em> is configured.</para>
///
/// <para>Disabled by default: the provider refuses to operate until
/// <c>Enabled</c> is set and the plugin is allowlisted. The provider kind
/// contributed by this plugin is <c>azure</c>.</para>
/// </summary>
public sealed record AzureSandboxOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.azure-sandbox";

    /// <summary>Sandbox provider kind this plugin contributes.</summary>
    public const string ProviderKind = "azure";

    /// <summary>Master switch. Default false: the provider refuses to operate until enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Azure Resource Manager endpoint (e.g. <c>https://management.azure.com</c>).
    /// Must be https (http only for loopback test URLs under <see cref="AllowUnsafeHttp"/>).
    /// </summary>
    public string ManagementUrl { get; init; } = "https://management.azure.com";

    /// <summary>
    /// Azure subscription id owning the sandbox resources. Required: empty refuses provisioning.
    /// Compared by exact (ordinal) match and echoed into every resource id.
    /// </summary>
    public string SubscriptionId { get; init; } = string.Empty;

    /// <summary>
    /// Caller-owned resource group containing the sandbox resources. Required: empty refuses
    /// provisioning. The provider never creates or deletes the resource group itself.
    /// </summary>
    public string ResourceGroupName { get; init; } = string.Empty;

    /// <summary>
    /// Azure region for new resources (e.g. <c>westeurope</c>). Required: empty refuses provisioning.
    /// Compared by exact (ordinal, case-insensitive) match against existing resources.
    /// </summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>
    /// VM size for every sandbox (e.g. <c>Standard_D2s_v5</c>). Required: empty refuses provisioning.
    /// </summary>
    public string VmSize { get; init; } = string.Empty;

    /// <summary>
    /// Caller-owned virtual network containing <see cref="SubnetName"/>. Required.
    /// The provider never creates, modifies, or deletes the VNet or the subnet.
    /// </summary>
    public string VirtualNetworkName { get; init; } = string.Empty;

    /// <summary>
    /// Caller-owned subnet new NICs attach to. Required. Never created or deleted by the provider.
    /// </summary>
    public string SubnetName { get; init; } = string.Empty;

    /// <summary>
    /// Platform image publisher (e.g. <c>Canonical</c>). Required: empty refuses provisioning.
    /// </summary>
    public string ImagePublisher { get; init; } = string.Empty;

    /// <summary>
    /// Platform image offer (e.g. <c>0001-com-ubuntu-server-jammy</c>). Required.
    /// </summary>
    public string ImageOffer { get; init; } = string.Empty;

    /// <summary>
    /// Platform image SKU (e.g. <c>22_04-lts-gen2</c>). Required.
    /// </summary>
    public string ImageSku { get; init; } = string.Empty;

    /// <summary>
    /// Immutable platform image version (e.g. <c>22.04.20240101120000</c>). Required and
    /// immutable: <c>latest</c> is rejected so every boot pins an exact, reviewable image.
    /// </summary>
    public string ImageVersion { get; init; } = string.Empty;

    /// <summary>Guest login user for SSH (the image's default user, e.g. <c>azureuser</c>).</summary>
    public string AdminUsername { get; init; } = "azureuser";

    /// <summary>
    /// Allocate a per-sandbox public IP and SSH to it. Default false: the sandbox is
    /// reached on its private address. The public IP is provider-owned and deleted on disposal.
    /// </summary>
    public bool AllocatePublicIp { get; init; }

    /// <summary>
    /// Name of the environment variable holding the ARM bearer token.
    /// The value is never read from configuration files, never logged, and never persisted.
    /// </summary>
    public string TokenEnvVar { get; init; } = "AZURE_ACCESS_TOKEN";

    /// <summary>
    /// Owner host id stamped into resource tags so leak reaping after a restart only
    /// touches this host's sandboxes. Empty falls back to the machine name at call time.
    /// </summary>
    public string OwnerId { get; init; } = string.Empty;

    /// <summary>
    /// Name prefix for every VM (and derived NIC/NSG/public-IP/disk name) this provider
    /// creates. Must start with <c>codeybox-</c> so leak reaping can identify managed
    /// resources without touching unrelated ones.
    /// </summary>
    public string VmNamePrefix { get; init; } = "codeybox-";

    /// <summary>
    /// CIDR list allowed to reach TCP/22 on sandbox network security groups — the
    /// orchestrator egress addresses. Compared and stored verbatim; each entry must
    /// parse as a CIDR. Required non-empty: without it neither the provider nor any
    /// operator could SSH in, so provisioning refuses to run.
    /// </summary>
    public IReadOnlyList<string> OrchestratorSshCidrs { get; init; } = [];

    /// <summary>Upper bound on NSG security rules created per sandbox (1–1024).</summary>
    public int MaxSecurityRules { get; init; } = 64;

    /// <summary>OS disk size in GiB (30–4095). The OS disk is provider-owned and deleted on disposal.</summary>
    public int OsDiskSizeGiB { get; init; } = 64;

    /// <summary>Per-request HTTP timeout for ARM calls, in seconds (1–600).</summary>
    public int HttpTimeoutSeconds { get; init; } = AzureClientLimits.DefaultHttpTimeoutSeconds;

    /// <summary>Default ceiling for a provisioning-state wait, in seconds (30–3600).</summary>
    public int ReadyTimeoutSeconds { get; init; } = 600;

    /// <summary>Base delay between status polls, in milliseconds (200–60000). Backs off exponentially.</summary>
    public int PollIntervalMilliseconds { get; init; } = AzureClientLimits.DefaultPollIntervalMilliseconds;

    /// <summary>Ceiling for the exponential poll backoff and for honoured Retry-After, in milliseconds (1000–120000).</summary>
    public int MaxPollIntervalMilliseconds { get; init; } = AzureClientLimits.DefaultMaxPollIntervalMilliseconds;

    /// <summary>Upper bound on a single decoded API response body, in bytes (64 KiB–64 MiB).</summary>
    public int MaxResponseBytes { get; init; } = AzureClientLimits.DefaultMaxResponseBytes;

    /// <summary>Upper bound on items collected from one list operation, across pages (1–100000).</summary>
    public int MaxListItems { get; init; } = AzureClientLimits.DefaultMaxListItems;

    /// <summary>Maximum list pages walked by a paged listing (1–500). Hitting the cap fails loudly.</summary>
    public int MaxListPages { get; init; } = AzureClientLimits.DefaultMaxListPages;

    /// <summary>Upper bound on cloud-init userData bytes sent on VM create (1 KiB–1 MiB).</summary>
    public int MaxUserDataBytes { get; init; } = AzureClientLimits.DefaultMaxUserDataBytes;

    /// <summary>Ceiling for the SSH-readiness wait, in seconds (30–3600).</summary>
    public int SshReadyTimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// Base backoff (seconds, 5–3600) stamped on provisioning-deferred exceptions so the
    /// orchestrator rechecks quota/capacity after a bounded wait rather than hot-looping.
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

    /// <summary>
    /// ARM API version for Microsoft.Compute resources. Pinned to a supported version;
    /// change only to adopt a newer supported version.
    /// </summary>
    public string ComputeApiVersion { get; init; } = AzureApiClient.ComputeApiVersion;

    /// <summary>
    /// ARM API version for Microsoft.Network resources. Pinned to a supported version;
    /// change only to adopt a newer supported version.
    /// </summary>
    public string NetworkApiVersion { get; init; } = AzureApiClient.NetworkApiVersion;

    /// <summary>
    /// Test hook: allow plain-http management URLs, but only for loopback hosts
    /// (localhost / 127.0.0.1 / ::1). Remote http URLs are refused even with this
    /// set — the bearer token rides every request. Never set in production.
    /// </summary>
    public bool AllowUnsafeHttp { get; init; }

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid values fall
    /// back to safe defaults via <see cref="PluginConfigReaders"/> so a bad hot-reload
    /// never crashes an operation.
    /// </summary>
    public static AzureSandboxOptions FromConfiguration(IConfigurationSection? section)
    {
        var defaults = new AzureSandboxOptions();
        if (section is null)
            return defaults;

        return new AzureSandboxOptions
        {
            Enabled = PluginConfigReaders.ReadBool(section, "Enabled", defaults.Enabled),
            ManagementUrl = PluginConfigReaders.ReadNonEmpty(section, "ManagementUrl", defaults.ManagementUrl).Trim(),
            SubscriptionId = (section["SubscriptionId"] ?? string.Empty).Trim(),
            ResourceGroupName = (section["ResourceGroupName"] ?? string.Empty).Trim(),
            Location = (section["Location"] ?? string.Empty).Trim(),
            VmSize = (section["VmSize"] ?? string.Empty).Trim(),
            VirtualNetworkName = (section["VirtualNetworkName"] ?? string.Empty).Trim(),
            SubnetName = (section["SubnetName"] ?? string.Empty).Trim(),
            ImagePublisher = (section["ImagePublisher"] ?? string.Empty).Trim(),
            ImageOffer = (section["ImageOffer"] ?? string.Empty).Trim(),
            ImageSku = (section["ImageSku"] ?? string.Empty).Trim(),
            ImageVersion = (section["ImageVersion"] ?? string.Empty).Trim(),
            AdminUsername = PluginConfigReaders.ReadNonEmpty(section, "AdminUsername", defaults.AdminUsername),
            AllocatePublicIp = PluginConfigReaders.ReadBool(section, "AllocatePublicIp", defaults.AllocatePublicIp),
            TokenEnvVar = PluginConfigReaders.ReadNonEmpty(section, "TokenEnvVar", defaults.TokenEnvVar),
            OwnerId = (section["OwnerId"] ?? string.Empty).Trim(),
            VmNamePrefix = PluginConfigReaders.ReadNonEmpty(section, "VmNamePrefix", defaults.VmNamePrefix),
            OrchestratorSshCidrs = PluginConfigReaders.ReadList(
                section.GetSection("OrchestratorSshCidrs"), defaults.OrchestratorSshCidrs),
            MaxSecurityRules = ReadClampedInt(section, "MaxSecurityRules", defaults.MaxSecurityRules, 1, 1024),
            OsDiskSizeGiB = ReadClampedInt(section, "OsDiskSizeGiB", defaults.OsDiskSizeGiB, 30, 4095),
            HttpTimeoutSeconds = ReadClampedInt(section, "HttpTimeoutSeconds", defaults.HttpTimeoutSeconds, 1, 600),
            ReadyTimeoutSeconds = ReadClampedInt(section, "ReadyTimeoutSeconds", defaults.ReadyTimeoutSeconds, 30, 3600),
            PollIntervalMilliseconds = ReadClampedInt(section, "PollIntervalMilliseconds", defaults.PollIntervalMilliseconds, 200, 60_000),
            MaxPollIntervalMilliseconds = ReadClampedInt(section, "MaxPollIntervalMilliseconds", defaults.MaxPollIntervalMilliseconds, 1000, 120_000),
            MaxResponseBytes = ReadClampedInt(section, "MaxResponseBytes", defaults.MaxResponseBytes, 64 * 1024, 64 * 1024 * 1024),
            MaxListItems = ReadClampedInt(section, "MaxListItems", defaults.MaxListItems, 1, 100_000),
            MaxListPages = ReadClampedInt(section, "MaxListPages", defaults.MaxListPages, 1, 500),
            MaxUserDataBytes = ReadClampedInt(section, "MaxUserDataBytes", defaults.MaxUserDataBytes, 1024, 1024 * 1024),
            SshReadyTimeoutSeconds = ReadClampedInt(
                section, "SshReadyTimeoutSeconds", defaults.SshReadyTimeoutSeconds, 30, 3600),
            ProvisioningRecheckSeconds = ReadClampedInt(
                section, "ProvisioningRecheckSeconds", defaults.ProvisioningRecheckSeconds, 5, 3600),
            SshBinary = PluginConfigReaders.ReadNonEmpty(section, "SshBinary", defaults.SshBinary),
            SshKeygenBinary = PluginConfigReaders.ReadNonEmpty(section, "SshKeygenBinary", defaults.SshKeygenBinary),
            SshPort = ReadClampedInt(section, "SshPort", defaults.SshPort, 1, 65535),
            SshConnectTimeoutSeconds = ReadClampedInt(
                section, "SshConnectTimeoutSeconds", defaults.SshConnectTimeoutSeconds, 1, 300),
            ComputeApiVersion = PluginConfigReaders.ReadNonEmpty(
                section, "ComputeApiVersion", defaults.ComputeApiVersion),
            NetworkApiVersion = PluginConfigReaders.ReadNonEmpty(
                section, "NetworkApiVersion", defaults.NetworkApiVersion),
            AllowUnsafeHttp = PluginConfigReaders.ReadBool(section, "AllowUnsafeHttp", defaults.AllowUnsafeHttp),
        };
    }

    private static int ReadClampedInt(
        IConfigurationSection section, string name, int defaultValue, int min, int max) =>
        Math.Clamp(PluginConfigReaders.ReadInt(section, name, defaultValue), min, max);

    /// <summary>Projects the size/timeout knobs onto the REST client's bounds record.</summary>
    public AzureClientLimits ToClientLimits() => new()
    {
        HttpTimeout = TimeSpan.FromSeconds(HttpTimeoutSeconds),
        PollInterval = TimeSpan.FromMilliseconds(PollIntervalMilliseconds),
        MaxPollInterval = TimeSpan.FromMilliseconds(MaxPollIntervalMilliseconds),
        MaxResponseBytes = MaxResponseBytes,
        MaxListItems = MaxListItems,
        MaxListPages = MaxListPages,
        AllowUnsafeHttp = AllowUnsafeHttp,
    };
}
