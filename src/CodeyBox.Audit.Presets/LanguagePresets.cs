using CodeyBox.Audit.Presets.Presets;

namespace CodeyBox.Audit.Presets;

/// <summary>
/// Built-in language preset registry. Dotnet is intentionally just one
/// supported language among the initial built-in set.
/// </summary>
internal static class LanguagePresets
{
    public static void Register(PresetCatalog catalog)
    {
        CSharpPresets.Register(catalog);
        PythonPresets.Register(catalog);
        NodePresets.Register(catalog);
        GoPresets.Register(catalog);
        RustPresets.Register(catalog);
    }
}
