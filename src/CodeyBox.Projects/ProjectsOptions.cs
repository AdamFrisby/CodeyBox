using Microsoft.Extensions.Configuration;

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
    public bool? GraphicalSandbox { get; set; }
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
    public bool? SkipCredentialSmokeTest { get; set; }

    /// <summary>
    /// Optional per-project cap on the priority accepted by the API. Maps to
    /// <see cref="CodeyBox.Core.Project.MaxPriority"/>. Null = no additional
    /// project-level cap (the global [-1000, 1000] bound still applies).
    /// </summary>
    public int? MaxPriority { get; set; }

    public bool? GraphicalSandbox { get; set; }

    /// <summary>
    /// Per-project Claude resumable-session worker opt-in. Maps to
    /// <see cref="CodeyBox.Core.Project.ClaudeSession"/>. Null = the project
    /// keeps the legacy per-phase fresh-sandbox pipeline.
    /// </summary>
    public ProjectClaudeSessionConfigOptions? ClaudeSession { get; set; }
}

/// <summary>
/// Config-binding shape for <see cref="CodeyBox.Core.ProjectClaudeSessionConfig"/>.
/// Lives under <c>CodeyBox:Projects:&lt;id&gt;:ClaudeSession</c>.
/// </summary>
public sealed class ProjectClaudeSessionConfigOptions
{
    /// <summary>
    /// Per-project opt-in for the resumable Claude session worker. Composes
    /// with the global <c>CodeyBox:ClaudeSession:Enabled</c> flag. Null = false.
    /// </summary>
    public bool? Enabled { get; set; }
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
    /// <summary>
    /// argv for the pre-merge CI gate (see <see cref="ProjectUpstream.PreMergeVerifyArgv"/>).
    /// List-mutable for IConfiguration binding; the immutable copy lives on
    /// <see cref="ProjectUpstream"/>.
    /// </summary>
    public List<string>? PreMergeVerifyArgv { get; set; }

    /// <summary>
    /// Opt-in acknowledgement that a <c>Kind=noop</c> + local
    /// <c>RepositoryUrl</c> combination is intentional (see
    /// <see cref="ProjectUpstream.AcknowledgeSandboxIsolation"/>). The startup
    /// validator refuses that combination unless this flag is <c>true</c>.
    /// </summary>
    public bool? AcknowledgeSandboxIsolation { get; set; }
}

public sealed class ProjectAuditConfig
{
    public string? Profile { get; set; }
    public Dictionary<string, ProjectAuditConfig>? Profiles { get; set; }
    public int? MaxIterations { get; set; }
    public int? BudgetOverrideMaxIterations { get; set; }
    public Dictionary<string, int>? ComplexityIterationBudgets { get; set; }
    public string? FailingSeverity { get; set; }
    public int? PerIterationTimeoutMinutes { get; set; }
    public bool? StopOnFirstFailure { get; set; }
    /// <summary>
    /// Require a repo-root build.sh during audit. Null inherits; false keeps
    /// the build-script auditor skip-if-absent.
    /// </summary>
    public bool? BuildScriptRequired { get; set; }

    /// <summary>
    /// Minutes of zero activity before stuck detection fires.
    /// null = inherit global default; 0 = disabled; &gt;0 = explicit threshold.
    /// </summary>
    public int? StuckThresholdMinutes { get; set; }
    public bool? AutoRetryOnStuck { get; set; }
    public int? MaxStuckRetries { get; set; }
    public int? MergeScopeBufferLines { get; set; }

