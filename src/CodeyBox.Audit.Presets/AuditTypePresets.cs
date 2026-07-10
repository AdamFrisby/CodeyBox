using System.Text.RegularExpressions;
using CodeyBox.Audit.Llm;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.Audit.Presets;

/// <summary>
/// Built-in audit-type presets. Cross-language: applicable regardless of
/// what the project is written in.
///
/// Refactored to be fully data-driven: auditors and diff-patterns are
/// loaded from YAML via <see cref="PresetConfigLoader"/>.
/// </summary>
internal static class AuditTypePresets
{
    public static void Register(
        PresetCatalog catalog,
        IReadOnlyDictionary<string, AuditTypePresetDefinition> auditTypes,
        string frameTemplate,
        string planFrameTemplate)
    {
        foreach (var (id, definition) in auditTypes)
        {
            var captured = definition;
            catalog.RegisterAuditType(id, ctx => BuildAuditType(captured, frameTemplate, planFrameTemplate, ctx));
        }
    }

    private static IReadOnlyList<IAuditor> BuildAuditType(
        AuditTypePresetDefinition definition,
        string frameTemplate,
        string planFrameTemplate,
        PresetContext ctx)
    {
        var auditors = new List<IAuditor>();
        var targets = ParseTargets(definition.Targets);

        // 1. Add explicitly configured auditors (usually ShellCommandAuditors).
        AddConfiguredAuditors(auditors, definition.Id, definition.Auditors, targets);
        AddConfiguredAuditors(auditors, definition.Id, definition.CodeOnlyAuditors, AuditTargets.CodeOnly);

        // 2. Add DiffPatternAuditor if patterns are configured.
        AddPatterns(auditors, $"{definition.Id}:deterministic-patterns", definition.Patterns, targets);
        AddPatterns(auditors, $"{definition.Id}:repository-patterns", definition.CodeOnlyPatterns, AuditTargets.CodeOnly);

        // 3. Add LlmReviewAuditor if a review focus is present.
        if (!string.IsNullOrWhiteSpace(definition.ReviewFocus))
        {
            auditors.Add(Llm(definition, frameTemplate, planFrameTemplate, targets, ctx));
        }

        return auditors;
    }

    private static void AddConfiguredAuditors(
        List<IAuditor> auditors,
        string auditTypeId,
        IReadOnlyList<AuditorDefinition> definitions,
        IReadOnlySet<AuditTarget> targets)
    {
        foreach (var a in definitions)
        {
            var role = PresetConfigLoader.ParseAuditorRole(
                $"audit-type '{auditTypeId}'", $"/auditors/{a.Name}/role", a.Role);
            var gateEvidence = PresetConfigLoader.ParseBuildTestGateEvidence(
                $"audit-type '{auditTypeId}'", $"/auditors/{a.Name}/gateEvidence", a.Role, a.GateEvidence);
            var missingToolSeverity = PresetConfigLoader.ParseOptionalAuditSeverity(
                $"audit-type '{auditTypeId}'", $"/auditors/{a.Name}/missingToolSeverity", a.MissingToolSeverity);
            var required = PresetConfigLoader.ParseAuditCapabilities(
                $"audit-type '{auditTypeId}'", $"/auditors/{a.Name}/requiredCapabilities", a.RequiredCapabilities);
            if (string.IsNullOrWhiteSpace(a.Script))
            {
                auditors.Add(Shell(
                    a.Name,
                    a.CanShortCircuitOnBlockingFinding,
                    role,
                    gateEvidence,
                    missingToolSeverity,
                    required,
                    targets,
                    [.. a.Argv]));
            }
            else
            {
                // Note: Audit-types use ShellCommandAuditor directly for scripts,
                // as they don't need the language-marker logic that
                // LanguagePresetHelpers.ShellScript provides.
                auditors.Add(new ShellCommandAuditor(new ShellCommandAuditorOptions
                {
                    Name = a.Name,
                    Argv = ["sh", "-c", a.Script],
                    ToolName = a.ToolName,
                    TreatExit127AsMissingTool = a.TreatExit127AsMissingTool,
                    MissingToolSeverity = missingToolSeverity,
                    Required = required,
                    Targets = targets,
                    CanShortCircuitOnBlockingFinding = a.CanShortCircuitOnBlockingFinding,
                    Role = role,
                    BuildTestGateEvidence = gateEvidence,
                }));
            }
        }
    }

    private static void AddPatterns(
        List<IAuditor> auditors,
        string name,
        IReadOnlyList<DiffPatternDefinition> patterns,
        IReadOnlySet<AuditTarget> targets)
    {
        if (patterns.Count == 0)
            return;

        auditors.Add(new DiffPatternAuditor(new DiffPatternAuditorOptions
            {
                Name = name,
                Patterns = MaterialisePatterns(patterns),
                Targets = targets,
            }));
    }

    private static IReadOnlyList<DiffPattern> MaterialisePatterns(IReadOnlyList<DiffPatternDefinition> definitions)
        => definitions.Select(d => new DiffPattern
        {
            Regex = new Regex(d.Regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            Description = d.Description,
            Severity = AuditSeverityParser.Parse(d.Severity),
        }).ToList();

    private static IAuditor Llm(
        AuditTypePresetDefinition definition,
        string frameTemplate,
        string planFrameTemplate,
        IReadOnlySet<AuditTarget> targets,
        PresetContext ctx)
        => new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = definition.LlmAuditorName ?? $"{definition.Id}:llm-review",
            Agent = ctx.Agent,
            ReviewFocus = definition.ReviewFocus,
            PlanReviewFocus = definition.PlanReviewFocus,
            FrameTemplate = frameTemplate,
            PlanFrameTemplate = planFrameTemplate,
            Targets = targets,
        });

    private static IReadOnlySet<AuditTarget> ParseTargets(IReadOnlyList<string> targets)
        => targets.Count == 0
            ? AuditTargets.CodeOnly
            : AuditTargets.Of(targets.Select(t => new AuditTarget(t)).ToArray());

    private static IAuditor Shell(
        string name,
        bool canShortCircuitOnBlockingFinding,
        AuditorRole role,
        BuildTestGateEvidence gateEvidence,
        AuditSeverity? missingToolSeverity,
        AuditCapabilities required,
        IReadOnlySet<AuditTarget> targets,
        params string[] argv)
        => new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = name,
            Argv = argv,
            MissingToolSeverity = missingToolSeverity,
            Required = required,
            Targets = targets,
            CanShortCircuitOnBlockingFinding = canShortCircuitOnBlockingFinding,
            Role = role,
            BuildTestGateEvidence = gateEvidence,
        });
}

/// <summary>
/// Built-in named audit profiles. Profiles select full auditor bundles for a
/// work-item shape; audit types remain the lower-level category presets.
/// </summary>
public static class AuditProfilePresets
{
    public const string Uat = "uat";

    public static IReadOnlyDictionary<string, ProjectAudit> CreateBuiltIns()
        => new Dictionary<string, ProjectAudit>(StringComparer.OrdinalIgnoreCase)
        {
            [Uat] = CreateUat(),
        };

    public static ProjectAudit CreateUat() => new()
    {
        MaxIterations = 5,
        Languages = ["csharp"],
        LanguagesConfigured = true,
        AuditTypes = ["security", "cheating"],

        // UAT generation produces a test plan/list, not a production-code
        // patch. The completeness and cheating LLM reviewers repeatedly
        // overfit that meta-shape; keep deterministic shortcut checks and the
        // security LLM review, but omit cheating:llm-review here.
        ExcludedAuditors = ["cheating:llm-review"],
    };
}
