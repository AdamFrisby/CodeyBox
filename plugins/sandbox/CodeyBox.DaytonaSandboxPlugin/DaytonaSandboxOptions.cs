using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.DaytonaSandboxPlugin;

/// <summary>
/// Operator knobs for the Daytona sandbox-provider plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.daytona-sandbox</c>. Every operational value
/// lives here — never as a literal in source — and the section is re-read on
/// every sandbox operation so edits take effect without a host restart.
///
/// <para>Secrets never appear here: <see cref="ApiKeyEnvVar"/> names an
/// environment variable whose value the operator provisions from the host
/// credential chain (vault agent, systemd credentials, container secrets).
/// Only the variable name is configured; the value is re-read from the
/// process environment at call time so host-side rotation propagates without
/// a restart.</para>
/// </summary>
public sealed record DaytonaSandboxOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.daytona-sandbox";

    /// <summary>Sandbox provider kind this plugin contributes.</summary>
    public const string ProviderKind = "daytona";

    /// <summary>
    /// Label applied to every sandbox this provider creates. Leak reaping and
    /// managed-inventory listing match on this label (plus the name prefix),
    /// so a sandbox not created by CodeyBox is never swept.
    /// </summary>
    internal const string ManagedLabelKey = "codeybox.managed";
    internal const string ManagedLabelValue = "true";

    /// <summary>Label carrying the owning work item id on a managed sandbox.</summary>
    internal const string WorkItemLabelKey = "codeybox.work-item";

    /// <summary>
    /// Label carrying the SHA-256 of the recovery token issued by
    /// <c>IPreemptibleSandbox.RetainForInfrastructureRecoveryAsync</c>. The
    /// token hash — never the token — lands on the service-side record, so the
    /// label is not itself a credential to reopen the sandbox.
    /// </summary>
    internal const string RecoveryTokenHashLabelKey = "codeybox.recovery-token-sha256";

    /// <summary>Master switch. Default false: the provider refuses to provision until enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Daytona control-plane base URL. Must be https.</summary>
    public string ApiUrl { get; init; } = "https://app.daytona.io/api";

    /// <summary>
    /// Toolbox proxy base URL. Each sandbox's toolbox endpoint is reached at
    /// <c>{ToolboxProxyUrl}/{sandboxId}/...</c>; a per-sandbox override returned
    /// by the service (<c>toolboxProxyUrl</c>) takes precedence.
    /// </summary>
    public string ToolboxProxyUrl { get; init; } = "https://proxy.app.daytona.io/toolbox";

    /// <summary>
    /// Name of the environment variable holding the Daytona API key. The value
    /// is never read from configuration files.
    /// </summary>
    public string ApiKeyEnvVar { get; init; } = "DAYTONA_API_KEY";

    /// <summary>
    /// Optional Daytona organization id, sent as <c>X-Daytona-Organization-ID</c>
    /// on every request. Empty uses the key's default organization.
    /// </summary>
    public string OrganizationId { get; init; } = string.Empty;

    /// <summary>Target region id the sandbox is scheduled into (<c>target</c>). Empty = organization default.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>
    /// Snapshot name/id used when the work spec's <c>ImageReference</c> does not
    /// name one. Must resolve to a snapshot Daytona can boot — a name like
    /// <c>daytona-small</c> or an id. Empty means the caller's
    /// <c>SandboxSpec.ImageReference</c> is required.
    /// </summary>
    public string DefaultSnapshot { get; init; } = string.Empty;

    /// <summary>
    /// Sandbox class passed to snapshot creates (<c>sandboxClass</c>). The
    /// default empty value requests Daytona's default (container) class; set
    /// <c>linux-vm</c> to run each sandbox as a VM with a dedicated guest
    /// kernel — that is what makes the provider report
    /// <see cref="SandboxIsolationLevel.DedicatedKernel"/> instead of
    /// <see cref="SandboxIsolationLevel.SharedKernel"/>.
    /// </summary>
    public string SandboxClass { get; init; } = string.Empty;

    /// <summary>
    /// Name prefix for every sandbox the provider creates. Must start with
    /// <c>codeybox-</c> so leak reaping can identify managed sandboxes without
    /// touching unrelated ones.
    /// </summary>
    public string NamePrefix { get; init; } = DaytonaSandboxProvider.DefaultNamePrefix;

    /// <summary>
    /// Operator commands run inside the sandbox immediately after it reaches
    /// the started state — the provisioning path for toolchains and agent CLIs
    /// on a provider with no cloud-init. Commands run through
    /// <c>bash -lc</c> before the network policy and mounts are applied, so
    /// package installs see open egress; they are operator-trusted and run
    /// before any agent code.
    /// </summary>
    public IReadOnlyList<string> SetupCommands { get; init; } = [];

    // No network-profile map on purpose: a named SandboxNetworkPolicy.ProfileName
    // implies host-side nftables enforcement, which cannot exist on hosted
    // infrastructure — placement refuses those specs for this (NotEnforced) kind,
    // and the provider refuses again if one ever reaches it.
    /// <summary>
    /// Per-request HTTP timeout for control-plane and toolbox REST calls, in
    /// seconds (1–600). Does not bound the exec log stream — that is a
    /// WebSocket whose lifetime is the command's.
    /// </summary>
    public int HttpTimeoutSeconds { get; init; } = 60;

    /// <summary>Seconds to wait for a new sandbox to reach the started state (30–3600).</summary>
    public int ReadyTimeoutSeconds { get; init; } = 300;

    /// <summary>Seconds to wait for start/stop/pause transitions to settle (10–1800).</summary>
    public int TransitionTimeoutSeconds { get; init; } = 300;

    /// <summary>Milliseconds between sandbox state polls while waiting for a transition (200–60000).</summary>
    public int PollIntervalMilliseconds { get; init; } = 2000;

    /// <summary>
    /// Minutes after which Daytona deletes a stopped sandbox. Must stay
    /// negative (never auto-delete): a stopped sandbox retained by
    /// suspend/preempt/recovery must survive until the pipeline resumes or
    /// reaps it itself.
    /// </summary>
    public int AutoDeleteIntervalMinutes { get; init; } = -1;

    /// <summary>
    /// Minutes of idleness after which Daytona stops a sandbox. Null leaves the
    /// service default; 0 disables auto-stop. Non-null positive values are an
    /// operator decision — an auto-stopped sandbox reads as an execution outage
    /// to the pipeline's progress watchdogs.
    /// </summary>
    public int? AutoStopIntervalMinutes { get; init; }

    /// <summary>
    /// Maximum list pages walked by managed-inventory and snapshot listing
    /// calls (1–500). Hitting the cap fails loudly rather than reporting a
    /// truncated inventory as complete.
    /// </summary>
    public int MaxListPages { get; init; } = 100;

    /// <summary>Seconds a baseline bake may take before it is abandoned (300–14400).</summary>
    public int BaselineBakeTimeoutSeconds { get; init; } = 5400;

    /// <summary>
    /// Name prefix for baked baseline snapshots (<c>POST /snapshots</c> and
    /// sandbox-snapshot bakes). Must start with <c>codeybox-</c> so baseline
    /// reaping scopes to provider-owned snapshots.
    /// </summary>
    public string BaselineSnapshotPrefix { get; init; } = "codeybox-baseline-";

    /// <summary>Baseline snapshot image name used as the bake source when a work spec does not pin one.</summary>
    public string BaselineSourceImage { get; init; } = "daytonaio/sandbox:latest";

    /// <summary>
    /// Upper bound on one exec's captured stdout or stderr when the caller
    /// supplies no <c>MaxStdoutBytes</c>/<c>MaxStderrBytes</c> — the DoS
    /// backstop against an in-sandbox process emitting unbounded output.
    /// Callers that set an explicit cap keep it.
    /// </summary>
    public int MaxExecOutputBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>Base64-encoded tar archive bound when syncing a writable mount back to the host.</summary>
    public int MaxSyncArchiveBase64Bytes { get; init; } = 128 * 1024 * 1024;

    /// <summary>Compressed tar size bound when syncing a writable mount back to the host.</summary>
    public int MaxSyncArchiveBytes { get; init; } = 96 * 1024 * 1024;

    /// <summary>Expanded-size bound when validating a synced tar before host extraction.</summary>
    public long MaxSyncArchiveExpandedBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Entry-count bound when validating a synced tar before host extraction.</summary>
    public int MaxSyncArchiveEntries { get; init; } = 200_000;

    /// <summary>Base64 payload bound when staging or syncing a single file.</summary>
    public int MaxFileSyncBase64Bytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>Decoded-size bound when staging or syncing a single file.</summary>
    public long MaxFileSyncBytes { get; init; } = 48L * 1024 * 1024;

    /// <summary>
    /// Bounds the base64 environment/stdin payload delivered to a session exec
    /// over the command-input channel. A larger payload risks the in-sandbox
    /// stdin buffer stalling before the bootstrap drains it.
    /// </summary>
    public int MaxExecInputBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// Base backoff (seconds, 5–3600) stamped on provisioning-deferred
    /// exceptions so the orchestrator rechecks capacity/auth/quota after a
    /// bounded wait rather than hot-looping against a rejecting service.
    /// </summary>
    public int ProvisioningRecheckSeconds { get; init; } = 60;

    /// <summary>
    /// Test hook: allow plain-http control-plane and toolbox URLs. Never set in
    /// production — the API key rides every request.
    /// </summary>
    public bool AllowUnsafeHttp { get; init; }

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid
    /// values fall back to safe defaults via <see cref="PluginConfigReaders"/>
    /// so a bad hot-reload never crashes a create/exec.
    /// </summary>
    public static DaytonaSandboxOptions FromConfiguration(IConfigurationSection? section)
    {
        var defaults = new DaytonaSandboxOptions();
        if (section is null)
            return defaults;

        return new DaytonaSandboxOptions
        {
            Enabled = PluginConfigReaders.ReadBool(section, "Enabled", defaults.Enabled),
            ApiUrl = PluginConfigReaders.ReadNonEmpty(section, "ApiUrl", defaults.ApiUrl),
            ToolboxProxyUrl = PluginConfigReaders.ReadNonEmpty(section, "ToolboxProxyUrl", defaults.ToolboxProxyUrl),
            ApiKeyEnvVar = PluginConfigReaders.ReadNonEmpty(section, "ApiKeyEnvVar", defaults.ApiKeyEnvVar),
            OrganizationId = (section["OrganizationId"] ?? string.Empty).Trim(),
            Target = (section["Target"] ?? string.Empty).Trim(),
            DefaultSnapshot = (section["DefaultSnapshot"] ?? string.Empty).Trim(),
            SandboxClass = (section["SandboxClass"] ?? string.Empty).Trim(),
            NamePrefix = PluginConfigReaders.ReadNonEmpty(section, "NamePrefix", defaults.NamePrefix),
            SetupCommands = PluginConfigReaders.ReadList(section.GetSection("SetupCommands"), defaults.SetupCommands),
            HttpTimeoutSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "HttpTimeoutSeconds", defaults.HttpTimeoutSeconds), 1, 600),
            ReadyTimeoutSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "ReadyTimeoutSeconds", defaults.ReadyTimeoutSeconds), 30, 3600),
            TransitionTimeoutSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "TransitionTimeoutSeconds", defaults.TransitionTimeoutSeconds), 10, 1800),
            PollIntervalMilliseconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "PollIntervalMilliseconds", defaults.PollIntervalMilliseconds), 200, 60_000),
            AutoDeleteIntervalMinutes = PluginConfigReaders.ReadInt(
                section, "AutoDeleteIntervalMinutes", defaults.AutoDeleteIntervalMinutes),
            AutoStopIntervalMinutes = ReadNullableInt(section, "AutoStopIntervalMinutes"),
            MaxListPages = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxListPages", defaults.MaxListPages), 1, 500),
            BaselineBakeTimeoutSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "BaselineBakeTimeoutSeconds", defaults.BaselineBakeTimeoutSeconds), 300, 14_400),
            BaselineSnapshotPrefix = PluginConfigReaders.ReadNonEmpty(
                section, "BaselineSnapshotPrefix", defaults.BaselineSnapshotPrefix),
            BaselineSourceImage = PluginConfigReaders.ReadNonEmpty(
                section, "BaselineSourceImage", defaults.BaselineSourceImage),
            MaxExecOutputBytes = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxExecOutputBytes", defaults.MaxExecOutputBytes), 1024, 512 * 1024 * 1024),
            MaxSyncArchiveBase64Bytes = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxSyncArchiveBase64Bytes", defaults.MaxSyncArchiveBase64Bytes), 1024, 1024 * 1024 * 1024),
            MaxSyncArchiveBytes = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxSyncArchiveBytes", defaults.MaxSyncArchiveBytes), 1024, 512 * 1024 * 1024),
            MaxSyncArchiveExpandedBytes = Math.Clamp(
                ReadLongClamped(section, "MaxSyncArchiveExpandedBytes", defaults.MaxSyncArchiveExpandedBytes), 1024, 4L * 1024 * 1024 * 1024),
            MaxSyncArchiveEntries = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxSyncArchiveEntries", defaults.MaxSyncArchiveEntries), 1, 2_000_000),
            MaxFileSyncBase64Bytes = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxFileSyncBase64Bytes", defaults.MaxFileSyncBase64Bytes), 1024, 256 * 1024 * 1024),
            MaxFileSyncBytes = Math.Clamp(
                ReadLongClamped(section, "MaxFileSyncBytes", defaults.MaxFileSyncBytes), 1024, 192L * 1024 * 1024),
            MaxExecInputBytes = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxExecInputBytes", defaults.MaxExecInputBytes), 1024, 64 * 1024 * 1024),
            ProvisioningRecheckSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "ProvisioningRecheckSeconds", defaults.ProvisioningRecheckSeconds), 5, 3600),
            AllowUnsafeHttp = PluginConfigReaders.ReadBool(section, "AllowUnsafeHttp", defaults.AllowUnsafeHttp),
        };
    }

    private static long ReadLongClamped(IConfigurationSection section, string key, long fallback)
    {
        var raw = section[key];
        return long.TryParse(raw?.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static int? ReadNullableInt(IConfigurationSection section, string key)
    {
        var raw = section[key];
        return int.TryParse(raw?.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
