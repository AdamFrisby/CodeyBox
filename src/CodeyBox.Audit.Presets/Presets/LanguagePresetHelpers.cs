using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.Audit.Presets.Presets;

internal static class LanguagePresetHelpers
{
    public const string CSharpMarkerScript =
        "find . \\( -name '*.csproj' -o -name '*.sln' -o -name '*.slnx' \\) -print -quit | grep -q .";
    public const string PythonMarkerScript =
        "test -f pyproject.toml -o -f setup.py -o -f setup.cfg -o -f requirements.txt";
    public const string NodeMarkerScript = "test -f package.json";
    public const string GoMarkerScript = "test -f go.mod";
    public const string RustMarkerScript = "test -f Cargo.toml";

    public static IAuditor Shell(
        string language,
        string markerDescription,
        string markerScript,
        string name,
        params string[] argv)
        => new LanguagePresetAuditor(
            language,
            markerDescription,
            markerScript,
            new ShellCommandAuditor(new ShellCommandAuditorOptions { Name = name, Argv = argv }));

    public static IAuditor ShellScript(
        string language,
        string markerDescription,
        string markerScript,
        string name,
        string script,
        string? toolName = null)
        => new LanguagePresetAuditor(
            language,
            markerDescription,
            markerScript,
            new ShellCommandAuditor(new ShellCommandAuditorOptions
            {
                Name = name,
                Argv = ["sh", "-c", script],
                ToolName = toolName,
            }));
}
