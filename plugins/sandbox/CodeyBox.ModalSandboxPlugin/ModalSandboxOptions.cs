using Microsoft.Extensions.Configuration;

namespace CodeyBox.ModalPlugin;

/// <summary>
/// Operator-configurable knobs for the Modal Sandboxes provider plugin.
/// Bound from <c>CodeyBox:Plugins:codeybox.modal</c> via
/// <c>IPluginHost.ScopedConfig</c> and re-read on every <c>CreateAsync</c> so
/// edits take effect without a host restart.
///
/// <para>The Modal API credentials never live here: only the
/// <see cref="TokenIdEnvironmentVariable"/> and
/// <see cref="TokenSecretEnvironmentVariable"/> names are configured, and the
/// values are resolved from the process environment at use time (production)
/// or injected directly (tests).</para>
/// </summary>
public sealed record ModalSandboxOptions
{
    /// <summary>Provider kind this plugin contributes. Matched by exact ordinal equality.</summary>
    public const string ProviderKind = "modal";

    /// <summary>Plugin id under <c>CodeyBox:Plugins:</c>.</summary>
    public const string PluginId = "codeybox.modal";

    /// <summary>Modal control-plane base URL. Must be absolute https (http only with <see cref="AllowUnsafeHttp"/>).</summary>
    public string ApiBaseUrl { get; init; } = "https://api.modal.com";

    /// <summary>Name of the environment variable holding the Modal token id.</summary>
    public string TokenIdEnvironmentVariable { get; init; } = "MODAL_TOKEN_ID";

    /// <summary>Name of the environment variable holding the Modal token secret.</summary>
    public string TokenSecretEnvironmentVariable { get; init; } = "MODAL_TOKEN_SECRET";

    /// <summary>
    /// Resolved token id. Production wiring leaves this null and resolves
    /// <see cref="TokenIdEnvironmentVariable"/> at use time; tests inject it.
    /// </summary>
    public string? TokenId { get; init; }

    /// <summary>
    /// Resolved token secret. Production wiring leaves this null and resolves
    /// <see cref="TokenSecretEnvironmentVariable"/> at use time; tests inject it.
    /// </summary>
    public string? TokenSecret { get; init; }

    /// <summary>Modal app under which sandboxes are created (passed through, created if missing).</summary>
    public string AppName { get; init; } = "codeybox";

    /// <summary>
    /// Custom image sandboxes start from (registry ref or Modal image id), baked
    /// by the operator with the agent toolchain. Null uses the service default image.
    /// </summary>
    public string? ImageRef { get; init; }

    /// <summary>
    /// Optional filesystem snapshot new sandboxes are restored from. Mutually
    /// exclusive with nothing: when both are set the snapshot wins and
    /// <see cref="ImageRef"/> is ignored for that create.
    /// </summary>
    public string? SnapshotId { get; init; }

    /// <summary>Prefix for sandbox names created by this provider.</summary>
    public string NamePrefix { get; init; } = "codeybox-";

    /// <summary>Test-only escape hatch for local mock servers. Never enable in production.</summary>
    public bool AllowUnsafeHttp { get; init; }

    /// <summary>CPU cores requested per sandbox (service-side scheduling hint).</summary>
    public double CpuCount { get; init; } = 2;

    /// <summary>Memory MiB requested per sandbox (service-side scheduling hint).</summary>
    public int MemoryMiB { get; init; } = 4096;

    /// <summary>Idle seconds after which the service may reclaim the sandbox.</summary>
    public int IdleTimeoutSeconds { get; init; } = 600;

    /// <summary>How long create waits for the sandbox to reach running before failing.</summary>
    public TimeSpan WaitForRunningTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Interval between exec-output polls while an exec runs.</summary>
    public TimeSpan ExecPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Per-request HTTP timeout for Modal API calls.</summary>
    public TimeSpan ApiTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Cap on captured stdout/stderr bytes per exec (host memory backstop).</summary>
    public int MaxExecOutputBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>Cap on one staged file's bytes when materialising host mounts into the sandbox.</summary>
    public long MaxStageFileBytes { get; init; } = 48L * 1024 * 1024;

    /// <summary>Cap on total staged bytes per sandbox create.</summary>
    public long MaxStageTotalBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Cap on staged file count per sandbox create.</summary>
    public int MaxStageFileCount { get; init; } = 5000;

    /// <summary>Cap on one read-back file's bytes during teardown sync-back.</summary>
    public long MaxReadBackBytes { get; init; } = 48L * 1024 * 1024;

    /// <summary>Cap on the generated shell command's UTF-8 bytes per exec.</summary>
    public int MaxCommandBytes { get; init; } = 512 * 1024;

