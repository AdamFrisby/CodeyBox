using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.Audit.Presets.Presets;

internal static class LanguagePresetHelpers
{
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
        string? toolName = null,
        bool? treatExit127AsMissingTool = null)
        => new LanguagePresetAuditor(
            language,
            markerDescription,
            markerScript,
            new ShellCommandAuditor(new ShellCommandAuditorOptions
            {
                Name = name,
                Argv = ["sh", "-c", script],
                ToolName = toolName,
                TreatExit127AsMissingTool = treatExit127AsMissingTool,
            }));
}
