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
/// <para>Registered auditors (built-ins and plugins registered in DI via
/// <c>IPluginLoader</c>) are injected as <c>IEnumerable&lt;IAuditor&gt;</c>.
/// Plugin auditors are indexed by their <see cref="CodeyBoxPluginAttribute.Id"/>
/// for custom <c>Kind = "plugin"</c> entries, while built-in opt-in auditors
/// can be selected by name without coupling this layer to their concrete
/// implementation.</para>
/// </summary>
public sealed class ProjectAuditorComposer
{
    private readonly IPresetCatalog _catalog;
    private readonly PresetCatalogOptions _catalogOptions;
    private readonly Func<TestRunOptions>? _testRunOptions;
    private readonly IReadOnlyDictionary<string, IAuditor> _registeredAuditorsByName;
    private readonly IReadOnlyDictionary<string, IAuditor> _pluginAuditors;
    private readonly ILogger<ProjectAuditorComposer> _logger;

    /// <summary>
    /// DI constructor. Receives all <see cref="IAuditor"/> singletons registered
    /// by the host and plugin loader (empty enumerable when no optional auditors
    /// are loaded).
    /// </summary>
    /// <param name="testRunOptions">
    /// Live accessor for hot-reloadable <see cref="TestRunOptions"/> (blame-hang /
    /// test-specific idle-timeout). Threaded into the per-project
    /// <see cref="PresetCatalog"/> built for projects that carry preset overrides
    /// or a repository preset root, so those projects honour the same
    /// hot-reloadable knobs as the shared catalog rather than silently falling
    /// back to <see cref="TestRunOptions.Default"/>. Null keeps the default
    /// (byte-identical) behaviour used by tests.
    /// </param>
    public ProjectAuditorComposer(
        IPresetCatalog catalog,
        IEnumerable<IAuditor> registeredAuditors,
        ILogger<ProjectAuditorComposer> logger,
        PresetCatalogOptions? catalogOptions = null,
        Func<TestRunOptions>? testRunOptions = null)
    {
        _catalog = catalog;
        _catalogOptions = catalogOptions?.Clone() ?? new PresetCatalogOptions();
        _testRunOptions = testRunOptions;
        _logger = logger;

        var byName = new Dictionary<string, IAuditor>(StringComparer.OrdinalIgnoreCase);
        var index = new Dictionary<string, IAuditor>(StringComparer.OrdinalIgnoreCase);
        foreach (var auditor in registeredAuditors)
        {
            byName[auditor.Name] = auditor;
            var attr = auditor.GetType().GetCustomAttribute<CodeyBoxPluginAttribute>();
            if (attr is not null)
                index[attr.Id] = auditor;
        }
        _registeredAuditorsByName = byName;
        _pluginAuditors = index;
    }

    /// <summary>
    /// Backward-compatible constructor for tests that don't need plugin auditors.
    /// </summary>
    public ProjectAuditorComposer(IPresetCatalog catalog)
        : this(catalog, [], NullLogger<ProjectAuditorComposer>.Instance) { }

    /// <summary>
    /// Composes the project's effective auditor list filtered to a single
    /// review <paramref name="target"/>. The plan-review phase passes
    /// <see cref="AuditTarget.Plan"/> and the code-audit phase passes
    /// <see cref="AuditTarget.Code"/>; both draw from the same registry,
    /// preset selection, and config-driven active set (ExcludedAuditors etc.).
    /// Only auditors whose <see cref="IAuditor.Targets"/> contains the target
    /// survive — there is no bespoke per-phase wiring.
    /// </summary>
    public IReadOnlyList<IAuditor> ComposeForTarget(
        Project project,
        IAgentRunner agentForLlmAuditors,
        AuditTarget target,
        string? profile = null)
        => Compose(project, agentForLlmAuditors, profile, target)
            .Where(a => a.Targets.Contains(target))
            .ToList();

    public IReadOnlyList<IAuditor> Compose(
        Project project,
        IAgentRunner agentForLlmAuditors,
        string? profile = null)
        => Compose(project, agentForLlmAuditors, profile, target: null);

