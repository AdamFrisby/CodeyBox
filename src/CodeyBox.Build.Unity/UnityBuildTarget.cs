using System.Text.RegularExpressions;
using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Build.Unity;

/// <summary>
/// Adapter-owned Unity build-target descriptor resolved from the neutral
/// operator approval. Unity/editor concepts live only here, never in Core:
/// the framework sees <see cref="ExternalBuildTargetKey"/> with opaque
/// toolchain/platform strings.
/// </summary>
public sealed record UnityBuildTarget
{
    /// <summary>Adapter parameter carrying the Unity organization id.</summary>
    public const string ParamOrgId = "unity.orgId";

    /// <summary>Adapter parameter carrying the Unity project id.</summary>
    public const string ParamProjectId = "unity.projectId";

    /// <summary>Adapter parameter carrying the Unity editor version (exact).</summary>
    public const string ParamEditorVersion = "unity.editorVersion";

    /// <summary>Adapter parameter carrying the Unity build platform (exact).</summary>
    public const string ParamPlatform = "unity.platform";

    /// <summary>Adapter parameter selecting a clean (<c>cold</c>) or incremental (<c>warm</c>) build.</summary>
    public const string ParamCleanBuild = "unity.cleanBuild";

    /// <summary>
    /// Source/ref override parameters MUST NOT be supplied: the checkout
    /// commit comes only from the host-published candidate ref. Any of these
    /// keys in adapter parameters fails before dispatch.
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenSourceOverrides =
        ["unity.commitSha", "unity.branch", "unity.ref", "unity.checkout"];

    public required string ApprovedTargetName { get; init; }
    public required string OrganizationId { get; init; }
    public required string ProjectId { get; init; }
    public required string BuildTargetId { get; init; }
    public required string EditorVersion { get; init; }
    public required string Platform { get; init; }
    public required string Configuration { get; init; }
    public required string CacheClass { get; init; }
    public required bool CleanBuild { get; init; }

    /// <summary>Neutral comparable-build key: toolchain/platform/cache class stay opaque to Core.</summary>
    public ExternalBuildTargetKey ToTargetKey() => new()
    {
        ProviderId = UnityBuildAutomationOptions.ProviderId,
        TargetId = BuildTargetId,
        Configuration = Configuration,
        Toolchain = "unity-editor/" + EditorVersion,
        Platform = Platform,
        CacheClass = CacheClass,
    };

    /// <summary>Canonical approved workflow identity stamped into evidence.</summary>
    public string WorkflowIdentity =>
        $"unity:{OrganizationId}/{ProjectId}/{BuildTargetId}@{EditorVersion}";

    private static readonly Regex IdPattern = new(
        "^[A-Za-z0-9_.-]{1,128}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Resolves and validates the Unity target for one operator-approved
    /// neutral approval. Every identity check is an exact match; anything
    /// unapproved fails before dispatch with a typed error.
    /// </summary>
    public static UnityBuildTarget Resolve(
        string approvedTargetName,
        ExternalBuildTargetApproval approval,
        UnityBuildAutomationOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedTargetName);
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            throw new ExternalBuildNotEnabledException();
        if (!string.Equals(approval.ProviderId, UnityBuildAutomationOptions.ProviderId, StringComparison.Ordinal))
            throw new UnityBuildTargetRejectedException(
                $"Approved target '{approvedTargetName}' is not a Unity Build Automation target.");
        foreach (var forbidden in ForbiddenSourceOverrides)
            if (approval.Parameters.ContainsKey(forbidden))
                throw new UnityBuildSourceException(
                    $"Approved target '{approvedTargetName}' carries forbidden source override '{forbidden}': " +
                    "the checkout commit comes only from the host-published candidate ref.");
        if (!approval.AllowGitPublication)
            throw new UnityBuildSourceException(
                $"Approved target '{approvedTargetName}' does not allow Git-published candidates, " +
                "which is the only source handoff Unity Build Automation supports.");
        if (string.IsNullOrWhiteSpace(approval.TargetId) || !IdPattern.IsMatch(approval.TargetId))
            throw new UnityBuildTargetRejectedException(
                $"Approved target '{approvedTargetName}' has no usable Unity build-target id.");
        if (!approval.Parameters.TryGetValue(ParamOrgId, out var org) || !IsAllowedId(org, options.AllowedOrganizationIds))
            throw new UnityBuildTargetRejectedException(
                $"Approved target '{approvedTargetName}' names an organization that is not operator-approved.");
        if (!approval.Parameters.TryGetValue(ParamProjectId, out var project) || !IsAllowedId(project, options.AllowedProjectIds))
            throw new UnityBuildTargetRejectedException(
                $"Approved target '{approvedTargetName}' names a project that is not operator-approved.");
        approval.Parameters.TryGetValue(ParamEditorVersion, out var editor);
        if (string.IsNullOrWhiteSpace(editor) || !options.SupportedEditorVersions.Contains(editor))
            throw new UnityBuildTargetRejectedException(
                $"Approved target '{approvedTargetName}' names unsupported editor '{editor ?? "<missing>"}'.");
        approval.Parameters.TryGetValue(ParamPlatform, out var platform);
        if (string.IsNullOrWhiteSpace(platform) || !options.SupportedPlatforms.Contains(platform))
            throw new UnityBuildTargetRejectedException(
                $"Approved target '{approvedTargetName}' names unsupported platform '{platform ?? "<missing>"}'.");
        if (string.IsNullOrWhiteSpace(approval.Configuration) || !options.AllowedConfigurations.Contains(approval.Configuration))
            throw new UnityBuildTargetRejectedException(
                $"Approved target '{approvedTargetName}' names unsupported configuration '{approval.Configuration}'.");
        var clean = approval.Parameters.TryGetValue(ParamCleanBuild, out var cleanRaw)
            && (string.Equals(cleanRaw, "true", StringComparison.OrdinalIgnoreCase) || cleanRaw == "1");
        var cacheClass = clean ? "cold" : "warm";
        if (!options.AllowedCacheClasses.Contains(cacheClass))
            throw new UnityBuildTargetRejectedException(
                $"Approved target '{approvedTargetName}' cache class '{cacheClass}' is not allowed.");
        return new UnityBuildTarget
        {
            ApprovedTargetName = approvedTargetName,
            OrganizationId = org,
            ProjectId = project,
            BuildTargetId = approval.TargetId,
            EditorVersion = editor,
            Platform = platform,
            Configuration = approval.Configuration,
            CacheClass = cacheClass,
            CleanBuild = clean,
        };
    }

    private static bool IsAllowedId(string? value, IReadOnlyList<string> allowlist) =>
        !string.IsNullOrWhiteSpace(value) && IdPattern.IsMatch(value)
        && allowlist.Contains(value);
}
