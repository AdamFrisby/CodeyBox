using CodeyBox.Audit.Presets;
using Microsoft.Extensions.Options;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Projects;

/// <summary>
/// Config-backed <see cref="IProjectRepository"/>. Reads
/// <see cref="ProjectsOptions"/>, merges each project with the configured
/// defaults, and caches the resolved list.
///
/// When constructed with <see cref="IOptionsMonitor{ProjectsOptions}"/>
/// the resolved view is rebuilt whenever the configuration changes
/// (e.g. <c>appsettings.json</c> edit picked up by the framework's
/// file watcher). Reads return the latest atomically-swapped snapshot.
/// Rebuilds that throw (for example, duplicate ids) are logged and the
/// prior snapshot is retained. If a registered
/// <see cref="IValidateOptions{TOptions}"/> rejects a reload candidate, the
/// framework does not invoke this repository's change callback at all; the
/// existing snapshot remains in place because it is held independently here.
///
/// A future SQLite-backed CRUD impl can swap behind the same interface;
/// the orchestrator never needs to know the difference.
/// </summary>
public sealed class ProjectRepository : IProjectRepository, IDisposable
{
    private readonly ILogger<ProjectRepository> _logger;
    private readonly PresetCatalogOptions? _presetCatalogOptions;
    private readonly IDisposable? _changeSubscription;
    private Snapshot _snapshot;

    public ProjectRepository(IOptions<ProjectsOptions> options)
        : this(options, NullLogger<ProjectRepository>.Instance) { }

    public ProjectRepository(IOptions<ProjectsOptions> options, ILogger<ProjectRepository> logger)
        : this(options, logger, null) { }

    public ProjectRepository(
        IOptions<ProjectsOptions> options,
        ILogger<ProjectRepository> logger,
        PresetCatalogOptions? presetCatalogOptions)
    {
        _logger = logger;
        _presetCatalogOptions = presetCatalogOptions;
        _snapshot = Build(options.Value, presetCatalogOptions);
    }

    public ProjectRepository(
        IOptionsMonitor<ProjectsOptions> monitor,
        ILogger<ProjectRepository> logger,
        PresetCatalogOptions? presetCatalogOptions = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _logger = logger;
        _presetCatalogOptions = presetCatalogOptions;
        _snapshot = Build(monitor.CurrentValue, presetCatalogOptions);
        _changeSubscription = monitor.OnChange(Reload);
    }

    private void Reload(ProjectsOptions opts)
    {
        try
        {
            var next = Build(opts, _presetCatalogOptions);
            Volatile.Write(ref _snapshot, next);
            _logger.LogInformation(
                "ProjectRepository reloaded: {Count} project(s) [{Ids}]",
                next.List.Count, string.Join(",", next.List.Select(p => p.Id.Value)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ProjectRepository reload rejected; keeping prior snapshot. " +
                "Fix the configuration error and re-save to retry.");
        }
    }

    public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default)
        => Task.FromResult(Volatile.Read(ref _snapshot).ById.TryGetValue(id.Value, out var p) ? p : null);

    public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
        => Task.FromResult(Volatile.Read(ref _snapshot).List);

    public void Dispose() => _changeSubscription?.Dispose();

    private Snapshot Build(ProjectsOptions opts, PresetCatalogOptions? presetCatalogOptions)
    {
        var defaults = opts.Defaults ?? new ProjectDefaultsConfig();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<Project>(opts.Projects.Count);
        foreach (var pc in opts.Projects)
        {
            var project = Resolve(pc, defaults);
            if (!seen.Add(project.Id.Value))
                throw new InvalidOperationException($"Duplicate project id: {project.Id}");
            ValidateAuditPresetConfiguration(project, presetCatalogOptions);
            resolved.Add(project);
        }
        return new Snapshot(
            resolved,
            resolved.ToDictionary(p => p.Id.Value, StringComparer.Ordinal));
    }

    private sealed record Snapshot(IReadOnlyList<Project> List, IReadOnlyDictionary<string, Project> ById);