    private IReadOnlyList<IAuditor> Compose(
        Project project,
        IAgentRunner agentForLlmAuditors,
        string? profile,
        AuditTarget? target)
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
                IncludePluginAuditor(custom, auditors, target);
            }
            else
            {
                var descriptorTargets = ParseCustomAuditorTargets(custom);
                if (target is not null && !descriptorTargets.Contains(target.Value))
                    continue;
                auditors.Add(MaterialiseCustom(custom, ctx, catalog.LlmPromptFrameTemplate, catalog.LlmPlanPromptFrameTemplate));
            }
        }

        if (project.GraphicalSandbox
            && !auditors.Any(a => a.Name.Equals("gui:smoke", StringComparison.OrdinalIgnoreCase)))
        {
            IncludeRegisteredAuditor("gui:smoke", auditors, prepend: true);
        }

        // Always include the deterministic prompt-revision trailer auditor.
        // It is cheap (single git log -1), requires no agent credentials, and
        // enforces the cross-iteration invariant that the agent's HEAD commit
        // carries the CodeyBox-Prompt-Revision trailer the orchestrator
        // snapshotted at dispatch time. A missing or stale trailer means the
        // agent finished against an old prompt — a blocking finding.
        if (!auditors.Any(a => a.Name.Equals(
                PromptRevisionTrailerAuditor.AuditorName, StringComparison.OrdinalIgnoreCase)))
        {
            IncludeRegisteredAuditor(PromptRevisionTrailerAuditor.AuditorName, auditors, prepend: false);
        }

        // Always include the language-agnostic build-script auditor last. It
        // no-ops unless the branch carries a repo-root build.sh or the project
        // requires one, and PipelineRunner gives isolated auditors a fresh
        // sandbox so branch-controlled scripts cannot mutate later checks.
        if (!auditors.Any(a => a.Name.Equals(
                WellKnownAuditorNames.BuildScript, StringComparison.OrdinalIgnoreCase)))
        {
            IncludeRegisteredAuditor(WellKnownAuditorNames.BuildScript, auditors, prepend: false);
        }

        // Always include the mutation-rigor auditor when it has been
        // registered. It is config-gated: the auditor itself short-circuits
        // to a passing no-op when CodeyBox:Mutation:Enabled=false, so
        // auto-inclusion is cheap when the gate is off. Operators that want
        // the gate skipped on a specific project list it under ExcludedAuditors.
        if (_registeredAuditorsByName.ContainsKey("tests:mutation-rigor")
            && !auditors.Any(a => a.Name.Equals(
                "tests:mutation-rigor", StringComparison.OrdinalIgnoreCase)))
        {
            IncludeRegisteredAuditor("tests:mutation-rigor", auditors, prepend: false);
        }

        if (project.Audit.ExcludedAuditors.Count > 0)
        {
            var excluded = new HashSet<string>(project.Audit.ExcludedAuditors, StringComparer.OrdinalIgnoreCase);
            auditors.RemoveAll(a =>
                excluded.Contains(a.Name) &&
                !(project.Audit.BuildScriptRequired &&
                  a.Name.Equals(WellKnownAuditorNames.BuildScript, StringComparison.OrdinalIgnoreCase)));
        }

        return auditors;
    }

    private void IncludeRegisteredAuditor(string name, List<IAuditor> auditors, bool prepend)
    {
        if (!_registeredAuditorsByName.TryGetValue(name, out var auditor))
        {
            _logger.LogWarning(
                "Auditor '{AuditorName}' was requested by project composition but is not registered; skipping",
                name);
            return;
        }

        if (prepend)
            auditors.Insert(0, auditor);
        else
            auditors.Add(auditor);
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
        // Thread the hot-reloadable run-options accessor into the per-project
        // catalog so override / repo-preset-root projects still source
        // blame-hang and the test-specific idle timeout through the type;
        // dropping it here would silently fall back to TestRunOptions.Default.
        return new PresetCatalog(options, _testRunOptions);
    }

    private static bool HasProjectPresetOverrides(Project project)
        => project.Audit.LanguageOverrides.Count > 0 ||
           project.Audit.AuditTypeOverrides.Count > 0 ||
           project.Audit.LlmPromptFrameTemplate is not null ||
           project.Audit.LlmPlanPromptFrameTemplate is not null;

    private static void ValidateSelectedPresets(Project project, IPresetCatalog catalog)
    {
        var owner = $"Project '{project.Id.Value}'";
        PresetCatalogSelectionValidator.ValidateLanguageIds(owner, project.Audit.Languages, catalog.KnownLanguages);
        PresetCatalogSelectionValidator.ValidateAuditTypeIds(owner, project.Audit.AuditTypes, catalog.KnownAuditTypes);
    }

    private void IncludePluginAuditor(CustomAuditorDescriptor descriptor, List<IAuditor> auditors, AuditTarget? target)
    {
        if (HasCustomGateMetadata(descriptor))
            throw new InvalidOperationException(
                $"Custom plugin auditor '{descriptor.PluginId ?? descriptor.Name}' cannot set Role or GateEvidence; build-test-gate metadata is supported only for custom shell auditors");

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

        if (target is not null && TryParseCustomAuditorTargetNarrowing(descriptor, out var narrowedTargets)
            && !narrowedTargets.Contains(target.Value))
        {
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
    private static IAuditor MaterialiseCustom(
        CustomAuditorDescriptor c,
        PresetContext ctx,
        string frameTemplate,
        string planFrameTemplate)
    {
        if (string.IsNullOrWhiteSpace(c.Name))
            throw new InvalidOperationException($"Custom auditor of kind '{c.Kind}' requires a non-empty Name");
        var isShell = c.Kind.Equals("shell", StringComparison.OrdinalIgnoreCase);
        if (!isShell && HasCustomGateMetadata(c))
            throw new InvalidOperationException(
                $"Custom auditor '{c.Name}' of kind '{c.Kind}' cannot set Role or GateEvidence; build-test-gate metadata is supported only for custom shell auditors");

        var role = ParseCustomAuditorRole(c);
        var gateEvidence = ParseCustomGateEvidence(c, role);
        var targets = ParseCustomAuditorTargets(c);

        return c.Kind.ToLowerInvariant() switch
        {
            "shell" => new ShellCommandAuditor(new ShellCommandAuditorOptions
            {
                Name = c.Name,
                Argv = c.Argv.Count > 0 ? c.Argv : throw new InvalidOperationException($"shell auditor '{c.Name}' needs Argv"),
                Targets = targets,
                Role = role,
                BuildTestGateEvidence = gateEvidence,
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
                Targets = targets,
            }),
            "llm" => new LlmReviewAuditor(new LlmReviewAuditorOptions
            {
                Name = c.Name,
                Agent = ctx.Agent,
                ReviewFocus = c.ReviewFocus ?? throw new InvalidOperationException($"llm auditor '{c.Name}' needs ReviewFocus"),
                FrameTemplate = frameTemplate,
                PlanFrameTemplate = planFrameTemplate,
                Targets = targets,
            }),
            _ => throw new InvalidOperationException($"Unknown custom auditor kind '{c.Kind}' for '{c.Name}' (expected: shell | diff-pattern | llm)"),
        };
    }

    private static IReadOnlySet<AuditTarget> ParseCustomAuditorTargets(CustomAuditorDescriptor descriptor)
    {
        if (descriptor.Targets.Count == 0)
            return AuditTargets.CodeOnly;

        var targets = new List<AuditTarget>(descriptor.Targets.Count);
        foreach (var raw in descriptor.Targets)
        {
            var value = raw.Trim();
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Custom auditor '{descriptor.Name}' has an empty target value");
            targets.Add(new AuditTarget(value.ToLowerInvariant()));
        }

        return AuditTargets.Of(targets.ToArray());
    }

    private static bool TryParseCustomAuditorTargetNarrowing(
        CustomAuditorDescriptor descriptor,
        out IReadOnlySet<AuditTarget> targets)
    {
        if (descriptor.Targets.Count == 0)
        {
            targets = AuditTargets.CodeOnly;
            return false;
        }

        targets = ParseCustomAuditorTargets(descriptor);
        return true;
    }

    private static bool HasCustomGateMetadata(CustomAuditorDescriptor descriptor)
        => !string.IsNullOrWhiteSpace(descriptor.Role)
           || !string.IsNullOrWhiteSpace(descriptor.GateEvidence);

    private static AuditorRole ParseCustomAuditorRole(CustomAuditorDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Role))
            return AuditorRole.None;

        if (descriptor.Role.Equals("build-test-gate", StringComparison.OrdinalIgnoreCase))
            return AuditorRole.BuildTestGate;

        throw new InvalidOperationException(
            $"Custom auditor '{descriptor.Name}' has unsupported Role '{descriptor.Role}' (expected: build-test-gate)");
    }

    private static BuildTestGateEvidence ParseCustomGateEvidence(
        CustomAuditorDescriptor descriptor,
        AuditorRole role)
    {
        if (string.IsNullOrWhiteSpace(descriptor.GateEvidence))
            return BuildTestGateEvidence.None;

        if (role != AuditorRole.BuildTestGate)
            throw new InvalidOperationException(
                $"Custom auditor '{descriptor.Name}' sets GateEvidence but is not a build-test-gate");

        return descriptor.GateEvidence.Trim().ToLowerInvariant() switch
        {
            "build" => BuildTestGateEvidence.Build,
            "test" => BuildTestGateEvidence.Test,
            "build-and-test" => BuildTestGateEvidence.BuildAndTest,
            _ => throw new InvalidOperationException(
                $"Custom auditor '{descriptor.Name}' has unsupported GateEvidence '{descriptor.GateEvidence}' (expected: build, test, or build-and-test)"),
        };
    }
}
