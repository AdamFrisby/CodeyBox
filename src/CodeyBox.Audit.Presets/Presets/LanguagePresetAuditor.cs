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
        var marker = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", _markerScript],
            WorkingDirectory = workingDirectory,
        }, ct);

        if (marker.Success)
            return await _inner.RunAsync(sandbox, workingDirectory, context, ct);

        return new AuditResult(true, [new AuditFinding(
            AuditorName: Name,
            Severity: AuditSeverity.Info,
            Title: $"{_language} preset enabled but no {_markerDescription} found; skipping",
            Description: $"The project declares language '{_language}', but no {_markerDescription} marker file was present in the work tree.")]);
    }
}
