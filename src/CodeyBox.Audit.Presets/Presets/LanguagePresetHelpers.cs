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
        string[] argv,
        bool canShortCircuitOnBlockingFinding = false,
        AuditorRole role = AuditorRole.None,
        BuildTestGateEvidence gateEvidence = BuildTestGateEvidence.None)
        => new LanguagePresetAuditor(
            language,
            markerDescription,
            markerScript,
            new ShellCommandAuditor(new ShellCommandAuditorOptions
            {
                Name = name,
                Argv = argv,
                ResultClassifier = ResultClassifierFor(language, name, argv),
                CanShortCircuitOnBlockingFinding = canShortCircuitOnBlockingFinding,
                Role = role,
                BuildTestGateEvidence = gateEvidence,
            }));

    public static IAuditor ShellScript(
        string language,
        string markerDescription,
        string markerScript,
        string name,
        string script,
        string? toolName = null,
        bool? treatExit127AsMissingTool = null,
        bool canShortCircuitOnBlockingFinding = false,
        AuditorRole role = AuditorRole.None,
        BuildTestGateEvidence gateEvidence = BuildTestGateEvidence.None)
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
                CanShortCircuitOnBlockingFinding = canShortCircuitOnBlockingFinding,
                Role = role,
                BuildTestGateEvidence = gateEvidence,
            }));

    private static IShellCommandResultClassifier? ResultClassifierFor(
        string language,
        string name,
        IReadOnlyList<string> argv)
    {
        if (string.Equals(language, "csharp", StringComparison.Ordinal)
            && string.Equals(name, "csharp:format-check", StringComparison.Ordinal)
            && argv.Count >= 2
            && string.Equals(argv[0], "dotnet", StringComparison.Ordinal)
            && string.Equals(argv[1], "format", StringComparison.Ordinal))
        {
            return new DotnetFormatCommandResultClassifier();
        }

        if (string.Equals(language, "csharp", StringComparison.Ordinal)
            && string.Equals(name, "csharp:test-pass", StringComparison.Ordinal)
            && argv.Count >= 2
            && string.Equals(argv[0], "dotnet", StringComparison.Ordinal)
            && string.Equals(argv[1], "test", StringComparison.Ordinal))
        {
            return new DotnetTestCommandResultClassifier();
        }

        return null;
    }
}
