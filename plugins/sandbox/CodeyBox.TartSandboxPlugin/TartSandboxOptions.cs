using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.TartSandboxPlugin;

/// <summary>
/// Operator knobs for the Tart sandbox-provider plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.tart-sandbox</c>. Every operational value
/// lives here — never as a literal in source — and the section is re-read on
/// every sandbox operation so edits take effect without a host restart.
///
/// <para>Secrets never appear here: <see cref="SshPasswordEnvVar"/> names an
/// environment variable whose value the operator provisions from the host
/// credential chain (vault agent, systemd credentials, macOS Keychain via an
/// env-exporting launcher). Only the variable name is configured; the value
/// is re-read from the process environment at call time so host-side rotation
/// propagates without a restart.</para>
/// </summary>
public sealed record TartSandboxOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.tart-sandbox";

    /// <summary>Sandbox provider kind this plugin contributes.</summary>
    public const string ProviderKind = "tart";

    /// <summary>Master switch. Default false: the provider refuses to provision until enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Tart CLI binary. An absolute path keeps process resolution off the
    /// ambient <c>PATH</c>; the bare name falls back to <c>PATH</c> lookup.
    /// </summary>
    public string TartBinaryPath { get; init; } = "tart";

    /// <summary>
    /// OCI image reference cloned when the work spec's
    /// <c>ImageReference</c> is blank. Must name an image Tart can clone
    /// (for example <c>ghcr.io/cirruslabs/macos-sequoia-base:latest</c>).
    /// Empty means the caller's <c>SandboxSpec.ImageReference</c> is required.
    /// </summary>
    public string DefaultImage { get; init; } = "ghcr.io/cirruslabs/macos-sequoia-base:latest";

    /// <summary>
    /// Name prefix for every VM the provider clones. Must start with
    /// <c>codeybox-</c> so leak reaping can identify managed VMs without
    /// touching unrelated ones.
    /// </summary>
    public string NamePrefix { get; init; } = TartSandboxProvider.DefaultNamePrefix;

    /// <summary>Guest SSH username. Non-secret; baked into the VM image.</summary>
    public string SshUsername { get; init; } = "admin";

    /// <summary>
    /// Name of the environment variable holding the guest SSH password. The
    /// value is never read from configuration files.
    /// </summary>
    public string SshPasswordEnvVar { get; init; } = "TART_SSH_PASSWORD";

    /// <summary>
    /// Resolved guest SSH password. Production wiring leaves this null and
    /// resolves <see cref="SshPasswordEnvVar"/> at use time; tests inject it.
    /// </summary>
    public string? SshPassword { get; init; }

    /// <summary>
    /// Host path to an SSH private key for key-based guest auth. Empty means
    /// password auth via <c>sshpass -e</c>. This is a path, not key material:
    /// the key itself stays in a host-owned file. When both a reachable key
    /// and a password resolve, the key wins and the password is not used.
    /// </summary>
    public string SshPrivateKeyPath { get; init; } = string.Empty;

    /// <summary>Guest SSH port (1–65535).</summary>
    public int SshPort { get; init; } = 22;

    /// <summary>Default vCPU count applied with <c>tart set</c> when the spec leaves <c>CpuCount</c> unset (1–32).</summary>
    public int DefaultCpuCount { get; init; } = 4;

    /// <summary>Default guest RAM in GiB applied with <c>tart set</c> when the spec leaves <c>MemoryBytes</c> unset (1–128).</summary>
    public int DefaultMemoryGiB { get; init; } = 8;

    /// <summary>
    /// Extra arguments appended to every <c>tart run</c> invocation, after the
    /// provider's own flags and before the VM name. The default runs
    /// headless; clear it (and accept a GUI session) only on a host with a
    /// logged-in console user.
    /// </summary>
    public IReadOnlyList<string> ExtraRunArgs { get; init; } = ["--no-graphics"];

    /// <summary>
    /// Operator commands run inside the guest immediately after it becomes
    /// reachable — the provisioning path for toolchains and agent CLIs on
    /// images that do not bake them in. Operator-trusted; they run before any
    /// agent code, and (like all guest traffic here) outside any CodeyBox
    /// egress allowlist.
    /// </summary>
    public IReadOnlyList<string> SetupCommands { get; init; } = [];

    // No network-profile map on purpose: a named SandboxNetworkPolicy.ProfileName
    // implies host-side nftables enforcement, which cannot exist on macOS —
    // placement refuses those specs for this (NotEnforced) kind, and the
    // provider refuses again if one ever reaches it.

    /// <summary>Seconds for one SSH connect attempt (1–120).</summary>
    public int SshConnectTimeoutSeconds { get; init; } = 10;

    /// <summary>Seconds to wait for a cloned VM to answer SSH after <c>tart run</c> (10–3600).</summary>
    public int ReadyTimeoutSeconds { get; init; } = 300;

    /// <summary>Seconds to wait for stop/delete transitions to settle (5–900).</summary>
    public int TransitionTimeoutSeconds { get; init; } = 180;

    /// <summary>Milliseconds between guest-reachability polls (250–60000).</summary>
    public int PollIntervalMilliseconds { get; init; } = 2000;

    /// <summary>Per-invocation timeout for short control-plane CLI calls (<c>clone</c>, <c>ip</c>, <c>list</c>), in seconds (5–600).</summary>
    public int CliTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// When true the provider requires a macOS orchestrator host (Tart runs
    /// only on Apple Silicon macs). Leads with an explicit refusal naming the
    /// kind instead of a confusing missing-binary error. Tests disable it.
    /// </summary>
    public bool RequireMacOSHost { get; init; } = true;

    /// <summary>
    /// Upper bound on one exec's captured stdout or stderr when the caller
    /// supplies no <c>MaxStdoutBytes</c>/<c>MaxStderrBytes</c> — the DoS
    /// backstop against a guest process emitting unbounded output. Callers
    /// that set an explicit cap keep it.
    /// </summary>
    public int MaxExecOutputBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>Cap on the generated remote shell command's UTF-8 bytes per exec.</summary>
    public int MaxCommandBytes { get; init; } = 512 * 1024;

    /// <summary>Cap on merged environment payload bytes per exec.</summary>
    public int MaxEnvironmentBytes { get; init; } = 256 * 1024;

    /// <summary>Cap on exec stdin payload bytes.</summary>
    public int MaxStdinBytes { get; init; } = 1024 * 1024;

    /// <summary>Cap on one staged file's bytes when materialising host mounts into the guest.</summary>
    public long MaxStageFileBytes { get; init; } = 48L * 1024 * 1024;

    /// <summary>Cap on total staged bytes per sandbox create.</summary>
    public long MaxStageTotalBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Cap on staged file count per sandbox create.</summary>
    public int MaxStageFileCount { get; init; } = 5000;

    /// <summary>Cap on one read-back file's bytes during teardown sync-back.</summary>
    public long MaxReadBackBytes { get; init; } = 48L * 1024 * 1024;

    /// <summary>
    /// Explicit downgrade for non-secret tmpfs mounts to persistent guest
    /// directories. Until set, such mounts are refused so no caller silently
    /// depends on persistence tmpfs never promised.
    /// </summary>
    public bool AllowPersistentTmpfsDowngrade { get; init; }

    /// <summary>
    /// Base backoff (seconds, 5–3600) stamped on provisioning-deferred
    /// exceptions so the orchestrator rechecks capacity/auth/quota after a
    /// bounded wait rather than hot-looping against a failing host.
    /// </summary>
    public int ProvisioningRecheckSeconds { get; init; } = 60;

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid
    /// values fall back to safe defaults via <see cref="PluginConfigReaders"/>
    /// so a bad hot-reload never crashes a create/exec.
    /// </summary>
    public static TartSandboxOptions FromConfiguration(IConfigurationSection? section)
    {
        var defaults = new TartSandboxOptions();
        if (section is null)
            return defaults;

        return new TartSandboxOptions
        {
            Enabled = PluginConfigReaders.ReadBool(section, "Enabled", defaults.Enabled),
            TartBinaryPath = PluginConfigReaders.ReadNonEmpty(section, "TartBinaryPath", defaults.TartBinaryPath),
            DefaultImage = (section["DefaultImage"] ?? string.Empty).Trim(),
            NamePrefix = PluginConfigReaders.ReadNonEmpty(section, "NamePrefix", defaults.NamePrefix),
            SshUsername = PluginConfigReaders.ReadNonEmpty(section, "SshUsername", defaults.SshUsername),
            SshPasswordEnvVar = PluginConfigReaders.ReadNonEmpty(section, "SshPasswordEnvVar", defaults.SshPasswordEnvVar),
            SshPrivateKeyPath = (section["SshPrivateKeyPath"] ?? string.Empty).Trim(),
            SshPort = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "SshPort", defaults.SshPort), 1, 65535),
            DefaultCpuCount = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "DefaultCpuCount", defaults.DefaultCpuCount), 1, 32),
            DefaultMemoryGiB = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "DefaultMemoryGiB", defaults.DefaultMemoryGiB), 1, 128),
            ExtraRunArgs = PluginConfigReaders.ReadList(section.GetSection("ExtraRunArgs"), defaults.ExtraRunArgs),
            SetupCommands = PluginConfigReaders.ReadList(section.GetSection("SetupCommands"), defaults.SetupCommands),
            SshConnectTimeoutSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "SshConnectTimeoutSeconds", defaults.SshConnectTimeoutSeconds), 1, 120),
            ReadyTimeoutSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "ReadyTimeoutSeconds", defaults.ReadyTimeoutSeconds), 10, 3600),
            TransitionTimeoutSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "TransitionTimeoutSeconds", defaults.TransitionTimeoutSeconds), 5, 900),
            PollIntervalMilliseconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "PollIntervalMilliseconds", defaults.PollIntervalMilliseconds), 250, 60_000),
            CliTimeoutSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "CliTimeoutSeconds", defaults.CliTimeoutSeconds), 5, 600),
            RequireMacOSHost = PluginConfigReaders.ReadBool(section, "RequireMacOSHost", defaults.RequireMacOSHost),
            MaxExecOutputBytes = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxExecOutputBytes", defaults.MaxExecOutputBytes), 1024, 512 * 1024 * 1024),
            MaxCommandBytes = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxCommandBytes", defaults.MaxCommandBytes), 1024, 64 * 1024 * 1024),
            MaxEnvironmentBytes = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxEnvironmentBytes", defaults.MaxEnvironmentBytes), 1024, 64 * 1024 * 1024),
            MaxStdinBytes = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxStdinBytes", defaults.MaxStdinBytes), 1024, 64 * 1024 * 1024),
            MaxStageFileBytes = Math.Clamp(
                ReadLongOrFallback(section, "MaxStageFileBytes", defaults.MaxStageFileBytes), 1024, 512L * 1024 * 1024),
            MaxStageTotalBytes = Math.Clamp(
                ReadLongOrFallback(section, "MaxStageTotalBytes", defaults.MaxStageTotalBytes), 1024, 4L * 1024 * 1024 * 1024),
            MaxStageFileCount = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxStageFileCount", defaults.MaxStageFileCount), 1, 1_000_000),
            MaxReadBackBytes = Math.Clamp(
                ReadLongOrFallback(section, "MaxReadBackBytes", defaults.MaxReadBackBytes), 1024, 512L * 1024 * 1024),
            AllowPersistentTmpfsDowngrade = PluginConfigReaders.ReadBool(section, "AllowPersistentTmpfsDowngrade", defaults.AllowPersistentTmpfsDowngrade),
            ProvisioningRecheckSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "ProvisioningRecheckSeconds", defaults.ProvisioningRecheckSeconds), 5, 3600),
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
