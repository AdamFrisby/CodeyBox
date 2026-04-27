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
}

public sealed class ProjectConfig
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? BaseBranch { get; set; }
    public string? Agent { get; set; }
    public ProjectUpstreamConfig? Upstream { get; set; }
    public ProjectAuditConfig? Audit { get; set; }
}

public sealed class ProjectUpstreamConfig
{
    public string? Kind { get; set; }
    public string? GitHubOwner { get; set; }
    public string? GitHubRepository { get; set; }
    public string? GenericUrl { get; set; }
    public string? TokenEnvVar { get; set; }
}

public sealed class ProjectAuditConfig
{
    public int? MaxIterations { get; set; }
    public string? FailingSeverity { get; set; }
    public int? PerIterationTimeoutMinutes { get; set; }
    public bool? StopOnFirstFailure { get; set; }
    public List<string>? Languages { get; set; }
    public List<string>? AuditTypes { get; set; }
    public List<CustomAuditorConfig>? Custom { get; set; }
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
