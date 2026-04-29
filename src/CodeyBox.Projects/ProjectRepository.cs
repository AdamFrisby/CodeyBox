using Microsoft.Extensions.Options;
using CodeyBox.Core;

namespace CodeyBox.Projects;

/// <summary>
/// Config-backed <see cref="IProjectRepository"/>. Reads
/// <see cref="ProjectsOptions"/> at construction, merges each project with
/// the configured defaults, and caches the resolved list.
///
/// Intentionally immutable after startup: changes to appsettings.json
/// require a restart. A future SQLite-backed CRUD impl can swap behind the
/// same interface; the orchestrator never needs to know the difference.
/// </summary>
public sealed class ProjectRepository : IProjectRepository
{
    private readonly Dictionary<string, Project> _byId;
    private readonly IReadOnlyList<Project> _list;

    public ProjectRepository(IOptions<ProjectsOptions> options)
    {
        var opts = options.Value;
        var defaults = opts.Defaults ?? new ProjectDefaultsConfig();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<Project>(opts.Projects.Count);
        foreach (var pc in opts.Projects)
        {
            var project = Resolve(pc, defaults);
            if (!seen.Add(project.Id.Value))
                throw new InvalidOperationException($"Duplicate project id: {project.Id}");
            resolved.Add(project);
        }
        _list = resolved;
        _byId = resolved.ToDictionary(p => p.Id.Value, StringComparer.Ordinal);
    }

    public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default)
        => Task.FromResult(_byId.TryGetValue(id.Value, out var p) ? p : null);

    public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
        => Task.FromResult(_list);

    private static Project Resolve(ProjectConfig pc, ProjectDefaultsConfig defaults)
    {
        if (string.IsNullOrWhiteSpace(pc.Id))
            throw new InvalidOperationException("Project entry missing 'Id'");
        if (string.IsNullOrWhiteSpace(pc.RepositoryUrl))
            throw new InvalidOperationException($"Project '{pc.Id}' missing 'RepositoryUrl'");
        Validation.ValidateRepositoryUrl(pc.RepositoryUrl, $"projects[{pc.Id}].RepositoryUrl");

        return new Project
        {
            Id = new ProjectId(pc.Id),
            DisplayName = pc.DisplayName ?? pc.Id,
            RepositoryUrl = pc.RepositoryUrl,
            DefaultBaseBranch = pc.BaseBranch ?? defaults.BaseBranch,
            DefaultAgent = ParseAgent(pc.Agent ?? defaults.Agent),
            DefaultAgentClass = pc.DefaultAgentClass,
            Upstream = ResolveUpstream(pc.Upstream),
            Audit = ResolveAudit(pc.Audit, defaults.Audit),
            NetworkProfiles = ResolveNetworkProfiles(pc.NetworkProfiles, defaults.NetworkProfiles),
        };
    }

    private static ProjectNetworkProfiles ResolveNetworkProfiles(ProjectNetworkProfilesConfig? project, ProjectNetworkProfilesConfig? defaults) => new()
    {
        // Shallow merge per-field: project wins, defaults fill gaps.
        Work = project?.Work ?? defaults?.Work,
        Rework = project?.Rework ?? defaults?.Rework ?? project?.Work ?? defaults?.Work,
        AuditAgent = project?.AuditAgent ?? defaults?.AuditAgent,
        AuditTool = project?.AuditTool ?? defaults?.AuditTool,
        Merge = project?.Merge ?? defaults?.Merge,
    };

    private static AgentKind ParseAgent(string? value)
        => string.IsNullOrWhiteSpace(value) ? AgentKind.Claude : new AgentKind(value);

    private static ProjectUpstream ResolveUpstream(ProjectUpstreamConfig? c)
    {
        if (c is null) return ProjectUpstream.Noop;

        var kind = c.Kind ?? "noop";
        var mergeMethod = c.MergeMethod ?? "merge";

        // Validate at startup so misconfigured MergeMethod surfaces immediately.
        if (kind.Equals("github", StringComparison.OrdinalIgnoreCase) &&
            mergeMethod is not ("merge" or "squash" or "rebase"))
            throw new InvalidOperationException(
                $"Upstream.MergeMethod '{mergeMethod}' is invalid; valid values: merge, squash, rebase");

        return new ProjectUpstream
        {
            Kind = kind,
            GitHubOwner = c.GitHubOwner,
            GitHubRepository = c.GitHubRepository,
            GenericUrl = c.GenericUrl,
            TokenEnvVar = c.TokenEnvVar,
            MergeMethod = mergeMethod,
            AutoMerge = c.AutoMerge ?? false,
            PullRequestTitleTemplate = c.PullRequestTitleTemplate,
        };
    }

    private static ProjectAudit ResolveAudit(ProjectAuditConfig? project, ProjectAuditConfig? defaults)
    {
        // Shallow merge: project values win, defaults fill gaps. Lists are
        // taken whole from whichever side defines them — we don't try to
        // append defaults to project lists, which would be surprising.
        var mergedMaxIter = project?.MaxIterations ?? defaults?.MaxIterations ?? 3;
        var mergedSeverity = ParseSeverity(project?.FailingSeverity ?? defaults?.FailingSeverity);
        var mergedTimeoutMin = project?.PerIterationTimeoutMinutes ?? defaults?.PerIterationTimeoutMinutes ?? 10;
        var mergedStopOnFirst = project?.StopOnFirstFailure ?? defaults?.StopOnFirstFailure ?? false;
        var mergedLanguages = project?.Languages ?? defaults?.Languages ?? [];
        var mergedAuditTypes = project?.AuditTypes ?? defaults?.AuditTypes ?? [];
        var mergedCustom = (project?.Custom ?? defaults?.Custom ?? []).Select(ResolveCustom).ToList();

        return new ProjectAudit
        {
            MaxIterations = mergedMaxIter,
            FailingSeverity = mergedSeverity,
            PerIterationTimeout = TimeSpan.FromMinutes(mergedTimeoutMin),
            StopOnFirstFailure = mergedStopOnFirst,
            Languages = mergedLanguages,
            AuditTypes = mergedAuditTypes,
            Custom = mergedCustom,
        };
    }

    private static AuditSeverity ParseSeverity(string? s) => s?.ToLowerInvariant() switch
    {
        "info" => AuditSeverity.Info,
        "warning" or "warn" => AuditSeverity.Warning,
        _ => AuditSeverity.Error,
    };

    private static CustomAuditorDescriptor ResolveCustom(CustomAuditorConfig c)
    {
        if (string.IsNullOrWhiteSpace(c.Name)) throw new InvalidOperationException("Custom auditor missing 'Name'");
        if (string.IsNullOrWhiteSpace(c.Kind)) throw new InvalidOperationException($"Custom auditor '{c.Name}' missing 'Kind'");
        return new CustomAuditorDescriptor
        {
            Name = c.Name,
            Kind = c.Kind,
            Argv = c.Argv ?? [],
            ReviewFocus = c.ReviewFocus,
            Patterns = (c.Patterns ?? []).Select(p => new DiffPatternDescriptor
            {
                Description = p.Description ?? "(no description)",
                Regex = p.Regex ?? throw new InvalidOperationException($"Pattern in '{c.Name}' missing 'Regex'"),
            }).ToList(),
        };
    }
}