    public List<string>? Languages { get; set; }
    public Dictionary<string, ProjectLanguagePresetOverrideConfig>? LanguageOverrides { get; set; }
    public List<string>? AuditTypes { get; set; }
    public Dictionary<string, ProjectAuditTypeOverrideConfig>? AuditTypeOverrides { get; set; }
    public string? LlmPromptFrameTemplate { get; set; }
    public List<CustomAuditorConfig>? Custom { get; set; }
    public List<string>? ExcludedAuditors { get; set; }

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

public sealed class ProjectAuditTypeOverrideConfig
{
    public string? DisplayName { get; set; }
    public string? ReviewFocus { get; set; }
    public bool Replace { get; set; }
    public List<ProjectConfiguredAuditorConfig>? Auditors { get; set; }
    public List<DiffPatternConfig>? Patterns { get; set; }
}

public sealed class ProjectLanguagePresetOverrideConfig
{
    public bool Replace { get; set; }
    public List<ProjectConfiguredAuditorConfig>? Auditors { get; set; }
}

public sealed class ProjectConfiguredAuditorConfig
{
    public string? Name { get; set; }
    public List<string>? Argv { get; set; }
    public string? Script { get; set; }
    public string? ToolName { get; set; }
    public bool? TreatExit127AsMissingTool { get; set; }
    public bool CanShortCircuitOnBlockingFinding { get; set; }
}

public static class ProjectsOptionsBinder
{
    public static ProjectsOptions Bind(IConfiguration section)
    {
        var options = section.Get<ProjectsOptions>() ?? new ProjectsOptions();
        ApplyCustomMaps(options, section);
        return options;
    }

    /// <summary>
    /// Applies the map-shaped binding (audit-type / language overrides /
    /// profile inheritance) that the framework's default <c>section.Bind()</c>
    /// can't reach. Idempotent: callable from a
    /// <see cref="Microsoft.Extensions.Options.IPostConfigureOptions{T}"/>
    /// after the standard Bind has already run, so hot-reload through
    /// <c>IOptionsMonitor&lt;ProjectsOptions&gt;</c> re-applies these on
    /// every change.
    /// </summary>
    public static void ApplyCustomMaps(ProjectsOptions options, IConfiguration section)
    {
        ApplyAuditMaps(section.GetSection("Defaults:Audit"), options.Defaults.Audit);

        var projectSections = section.GetSection("Projects").GetChildren().ToList();
        for (var i = 0; i < options.Projects.Count && i < projectSections.Count; i++)
        {
            ApplyAuditMaps(projectSections[i].GetSection("Audit"), options.Projects[i].Audit);
        }
    }

    private static void ApplyAuditMaps(IConfigurationSection auditSection, ProjectAuditConfig? audit)
    {
        if (audit is null)
            return;

        ApplyLanguageMap(auditSection, audit);
        ApplyAuditTypeMap(auditSection, audit);

        var profileSections = auditSection.GetSection("Profiles").GetChildren().ToList();
        foreach (var profileSection in profileSections)
        {
            if (audit.Profiles is null || !audit.Profiles.TryGetValue(profileSection.Key, out var profile))
                continue;
            ApplyAuditMaps(profileSection, profile);
        }
    }

    private static void ApplyLanguageMap(IConfigurationSection auditSection, ProjectAuditConfig? audit)
    {
        if (audit is null)
            return;

        var overridesSection = auditSection.GetSection("Languages:Overrides");
        if (!overridesSection.Exists())
            return;

        audit.LanguageOverrides = overridesSection
            .GetChildren()
            .Select(c => new
            {
                Id = c.Key,
                Override = c.Get<ProjectLanguagePresetOverrideConfig>() ?? new ProjectLanguagePresetOverrideConfig(),
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .ToDictionary(x => x.Id, x => x.Override, StringComparer.OrdinalIgnoreCase);
    }

    private static void ApplyAuditTypeMap(IConfigurationSection auditSection, ProjectAuditConfig? audit)
    {
        if (audit is null)
            return;

        var auditTypesSection = auditSection.GetSection("AuditTypes");
        if (!auditTypesSection.Exists())
            return;

        var children = auditTypesSection.GetChildren().ToList();
        if (children.Count == 0 || children.All(c => int.TryParse(c.Key, out _)))
            return;

        audit.AuditTypes = children.Select(c => c.Key).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        audit.AuditTypeOverrides = children
            .Select(c => new
            {
                Id = c.Key,
                Override = c.Get<ProjectAuditTypeOverrideConfig>() ?? new ProjectAuditTypeOverrideConfig(),
            })
            .Where(x => x.Override.DisplayName is not null || x.Override.ReviewFocus is not null)
            .ToDictionary(x => x.Id, x => x.Override, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class CustomAuditorConfig
{
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public string? PluginId { get; set; }
    public List<string>? Argv { get; set; }
    public string? ReviewFocus { get; set; }
    public List<DiffPatternConfig>? Patterns { get; set; }
}

public sealed class DiffPatternConfig
{
    public string? Description { get; set; }
    public string? Regex { get; set; }
    public string? Severity { get; set; }
}
