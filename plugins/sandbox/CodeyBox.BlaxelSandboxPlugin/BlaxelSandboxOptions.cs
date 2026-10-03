using Microsoft.Extensions.Configuration;

namespace CodeyBox.BlaxelPlugin;

/// <summary>
/// Operator-configurable knobs for the Blaxel perpetual-sandbox provider plugin.
/// Bound from <c>CodeyBox:Plugins:codeybox.blaxel</c> via
/// <c>IPluginHost.ScopedConfig</c> and re-read on every <c>CreateAsync</c> so
/// edits take effect without a host restart.
///
/// <para>Service credentials never live here: only the <see cref="ApiKeyEnvironmentVariable"/>
/// and <see cref="WorkspaceEnvironmentVariable"/> names are configured, and both values are
/// resolved from the process environment at use time (production) or injected directly
/// (tests).</para>
/// </summary>
public sealed record BlaxelSandboxOptions
{
    /// <summary>Provider kind this plugin contributes. Matched by exact ordinal equality.</summary>
    public const string ProviderKind = "blaxel";

    /// <summary>Plugin id under <c>CodeyBox:Plugins:</c>.</summary>
    public const string PluginId = "codeybox.blaxel";

    /// <summary>Blaxel control-plane base URL. Must be absolute https (http only with <see cref="AllowUnsafeHttp"/>).</summary>
    public string ApiBaseUrl { get; init; } = "https://api.blaxel.ai/v0";

    /// <summary>Name of the environment variable holding the Blaxel API key (<c>BL_API_KEY</c>).</summary>
    public string ApiKeyEnvironmentVariable { get; init; } = "BL_API_KEY";

    /// <summary>Name of the environment variable holding the Blaxel workspace name (<c>BL_WORKSPACE</c>).</summary>
    public string WorkspaceEnvironmentVariable { get; init; } = "BL_WORKSPACE";

    /// <summary>
    /// Resolved API key. Production wiring leaves this null and resolves
    /// <see cref="ApiKeyEnvironmentVariable"/> at use time; tests inject it.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Resolved workspace name. Production wiring leaves this null and resolves
    /// <see cref="WorkspaceEnvironmentVariable"/> at use time; tests inject it.
    /// </summary>
    public string? Workspace { get; init; }

    /// <summary>Prefix for sandbox names created by this provider (letter-led, lowercase, digits, hyphens).</summary>
    public string NamePrefix { get; init; } = "codeybox-";

    /// <summary>Test-only escape hatch for local mock servers. Never enable in production.</summary>
    public bool AllowUnsafeHttp { get; init; }

    /// <summary>Sandbox image new sandboxes boot from (public or custom template image).</summary>
    public string Image { get; init; } = "blaxel/base-image:latest";

    /// <summary>Memory allocation in megabytes (also sizes CPU: cores = memory / 2048).</summary>
    public int MemoryMb { get; init; } = 8192;

    /// <summary>
    /// Blaxel region new sandboxes are created in (e.g. <c>us-pdx-1</c>).
    /// Empty means the platform picks the closest region.
    /// </summary>
    public string Region { get; init; } = string.Empty;

    /// <summary>
    /// Max-age from creation after which Blaxel deletes the sandbox
    /// (e.g. <c>48h</c>; units s, m, h, d, w). Bounds snapshot-storage cost for
    /// sandboxes a failed teardown leaves behind.
    /// </summary>
    public string Ttl { get; init; } = "48h";

    /// <summary>How long create/resume waits for the sandbox to reach a usable state before failing.</summary>
    public TimeSpan WaitForRunningTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Interval between process-status polls while an exec runs.</summary>
    public TimeSpan ExecPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Per-request HTTP timeout for Blaxel API calls.</summary>
    public TimeSpan ApiTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Bound on waiting for a busy sandbox to settle into standby after suspend is requested.</summary>
    public TimeSpan SuspendWaitTimeout { get; init; } = TimeSpan.FromMinutes(3);

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
    public static BlaxelSandboxOptions FromConfiguration(IConfigurationSection section)
    {
        var defaults = new BlaxelSandboxOptions();
        if (section is null)
        {
            return defaults;
        }

        return defaults with
        {
            ApiBaseUrl = ReadString(section, "ApiBaseUrl", defaults.ApiBaseUrl),
            ApiKeyEnvironmentVariable = ReadString(section, "ApiKeyEnvironmentVariable", defaults.ApiKeyEnvironmentVariable),
            WorkspaceEnvironmentVariable = ReadString(section, "WorkspaceEnvironmentVariable", defaults.WorkspaceEnvironmentVariable),
            NamePrefix = ReadString(section, "NamePrefix", defaults.NamePrefix),
            AllowUnsafeHttp = ReadBool(section, "AllowUnsafeHttp", defaults.AllowUnsafeHttp),
            Image = ReadString(section, "Image", defaults.Image),
            MemoryMb = ReadInt(section, "MemoryMb", defaults.MemoryMb),
            Region = ReadOptionalString(section, "Region") ?? string.Empty,
            Ttl = ReadString(section, "Ttl", defaults.Ttl),
            WaitForRunningTimeout = ReadInterval(section, "WaitForRunningTimeoutSeconds", defaults.WaitForRunningTimeout),
            ExecPollInterval = ReadInterval(section, "ExecPollIntervalSeconds", defaults.ExecPollInterval),
            ApiTimeout = ReadInterval(section, "ApiTimeoutSeconds", defaults.ApiTimeout),
            SuspendWaitTimeout = ReadInterval(section, "SuspendWaitTimeoutSeconds", defaults.SuspendWaitTimeout),
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
