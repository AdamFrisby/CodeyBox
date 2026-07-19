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
        BuildTestGateEvidence gateEvidence = BuildTestGateEvidence.None,
        AuditSeverity? missingToolSeverity = null,
        AuditCapabilities required = AuditCapabilities.None,
        Func<TestRunOptions>? testRunOptions = null,
        TestFailureAttributionOptionsSnapshot? testFailureAttributionOptions = null)
    {
        // Any dotnet-driven language gate restores on first use, so it must
        // survive a root-owned ~/.nuget on unprivileged build hosts. Enable the
        // shared self-heal for dotnet invocations (build/test/format); it is a
        // no-op on a healthy home and untouched for every other tool.
        var selfHealNuGetHome = argv.Length > 0 && string.Equals(argv[0], "dotnet", StringComparison.Ordinal);

        var inner = IsDotnetTestPass(language, name)
            ? (IAuditor)new DotnetTestAuditor(new DotnetTestAuditorOptions
            {
                Name = name,
                BaseArgv = argv,
                CanShortCircuitOnBlockingFinding = canShortCircuitOnBlockingFinding,
                Role = role,
                BuildTestGateEvidence = gateEvidence,
                RunOptionsAccessor = testRunOptions,
                SelfHealNuGetHome = selfHealNuGetHome,
                TestFailureAttributionOptions = testFailureAttributionOptions,
            })
            : new ShellCommandAuditor(new ShellCommandAuditorOptions
            {
                Name = name,
                Argv = argv,
                // NuGet-home self-heal for dotnet build/format is applied via the
                // single SelfHealNuGetHome mechanism below (see the comment above);
                // it wraps the invocation in NuGetHomeSelfHeal so restore survives an
                // unusable ~/.nuget. This is the one NuGet-home heal source every
                // .NET gate shares.
                ResultClassifier = ResultClassifierFor(language, name, argv),
                MissingToolSeverity = missingToolSeverity,
                Required = required,
                CanShortCircuitOnBlockingFinding = canShortCircuitOnBlockingFinding,
                Role = role,
                BuildTestGateEvidence = gateEvidence,
                SelfHealNuGetHome = selfHealNuGetHome,
                TestFailureAttributionOptions = testFailureAttributionOptions,
            });

        return new LanguagePresetAuditor(language, markerDescription, markerScript, inner);
    }

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
        BuildTestGateEvidence gateEvidence = BuildTestGateEvidence.None,
        AuditSeverity? missingToolSeverity = null,
        AuditCapabilities required = AuditCapabilities.None)
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
                MissingToolSeverity = missingToolSeverity,
                Required = required,
                CanShortCircuitOnBlockingFinding = canShortCircuitOnBlockingFinding,
                Role = role,
                BuildTestGateEvidence = gateEvidence,
            }));

    private static bool IsDotnetTestPass(string language, string name)
        => string.Equals(language, "csharp", StringComparison.Ordinal)
           && string.Equals(name, "csharp:test-pass", StringComparison.Ordinal);

    private static IAuditResultClassifier? ResultClassifierFor(
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

        return null;
    }
}