    private static void ValidateAuditPresetConfiguration(Project project, PresetCatalogOptions? presetCatalogOptions)
    {
        if (!project.Audit.Profile.Equals(ProjectAudit.DefaultProfileName, StringComparison.OrdinalIgnoreCase) &&
            !project.Audit.Profiles.ContainsKey(project.Audit.Profile))
        {
            throw new InvalidOperationException(
                $"Project '{project.Id.Value}' audit profile '{project.Audit.Profile}' is not defined");
        }

        var options = presetCatalogOptions?.Clone() ?? new PresetCatalogOptions();
        ApplyRepositoryPresetRoot(project, options);

        try
        {
            ValidateAuditBundle(project, project.Audit, options);
            foreach (var profile in project.Audit.Profiles.Values)
                ValidateAuditBundle(project, profile, options);
        }
        catch (PresetConfigurationException ex)
        {
            throw new InvalidOperationException(
                $"Project '{project.Id.Value}' audit preset configuration is invalid: {ex.Message}", ex);
        }
    }

    private static void ValidateAuditBundle(Project project, ProjectAudit audit, PresetCatalogOptions baseOptions)
    {
        var options = baseOptions.Clone();
        ApplyPresetOverrideOptions(project with { Audit = audit }, options);
        var catalog = new PresetCatalog(options);
        ValidateSelectedPresets(project with { Audit = audit }, catalog);
    }

    internal static void ApplyPresetOverrideOptions(Project project, PresetCatalogOptions options)
    {
        foreach (var (id, ov) in project.Audit.LanguageOverrides)
        {
            options.LanguageOverrides[id] = new LanguagePresetOverride
            {
                Replace = ov.Replace,
                Auditors = ov.Auditors.Select(a => new ConfiguredAuditor
                {
                    Name = a.Name,
                    Argv = [.. a.Argv],
                    Script = a.Script,
                    ToolName = a.ToolName,
                    TreatExit127AsMissingTool = a.TreatExit127AsMissingTool,
                }).ToList(),
            };
        }

        foreach (var (id, ov) in project.Audit.AuditTypeOverrides)
        {
            options.AuditTypeOverrides[id] = new AuditTypePresetOverride
            {
                DisplayName = ov.DisplayName,
                ReviewFocus = ov.ReviewFocus,
                Replace = ov.Replace,
                Auditors = ov.Auditors.Select(a => new ConfiguredAuditor
                {
                    Name = a.Name,
                    Argv = [.. a.Argv],
                    Script = a.Script,
                    ToolName = a.ToolName,
                    TreatExit127AsMissingTool = a.TreatExit127AsMissingTool,
                }).ToList(),
                Patterns = ov.Patterns.Select(p => new ConfiguredDiffPattern
                {
                    Regex = p.Regex,
                    Description = p.Description,
                    Severity = p.Severity,
                }).ToList(),
            };
        }

        if (project.Audit.LlmPromptFrameTemplate is not null)
            options.LlmPromptFrameTemplate = project.Audit.LlmPromptFrameTemplate;
    }

    internal static bool ApplyRepositoryPresetRoot(Project project, PresetCatalogOptions options)
    {
        var root = ResolveRepositoryPresetRoot(project.RepositoryUrl);
        if (root is null)
            return false;

        if (!options.AdditionalProjectRoots.Contains(root, StringComparer.Ordinal))
            options.AdditionalProjectRoots.Add(root);
        return true;
    }

