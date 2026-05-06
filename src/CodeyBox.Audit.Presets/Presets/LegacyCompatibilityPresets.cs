namespace CodeyBox.Audit.Presets.Presets;

internal static class LegacyCompatibilityPresets
{
    public static void Register(PresetCatalog catalog)
    {
        catalog.RegisterLanguage("ruby", _ => []);
        catalog.RegisterLanguage("shell", _ => []);
    }
}
