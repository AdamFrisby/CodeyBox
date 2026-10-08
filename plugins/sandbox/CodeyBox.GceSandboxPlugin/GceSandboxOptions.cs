using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.GceSandboxPlugin;

/// <summary>
/// Operator knobs for the Google Compute Engine sandbox-provider plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.gce-sandbox</c>. Every operational value lives here —
/// never as a literal in source — and the section is re-read on every operation so
/// edits take effect without a host restart.
///
/// <para>Secrets never appear here: the OAuth2 access token for the workload/machine
/// identity is read from the host credential chain (process environment) at call
/// time, so rotation propagates without a restart and a config file can never carry
/// secret material. Only the variable <em>name</em> is configured.</para>
///
/// <para>Disabled by default: the provider refuses to operate until <c>Enabled</c> is
/// set and the plugin is allowlisted. The provider kind contributed by this plugin is
/// <c>gce</c>. The guest image must be a pinned concrete image (self-link or
/// <c>projects/…/global/images/…</c>); image families are rejected so a moving family
/// can never silently change the guest.</para>
/// </summary>
public sealed record GceSandboxOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.gce-sandbox";

    /// <summary>Sandbox provider kind this plugin contributes.</summary>
    public const string ProviderKind = "gce";

    /// <summary>Master switch. Default false: the provider refuses to operate until enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Compute Engine API base URL (e.g. <c>https://compute.googleapis.com/compute/v1</c>).
    /// Overridden in isolated tests to point at a fake handler. Plain http is refused
    /// except for loopback hosts under <see cref="AllowUnsafeHttp"/>.
    /// </summary>
    public string ComputeBaseUrl { get; init; } = "https://compute.googleapis.com/compute/v1";

    /// <summary>GCP project hosting the sandbox VMs. Required: empty refuses provisioning.</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>Zone for sandbox VMs (e.g. <c>europe-west1-b</c>). Required: empty refuses provisioning.</summary>
    public string Zone { get; init; } = string.Empty;

    /// <summary>
    /// Machine type for sandbox VMs. Either a short name (<c>e2-medium</c>, resolved
    /// against <c>zones/{Zone}/machineTypes/</c>) or a full self-link. Required.
    /// </summary>
    public string MachineType { get; init; } = string.Empty;

    /// <summary>
    /// Pinned concrete boot image: a full self-link or a
    /// <c>projects/{project}/global/images/{image}</c> path. Required. Values naming an
    /// image <c>family</c> are rejected — families move, pins do not.
    /// </summary>
    public string ImageName { get; init; } = string.Empty;

    /// <summary>
    /// VPC network for sandbox VMs: a full self-link or a
    /// <c>projects/{project}/global/networks/{network}</c> path. Required.
    /// </summary>
    public string Network { get; init; } = string.Empty;

    /// <summary>
    /// Subnetwork for sandbox VMs: a full self-link or a
    /// <c>projects/{project}/regions/{region}/subnetworks/{subnet}</c> path. Required.
    /// </summary>
    public string Subnetwork { get; init; } = string.Empty;

    /// <summary>
    /// Boot disk size in GB (10–512). Applied to the instance's initializeParams.
    /// </summary>
    public int BootDiskSizeGb { get; init; } = 20;

    /// <summary>
    /// Explicit boot-disk autoDelete policy. True (default) lets Compute delete the boot
    /// disk with the instance; false means the provider deletes the owned disk explicitly
    /// after the instance deletion is confirmed.
    /// </summary>
    public bool BootDiskAutoDelete { get; init; } = true;

    /// <summary>
    /// Reserve a static external address per sandbox instead of using an ephemeral one.
    /// Default false. Reserved addresses are owned (labels + linkage) and deleted on
    /// teardown after ownership revalidation.
    /// </summary>
    public bool ReserveStaticAddress { get; init; }

    /// <summary>Address type for reserved addresses: <c>EXTERNAL</c> only (no internal reservations).</summary>
    public string AddressType { get; init; } = "EXTERNAL";

    /// <summary>Guest login user for SSH (the image's default user, e.g. <c>ubuntu</c>).</summary>
    public string SshUser { get; init; } = "ubuntu";

    /// <summary>
    /// Owner host id stamped into instance labels/metadata so leak reaping after a restart
    /// only touches this host's sandboxes. Empty falls back to the machine name at call time.
    /// Lowercased and sanitized to the GCE label alphabet before use.
    /// </summary>
    public string OwnerId { get; init; } = string.Empty;

    /// <summary>
    /// Name prefix for every instance this provider creates. Must start with
    /// <c>codeybox-</c> and satisfy the GCE instance-name grammar so leak reaping can
    /// identify managed instances without touching unrelated ones.
    /// </summary>
    public string InstanceNamePrefix { get; init; } = "codeybox-";

    /// <summary>
    /// CIDR list allowed to reach TCP/22 by the per-sandbox firewall rule — the
    /// orchestrator egress addresses. Compared and stored verbatim; each entry must
    /// parse as a CIDR. Required non-empty: without it neither the provider nor any
    /// operator could SSH in, so provisioning refuses to run.
    /// </summary>
    public IReadOnlyList<string> OrchestratorSshCidrs { get; init; } = [];

    /// <summary>
    /// Name of the environment variable holding the OAuth2 access token for the
    /// workload/machine identity. The value is never read from configuration files.
    /// </summary>
    public string AccessTokenEnvVar { get; init; } = GceCredentialChain.AccessTokenEnvVarName;

    /// <summary>Per-request HTTP timeout for Compute calls, in seconds (1–600).</summary>
    public int HttpTimeoutSeconds { get; init; } = 60;

    /// <summary>Ceiling for the zone-operation wait, in seconds (30–3600).</summary>
    public int ReadyTimeoutSeconds { get; init; } = 600;

    /// <summary>Base delay between operation polls, in milliseconds (200–60000). Backs off exponentially.</summary>
    public int PollIntervalMilliseconds { get; init; } = 2000;

    /// <summary>Ceiling for the exponential poll backoff, in milliseconds (1000–120000).</summary>
    public int MaxPollIntervalMilliseconds { get; init; } = 15000;

    /// <summary>Upper bound on a single decoded API response body, in bytes (64 KiB–64 MiB).</summary>
    public int MaxResponseBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Upper bound on items collected from one list operation, across pages (1–100000).</summary>
    public int MaxListItems { get; init; } = 5000;

    /// <summary>Maximum list pages walked by a paged listing (1–500). Hitting the cap fails loudly.</summary>
    public int MaxListPages { get; init; } = 100;

    /// <summary>Upper bound on startup-script bytes sent on instance insert (1 KiB–1 MiB).</summary>
    public int MaxStartupScriptBytes { get; init; } = 64 * 1024;

    /// <summary>Ceiling for the SSH-readiness wait, in seconds (30–3600).</summary>
    public int SshReadyTimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// Base backoff (seconds, 5–3600) stamped on provisioning-deferred exceptions so the
    /// orchestrator rechecks quota/capacity after a bounded wait rather than hot-looping
    /// against a rejecting cloud.
    /// </summary>
    public int ProvisioningRecheckSeconds { get; init; } = 60;

    /// <summary>Maximum insert attempts after an ambiguous (unknown-outcome) create (1–5).</summary>
    public int MaxInsertAttempts { get; init; } = 2;

    /// <summary>OpenSSH client binary for the data plane. Resolved via $PATH when bare.</summary>
    public string SshBinary { get; init; } = "ssh";

    /// <summary>ssh-keygen binary used for ephemeral per-sandbox keypairs. Resolved via $PATH when bare.</summary>
    public string SshKeygenBinary { get; init; } = "ssh-keygen";

    /// <summary>SSH port on the guest (1–65535).</summary>
    public int SshPort { get; init; } = 22;

    /// <summary>OpenSSH ConnectTimeout for data-plane connections, in seconds (1–300).</summary>
    public int SshConnectTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Test hook: allow plain-http Compute base URLs, but only for loopback hosts
    /// (localhost / 127.0.0.1 / ::1). Remote http URLs are refused even with this set —
    /// the access token rides every request. Never set in production.
    /// </summary>
    public bool AllowUnsafeHttp { get; init; }

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid values fall
    /// back to safe defaults via <see cref="PluginConfigReaders"/> so a bad hot-reload
    /// never crashes an operation.
    /// </summary>
    public static GceSandboxOptions FromConfiguration(IConfigurationSection? section)
    {
        var defaults = new GceSandboxOptions();
        if (section is null)
            return defaults;

        return new GceSandboxOptions
        {
            Enabled = PluginConfigReaders.ReadBool(section, "Enabled", defaults.Enabled),
            ComputeBaseUrl = PluginConfigReaders.ReadNonEmpty(section, "ComputeBaseUrl", defaults.ComputeBaseUrl).TrimEnd('/'),
            Project = (section["Project"] ?? string.Empty).Trim(),
            Zone = (section["Zone"] ?? string.Empty).Trim(),
            MachineType = (section["MachineType"] ?? string.Empty).Trim(),
            ImageName = (section["ImageName"] ?? string.Empty).Trim(),
            Network = (section["Network"] ?? string.Empty).Trim(),
            Subnetwork = (section["Subnetwork"] ?? string.Empty).Trim(),
            BootDiskSizeGb = ReadClampedInt(section, "BootDiskSizeGb", defaults.BootDiskSizeGb, 10, 512),
            BootDiskAutoDelete = PluginConfigReaders.ReadBool(section, "BootDiskAutoDelete", defaults.BootDiskAutoDelete),
            ReserveStaticAddress = PluginConfigReaders.ReadBool(section, "ReserveStaticAddress", defaults.ReserveStaticAddress),
            AddressType = PluginConfigReaders.ReadNonEmpty(section, "AddressType", defaults.AddressType),
            SshUser = PluginConfigReaders.ReadNonEmpty(section, "SshUser", defaults.SshUser),
            OwnerId = (section["OwnerId"] ?? string.Empty).Trim(),
            InstanceNamePrefix = PluginConfigReaders.ReadNonEmpty(section, "InstanceNamePrefix", defaults.InstanceNamePrefix),
            OrchestratorSshCidrs = PluginConfigReaders.ReadList(
                section.GetSection("OrchestratorSshCidrs"), defaults.OrchestratorSshCidrs),
            AccessTokenEnvVar = PluginConfigReaders.ReadNonEmpty(
                section, "AccessTokenEnvVar", defaults.AccessTokenEnvVar),
            HttpTimeoutSeconds = ReadClampedInt(section, "HttpTimeoutSeconds", defaults.HttpTimeoutSeconds, 1, 600),
            ReadyTimeoutSeconds = ReadClampedInt(section, "ReadyTimeoutSeconds", defaults.ReadyTimeoutSeconds, 30, 3600),
            PollIntervalMilliseconds = ReadClampedInt(section, "PollIntervalMilliseconds", defaults.PollIntervalMilliseconds, 200, 60_000),
            MaxPollIntervalMilliseconds = ReadClampedInt(section, "MaxPollIntervalMilliseconds", defaults.MaxPollIntervalMilliseconds, 1000, 120_000),
            MaxResponseBytes = ReadClampedInt(section, "MaxResponseBytes", defaults.MaxResponseBytes, 64 * 1024, 64 * 1024 * 1024),
            MaxListItems = ReadClampedInt(section, "MaxListItems", defaults.MaxListItems, 1, 100_000),
            MaxListPages = ReadClampedInt(section, "MaxListPages", defaults.MaxListPages, 1, 500),
            MaxStartupScriptBytes = ReadClampedInt(section, "MaxStartupScriptBytes", defaults.MaxStartupScriptBytes, 1024, 1024 * 1024),
            SshReadyTimeoutSeconds = ReadClampedInt(
                section, "SshReadyTimeoutSeconds", defaults.SshReadyTimeoutSeconds, 30, 3600),
            ProvisioningRecheckSeconds = ReadClampedInt(
                section, "ProvisioningRecheckSeconds", defaults.ProvisioningRecheckSeconds, 5, 3600),
            MaxInsertAttempts = ReadClampedInt(section, "MaxInsertAttempts", defaults.MaxInsertAttempts, 1, 5),
            SshBinary = PluginConfigReaders.ReadNonEmpty(section, "SshBinary", defaults.SshBinary),
            SshKeygenBinary = PluginConfigReaders.ReadNonEmpty(section, "SshKeygenBinary", defaults.SshKeygenBinary),
            SshPort = ReadClampedInt(section, "SshPort", defaults.SshPort, 1, 65535),
            SshConnectTimeoutSeconds = ReadClampedInt(
                section, "SshConnectTimeoutSeconds", defaults.SshConnectTimeoutSeconds, 1, 300),
            AllowUnsafeHttp = PluginConfigReaders.ReadBool(section, "AllowUnsafeHttp", defaults.AllowUnsafeHttp),
        };
    }

    private static int ReadClampedInt(
        IConfigurationSection section, string name, int defaultValue, int min, int max) =>
        Math.Clamp(PluginConfigReaders.ReadInt(section, name, defaultValue), min, max);

    /// <summary>Projects the size/timeout knobs onto the REST client's bounds record.</summary>
    public GceClientLimits ToClientLimits() => new()
    {
        HttpTimeout = TimeSpan.FromSeconds(HttpTimeoutSeconds),
        PollInterval = TimeSpan.FromMilliseconds(PollIntervalMilliseconds),
        MaxPollInterval = TimeSpan.FromMilliseconds(MaxPollIntervalMilliseconds),
        MaxResponseBytes = MaxResponseBytes,
        MaxListItems = MaxListItems,
        MaxListPages = MaxListPages,
        MaxStartupScriptBytes = MaxStartupScriptBytes,
        AllowUnsafeHttp = AllowUnsafeHttp,
    };
}
