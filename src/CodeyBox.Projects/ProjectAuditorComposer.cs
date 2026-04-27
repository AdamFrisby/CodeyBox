using System.Text.RegularExpressions;
using CodeyBox.Audit;
using CodeyBox.Audit.Llm;
using CodeyBox.Audit.Presets;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.Projects;

/// <summary>
/// Resolves a project's effective auditor list at pipeline time:
/// <c>Languages.SelectMany(preset) + AuditTypes.SelectMany(preset) + Custom</c>.
/// Stateless; safe to share as a singleton.
/// </summary>
public sealed class ProjectAuditorComposer
{
    private readonly IPresetCatalog _catalog;

    public ProjectAuditorComposer(IPresetCatalog catalog)
    {
        _catalog = catalog;
    }

    public IReadOnlyList<IAuditor> Compose(Project project, IAgentRunner agentForLlmAuditors)
    {
        var ctx = new PresetContext(agentForLlmAuditors);
        var auditors = new List<IAuditor>();

        foreach (var lang in project.Audit.Languages)
            auditors.AddRange(_catalog.ResolveLanguage(lang, ctx));

        foreach (var type in project.Audit.AuditTypes)
            auditors.AddRange(_catalog.ResolveAuditType(type, ctx));

        foreach (var custom in project.Audit.Custom)
            auditors.Add(MaterialiseCustom(custom, ctx));

        return auditors;
    }

    /// <summary>
    /// Builds a one-off auditor from a config descriptor. Kinds:
    ///   "shell"        — ShellCommandAuditor with the given Argv
    ///   "diff-pattern" — DiffPatternAuditor with the given Patterns
    ///   "llm"          — LlmReviewAuditor with the given ReviewFocus
    /// </summary>
    private static IAuditor MaterialiseCustom(CustomAuditorDescriptor c, PresetContext ctx)
    {
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
                    Regex = new Regex(p.Regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                    Description = p.Description,
                }).ToList(),
            }),
            "llm" => new LlmReviewAuditor(new LlmReviewAuditorOptions
            {
                Name = c.Name,
                Agent = ctx.Agent,
                ReviewFocus = c.ReviewFocus ?? throw new InvalidOperationException($"llm auditor '{c.Name}' needs ReviewFocus"),
            }),
            _ => throw new InvalidOperationException($"Unknown custom auditor kind '{c.Kind}' for '{c.Name}' (expected: shell | diff-pattern | llm)"),
        };
    }
}
