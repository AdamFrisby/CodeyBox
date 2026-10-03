using Microsoft.Extensions.Configuration;

namespace CodeyBox.E2bSandboxPlugin;

/// <summary>
/// Operator-configurable knobs for the E2B sandbox provider plugin. Bound from
/// <c>CodeyBox:Plugins:codeybox.e2b-sandbox</c> via
/// <c>IPluginHost.ScopedConfig</c> and re-read on every <c>CreateAsync</c> so
/// edits take effect without a host restart.
///
/// <para>The E2B API key never lives here: only the
/// <see cref="ApiKeyEnvVar"/> name is configured, and the key is resolved from
/// the process environment (the host credential chain) at use time; tests
/// inject <see cref="ApiKey"/> directly.</para>
/// </summary>
public sealed record E2bSandboxOptions
{
    /// <summary>Provider kind this plugin contributes. Matched by exact ordinal equality.</summary>
    public const string ProviderKind = "e2b";

    /// <summary>Plugin id under <c>CodeyBox:Plugins:</c>.</summary>
    public const string PluginId = "codeybox.e2b-sandbox";

    /// <summary>Metadata key marking sandboxes this provider owns.</summary>
    public const string ManagedMetadataKey = "codeybox-managed";

    /// <summary>Metadata value marking sandboxes this provider owns.</summary>
    public const string ManagedMetadataValue = "true";

    /// <summary>Metadata key carrying the owning provider kind.</summary>
    public const string ProviderMetadataKey = "codeybox-provider";

    /// <summary>E2B control-plane base URL. Must be absolute https (http only with <see cref="AllowUnsafeHttp"/>).</summary>
    public string ApiBaseUrl { get; init; } = "https://api.e2b.dev";

    /// <summary>Name of the environment variable holding the E2B API key (<c>e2b_…</c>).</summary>
    public string ApiKeyEnvVar { get; init; } = "E2B_API_KEY";

    /// <summary>
    /// Resolved API key. Production wiring leaves this null and resolves
    /// <see cref="ApiKeyEnvVar"/> at use time; tests inject it.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Sandbox domain used to derive per-sandbox envd and preview hosts
    /// (<c>{port}-{sandboxId}.{domain}</c>). Verified against the E2B JS SDK
    /// (<c>e2b@2.52.0</c>): supported values are <c>e2b.app</c>,
    /// <c>e2b.dev</c>, <c>e2b.pro</c>.
    /// </summary>
    public string SandboxDomain { get; init; } = "e2b.app";

    /// <summary>
    /// envd daemon port on the per-sandbox host. Verified against the E2B JS
    /// SDK default (<c>ConnectionConfig.envdPort = 49983</c>).
    /// </summary>
    public int EnvdPort { get; init; } = 49983;

    /// <summary>E2B template new sandboxes are created from (default <c>base</c>).</summary>
    public string TemplateId { get; init; } = "base";

    /// <summary>Prefix for the <c>codeybox-name</c> sandbox metadata value (letter-led, lowercase, digits, hyphens).</summary>
    public string NamePrefix { get; init; } = "codeybox-";

    /// <summary>Sandbox lifetime in seconds requested at create (60…86400). The service pauses the sandbox on expiry.</summary>
    public int SandboxTimeoutSeconds { get; init; } = 3600;

