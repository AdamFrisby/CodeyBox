using CodeyBox.Core;

namespace CodeyBox.Audit.Presets;

/// <summary>
/// Default <see cref="IPresetCatalog"/>: returns the built-in language and
/// audit-type bundles. Operators can wrap this with their own catalog (e.g.
/// to add a "java" preset or override "python" to use mypy instead of
/// pyright) without touching orchestrator code.
/// </summary>
public sealed class PresetCatalog : IPresetCatalog
{
    private readonly Dictionary<string, Func<PresetContext, IReadOnlyList<IAuditor>>> _languages =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<PresetContext, IReadOnlyList<IAuditor>>> _auditTypes =
        new(StringComparer.OrdinalIgnoreCase);

    public PresetCatalog()
    {
        LanguagePresets.Register(this);
        AuditTypePresets.Register(this);
    }

    public IReadOnlyList<IAuditor> ResolveLanguage(string name, PresetContext ctx)
        => _languages.TryGetValue(name, out var f) ? f(ctx) : [];

    public IReadOnlyList<IAuditor> ResolveAuditType(string name, PresetContext ctx)
        => _auditTypes.TryGetValue(name, out var f) ? f(ctx) : [];

    public IReadOnlyList<string> KnownLanguages => [.. _languages.Keys];
    public IReadOnlyList<string> KnownAuditTypes => [.. _auditTypes.Keys];

    internal void RegisterLanguage(string name, Func<PresetContext, IReadOnlyList<IAuditor>> factory)
        => _languages[name] = factory;
    internal void RegisterAuditType(string name, Func<PresetContext, IReadOnlyList<IAuditor>> factory)
        => _auditTypes[name] = factory;
}
