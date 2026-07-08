using CodeyBox.Core;

namespace CodeyBox.Audit.Presets;

/// <summary>
/// Maps preset names ("python", "security", "cheating", …) to bundles of
/// auditors. Composed at startup from built-in YAML defaults and project-specific
/// configuration.
///
/// Two name spaces, kept logically separate but sharing the same catalog:
///   - <b>Languages</b>: tooling specific to a language (linters, type
///     checkers). Selected via <c>ProjectAudit.Languages</c>.
///   - <b>Audit types</b>: cross-language categories (security, architecture,
///     quality, completeness, cheating). Selected via <c>ProjectAudit.AuditTypes</c>.
///
/// The composer that resolves a project's auditor list calls this catalog
/// once per name and concatenates the results.
/// </summary>
public interface IPresetCatalog
{
    /// <summary>Auditors for a language preset (e.g. "python"). Empty list if unknown.</summary>
    IReadOnlyList<IAuditor> ResolveLanguage(string name, PresetContext ctx);

    /// <summary>Auditors for an audit-type preset (e.g. "security", "cheating"). Empty list if unknown.</summary>
    IReadOnlyList<IAuditor> ResolveAuditType(string name, PresetContext ctx);

    /// <summary>All known language preset names (for discoverability / API listing).</summary>
    IReadOnlyList<string> KnownLanguages { get; }

    /// <summary>All known audit-type preset names.</summary>
    IReadOnlyList<string> KnownAuditTypes { get; }

    /// <summary>Validated LLM review frame template used by configured LLM auditors.</summary>
    string LlmPromptFrameTemplate { get; }

    /// <summary>
    /// Validated LLM plan-review frame template used by configured LLM auditors.
    /// External catalogs that predate plan review remain source-compatible via
    /// the standard built-in plan frame.
    /// </summary>
    string LlmPlanPromptFrameTemplate => CodeyBox.Audit.Llm.LlmPromptFrameTemplate.DefaultPlanFrameTemplate;
}

/// <summary>
/// Per-resolution context handed to preset factories. Lets an LLM-based
/// preset (architecture review, etc.) pick the right agent runner for the
/// project that owns this audit.
/// </summary>
public sealed record PresetContext(IAgentRunner Agent);
