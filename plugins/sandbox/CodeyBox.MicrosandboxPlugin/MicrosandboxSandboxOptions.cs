using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.MicrosandboxPlugin;

/// <summary>
/// Operator knobs for the microsandbox provider plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.microsandbox-sandbox</c>. Every operational value
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
public sealed record MicrosandboxSandboxOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.microsandbox-sandbox";

    /// <summary>Sandbox provider kind this plugin contributes.</summary>
    public const string ProviderKind = "microsandbox";

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

    /// <summary>
    /// Microsandbox server base URL (the <c>/v1</c> API root is appended by the
    /// client). The server runs on the orchestrator host (default loopback) and
    /// manages local microVMs. Must be https unless <see cref="AllowUnsafeHttp"/>
    /// permits a loopback http URL for local dev.
    /// </summary>
    public string ServerUrl { get; init; } = "https://127.0.0.1:5555";

    /// <summary>
    /// Name of the environment variable holding the microsandbox API key. The value
    /// is never read from configuration files.
    /// </summary>
    public string ApiKeyEnvVar { get; init; } = "MICROSANDBOX_API_KEY";

    /// <summary>
    /// Image reference used when the work spec's <c>ImageReference</c> is blank.
    /// Should name an image the local server has pulled. Empty means the caller's
    /// <c>SandboxSpec.ImageReference</c> is required.
    /// </summary>
    public string DefaultImage { get; init; } = string.Empty;

    /// <summary>
    /// Name prefix for every sandbox the provider creates. Must start with
    /// <c>codeybox-</c> so leak reaping can identify managed sandboxes without
    /// touching unrelated ones.
    /// </summary>
    public string NamePrefix { get; init; } = MicrosandboxSandboxProvider.DefaultNamePrefix;

    /// <summary>
    /// Operator commands run inside the sandbox immediately after it reaches
    /// the running state — the provisioning path for toolchains and agent CLIs
    /// on images that do not bake them in. Commands run through
    /// <c>sh -lc</c> before the network restriction is re-asserted, so package
    /// installs see open egress; they are operator-trusted and run before any
    /// agent code.
    /// </summary>
    public IReadOnlyList<string> SetupCommands { get; init; } = [];

    // No network-profile map on purpose: a named SandboxNetworkPolicy.ProfileName
    // implies host-side nftables enforcement, which this local plugin backend
    // does not attach — placement refuses those specs for this (NotEnforced)
    // kind, and the provider refuses again if one ever reaches it.

    /// <summary>Default vCPU count for sandboxes when the spec leaves <c>CpuCount</c> unset (1–64).</summary>
    public int DefaultCpuCount { get; init; } = 2;

    /// <summary>Default guest RAM in MiB when the spec leaves <c>MemoryBytes</c> unset (256–262144).</summary>
    public int DefaultMemoryMiB { get; init; } = 4096;

    /// <summary>Default guest disk in GiB when the spec leaves <c>DiskBytes</c> unset (4–1024).</summary>
    public int DefaultDiskGiB { get; init; } = 16;

    /// <summary>
    /// Per-request HTTP timeout for server REST calls, in seconds (1–600).
    /// Does not bound exec polling — that is driven by the wall clock and the
    /// configured poll interval.
    /// </summary>
    public int HttpTimeoutSeconds { get; init; } = 60;

    /// <summary>Seconds to wait for a new sandbox to reach the running state (5–1800).</summary>
    public int ReadyTimeoutSeconds { get; init; } = 300;

    /// <summary>Seconds to wait for start/stop/pause/resume transitions to settle (5–900).</summary>
    public int TransitionTimeoutSeconds { get; init; } = 180;

    /// <summary>Milliseconds between sandbox state and exec polls (100–60000).</summary>
    public int PollIntervalMilliseconds { get; init; } = 500;

    /// <summary>
    /// Maximum sandboxes walked by managed-inventory and snapshot listing calls
    /// (1–500). Hitting the cap fails loudly rather than reporting a
    /// truncated inventory as complete.
    /// </summary>
    public int MaxListPages { get; init; } = 100;

    /// <summary>Seconds a baseline bake may take before it is abandoned (300–14400).</summary>
    public int BaselineBakeTimeoutSeconds { get; init; } = 3600;

    /// <summary>
    /// Name prefix for baked baseline snapshots. Must start with
    /// <c>codeybox-</c> so baseline reaping scopes to provider-owned snapshots.
    /// </summary>
    public string BaselineSnapshotPrefix { get; init; } = "codeybox-baseline-";

    /// <summary>Baseline bake source image used when a work spec does not pin one.</summary>
    public string BaselineSourceImage { get; init; } = "microsandbox/base:latest";

    /// <summary>
    /// Upper bound on one exec's captured stdout or stderr when the caller
    /// supplies no <c>MaxStdoutBytes</c>/<c>MaxStderrBytes</c> — the DoS
    /// backstop against an in-sandbox process emitting unbounded output.
    /// Callers that set an explicit cap keep it.
    /// </summary>
    public int MaxExecOutputBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>Bounds the environment/stdin payload delivered to a sandbox exec.</summary>
    public int MaxExecInputBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Base64 payload bound when reading or writing a single file.</summary>
    public int MaxFileSyncBase64Bytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>Decoded-size bound when reading or writing a single file.</summary>
    public long MaxFileSyncBytes { get; init; } = 48L * 1024 * 1024;

    /// <summary>
    /// Base backoff (seconds, 5–3600) stamped on provisioning-deferred
    /// exceptions so the orchestrator rechecks capacity/auth/quota after a
    /// bounded wait rather than hot-looping against a rejecting service.
    /// </summary>
    public int ProvisioningRecheckSeconds { get; init; } = 60;

    /// <summary>
    /// Test hook: allow plain-http server URLs, but only for loopback hosts
    /// (localhost / 127.0.0.1 / ::1). Remote http URLs are refused even with
    /// this set — the API key rides every request. Never set in production.
    /// </summary>
    public bool AllowUnsafeHttp { get; init; }

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid
    /// values fall back to safe defaults via <see cref="PluginConfigReaders"/>
    /// so a bad hot-reload never crashes a create/exec.
    /// </summary>
    public static MicrosandboxSandboxOptions FromConfiguration(IConfigurationSection? section)
    {
        var defaults = new MicrosandboxSandboxOptions();
        if (section is null)
            return defaults;

        return new MicrosandboxSandboxOptions
        {
            Enabled = PluginConfigReaders.ReadBool(section, "Enabled", defaults.Enabled),
            ServerUrl = PluginConfigReaders.ReadNonEmpty(section, "ServerUrl", defaults.ServerUrl),
            ApiKeyEnvVar = PluginConfigReaders.ReadNonEmpty(section, "ApiKeyEnvVar", defaults.ApiKeyEnvVar),
            DefaultImage = (section["DefaultImage"] ?? string.Empty).Trim(),
            NamePrefix = PluginConfigReaders.ReadNonEmpty(section, "NamePrefix", defaults.NamePrefix),
            SetupCommands = PluginConfigReaders.ReadList(section.GetSection("SetupCommands"), defaults.SetupCommands),
            DefaultCpuCount = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "DefaultCpuCount", defaults.DefaultCpuCount), 1, 64),
            DefaultMemoryMiB = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "DefaultMemoryMiB", defaults.DefaultMemoryMiB), 256, 262144),
            DefaultDiskGiB = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "DefaultDiskGiB", defaults.DefaultDiskGiB), 4, 1024),
            HttpTimeoutSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "HttpTimeoutSeconds", defaults.HttpTimeoutSeconds), 1, 600),
            ReadyTimeoutSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "ReadyTimeoutSeconds", defaults.ReadyTimeoutSeconds), 5, 1800),
            TransitionTimeoutSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "TransitionTimeoutSeconds", defaults.TransitionTimeoutSeconds), 5, 900),
            PollIntervalMilliseconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "PollIntervalMilliseconds", defaults.PollIntervalMilliseconds), 100, 60_000),
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
            MaxExecInputBytes = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxExecInputBytes", defaults.MaxExecInputBytes), 1024, 64 * 1024 * 1024),
            MaxFileSyncBase64Bytes = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxFileSyncBase64Bytes", defaults.MaxFileSyncBase64Bytes), 1024, 512 * 1024 * 1024),
            MaxFileSyncBytes = Math.Clamp(
                ReadLongOrFallback(section, "MaxFileSyncBytes", defaults.MaxFileSyncBytes), 1024, 512L * 1024 * 1024),
            ProvisioningRecheckSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "ProvisioningRecheckSeconds", defaults.ProvisioningRecheckSeconds), 5, 3600),
            AllowUnsafeHttp = PluginConfigReaders.ReadBool(section, "AllowUnsafeHttp", defaults.AllowUnsafeHttp),
        };
    }

    private static long ReadLongOrFallback(IConfigurationSection section, string key, long fallback)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw)
            || !long.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return fallback;
        return parsed;
    }
}
