using CodeyBox.Core;

namespace CodeyBox.Audit.Presets;

/// <summary>
/// Default <see cref="IPresetCatalog"/>: loads built-in preset YAML from
/// embedded resources, then composes optional project-root and appsettings
/// overrides before any audit work starts.
/// </summary>
public sealed class PresetCatalog : IPresetCatalog
{
    private readonly Dictionary<string, Func<PresetContext, IReadOnlyList<IAuditor>>> _languages =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<PresetContext, IReadOnlyList<IAuditor>>> _auditTypes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, AuditTypePresetDefinition> _auditTypeDefinitions;
    private readonly TestFailureAttributionOptionsSnapshot? _testFailureAttributionOptions;

    public PresetCatalog()
        : this(null) { }

    public PresetCatalog(PresetCatalogOptions? options)
        : this(options, testRunOptions: null, testFailureAttributionOptions: null) { }

    /// <param name="testRunOptions">
    /// Live accessor for hot-reloadable <see cref="TestRunOptions"/> (blame-hang
    /// / test-specific idle-timeout) sourced by the host from pipeline tuning.
    /// Null keeps the default (byte-identical) test-runner behaviour, which is
    /// what unit tests and the parameterless path use.
    /// </param>
    /// <param name="testFailureAttributionOptions">
    /// Hot-reloadable test-failure-attribution options. When set, a classified
    /// dotnet-test failure triggers a base-checkout rerun that attributes each
    /// failing test to the diff or to pre-existing state. Null disables the
    /// feature (classification fails closed to diff-attributable).
    /// </param>
    public PresetCatalog(
        PresetCatalogOptions? options,
        Func<TestRunOptions>? testRunOptions,
        TestFailureAttributionOptionsSnapshot? testFailureAttributionOptions = null)
    {
        _testFailureAttributionOptions = testFailureAttributionOptions;
        var snapshot = new PresetConfigLoader().Load(options);
        LlmPromptFrameTemplate = snapshot.LlmPromptFrame;
        LlmPlanPromptFrameTemplate = snapshot.LlmPlanPromptFrame;
        _auditTypeDefinitions = snapshot.AuditTypes;

        foreach (var (name, definition) in snapshot.Languages)
        {
            var captured = definition;
            RegisterLanguage(name, _ => PresetConfigLoader.MaterialiseLanguage(
                captured,
                testRunOptions,
                _testFailureAttributionOptions));
        }

        AuditTypePresets.Register(this, snapshot.AuditTypes, snapshot.LlmPromptFrame, snapshot.LlmPlanPromptFrame);
    }

    public IReadOnlyList<IAuditor> ResolveLanguage(string name, PresetContext ctx)
        => _languages.TryGetValue(name, out var f) ? f(ctx) : [];

    public IReadOnlyList<IAuditor> ResolveAuditType(string name, PresetContext ctx)
        => _auditTypes.TryGetValue(name, out var f) ? f(ctx) : [];

    public IReadOnlyList<string> KnownLanguages => [.. _languages.Keys];
    public IReadOnlyList<string> KnownAuditTypes => [.. _auditTypes.Keys];
    public string LlmPromptFrameTemplate { get; }
    public string LlmPlanPromptFrameTemplate { get; }

    public string GetAuditTypeReviewFocus(string id)
        => _auditTypeDefinitions.TryGetValue(id, out var definition) ? definition.ReviewFocus : string.Empty;

    internal void RegisterLanguage(string name, Func<PresetContext, IReadOnlyList<IAuditor>> factory)
        => _languages[name] = factory;
    internal void RegisterAuditType(string name, Func<PresetContext, IReadOnlyList<IAuditor>> factory)
        => _auditTypes[name] = factory;
}
