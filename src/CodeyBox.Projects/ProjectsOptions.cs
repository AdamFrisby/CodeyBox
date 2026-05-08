namespace CodeyBox.Projects;

/// <summary>
/// Config-binding shape for the <c>CodeyBox</c> section of appsettings.
/// Lives separate from <see cref="CodeyBox.Core.Project"/> because the
/// config form lets callers omit fields and inherit from <see cref="Defaults"/>.
/// The repository merges Defaults+overrides into a fully-resolved
/// <see cref="CodeyBox.Core.Project"/> at load time.
/// </summary>
public sealed class ProjectsOptions
{
    public ProjectDefaultsConfig Defaults { get; set; } = new();
    public List<ProjectConfig> Projects { get; set; } = [];
}

public sealed class ProjectDefaultsConfig
{
    public string? Agent { get; set; }
    public string? BaseBranch { get; set; }
    public ProjectAuditConfig? Audit { get; set; }
    public ProjectNetworkProfilesConfig? NetworkProfiles { get; set; }
}

public sealed class ProjectConfig
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? BaseBranch { get; set; }
    public string? Agent { get; set; }
    public string? DefaultAgentClass { get; set; }
    public ProjectUpstreamConfig? Upstream { get; set; }
    public ProjectAuditConfig? Audit { get; set; }
    public ProjectNetworkProfilesConfig? NetworkProfiles { get; set; }
    public ProjectBudgetConfig? Budget { get; set; }
    public ProjectReleaseConfigOptions? Release { get; set; }
}

/// <summary>
/// Config-binding shape for per-project release management settings.
/// Maps to <see cref="CodeyBox.Core.ProjectReleaseConfig"/> after resolution.
/// All fields are nullable; missing fields take the defaults defined on
/// <see cref="CodeyBox.Core.ProjectReleaseConfig"/>.
/// </summary>
public sealed class ProjectReleaseConfigOptions
{
    public bool? Enabled { get; set; }
    public string? BranchNameTemplate { get; set; }

    /// <summary>
    /// Auto-sync interval in minutes. Null = disabled (no periodic merge of main
    /// into the release branch). 0 = also disabled. Default: 720 (12 h).
    /// </summary>
    public int? AutoSyncMainIntervalMinutes { get; set; }

    /// <summary>Named deep auditors to run during the in_review phase.</summary>
    public List<string>? DeepAuditors { get; set; }

    public int? DeepAuditMaxIterations { get; set; }
    public bool? CreateGitHubRelease { get; set; }
    public string? GitHubTagTemplate { get; set; }
}

/// <summary>
/// Config-binding shape for per-project budget caps.
/// All fields default to null (0 = unlimited when mapped to ProjectBudget).
/// </summary>
public sealed class ProjectBudgetConfig
{
    /// <summary>Max work items that can START per rolling hour. Null or 0 = unlimited.</summary>
    public int? MaxItemsPerHour { get; set; }
    /// <summary>Max work items that can START per rolling 24h. Null or 0 = unlimited.</summary>
    public int? MaxItemsPerDay { get; set; }
    /// <summary>Max in-flight (non-terminal, non-Queued) items simultaneously. Null or 0 = unlimited.</summary>
    public int? MaxConcurrentForProject { get; set; }
}

public sealed class ProjectNetworkProfilesConfig
{
    public string? Work { get; set; }
    public string? Rework { get; set; }
    public string? AuditAgent { get; set; }
    public string? AuditTool { get; set; }
    public string? Merge { get; set; }
}

public sealed class ProjectUpstreamConfig
{
    public string? Kind { get; set; }
    public string? GitHubOwner { get; set; }
    public string? GitHubRepository { get; set; }
    public string? GenericUrl { get; set; }
    public string? TokenEnvVar { get; set; }
    public string? MergeMethod { get; set; }
    public bool? AutoMerge { get; set; }
    public string? PullRequestTitleTemplate { get; set; }
}

public sealed class ProjectAuditConfig
{
    public int? MaxIterations { get; set; }
    public string? FailingSeverity { get; set; }
    public int? PerIterationTimeoutMinutes { get; set; }
    public bool? StopOnFirstFailure { get; set; }

    /// <summary>
    /// Minutes of zero activity before stuck detection fires.
    /// null = inherit global default; 0 = disabled; &gt;0 = explicit threshold.
    /// </summary>
    public int? StuckThresholdMinutes { get; set; }
    public bool? AutoRetryOnStuck { get; set; }
    public int? MaxStuckRetries { get; set; }
    public int? MergeScopeBufferLines { get; set; }

    public List<string>? Languages { get; set; }
    public List<string>? AuditTypes { get; set; }
    public List<CustomAuditorConfig>? Custom { get; set; }

    /// <summary>
    /// Agent used for LLM-based auditors. Null = use the project's primary agent.
    /// </summary>
    public string? AuditAgent { get; set; }

    /// <summary>
    /// Per-auditor agent overrides. Keys are auditor names; values are agent kind strings.
    /// </summary>
    public Dictionary<string, string>? PerAuditorAgent { get; set; }

    /// <summary>
    /// Maximum number of LLM auditors to run concurrently within an audit iteration.
    /// Null = inherit global default. Set to 1 to serialise (useful for 429-prone accounts).
    /// </summary>
    public int? MaxLlmAuditorParallelism { get; set; }
}

public sealed class CustomAuditorConfig
{
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public List<string>? Argv { get; set; }
    public string? ReviewFocus { get; set; }
    public List<DiffPatternConfig>? Patterns { get; set; }
}

public sealed class DiffPatternConfig
{
    public string? Description { get; set; }
    public string? Regex { get; set; }
}
