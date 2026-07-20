using CodeyBox.Core;

namespace CodeyBox.Audit.Presets.Presets;

internal sealed class LanguagePresetAuditor : IAuditor, IShellAuditorArgvProvider, IAuditorLanguageContext, ITestRunnerAuditorProvider
{
    private const int MaxRawOutputChars = 1_000_000;

    private readonly string _language;
    private readonly string _markerDescription;
    private readonly string _markerScript;
    private readonly IAuditor _inner;

    public LanguagePresetAuditor(
        string language,
        string markerDescription,
        string markerScript,
        IAuditor inner)
    {
        _language = language;
        _markerDescription = markerDescription;
        _markerScript = markerScript;
        _inner = inner;
    }

    public string Name => _inner.Name;
    public string Kind => _inner.Kind;
    public AuditCapabilities Required => _inner.Required;
    public bool CanShortCircuitOnBlockingFinding => _inner.CanShortCircuitOnBlockingFinding;
    public string? SelfReviewGuidance => _inner.SelfReviewGuidance;
    public AuditorRole Role => _inner.Role;
    public BuildTestGateEvidence BuildTestGateEvidence => _inner.BuildTestGateEvidence;
    public IReadOnlyList<string> Argv => _inner is IShellAuditorArgvProvider provider ? provider.Argv : [];

    /// <summary>
    /// Exposes the wrapped test runner (e.g. <c>DotnetTestAuditor</c>) so the
    /// pipeline can read its test-specific idle-timeout preference through the
    /// multi-project wrapper without the wrapper itself claiming to be a test
    /// runner for non-test auditors (build/format/lint).
    /// </summary>
    public ITestRunnerAuditor? TestRunner => _inner as ITestRunnerAuditor;

    public string Language => _language;
    public string MarkerScript => _markerScript;

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        var discovery = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", _markerScript],
            WorkingDirectory = workingDirectory,
        }, ct);

        if (discovery.ExitCode != 0)
            return new AuditResult(false, [new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: $"{_language} preset discovery failed; treating as blocking",
                Description: $"The {_language} preset could not discover project marker files. Discovery exited with code {discovery.ExitCode}. Stderr: {discovery.Stderr}")]);

        var projectDirectories = LanguagePresetProjectDiscovery.ParseProjectDirectories(discovery.Stdout);
        if (projectDirectories.Count > 0)
            return await RunInnerForProjectDirectoriesAsync(sandbox, workingDirectory, context, projectDirectories, ct);

        var gateSkipped = Role == AuditorRole.BuildTestGate;
        return new AuditResult(!gateSkipped, [new AuditFinding(
            AuditorName: Name,
            Severity: gateSkipped ? AuditSeverity.Error : AuditSeverity.Info,
            Title: $"{_language} preset enabled but no {_markerDescription} found; skipping",
            Description: $"The project declares language '{_language}', but no {_markerDescription} marker file was present in the work tree.")]);
    }

    private async Task<AuditResult> RunInnerForProjectDirectoriesAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        IReadOnlyList<string> projectDirectories,
        CancellationToken ct)
    {
        var allFindings = new List<AuditFinding>();
        var testFailureAttributions = new List<TestFailureAttributionResult>();
        var rawParts = new List<string>();
        var passed = true;
        var rawOutputChars = 0;
        var rawOutputTruncated = false;
        bool? buildTestGateEvidenceVerified = null;

        var projectDirectoriesToRun = LanguageProjectDiscovery.SelectProjectDirectoriesToRun(
            _language,
            projectDirectories,
            out var skippedDueToLimit);

        foreach (var projectDirectory in projectDirectoriesToRun)
        {
            var result = await _inner.RunAsync(
                sandbox,
                LanguagePresetProjectDiscovery.ResolveWorkingDirectory(workingDirectory, projectDirectory),
                context,
                ct);

            passed &= result.Passed ||
                (result.Findings.Count > 0 && result.Findings.All(f => f.Severity != AuditSeverity.Error));
            buildTestGateEvidenceVerified = MergeBuildTestGateEvidenceVerified(
                buildTestGateEvidenceVerified,
                result.BuildTestGateEvidenceVerified);
            allFindings.AddRange(result.Findings);
            testFailureAttributions.AddRange(result.TestFailureAttributions);
            if (!string.IsNullOrWhiteSpace(result.RawOutput))
                AppendRawPart(rawParts, $"## {projectDirectory}\n{result.RawOutput}", ref rawOutputChars, ref rawOutputTruncated);
        }

        if (skippedDueToLimit > 0)
        {
            passed = false;
            allFindings.Add(new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: $"{_language} project directory limit reached",
                Description: $"Discovery found {projectDirectories.Count} {_language} project directories. Only the first {LanguageProjectDiscovery.MaxProjectDirectoriesToRun} were audited and {skippedDueToLimit} were skipped to prevent repository-controlled marker files from exhausting audit workers."));
        }

        if (rawOutputTruncated)
            allFindings.Add(new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Info,
                Title: $"{_language} preset raw output truncated",
                Description: $"Combined raw output exceeded {MaxRawOutputChars} characters and was truncated before storage."));

        return new AuditResult(
            passed,
            allFindings,
            RawOutput: string.Join("\n\n", rawParts),
            AgentStderr: null,
            AgentSummary: null,
            AgentStdout: null,
            TestFailureAttributions: testFailureAttributions)
        {
            BuildTestGateEvidenceVerified = buildTestGateEvidenceVerified,
        };
    }

    private static bool? MergeBuildTestGateEvidenceVerified(bool? current, bool? next)
    {
        if (current == false || next == false)
            return false;
        if (current == true || next == true)
            return true;
        return null;
    }

    private static void AppendRawPart(
        List<string> rawParts,
        string rawPart,
        ref int rawOutputChars,
        ref bool rawOutputTruncated)
    {
        if (rawOutputTruncated)
            return;

        var remaining = MaxRawOutputChars - rawOutputChars;
        if (remaining <= 0)
        {
            rawOutputTruncated = true;
            return;
        }

        if (rawPart.Length > remaining)
        {
            rawParts.Add(rawPart[..remaining]);
            rawOutputChars += remaining;
            rawOutputTruncated = true;
            return;
        }

        rawParts.Add(rawPart);
        rawOutputChars += rawPart.Length;
    }
}

