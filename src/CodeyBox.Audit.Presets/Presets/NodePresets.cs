namespace CodeyBox.Audit.Presets.Presets;

internal static class NodePresets
{
    public static void Register(PresetCatalog catalog)
    {
        RegisterNodeLike(catalog, "node", "node");
        RegisterNodeLike(catalog, "javascript", "javascript");
        RegisterNodeLike(catalog, "typescript", "typescript");
    }

    private static void RegisterNodeLike(PresetCatalog catalog, string language, string auditorPrefix)
        => catalog.RegisterLanguage(language, _ =>
        [
            LanguagePresetHelpers.Shell(
                language,
                "package.json",
                LanguagePresetHelpers.NodeMarkerScript,
                $"{auditorPrefix}:format-check",
                "prettier", "--check", "."),
            LanguagePresetHelpers.Shell(
                language,
                "package.json",
                LanguagePresetHelpers.NodeMarkerScript,
                $"{auditorPrefix}:lint",
                "eslint", "."),
            LanguagePresetHelpers.Shell(
                language,
                "package.json",
                LanguagePresetHelpers.NodeMarkerScript,
                $"{auditorPrefix}:test-pass",
                "npm", "test"),
        ]);
}
