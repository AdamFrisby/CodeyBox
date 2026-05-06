namespace CodeyBox.Audit.Presets.Presets;

internal static class ShellPresets
{
    public static void Register(PresetCatalog catalog)
        => catalog.RegisterLanguage("shell", _ =>
        [
            LanguagePresetHelpers.ShellScript(
                "shell",
                "*.sh",
                LanguagePresetHelpers.ShellMarkerScript,
                "shell:lint",
                "find . -name '*.sh' -print0 | xargs -0 shellcheck",
                "shellcheck"),
        ]);
}