    /// <summary>How long create waits for the sandbox to reach running before failing.</summary>
    public TimeSpan WaitForRunningTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Interval between status polls while waiting for running.</summary>
    public TimeSpan StatusPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Per-request HTTP timeout for E2B API calls.</summary>
    public TimeSpan ApiTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Preview URLs (E2B's <c>https://{port}-{sandboxId}.{domain}</c> host) are
    /// a declared <c>port-publishing</c> capability that placement can require,
    /// but they are never enabled by default: publishing a port exposes the
    /// running sandbox over the public internet. The operator opts in here.
    /// </summary>
    public bool EnablePreviewUrls { get; init; }

    /// <summary>Guest ports preview URLs may be minted for. Empty means no port may be published.</summary>
    public IReadOnlyList<int> AllowedPreviewPorts { get; init; } = [];

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

    /// <summary>Test-only escape hatch for local mock servers. Never enable in production.</summary>
    public bool AllowUnsafeHttp { get; init; }

    /// <summary>
    /// Explicit downgrade for non-secret tmpfs mounts (e.g. scratch dirs) to persistent
    /// guest directories. Until set, such mounts are refused so no caller silently
    /// depends on persistence tmpfs never promised.
    /// </summary>
    public bool AllowPersistentTmpfsDowngrade { get; init; }

    /// <summary>Reads options from the plugin's scoped configuration section.</summary>
    public static E2bSandboxOptions FromConfiguration(IConfigurationSection section)
    {
        var defaults = new E2bSandboxOptions();
        if (section is null)
        {
            return defaults;
        }

        return defaults with
        {
            ApiBaseUrl = ReadString(section, "ApiBaseUrl", defaults.ApiBaseUrl),
            ApiKeyEnvVar = ReadString(section, "ApiKeyEnvVar", defaults.ApiKeyEnvVar),
            SandboxDomain = ReadString(section, "SandboxDomain", defaults.SandboxDomain),
            EnvdPort = ReadInt(section, "EnvdPort", defaults.EnvdPort),
            TemplateId = ReadString(section, "TemplateId", defaults.TemplateId),
            NamePrefix = ReadString(section, "NamePrefix", defaults.NamePrefix),
            SandboxTimeoutSeconds = ReadInt(section, "SandboxTimeoutSeconds", defaults.SandboxTimeoutSeconds),
            WaitForRunningTimeout = ReadInterval(section, "WaitForRunningTimeoutSeconds", defaults.WaitForRunningTimeout),
            StatusPollInterval = ReadInterval(section, "StatusPollIntervalSeconds", defaults.StatusPollInterval),
            ApiTimeout = ReadInterval(section, "ApiTimeoutSeconds", defaults.ApiTimeout),
            EnablePreviewUrls = ReadBool(section, "EnablePreviewUrls", defaults.EnablePreviewUrls),
            AllowedPreviewPorts = ReadIntList(section, "AllowedPreviewPorts"),
            MaxExecOutputBytes = ReadInt(section, "MaxExecOutputBytes", defaults.MaxExecOutputBytes),
            MaxStageFileBytes = ReadLong(section, "MaxStageFileBytes", defaults.MaxStageFileBytes),
            MaxStageTotalBytes = ReadLong(section, "MaxStageTotalBytes", defaults.MaxStageTotalBytes),
            MaxStageFileCount = ReadInt(section, "MaxStageFileCount", defaults.MaxStageFileCount),
            MaxReadBackBytes = ReadLong(section, "MaxReadBackBytes", defaults.MaxReadBackBytes),
            MaxCommandBytes = ReadInt(section, "MaxCommandBytes", defaults.MaxCommandBytes),
            MaxEnvironmentBytes = ReadInt(section, "MaxEnvironmentBytes", defaults.MaxEnvironmentBytes),
            MaxStdinBytes = ReadInt(section, "MaxStdinBytes", defaults.MaxStdinBytes),
            MaxListPages = ReadInt(section, "MaxListPages", defaults.MaxListPages),
            AllowUnsafeHttp = ReadBool(section, "AllowUnsafeHttp", defaults.AllowUnsafeHttp),
            AllowPersistentTmpfsDowngrade = ReadBool(section, "AllowPersistentTmpfsDowngrade", defaults.AllowPersistentTmpfsDowngrade),
        };
    }

    private static string ReadString(IConfigurationSection section, string key, string fallback)
    {
        var raw = section[key];
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
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

    private static IReadOnlyList<int> ReadIntList(IConfigurationSection section, string key)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var ports = new List<int>();
        foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(piece, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var port)
                && port is >= 1 and <= 65535
                && !ports.Contains(port))
            {
                ports.Add(port);
            }
        }

        return ports;
    }
}
