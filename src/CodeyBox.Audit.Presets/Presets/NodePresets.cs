namespace CodeyBox.Audit.Presets.Presets;

internal static class NodePresets
{
    public static void Register(PresetCatalog catalog)
        => catalog.RegisterLanguage("node", _ =>
        [
            LanguagePresetHelpers.Shell(
                "node",
                "package.json",
                LanguagePresetHelpers.NodeMarkerScript,
                "node:format-check",
                "prettier", "--check", "."),
            LanguagePresetHelpers.Shell(
                "node",
                "package.json",
                LanguagePresetHelpers.NodeMarkerScript,
                "node:lint",
                "eslint", "."),
            LanguagePresetHelpers.Shell(
                "node",
                "package.json",
                LanguagePresetHelpers.NodeMarkerScript,
                "node:test-pass",
                "npm", "test"),
        ]);
}
