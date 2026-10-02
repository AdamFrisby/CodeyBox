using Microsoft.Extensions.Configuration;

namespace CodeyBox.RunloopPlugin;

/// <summary>
/// Operator-configurable knobs for the Runloop Devboxes sandbox provider plugin.
/// Bound from <c>CodeyBox:Plugins:codeybox.runloop</c> via
/// <c>IPluginHost.ScopedConfig</c> and re-read on every <c>CreateAsync</c> so
/// edits take effect without a host restart.
///
/// <para>The Runloop API bearer token never lives here: only the
/// <see cref="TokenEnvironmentVariable"/> name is configured, and the token is
/// resolved from the process environment at use time (production) or injected
/// directly (tests).</para>
/// </summary>
public sealed record RunloopSandboxOptions
{
    /// <summary>Provider kind this plugin contributes. Matched by exact ordinal equality.</summary>
    public const string ProviderKind = "runloop";

    /// <summary>Plugin id under <c>CodeyBox:Plugins:</c>.</summary>
    public const string PluginId = "codeybox.runloop";

    /// <summary>Runloop REST API base URL. Must be absolute https (http only with <see cref="AllowUnsafeHttp"/>).</summary>
    public string ApiBaseUrl { get; init; } = "https://api.runloop.ai";

    /// <summary>Name of the environment variable holding the Runloop API bearer token.</summary>
    public string TokenEnvironmentVariable { get; init; } = "RUNLOOP_API_KEY";

    /// <summary>
    /// Resolved bearer token. Production wiring leaves this null and resolves
    /// <see cref="TokenEnvironmentVariable"/> at use time; tests inject it.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>Prefix for devbox names created by this provider (lowercase letters, digits, hyphens).</summary>
    public string NamePrefix { get; init; } = "codeybox-";

    /// <summary>Test-only escape hatch for local mock servers. Never enable in production.</summary>
    public bool AllowUnsafeHttp { get; init; }

    /// <summary>Optional blueprint id new devboxes are created from (pre-installed toolchain).</summary>
    public string? BlueprintId { get; init; }

    /// <summary>Optional blueprint name (latest successful build) new devboxes are created from.</summary>
    public string? BlueprintName { get; init; }

    /// <summary>Optional disk snapshot id new devboxes are restored from.</summary>
    public string? SnapshotId { get; init; }

    /// <summary>
    /// Runloop resource size preset (X_SMALL, SMALL, MEDIUM, LARGE, X_LARGE, XX_LARGE, CUSTOM_SIZE).
    /// Per-spec CPU/memory/disk limits are advisory only and do not resize the devbox.
    /// </summary>
    public string ResourceSize { get; init; } = "MEDIUM";

    /// <summary>Custom CPU cores for CUSTOM_SIZE (0.5, 1, or a multiple of 2; max 16).</summary>
    public double? CustomCpuCores { get; init; }

    /// <summary>Custom memory GiB for CUSTOM_SIZE (1 or a multiple of 2; max 64).</summary>
    public int? CustomMemoryGiB { get; init; }

    /// <summary>Custom disk GiB for CUSTOM_SIZE (multiple of 2; 2..64).</summary>
    public int? CustomDiskGiB { get; init; }

    /// <summary>Idle keep-alive seconds requested at create (default 1h floor, max 48h).</summary>
    public int KeepAliveSeconds { get; init; } = 3600;

    /// <summary>How long create waits for the devbox to reach running before failing.</summary>
    public TimeSpan WaitForRunningTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Interval between execution-status polls while an exec runs.</summary>
    public TimeSpan ExecPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Per-request HTTP timeout for Runloop API calls.</summary>
    public TimeSpan ApiTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Cap on captured stdout/stderr bytes per exec (host memory backstop).</summary>
    public int MaxExecOutputBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>Cap on one staged file's bytes when materialising host mounts into the devbox.</summary>
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

    /// <summary>Upper bound on devbox list pages per inventory sweep.</summary>
    public int MaxListPages { get; init; } = 10;

    /// <summary>
    /// Explicit downgrade for non-secret tmpfs mounts (e.g. scratch dirs) to persistent
    /// guest directories. Until set, such mounts are refused so no caller silently
    /// depends on persistence tmpfs never promised.
    /// </summary>
    public bool AllowPersistentTmpfsDowngrade { get; init; }

    /// <summary>Reads options from the plugin's scoped configuration section.</summary>
    public static RunloopSandboxOptions FromConfiguration(IConfigurationSection section)
    {
        var defaults = new RunloopSandboxOptions();
        if (section is null)
        {
            return defaults;
        }

        return defaults with
        {
            ApiBaseUrl = ReadString(section, "ApiBaseUrl", defaults.ApiBaseUrl),
            TokenEnvironmentVariable = ReadString(section, "TokenEnvironmentVariable", defaults.TokenEnvironmentVariable),
            NamePrefix = ReadString(section, "NamePrefix", defaults.NamePrefix),
            AllowUnsafeHttp = ReadBool(section, "AllowUnsafeHttp", defaults.AllowUnsafeHttp),
            BlueprintId = ReadOptionalString(section, "BlueprintId"),
            BlueprintName = ReadOptionalString(section, "BlueprintName"),
            SnapshotId = ReadOptionalString(section, "SnapshotId"),
            ResourceSize = ReadString(section, "ResourceSize", defaults.ResourceSize),
            CustomCpuCores = ReadOptionalDouble(section, "CustomCpuCores"),
            CustomMemoryGiB = ReadOptionalInt(section, "CustomMemoryGiB"),
            CustomDiskGiB = ReadOptionalInt(section, "CustomDiskGiB"),
            KeepAliveSeconds = ReadInt(section, "KeepAliveSeconds", defaults.KeepAliveSeconds),
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

    private static int? ReadOptionalInt(IConfigurationSection section, string key)
    {
        var raw = section[key];
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static double? ReadOptionalDouble(IConfigurationSection section, string key)
    {
        var raw = section[key];
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
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
