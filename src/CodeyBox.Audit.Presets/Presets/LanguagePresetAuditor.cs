using CodeyBox.Core;

namespace CodeyBox.Audit.Presets.Presets;

internal sealed class LanguagePresetAuditor : IAuditor
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

        var projectDirectories = ParseProjectDirectories(discovery.Stdout);
        if (projectDirectories.Count > 0)
            return await RunInnerForProjectDirectoriesAsync(sandbox, workingDirectory, context, projectDirectories, ct);

        return new AuditResult(true, [new AuditFinding(
            AuditorName: Name,
            Severity: AuditSeverity.Info,
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
        var rawParts = new List<string>();
        var passed = true;
        var rawOutputChars = 0;
        var rawOutputTruncated = false;

        var projectDirectoriesToRun = LanguageProjectDiscovery.SelectProjectDirectoriesToRun(
            _language,
            projectDirectories,
            out var skippedDueToLimit);

        if (skippedDueToLimit > 0)
            allFindings.Add(new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Info,
                Title: $"{_language} preset project directory limit reached",
                Description: $"Discovered {projectDirectories.Count} {_language} project directories; running the first {projectDirectoriesToRun.Count} to keep audit execution bounded. Skipped {skippedDueToLimit}."));

        foreach (var projectDirectory in projectDirectoriesToRun)
        {
            var result = await _inner.RunAsync(
                sandbox,
                ResolveWorkingDirectory(workingDirectory, projectDirectory),
                context,
                ct);

            passed &= result.Passed;
            allFindings.AddRange(result.Findings);
            if (!string.IsNullOrWhiteSpace(result.RawOutput))
                AppendRawPart(rawParts, $"## {projectDirectory}\n{result.RawOutput}", ref rawOutputChars, ref rawOutputTruncated);
        }

        if (rawOutputTruncated)
            allFindings.Add(new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Info,
                Title: $"{_language} preset raw output truncated",
                Description: $"Combined raw output exceeded {MaxRawOutputChars} characters and was truncated before storage."));

        return new AuditResult(passed, allFindings, RawOutput: string.Join("\n\n", rawParts));
    }

    private static IReadOnlyList<string> ParseProjectDirectories(string output)
        => output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

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

    private static string ResolveWorkingDirectory(string workingDirectory, string projectDirectory)
    {
        if (projectDirectory == ".")
            return workingDirectory;

        return workingDirectory.TrimEnd('/') + "/" + projectDirectory.TrimStart('.', '/');
    }
}
