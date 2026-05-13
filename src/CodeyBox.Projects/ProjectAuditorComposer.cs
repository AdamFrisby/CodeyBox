using System.Reflection;
using System.Text.RegularExpressions;
using CodeyBox.Audit;
using CodeyBox.Audit.Llm;
using CodeyBox.Audit.Presets;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Projects;

/// <summary>
/// Resolves a project's effective auditor list at pipeline time:
/// <c>Languages.SelectMany(preset) + AuditTypes.SelectMany(preset) + Custom</c>.
/// Stateless; safe to share as a singleton.
///
/// <para>Plugin auditors (registered in DI via <c>IPluginLoader</c>) are injected
/// as <c>IEnumerable&lt;IAuditor&gt;</c> and indexed by their
/// <see cref="CodeyBoxPluginAttribute.Id"/> at construction time. Custom entries
/// with <c>Kind = "plugin"</c> are resolved against this index; unknown IDs log a
/// warning and are skipped rather than failing the audit.</para>
/// </summary>
public sealed class ProjectAuditorComposer
{
    private readonly IPresetCatalog _catalog;
    private readonly PresetCatalogOptions _catalogOptions;
    private readonly IReadOnlyDictionary<string, IAuditor> _pluginAuditors;
    private readonly ILogger<ProjectAuditorComposer> _logger;

    /// <summary>
    /// DI constructor. Receives all <see cref="IAuditor"/> singletons registered
    /// by the plugin loader (empty enumerable when no plugins are loaded).
    /// </summary>
    public ProjectAuditorComposer(
        IPresetCatalog catalog,
        IEnumerable<IAuditor> pluginAuditors,
        ILogger<ProjectAuditorComposer> logger,
        PresetCatalogOptions? catalogOptions = null)
    {
        _catalog = catalog;
        _catalogOptions = catalogOptions?.Clone() ?? new PresetCatalogOptions();
        _logger = logger;

        var index = new Dictionary<string, IAuditor>(StringComparer.OrdinalIgnoreCase);
        foreach (var auditor in pluginAuditors)
        {
            var attr = auditor.GetType().GetCustomAttribute<CodeyBoxPluginAttribute>();
            if (attr is not null)
                index[attr.Id] = auditor;
        }
        _pluginAuditors = index;
    }

    /// <summary>
    /// Backward-compatible constructor for tests that don't need plugin auditors.
    /// </summary>
    public ProjectAuditorComposer(IPresetCatalog catalog)
        : this(catalog, [], NullLogger<ProjectAuditorComposer>.Instance) { }

    public IReadOnlyList<IAuditor> Compose(
        Project project,
        IAgentRunner agentForLlmAuditors,
        string? profile = null)
    {
        var audit = project.Audit.ResolveProfile(profile);
        project = project with { Audit = audit };
        var catalog = ResolveCatalog(project);
        ValidateSelectedPresets(project, catalog);
        var ctx = new PresetContext(agentForLlmAuditors);
        var auditors = new List<IAuditor>();

        foreach (var lang in project.Audit.Languages)
            auditors.AddRange(catalog.ResolveLanguage(lang, ctx));

        foreach (var type in project.Audit.AuditTypes)
            auditors.AddRange(catalog.ResolveAuditType(type, ctx));

        foreach (var custom in project.Audit.Custom)
        {
            if (custom.Kind.Equals("plugin", StringComparison.OrdinalIgnoreCase))
            {
                IncludePluginAuditor(custom, auditors);
            }
            else
            {
                auditors.Add(MaterialiseCustom(custom, ctx, catalog.LlmPromptFrameTemplate));
            }
        }

        if (project.Audit.ExcludedAuditors.Count > 0)
        {
            var excluded = new HashSet<string>(project.Audit.ExcludedAuditors, StringComparer.OrdinalIgnoreCase);
            auditors.RemoveAll(a => excluded.Contains(a.Name));
        }

        return auditors;
    }

    private IPresetCatalog ResolveCatalog(Project project)
    {
        if (_catalog is not PresetCatalog)
            return _catalog;

        var options = _catalogOptions.Clone();
        var hasRepositoryPresetRoot = ProjectRepository.ApplyRepositoryPresetRoot(project, options);
        if (!hasRepositoryPresetRoot && !HasProjectPresetOverrides(project))
            return _catalog;

        ProjectRepository.ApplyPresetOverrideOptions(project, options);
        return new PresetCatalog(options);
    }

    private static bool HasProjectPresetOverrides(Project project)
        => project.Audit.LanguageOverrides.Count > 0 ||
           project.Audit.AuditTypeOverrides.Count > 0 ||
           project.Audit.LlmPromptFrameTemplate is not null;

    private static void ValidateSelectedPresets(Project project, IPresetCatalog catalog)
    {
        var owner = $"Project '{project.Id.Value}'";
        PresetCatalogSelectionValidator.ValidateLanguageIds(owner, project.Audit.Languages, catalog.KnownLanguages);
        PresetCatalogSelectionValidator.ValidateAuditTypeIds(owner, project.Audit.AuditTypes, catalog.KnownAuditTypes);
    }

    private void IncludePluginAuditor(CustomAuditorDescriptor descriptor, List<IAuditor> auditors)
    {
        if (string.IsNullOrWhiteSpace(descriptor.PluginId))
        {
            _logger.LogWarning(
                "Custom auditor entry has Kind='plugin' but no PluginId; skipping");
            return;
        }

        if (!_pluginAuditors.TryGetValue(descriptor.PluginId, out var auditor))
        {
            _logger.LogWarning(
                "Plugin auditor '{PluginId}' is not loaded or not in the allowlist; skipping entry",
                descriptor.PluginId);
            return;
        }

        auditors.Add(auditor);
    }

    /// <summary>
    /// Builds a one-off auditor from a config descriptor. Kinds:
    ///   "shell"        — ShellCommandAuditor with the given Argv
    ///   "diff-pattern" — DiffPatternAuditor with the given Patterns
    ///   "llm"          — LlmReviewAuditor with the given ReviewFocus
    /// </summary>
    private static IAuditor MaterialiseCustom(CustomAuditorDescriptor c, PresetContext ctx, string frameTemplate)
    {
        if (string.IsNullOrWhiteSpace(c.Name))
            throw new InvalidOperationException($"Custom auditor of kind '{c.Kind}' requires a non-empty Name");

        return c.Kind.ToLowerInvariant() switch
        {
            "shell" => new ShellCommandAuditor(new ShellCommandAuditorOptions
            {
                Name = c.Name,
                Argv = c.Argv.Count > 0 ? c.Argv : throw new InvalidOperationException($"shell auditor '{c.Name}' needs Argv"),
            }),
            "diff-pattern" => new DiffPatternAuditor(new DiffPatternAuditorOptions
            {
                Name = c.Name,
                Patterns = c.Patterns.Select(p => new DiffPattern
                {
                    Regex = new Regex(p.Regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeout: TimeSpan.FromSeconds(5)),
                    Description = p.Description,
                    Severity = AuditSeverityParser.Parse(p.Severity),
                }).ToList(),
            }),
            "llm" => new LlmReviewAuditor(new LlmReviewAuditorOptions
            {
                Name = c.Name,
                Agent = ctx.Agent,
                ReviewFocus = c.ReviewFocus ?? throw new InvalidOperationException($"llm auditor '{c.Name}' needs ReviewFocus"),
                FrameTemplate = frameTemplate,
            }),
            _ => throw new InvalidOperationException($"Unknown custom auditor kind '{c.Kind}' for '{c.Name}' (expected: shell | diff-pattern | llm)"),
        };
    }
}
