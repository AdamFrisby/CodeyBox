namespace CodeyBox.Audit.Presets.Presets;

internal static class RustPresets
{
    public static void Register(PresetCatalog catalog)
        => catalog.RegisterLanguage("rust", _ =>
        [
            LanguagePresetHelpers.Shell(
                "rust",
                "Cargo.toml",
                LanguagePresetHelpers.RustMarkerScript,
                "rust:format-check",
                "cargo", "fmt", "--check"),
            LanguagePresetHelpers.Shell(
                "rust",
                "Cargo.toml",
                LanguagePresetHelpers.RustMarkerScript,
                "rust:lint",
                "cargo", "clippy", "--", "-D", "warnings"),
            LanguagePresetHelpers.Shell(
                "rust",
                "Cargo.toml",
                LanguagePresetHelpers.RustMarkerScript,
                "rust:test-pass",
                "cargo", "test"),
        ]);
}