    internal static string? ResolveRepositoryPresetRoot(string repositoryUrl)
    {
        string? path = null;
        if (Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile)
                return null;
            path = uri.LocalPath;
        }
        else if (Path.IsPathRooted(repositoryUrl) || repositoryUrl.StartsWith(".", StringComparison.Ordinal))
        {
            path = repositoryUrl;
        }

        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = Path.GetFullPath(path);
        if (Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase))
            path = Directory.GetParent(path)?.FullName ?? path;

        return Directory.Exists(path) ? path : null;
    }

    private static void ValidateSelectedPresets(Project project, IPresetCatalog catalog)
    {
        var owner = $"Project '{project.Id.Value}'";
        PresetCatalogSelectionValidator.ValidateLanguageIds(owner, project.Audit.Languages, catalog.KnownLanguages);
        PresetCatalogSelectionValidator.ValidateAuditTypeIds(owner, project.Audit.AuditTypes, catalog.KnownAuditTypes);
    }

    private Project Resolve(ProjectConfig pc, ProjectDefaultsConfig defaults)
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
            Audit = ResolveAudit(pc.Id, pc.Audit, defaults.Audit),
            NetworkProfiles = ResolveNetworkProfiles(pc.NetworkProfiles, defaults.NetworkProfiles),
            Budget = ResolveBudget(pc.Budget),
            ReleaseConfig = ResolveReleaseConfig(pc.Release),
            SkipCredentialSmokeTest = pc.SkipCredentialSmokeTest ?? false,
            MaxPriority = pc.MaxPriority,
            GraphicalSandbox = pc.GraphicalSandbox ?? defaults.GraphicalSandbox ?? false,
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

    private ProjectAudit ResolveAudit(string? projectId, ProjectAuditConfig? project, ProjectAuditConfig? defaults)
    {
        var baseAudit = ResolveAuditBundle(project, defaults, project?.Profile ?? defaults?.Profile ?? ProjectAudit.DefaultProfileName);
        var profiles = ResolveAuditProfiles(project, defaults, baseAudit);
        return baseAudit with { Profiles = profiles };
    }

    private ProjectAudit ResolveAuditBundle(ProjectAuditConfig? project, ProjectAuditConfig? defaults, string selectedProfile)
    {
        // Shallow merge: project values win, defaults fill gaps. Lists are
        // taken whole from whichever side defines them — we don't try to
        // append defaults to project lists, which would be surprising.
        var mergedMaxIter = project?.MaxIterations ?? defaults?.MaxIterations ?? 3;
        var mergedSeverity = AuditSeverityParser.Parse(project?.FailingSeverity ?? defaults?.FailingSeverity);
        var mergedTimeoutMin = project?.PerIterationTimeoutMinutes ?? defaults?.PerIterationTimeoutMinutes ?? 120;
        var mergedStopOnFirst = project?.StopOnFirstFailure ?? defaults?.StopOnFirstFailure ?? false;
        var languagesConfigured = project?.Languages is not null || defaults?.Languages is not null;
        var configuredLanguages = project?.Languages ?? defaults?.Languages ?? ProjectAuditLanguages.Default;
        var mergedLanguages = FilterConfiguredLanguages(configuredLanguages);
        var mergedLanguageOverrides = MergeLanguageOverrides(defaults?.LanguageOverrides, project?.LanguageOverrides);
        var mergedAuditTypes = project?.AuditTypes ?? defaults?.AuditTypes ?? [];
        var mergedAuditTypeOverrides = MergeAuditTypeOverrides(defaults?.AuditTypeOverrides, project?.AuditTypeOverrides);
        var mergedFrameTemplate = project?.LlmPromptFrameTemplate ?? defaults?.LlmPromptFrameTemplate;
        var mergedCustom = (project?.Custom ?? defaults?.Custom ?? []).Select(ResolveCustom).ToList();
        var mergedExcludedAuditors = project?.ExcludedAuditors ?? defaults?.ExcludedAuditors ?? [];

        // Stuck-probe config. null in config = -1 (inherit from PipelineOptions global).
        // 0 = explicitly disabled for this project. >0 = explicit threshold.
        var rawStuck = project?.StuckThresholdMinutes ?? defaults?.StuckThresholdMinutes;
        var mergedStuck = rawStuck.HasValue ? rawStuck.Value : -1;
        var mergedAutoRetry = project?.AutoRetryOnStuck ?? defaults?.AutoRetryOnStuck ?? false;
        var mergedMaxRetries = project?.MaxStuckRetries ?? defaults?.MaxStuckRetries ?? 2;
        var mergedMergeScopeBufferLines = Math.Max(0, project?.MergeScopeBufferLines ?? defaults?.MergeScopeBufferLines ?? 5);

        var rawAuditAgent = project?.AuditAgent ?? defaults?.AuditAgent;
        var mergedAuditAgent = string.IsNullOrWhiteSpace(rawAuditAgent)
            ? (AgentKind?)null
            : new AgentKind(rawAuditAgent);

        var rawPerAuditor = project?.PerAuditorAgent ?? defaults?.PerAuditorAgent;
        var mergedPerAuditorAgent = rawPerAuditor is null
            ? (IReadOnlyDictionary<string, AgentKind>)new Dictionary<string, AgentKind>()
            : rawPerAuditor.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                           .ToDictionary(kvp => kvp.Key, kvp => new AgentKind(kvp.Value));

        // Clamp to >= 1: SemaphoreSlim requires maxCount >= 1.
        var mergedMaxLlmPar = Math.Max(1, project?.MaxLlmAuditorParallelism ?? defaults?.MaxLlmAuditorParallelism ?? 3);

        return new ProjectAudit
        {
            Profile = selectedProfile,
            MaxIterations = mergedMaxIter,
            FailingSeverity = mergedSeverity,
            PerIterationTimeout = TimeSpan.FromMinutes(mergedTimeoutMin),
            StopOnFirstFailure = mergedStopOnFirst,
            StuckThresholdMinutes = mergedStuck,
            AutoRetryOnStuck = mergedAutoRetry,
            MaxStuckRetries = mergedMaxRetries,
            MergeScopeBufferLines = mergedMergeScopeBufferLines,
            Languages = mergedLanguages,
            LanguagesConfigured = languagesConfigured,
            LanguageOverrides = mergedLanguageOverrides,
            AuditTypes = mergedAuditTypes,
            AuditTypeOverrides = mergedAuditTypeOverrides,
            LlmPromptFrameTemplate = mergedFrameTemplate,
            Custom = mergedCustom,
            ExcludedAuditors = mergedExcludedAuditors,
            AuditAgent = mergedAuditAgent,
            PerAuditorAgent = mergedPerAuditorAgent,
            MaxLlmAuditorParallelism = mergedMaxLlmPar,
        };
    }

    private IReadOnlyDictionary<string, ProjectAudit> ResolveAuditProfiles(
        ProjectAuditConfig? project,
        ProjectAuditConfig? defaults,
        ProjectAudit baseAudit)
    {
        var profiles = new Dictionary<string, ProjectAudit>(
            AuditProfilePresets.CreateBuiltIns(),
            StringComparer.OrdinalIgnoreCase);

        if (defaults?.Profiles is not null)
        {
            foreach (var (name, config) in defaults.Profiles)
            {
                var fallback = profiles.TryGetValue(name, out var existing) ? existing : baseAudit;
                profiles[name] = ResolveAuditProfileBundle(config, fallback, name);
            }
        }

        if (project?.Profiles is not null)
        {
            foreach (var (name, config) in project.Profiles)
            {
                var fallback = profiles.TryGetValue(name, out var existing) ? existing : baseAudit;
                profiles[name] = ResolveAuditProfileBundle(config, fallback, name);
            }
        }

        return profiles;
    }

    private ProjectAudit ResolveAuditProfileBundle(ProjectAuditConfig config, ProjectAudit fallback, string profileName)
    {
        var resolved = ResolveAuditBundle(config, ProjectAuditToConfig(fallback), profileName);
        return resolved with { Profile = profileName };
    }

    private static ProjectAuditConfig ProjectAuditToConfig(ProjectAudit audit)
        => new()
        {
            Profile = audit.Profile,
            MaxIterations = audit.MaxIterations,
            FailingSeverity = audit.FailingSeverity.ToString(),
            PerIterationTimeoutMinutes = (int)audit.PerIterationTimeout.TotalMinutes,
            StopOnFirstFailure = audit.StopOnFirstFailure,
            StuckThresholdMinutes = audit.StuckThresholdMinutes,
            AutoRetryOnStuck = audit.AutoRetryOnStuck,
            MaxStuckRetries = audit.MaxStuckRetries,
            MergeScopeBufferLines = audit.MergeScopeBufferLines,
            Languages = [.. audit.Languages],
            LanguageOverrides = audit.LanguageOverrides.ToDictionary(
                kvp => kvp.Key,
                kvp => new ProjectLanguagePresetOverrideConfig
                {
                    Replace = kvp.Value.Replace,
                    Auditors = kvp.Value.Auditors.Select(ProjectConfiguredAuditorToConfig).ToList(),
                },
                StringComparer.OrdinalIgnoreCase),
            AuditTypes = [.. audit.AuditTypes],
            AuditTypeOverrides = audit.AuditTypeOverrides.ToDictionary(
                kvp => kvp.Key,
                kvp => new ProjectAuditTypeOverrideConfig
                {
                    DisplayName = kvp.Value.DisplayName,
                    ReviewFocus = kvp.Value.ReviewFocus,
                    Replace = kvp.Value.Replace,
                    Auditors = kvp.Value.Auditors.Select(ProjectConfiguredAuditorToConfig).ToList(),
                    Patterns = kvp.Value.Patterns.Select(p => new DiffPatternConfig
                    {
                        Description = p.Description,
                        Regex = p.Regex,
                        Severity = p.Severity,
                    }).ToList(),
                },
                StringComparer.OrdinalIgnoreCase),
            LlmPromptFrameTemplate = audit.LlmPromptFrameTemplate,
            Custom = audit.Custom.Select(CustomAuditorToConfig).ToList(),
            ExcludedAuditors = [.. audit.ExcludedAuditors],
            AuditAgent = audit.AuditAgent?.Value,
            PerAuditorAgent = audit.PerAuditorAgent.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value),
            MaxLlmAuditorParallelism = audit.MaxLlmAuditorParallelism,
        };

    private static ProjectConfiguredAuditorConfig ProjectConfiguredAuditorToConfig(ProjectConfiguredAuditor auditor)
        => new()
        {
            Name = auditor.Name,
            Argv = [.. auditor.Argv],
            Script = auditor.Script,
            ToolName = auditor.ToolName,
            TreatExit127AsMissingTool = auditor.TreatExit127AsMissingTool,
        };

    private static CustomAuditorConfig CustomAuditorToConfig(CustomAuditorDescriptor auditor)
        => new()
        {
            Name = auditor.Name,
            Kind = auditor.Kind,
            PluginId = auditor.PluginId,
            Argv = [.. auditor.Argv],
            ReviewFocus = auditor.ReviewFocus,
            Patterns = auditor.Patterns.Select(p => new DiffPatternConfig
            {
                Description = p.Description,
                Regex = p.Regex,
                Severity = p.Severity,
            }).ToList(),
        };

    private static IReadOnlyDictionary<string, ProjectLanguagePresetOverride> MergeLanguageOverrides(
        Dictionary<string, ProjectLanguagePresetOverrideConfig>? defaults,
        Dictionary<string, ProjectLanguagePresetOverrideConfig>? project)
    {
        var merged = new Dictionary<string, ProjectLanguagePresetOverride>(StringComparer.OrdinalIgnoreCase);
        if (defaults is not null)
        {
            foreach (var (id, ov) in defaults)
                merged[id] = ResolveLanguageOverride(ov);
        }
        if (project is not null)
        {
            foreach (var (id, ov) in project)
                merged[id] = ResolveLanguageOverride(ov);
        }
        return merged;
    }

    private static ProjectLanguagePresetOverride ResolveLanguageOverride(ProjectLanguagePresetOverrideConfig config)
        => new()
        {
            Replace = config.Replace,
            Auditors = (config.Auditors ?? []).Select(ResolveConfiguredAuditor).ToList(),
        };

    private static ProjectConfiguredAuditor ResolveConfiguredAuditor(ProjectConfiguredAuditorConfig config)
        => new()
        {
            Name = config.Name ?? string.Empty,
            Argv = config.Argv ?? [],
            Script = config.Script,
            ToolName = config.ToolName,
            TreatExit127AsMissingTool = config.TreatExit127AsMissingTool,
        };

    private static IReadOnlyDictionary<string, ProjectAuditTypeOverride> MergeAuditTypeOverrides(
        Dictionary<string, ProjectAuditTypeOverrideConfig>? defaults,
        Dictionary<string, ProjectAuditTypeOverrideConfig>? project)
    {
        var merged = new Dictionary<string, ProjectAuditTypeOverride>(StringComparer.OrdinalIgnoreCase);
        if (defaults is not null)
        {
            foreach (var (id, ov) in defaults)
                merged[id] = ResolveAuditTypeOverride(ov);
        }
        if (project is not null)
        {
            foreach (var (id, ov) in project)
                merged[id] = ResolveAuditTypeOverride(ov);
        }
        return merged;
    }

    private static ProjectAuditTypeOverride ResolveAuditTypeOverride(ProjectAuditTypeOverrideConfig config)
        => new()
        {
            DisplayName = config.DisplayName,
            ReviewFocus = config.ReviewFocus,
            Replace = config.Replace,
            Auditors = (config.Auditors ?? []).Select(ResolveConfiguredAuditor).ToList(),
            Patterns = (config.Patterns ?? []).Select(p => new DiffPatternDescriptor
            {
                Description = p.Description ?? "(no description)",
                Regex = p.Regex ?? string.Empty,
                Severity = p.Severity,
            }).ToList(),
        };

    private static IReadOnlyList<string> FilterConfiguredLanguages(IEnumerable<string> languages)
    {
        var filtered = new List<string>();
        foreach (var language in languages)
        {
            if (string.IsNullOrWhiteSpace(language))
                continue;
            filtered.Add(language);
        }
        return filtered;
    }

    private static ProjectBudget ResolveBudget(ProjectBudgetConfig? c)
    {
        if (c is null) return new();
        static int ValidateCap(int? value, string name)
        {
            var v = value ?? 0;
            if (v < 0) throw new InvalidOperationException(
                $"Budget cap '{name}' must be >= 0 (got {v}). Use 0 for unlimited.");
            return v;
        }
        return new()
        {
            MaxItemsPerHour = ValidateCap(c.MaxItemsPerHour, nameof(c.MaxItemsPerHour)),
            MaxItemsPerDay = ValidateCap(c.MaxItemsPerDay, nameof(c.MaxItemsPerDay)),
            MaxConcurrentForProject = ValidateCap(c.MaxConcurrentForProject, nameof(c.MaxConcurrentForProject)),
        };
    }

    private static ProjectReleaseConfig ResolveReleaseConfig(ProjectReleaseConfigOptions? c)
    {
        if (c is null) return new();
        var defaults = new ProjectReleaseConfig();
        TimeSpan? syncInterval = c.AutoSyncMainIntervalMinutes.HasValue
            ? (c.AutoSyncMainIntervalMinutes.Value <= 0 ? null : TimeSpan.FromMinutes(c.AutoSyncMainIntervalMinutes.Value))
            : defaults.AutoSyncMainInterval;
        return new ProjectReleaseConfig
        {
            Enabled = c.Enabled ?? defaults.Enabled,
            BranchNameTemplate = c.BranchNameTemplate ?? defaults.BranchNameTemplate,
            AutoSyncMainInterval = syncInterval,
            DeepAuditors = c.DeepAuditors is not null ? [.. c.DeepAuditors] : defaults.DeepAuditors,
            DeepAuditMaxIterations = c.DeepAuditMaxIterations ?? defaults.DeepAuditMaxIterations,
            CreateGitHubRelease = c.CreateGitHubRelease ?? defaults.CreateGitHubRelease,
            GitHubTagTemplate = c.GitHubTagTemplate ?? defaults.GitHubTagTemplate,
        };
    }

    private static CustomAuditorDescriptor ResolveCustom(CustomAuditorConfig c)
    {
        if (string.IsNullOrWhiteSpace(c.Name)) throw new InvalidOperationException("Custom auditor missing 'Name'");
        if (string.IsNullOrWhiteSpace(c.Kind)) throw new InvalidOperationException($"Custom auditor '{c.Name}' missing 'Kind'");
        return new CustomAuditorDescriptor
        {
            Name = c.Name,
            Kind = c.Kind,
            PluginId = c.PluginId,
            Argv = c.Argv ?? [],
            ReviewFocus = c.ReviewFocus,
            Patterns = (c.Patterns ?? []).Select(p => new DiffPatternDescriptor
            {
                Description = p.Description ?? "(no description)",
                Regex = p.Regex ?? throw new InvalidOperationException($"Pattern in '{c.Name}' missing 'Regex'"),
                Severity = p.Severity,
            }).ToList(),
        };
    }
}
