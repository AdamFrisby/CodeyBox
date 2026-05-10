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

    public PresetCatalog()
        : this(null) { }

    public PresetCatalog(PresetCatalogOptions? options)
    {
        var snapshot = new PresetConfigLoader().Load(options);
        LlmPromptFrameTemplate = snapshot.LlmPromptFrame;
        _auditTypeDefinitions = snapshot.AuditTypes;

        foreach (var (name, definition) in snapshot.Languages)
        {
            var captured = definition;
            RegisterLanguage(name, _ => PresetConfigLoader.MaterialiseLanguage(captured));
        }

        AuditTypePresets.Register(this, snapshot.AuditTypes, snapshot.LlmPromptFrame);
    }

    public IReadOnlyList<IAuditor> ResolveLanguage(string name, PresetContext ctx)
        => _languages.TryGetValue(name, out var f) ? f(ctx) : [];

    public IReadOnlyList<IAuditor> ResolveAuditType(string name, PresetContext ctx)
        => _auditTypes.TryGetValue(name, out var f) ? f(ctx) : [];

    public IReadOnlyList<string> KnownLanguages => [.. _languages.Keys];
    public IReadOnlyList<string> KnownAuditTypes => [.. _auditTypes.Keys];
    public string LlmPromptFrameTemplate { get; }

    public string GetAuditTypeReviewFocus(string id)
        => _auditTypeDefinitions.TryGetValue(id, out var definition) ? definition.ReviewFocus : string.Empty;

    internal void RegisterLanguage(string name, Func<PresetContext, IReadOnlyList<IAuditor>> factory)
        => _languages[name] = factory;
    internal void RegisterAuditType(string name, Func<PresetContext, IReadOnlyList<IAuditor>> factory)
        => _auditTypes[name] = factory;
}
