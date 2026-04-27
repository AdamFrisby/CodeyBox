namespace CodeyBox.Core;

/// <summary>
/// A managed project: an upstream git repo, the auditor configuration
/// applied to its work items, and per-project defaults. Resolved at work-
/// item-pickup time from <see cref="IProjectRepository"/>.
///
/// Projects are independent: per-project upstream creds, per-project
/// auditor list, per-project agent default. The orchestrator pipeline
/// reads the project at the start of each work item and uses its config
/// for every phase (work, audit, merge, upstream).
/// </summary>
public sealed record Project
{
    public required ProjectId Id { get; init; }

    /// <summary>Human-readable name for logs and the API.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Origin git URL the host bare repo is seeded from on first reference,
    /// and the natural target of the configured upstream.
    /// </summary>
    public required string RepositoryUrl { get; init; }

    /// <summary>Default branch for new work items. May be overridden per-item.</summary>
    public string? DefaultBaseBranch { get; init; }

    /// <summary>Default agent for new work items. May be overridden per-item.</summary>
    public AgentKind DefaultAgent { get; init; } = AgentKind.Claude;

    /// <summary>
    /// Where to push merged work. Each project has its own upstream — the
    /// orchestrator resolves and instantiates the matching IUpstreamRemote
    /// at pipeline time so per-project tokens never cross project boundaries.
    /// </summary>
    public ProjectUpstream Upstream { get; init; } = ProjectUpstream.Noop;

    /// <summary>Audit configuration. Empty list disables the audit phase for this project.</summary>
    public ProjectAudit Audit { get; init; } = new();
}

/// <summary>
/// Per-project upstream config. <see cref="TokenEnvVar"/> lets each project
/// have its own credential — the orchestrator never holds tokens in config,
/// only the env var name.
/// </summary>
public sealed record ProjectUpstream
{
    public string Kind { get; init; } = "noop";
    public string? GitHubOwner { get; init; }
    public string? GitHubRepository { get; init; }
    public string? GenericUrl { get; init; }
    public string? TokenEnvVar { get; init; }

    public static ProjectUpstream Noop { get; } = new();
}

/// <summary>
/// Per-project audit config. <see cref="Languages"/> and <see cref="AuditTypes"/>
/// expand into preset auditor bundles at composition time. <see cref="Custom"/>
/// is a free-form list of additional auditor descriptors.
/// </summary>
public sealed record ProjectAudit
{
    public int MaxIterations { get; init; } = 3;
    public AuditSeverity FailingSeverity { get; init; } = AuditSeverity.Error;
    public TimeSpan PerIterationTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public bool StopOnFirstFailure { get; init; }

    public IReadOnlyList<string> Languages { get; init; } = [];
    public IReadOnlyList<string> AuditTypes { get; init; } = [];
    public IReadOnlyList<CustomAuditorDescriptor> Custom { get; init; } = [];
}

/// <summary>
/// Free-form auditor description. The composer picks the matching factory
/// (shell, llm, diff-pattern) based on <see cref="Kind"/> and applies the
/// remaining fields. Lets operators express one-off auditors in config
/// without writing code.
/// </summary>
public sealed record CustomAuditorDescriptor
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public IReadOnlyList<string> Argv { get; init; } = [];
    public string? ReviewFocus { get; init; }
    public IReadOnlyList<DiffPatternDescriptor> Patterns { get; init; } = [];
}

public sealed record DiffPatternDescriptor
{
    public required string Description { get; init; }
    public required string Regex { get; init; }
}
