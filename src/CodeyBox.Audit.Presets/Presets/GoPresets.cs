namespace CodeyBox.Audit.Presets.Presets;

internal static class GoPresets
{
    public static void Register(PresetCatalog catalog)
        => catalog.RegisterLanguage("go", _ =>
        [
            LanguagePresetHelpers.ShellScript(
                "go",
                "go.mod",
                LanguagePresetHelpers.GoMarkerScript,
                "go:format-check",
                "out=$(gofmt -l .) || exit $?; test -z \"$out\" || { printf '%s\\n' \"$out\"; exit 1; }",
                "gofmt"),
            LanguagePresetHelpers.Shell(
                "go",
                "go.mod",
                LanguagePresetHelpers.GoMarkerScript,
                "go:vet",
                "go", "vet", "./..."),
            LanguagePresetHelpers.Shell(
                "go",
                "go.mod",
                LanguagePresetHelpers.GoMarkerScript,
                "go:test-pass",
                "go", "test", "./..."),
        ]);
}
