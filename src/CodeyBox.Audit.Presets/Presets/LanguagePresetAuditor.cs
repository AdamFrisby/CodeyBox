using CodeyBox.Core;

namespace CodeyBox.Audit.Presets.Presets;

internal sealed class LanguagePresetAuditor : IAuditor
{
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

        foreach (var projectDirectory in projectDirectories)
        {
            var result = await _inner.RunAsync(
                sandbox,
                ResolveWorkingDirectory(workingDirectory, projectDirectory),
                context,
                ct);

            passed &= result.Passed;
            allFindings.AddRange(result.Findings);
            if (!string.IsNullOrWhiteSpace(result.RawOutput))
                rawParts.Add($"## {projectDirectory}\n{result.RawOutput}");
        }

        return new AuditResult(passed, allFindings, RawOutput: string.Join("\n\n", rawParts));
    }

    private static IReadOnlyList<string> ParseProjectDirectories(string output)
        => output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string ResolveWorkingDirectory(string workingDirectory, string projectDirectory)
    {
        if (projectDirectory == ".")
            return workingDirectory;

        return workingDirectory.TrimEnd('/') + "/" + projectDirectory.TrimStart('.', '/');
    }
}
