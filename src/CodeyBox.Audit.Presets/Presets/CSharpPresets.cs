namespace CodeyBox.Audit.Presets.Presets;

internal static class CSharpPresets
{
    public static void Register(PresetCatalog catalog)
        => catalog.RegisterLanguage("csharp", _ =>
        [
            LanguagePresetHelpers.Shell(
                "csharp",
                "*.csproj/*.sln/*.slnx",
                LanguagePresetHelpers.CSharpMarkerScript,
                "csharp:format-check",
                "dotnet", "format", "--verify-no-changes", "--no-restore"),
            LanguagePresetHelpers.Shell(
                "csharp",
                "*.csproj/*.sln/*.slnx",
                LanguagePresetHelpers.CSharpMarkerScript,
                "csharp:build-WaE",
                "dotnet", "build", "--no-incremental", "/warnaserror"),
            LanguagePresetHelpers.Shell(
                "csharp",
                "*.csproj/*.sln/*.slnx",
                LanguagePresetHelpers.CSharpMarkerScript,
                "csharp:test-pass",
                "dotnet", "test", "--no-build"),
        ]);
}
