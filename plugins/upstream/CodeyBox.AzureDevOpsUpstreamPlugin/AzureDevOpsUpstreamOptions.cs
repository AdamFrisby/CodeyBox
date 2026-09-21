using Microsoft.Extensions.Configuration;

namespace CodeyBox.AzureDevOpsUpstreamPlugin;

/// <summary>
/// Operational settings for the Azure DevOps upstream remote. Every value is
/// read fresh from configuration on each operation (operator ScopedConfig
/// section overlaid by per-project <c>Upstream.PluginConfig</c>), so an
/// operator can hot-reload them without restarting the orchestrator process.
/// No credential values live here — only the <em>name</em> of the env var
/// holding the PAT travels via <see cref="Core.UpstreamCompletionRequest.TokenEnvVar"/>.
/// </summary>
public sealed record AzureDevOpsUpstreamOptions
{
    /// <summary>Azure DevOps organisation name (cloud) or collection (server).</summary>
    public string Organization { get; init; } = string.Empty;

    /// <summary>Team project name (or GUID) holding the repository.</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>Repository name (or GUID).</summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>
    /// Instance base URL. Default targets Azure DevOps Services; point at an
    /// on-premises collection for Azure DevOps Server
    /// (e.g. <c>https://tfs.example.invalid:8080/tfs</c>).
    /// </summary>
    public string InstanceUrl { get; init; } = "https://dev.azure.com";

    /// <summary>
    /// REST API version sent as <c>api-version</c>. Requires 7.1 on Azure
    /// DevOps Services and Azure DevOps Server 2022+; lower to 6.0 for older
    /// on-premises servers (some newer fields are then absent).
    /// </summary>
    public string ApiVersion { get; init; } = "7.1";

    /// <summary>Per-request HTTP timeout, seconds. Clamped to 5–300.</summary>
    public int HttpTimeoutSeconds { get; init; } = 30;

    /// <summary>Page size (<c>$top</c>) for list calls. Clamped to 1–100.</summary>
    public int PageSize { get; init; } = 100;

    /// <summary>Maximum pages followed per list call. Clamped to 1–100.</summary>
    public int MaxPages { get; init; } = 25;

    /// <summary>Hard cap on PRs buffered by one list call, enforced before buffering. Clamped to 1–5000.</summary>
    public int MaxOpenPullRequests { get; init; } = 500;

    /// <summary>Bounded retries for safe (GET) calls on 429/5xx. Clamped to 0–5.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Base delay between retries, milliseconds. Clamped to 100–30000.</summary>
    public int RetryBaseDelayMilliseconds { get; init; } = 500;

    /// <summary>When true, auto-completed PRs delete their source branch.</summary>
    public bool DeleteSourceBranchOnMerge { get; init; }

    /// <summary>
    /// Binds operator ScopedConfig defaults, then overlays per-project
    /// <c>Upstream.PluginConfig</c> entries. Numeric knobs that fail to parse
    /// fall back to the default rather than failing the work item.
    /// </summary>
    public static AzureDevOpsUpstreamOptions Bind(
        IConfigurationSection scoped,
        IReadOnlyDictionary<string, string> projectConfig)
    {
        return new AzureDevOpsUpstreamOptions
        {
            Organization = Overlay(scoped, projectConfig, "Organization", string.Empty),
            Project = Overlay(scoped, projectConfig, "Project", string.Empty),
            Repository = Overlay(scoped, projectConfig, "Repository", string.Empty),
            InstanceUrl = Overlay(scoped, projectConfig, "InstanceUrl", "https://dev.azure.com"),
            ApiVersion = Overlay(scoped, projectConfig, "ApiVersion", "7.1"),
            HttpTimeoutSeconds = Clamp(OverlayInt(scoped, projectConfig, "HttpTimeoutSeconds", 30), 5, 300),
            PageSize = Clamp(OverlayInt(scoped, projectConfig, "PageSize", 100), 1, 100),
            MaxPages = Clamp(OverlayInt(scoped, projectConfig, "MaxPages", 25), 1, 100),
            MaxOpenPullRequests = Clamp(OverlayInt(scoped, projectConfig, "MaxOpenPullRequests", 500), 1, 5000),
            MaxRetries = Clamp(OverlayInt(scoped, projectConfig, "MaxRetries", 3), 0, 5),
            RetryBaseDelayMilliseconds = Clamp(OverlayInt(scoped, projectConfig, "RetryBaseDelayMilliseconds", 500), 100, 30000),
            DeleteSourceBranchOnMerge = OverlayBool(scoped, projectConfig, "DeleteSourceBranchOnMerge", false),
        };
    }

    /// <summary>Requires the three repository coordinates; throws a helpful error naming the missing key.</summary>
    /// <exception cref="InvalidOperationException">A required coordinate is missing.</exception>
    public void RequireCoordinates(Core.ProjectId projectId)
        => RequireCoordinates($"Project {projectId}");

    /// <summary>Requires coordinates with a caller-supplied scope label for paths without a project id.</summary>
    /// <exception cref="InvalidOperationException">A required coordinate is missing.</exception>
    public void RequireCoordinates(string scope)
    {
        if (string.IsNullOrWhiteSpace(Organization))
            throw new InvalidOperationException(
                $"{scope}: Azure DevOps plugin requires Upstream.PluginConfig.Organization");
        if (string.IsNullOrWhiteSpace(Project))
            throw new InvalidOperationException(
                $"{scope}: Azure DevOps plugin requires Upstream.PluginConfig.Project");
        if (string.IsNullOrWhiteSpace(Repository))
            throw new InvalidOperationException(
                $"{scope}: Azure DevOps plugin requires Upstream.PluginConfig.Repository");
        if (!Uri.TryCreate(InstanceUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                $"{scope}: Azure DevOps plugin InstanceUrl '{InstanceUrl}' must be an absolute http(s) URL");
        if (!IsValidApiVersion(ApiVersion))
            throw new InvalidOperationException(
                $"{scope}: Azure DevOps plugin ApiVersion '{ApiVersion}' is not a valid api-version");
    }

    private static bool IsValidApiVersion(string version)
        => !string.IsNullOrWhiteSpace(version)
            && version.Length <= 16
            && char.IsAsciiDigit(version[0])
            && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static string Overlay(
        IConfigurationSection scoped,
        IReadOnlyDictionary<string, string> projectConfig,
        string key,
        string current)
    {
        if (projectConfig.TryGetValue(key, out var projectValue)
            && !string.IsNullOrWhiteSpace(projectValue))
            return projectValue.Trim();
        var scopedValue = scoped[key];
        return string.IsNullOrWhiteSpace(scopedValue) ? current : scopedValue.Trim();
    }

    private static int OverlayInt(
        IConfigurationSection scoped,
        IReadOnlyDictionary<string, string> projectConfig, string key, int current)
    {
        if (projectConfig.TryGetValue(key, out var raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        var scopedRaw = scoped[key];
        if (!string.IsNullOrWhiteSpace(scopedRaw)
            && int.TryParse(scopedRaw, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var scopedParsed))
            return scopedParsed;
        return current;
    }

    private static bool OverlayBool(
        IConfigurationSection scoped,
        IReadOnlyDictionary<string, string> projectConfig, string key, bool current)
    {
        if (projectConfig.TryGetValue(key, out var raw)
            && bool.TryParse(raw, out var parsed))
            return parsed;
        var scopedRaw = scoped[key];
        if (!string.IsNullOrWhiteSpace(scopedRaw)
            && bool.TryParse(scopedRaw, out var scopedParsed))
            return scopedParsed;
        return current;
    }

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);
}