internal static class LanguagePresetProjectDiscovery
{
    public static IReadOnlyList<string> ParseProjectDirectories(string output)
        => output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static string ResolveWorkingDirectory(string workingDirectory, string projectDirectory)
    {
        if (projectDirectory == "." || string.IsNullOrWhiteSpace(projectDirectory))
            return workingDirectory;

        var relativeProjectDirectory = projectDirectory.Replace('\\', '/').Trim();
        if (relativeProjectDirectory.StartsWith("./", StringComparison.Ordinal))
            relativeProjectDirectory = relativeProjectDirectory[2..];

        if (string.IsNullOrWhiteSpace(relativeProjectDirectory) || relativeProjectDirectory == ".")
            return workingDirectory;

        if (relativeProjectDirectory.Contains("..", StringComparison.Ordinal) ||
            relativeProjectDirectory.StartsWith("/", StringComparison.Ordinal) ||
            LooksLikeWindowsRootedPath(relativeProjectDirectory) ||
            Path.IsPathRooted(relativeProjectDirectory))
        {
            throw new InvalidOperationException(
                $"Language preset discovery returned an unsafe project directory: '{projectDirectory}'. Project directories must be relative and stay within the repository root.");
        }

        relativeProjectDirectory = relativeProjectDirectory.Trim('/');
        if (string.IsNullOrWhiteSpace(relativeProjectDirectory) || relativeProjectDirectory == ".")
            return workingDirectory;

        return workingDirectory.TrimEnd('/') + "/" + relativeProjectDirectory;
    }

    private static bool LooksLikeWindowsRootedPath(string path)
        => path.Length >= 3 &&
           char.IsLetter(path[0]) &&
           path[1] == ':' &&
           path[2] == '/';
}
