namespace CodeyBox.Audit.Presets.Presets;

internal static class NodePresets
{
    public static void Register(PresetCatalog catalog)
    {
        RegisterNodeLike(catalog, "node");
        RegisterNodeLike(catalog, "javascript");
        RegisterNodeLike(catalog, "typescript");
    }

    private static void RegisterNodeLike(PresetCatalog catalog, string language)
    {
        catalog.RegisterLanguage(language, _ =>
        [
            LanguagePresetHelpers.Shell(
                language,
                "package.json",
                LanguagePresetHelpers.NodeMarkerScript,
                $"{language}:format-check",
                "prettier", "--check", "."),
            LanguagePresetHelpers.Shell(
                language,
                "package.json",
                LanguagePresetHelpers.NodeMarkerScript,
                $"{language}:lint",
                "eslint", "."),
            LanguagePresetHelpers.Shell(
                language,
                "package.json",
                LanguagePresetHelpers.NodeMarkerScript,
                $"{language}:test-pass",
                "npm", "test"),
        ]);
    }
}