    /// <summary>Cap on merged environment payload bytes per exec.</summary>
    public int MaxEnvironmentBytes { get; init; } = 256 * 1024;

    /// <summary>Cap on exec stdin payload bytes.</summary>
    public int MaxStdinBytes { get; init; } = 1024 * 1024;

    /// <summary>Upper bound on sandbox list pages per inventory sweep.</summary>
    public int MaxListPages { get; init; } = 10;

    /// <summary>
    /// Explicit downgrade for non-secret tmpfs mounts (e.g. scratch dirs) to persistent
    /// guest directories. Until set, such mounts are refused so no caller silently
    /// depends on persistence tmpfs never promised.
    /// </summary>
    public bool AllowPersistentTmpfsDowngrade { get; init; }

    /// <summary>Reads options from the plugin's scoped configuration section.</summary>
    public static ModalSandboxOptions FromConfiguration(IConfigurationSection section)
    {
        var defaults = new ModalSandboxOptions();
        if (section is null)
        {
            return defaults;
        }

        return defaults with
        {
            ApiBaseUrl = ReadString(section, "ApiBaseUrl", defaults.ApiBaseUrl),
            TokenIdEnvironmentVariable = ReadString(section, "TokenIdEnvironmentVariable", defaults.TokenIdEnvironmentVariable),
            TokenSecretEnvironmentVariable = ReadString(section, "TokenSecretEnvironmentVariable", defaults.TokenSecretEnvironmentVariable),
            AppName = ReadString(section, "AppName", defaults.AppName),
            ImageRef = ReadOptionalString(section, "ImageRef"),
            SnapshotId = ReadOptionalString(section, "SnapshotId"),
            NamePrefix = ReadString(section, "NamePrefix", defaults.NamePrefix),
            AllowUnsafeHttp = ReadBool(section, "AllowUnsafeHttp", defaults.AllowUnsafeHttp),
            CpuCount = ReadDouble(section, "CpuCount", defaults.CpuCount),
            MemoryMiB = ReadInt(section, "MemoryMiB", defaults.MemoryMiB),
            IdleTimeoutSeconds = ReadInt(section, "IdleTimeoutSeconds", defaults.IdleTimeoutSeconds),
            WaitForRunningTimeout = ReadInterval(section, "WaitForRunningTimeoutSeconds", defaults.WaitForRunningTimeout),
            ExecPollInterval = ReadInterval(section, "ExecPollIntervalSeconds", defaults.ExecPollInterval),
            ApiTimeout = ReadInterval(section, "ApiTimeoutSeconds", defaults.ApiTimeout),
            MaxExecOutputBytes = ReadInt(section, "MaxExecOutputBytes", defaults.MaxExecOutputBytes),
            MaxStageFileBytes = ReadLong(section, "MaxStageFileBytes", defaults.MaxStageFileBytes),
            MaxStageTotalBytes = ReadLong(section, "MaxStageTotalBytes", defaults.MaxStageTotalBytes),
            MaxStageFileCount = ReadInt(section, "MaxStageFileCount", defaults.MaxStageFileCount),
            MaxReadBackBytes = ReadLong(section, "MaxReadBackBytes", defaults.MaxReadBackBytes),
            MaxCommandBytes = ReadInt(section, "MaxCommandBytes", defaults.MaxCommandBytes),
            MaxEnvironmentBytes = ReadInt(section, "MaxEnvironmentBytes", defaults.MaxEnvironmentBytes),
            MaxStdinBytes = ReadInt(section, "MaxStdinBytes", defaults.MaxStdinBytes),
            MaxListPages = ReadInt(section, "MaxListPages", defaults.MaxListPages),
            AllowPersistentTmpfsDowngrade = ReadBool(section, "AllowPersistentTmpfsDowngrade", defaults.AllowPersistentTmpfsDowngrade),
        };
    }

    private static string ReadString(IConfigurationSection section, string key, string fallback)
    {
        var raw = section[key];
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
    }

    private static string? ReadOptionalString(IConfigurationSection section, string key)
    {
        var raw = section[key];
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static bool ReadBool(IConfigurationSection section, string key, bool fallback)
    {
        var raw = section[key];
        return bool.TryParse(raw, out var value) ? value : fallback;
    }

    private static int ReadInt(IConfigurationSection section, string key, int fallback)
    {
        var raw = section[key];
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }

    private static long ReadLong(IConfigurationSection section, string key, long fallback)
    {
        var raw = section[key];
        return long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }

    private static double ReadDouble(IConfigurationSection section, string key, double fallback)
    {
        var raw = section[key];
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }

    private static TimeSpan ReadInterval(IConfigurationSection section, string key, TimeSpan fallback)
    {
        var raw = section[key];
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0
            && seconds <= TimeSpan.FromHours(48).TotalSeconds)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return fallback;
    }
}
