namespace CodeyBox.Build.Unity;

/// <summary>
/// Hot-reloadable operator knobs for the Unity Build Automation adapter.
/// Disabled by default: nothing contacts Unity until the operator enables
/// both this adapter and the shared external-build framework and approves at
/// least one target on each side. All collections are exact-match allowlists;
/// substring or prefix matching is never used for identity decisions.
/// </summary>
public sealed class UnityBuildAutomationOptions
{
    public const string SectionName = "CodeyBox:UnityBuildAutomation";

    /// <summary>Stable provider id this adapter registers under.</summary>
    public const string ProviderId = "unity-build-automation";

    /// <summary>
    /// Fixed Unity Build Automation API base. Not configurable: arbitrary
    /// repository-controlled URLs are never selected, so credential-bearing
    /// requests cannot be redirected. Follows
    /// https://build-api.cloud.unity3d.com/docs/ .
    /// </summary>
    public const string ApiBaseUrl = "https://build-api.cloud.unity3d.com";

    /// <summary>Master switch for this adapter. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Exact Unity organization ids the operator approved. Empty approves nothing.</summary>
    public List<string> AllowedOrganizationIds { get; set; } = [];

    /// <summary>Exact Unity project ids the operator approved. Empty approves nothing.</summary>
    public List<string> AllowedProjectIds { get; set; } = [];

    /// <summary>Exact Unity editor versions supported (e.g. <c>2022.3.62f1</c>). Empty supports nothing.</summary>
    public List<string> SupportedEditorVersions { get; set; } = [];

    /// <summary>Exact Unity build-platform ids supported.</summary>
    public List<string> SupportedPlatforms { get; set; } =
        ["Android", "iOS", "WebGL", "StandaloneWindows64", "StandaloneOSX", "StandaloneLinux64"];

    /// <summary>Exact configuration profiles supported.</summary>
    public List<string> AllowedConfigurations { get; set; } = ["Release", "Debug"];

    /// <summary>Exact cache classes supported (<c>warm</c>/<c>cold</c>).</summary>
    public List<string> AllowedCacheClasses { get; set; } = ["warm", "cold"];

    /// <summary>
    /// When true (default), dispatch requires the host-published candidate ref
    /// to carry an exact commit SHA; branch names or "latest on branch" are
    /// rejected before dispatch. Latest-build-on-branch is never proof.
    /// </summary>
    public bool RequireExactCommit { get; set; } = true;

    /// <summary>Exact hosts accepted for artifact downloads (https only). No credentials in URLs.</summary>
    public List<string> AllowedArtifactHosts { get; set; } = ["build-api.cloud.unity3d.com"];

    /// <summary>Per-call HTTP timeout in seconds. Default 30.</summary>
    public int HttpTimeoutSeconds { get; set; } = 30;

    /// <summary>Max diagnostics chars retained per build (before the shared framework cap is applied).</summary>
    public int MaxDiagnosticsChars { get; set; } = 8192;

    /// <summary>Max request-identity dedup entries kept (bounded memory). Default 4096.</summary>
    public int MaxDedupEntries { get; set; } = 4096;

    public static bool IsValid(UnityBuildAutomationOptions? options) =>
        options is not null
        && options.HttpTimeoutSeconds is >= 1 and <= 300
        && options.MaxDiagnosticsChars is >= 256 and <= 1024 * 1024
        && options.MaxDedupEntries is >= 16 and <= 100_000
        && options.AllowedOrganizationIds.All(static s => !string.IsNullOrWhiteSpace(s))
        && options.AllowedProjectIds.All(static s => !string.IsNullOrWhiteSpace(s))
        && options.SupportedEditorVersions.All(static s => !string.IsNullOrWhiteSpace(s))
        && options.SupportedPlatforms.All(static s => !string.IsNullOrWhiteSpace(s))
        && options.AllowedConfigurations.All(static s => !string.IsNullOrWhiteSpace(s))
        && options.AllowedCacheClasses.All(static s => !string.IsNullOrWhiteSpace(s))
        && options.AllowedArtifactHosts.All(static s => !string.IsNullOrWhiteSpace(s));
}
