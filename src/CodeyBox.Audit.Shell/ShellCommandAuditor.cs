using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

/// <summary>
/// Audits the working tree by running an arbitrary command inside the
/// sandbox. Exit code 0 → pass; non-zero → fail with stdout/stderr captured
/// as a single Error finding. Use for linters, formatters, type-checkers,
/// SAST tools — anything with a shell-style "exit 0 = good" contract.
///
/// Does NOT need agent credentials, so it runs in the credential-free audit
/// sandbox. Operators concerned about a malicious build script reaching
/// agent secrets should keep their checks in this auditor type.
/// </summary>
public sealed class ShellCommandAuditor : IAuditor
{
    private readonly ShellCommandAuditorOptions _opts;

    public ShellCommandAuditor(ShellCommandAuditorOptions opts)
    {
        if (opts.Argv.Count == 0) throw new ArgumentException("Argv must be non-empty", nameof(opts));
        _opts = opts;
    }

    public string Name => _opts.Name;
    public string Kind => "shell";
    public AuditCapabilities Required => AuditCapabilities.None;

    public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = _opts.Argv,
            WorkingDirectory = workingDirectory,
        }, ct);

        var combinedOutput = string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stdout
            : result.Stdout + "\n" + result.Stderr;

        if (result.Success)
            return new AuditResult(true, [], RawOutput: combinedOutput);

        var description = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;

        // Exit 127 is only non-blocking when it is confirmed to be the
        // auditor's tool missing from the sandbox. Some tools, notably
        // npm, propagate exit 127 from repository-controlled scripts; those
        // remain blocking command failures.
        var missingTool = IsConfirmedMissingTopLevelTool(result, combinedOutput);
        var severity = missingTool
            ? AuditSeverity.Info
            : AuditSeverity.Error;
        var toolName = _opts.ToolName ?? _opts.Argv[0];
        var title = missingTool
            ? $"tool not installed in sandbox: {toolName} (auditor skipped — install the tool in MultipassExtraRuncmd)"
            : $"command exited {result.ExitCode}: {string.Join(' ', _opts.Argv)}";

        var finding = new AuditFinding(
            AuditorName: Name,
            Severity: severity,
            Title: title,
            Description: description.TrimEnd());
        return new AuditResult(false, [finding], RawOutput: combinedOutput);
    }

    private bool IsConfirmedMissingTopLevelTool(SandboxExecResult result, string combinedOutput)
    {
        if (result.ExitCode != 127)
            return false;

        if (_opts.TreatExit127AsMissingTool is not null)
            return _opts.TreatExit127AsMissingTool.Value;

        var toolName = _opts.ToolName ?? _opts.Argv[0];
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        var output = combinedOutput.Trim();
        return output.Contains($"{toolName}: not found", StringComparison.OrdinalIgnoreCase) ||
               output.Contains($"{toolName}: command not found", StringComparison.OrdinalIgnoreCase) ||
               output.Contains($"exec: \"{toolName}\"", StringComparison.OrdinalIgnoreCase) ||
               output.Contains($"executable file not found", StringComparison.OrdinalIgnoreCase) && output.Contains(toolName, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ShellCommandAuditorOptions
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Argv { get; init; }
    public string? ToolName { get; init; }
    public bool? TreatExit127AsMissingTool { get; init; }
}
