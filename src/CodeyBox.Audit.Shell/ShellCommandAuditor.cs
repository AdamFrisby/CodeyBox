using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

/// <summary>
/// Audits the working tree by running an arbitrary command inside the
/// sandbox. Most commands follow the shell-style "exit 0 = good" contract:
/// exit code 0 passes, and non-zero fails with stdout/stderr captured as a
/// single Error finding. If the top-level tool is confirmed missing before
/// the command runs, the auditor emits a non-blocking Info finding instead.
/// A command-specific result classifier can refine non-zero exits without
/// making this generic shell runner aware of language- or tool-specific
/// output formats.
///
/// Does NOT need agent credentials, so it runs in the credential-free audit
/// sandbox. Operators concerned about a malicious build script reaching
/// agent secrets should keep their checks in this auditor type.
/// </summary>
public sealed class ShellCommandAuditor : IAuditor, IShellAuditorArgvProvider
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
    public bool CanShortCircuitOnBlockingFinding => _opts.CanShortCircuitOnBlockingFinding;

    public string? SelfReviewGuidance
    {
        get
        {
            if (Name.Contains("build", StringComparison.OrdinalIgnoreCase) ||
                Name.Contains("format", StringComparison.OrdinalIgnoreCase))
            {
                return "run build (warnings-as-errors) + formatter before committing";
            }
            return null;
        }
    }


    /// <summary>
    /// The argv this auditor invokes. Exposed so the work-phase prompt builder
    /// can advise the agent to run these checks itself before committing,
    /// pre-empting iter-1 mechanical findings (format, lint, build-WaE).
    /// </summary>
    public IReadOnlyList<string> Argv => _opts.Argv;

    public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        var toolName = _opts.ToolName ?? _opts.Argv[0];
        if (await IsDirectToolMissingAsync(sandbox, workingDirectory, toolName, ct))
            return MissingToolResult(toolName, string.Empty);

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

        var finding = BuildCommandFinding(result, toolName);
        if (_opts.ResultClassifier is not null)
        {
            var classified = _opts.ResultClassifier.ClassifyFailedCommand(new ShellCommandResultContext(
                Name,
                _opts.Argv,
                result,
                combinedOutput,
                finding));
            if (classified is not null)
                return classified;
        }

        return new AuditResult(false, [finding], RawOutput: combinedOutput);
    }

    private AuditFinding BuildCommandFinding(SandboxExecResult result, string toolName)
    {
        var description = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;

        // Exit 127 is only non-blocking when it is confirmed to be the
        // auditor's tool missing from the sandbox. Some tools, notably npm,
        // propagate exit 127 from repository-controlled scripts; those remain
        // blocking command failures.
        var missingTool = IsConfirmedMissingTopLevelTool(result);
        var severity = missingTool
            ? AuditSeverity.Info
            : AuditSeverity.Error;
        var title = missingTool
            ? $"tool not installed in sandbox: {toolName} (auditor skipped — install the tool in MultipassExtraRuncmd)"
            : $"command exited {result.ExitCode}: {string.Join(' ', _opts.Argv)}";

        return new AuditFinding(
            AuditorName: Name,
            Severity: severity,
            Title: title,
            Description: description.TrimEnd());
    }

    private async Task<bool> IsDirectToolMissingAsync(
        ISandbox sandbox,
        string workingDirectory,
        string toolName,
        CancellationToken ct)
    {
        if (_opts.TreatExit127AsMissingTool is not null || string.IsNullOrWhiteSpace(toolName))
            return false;

        var probe = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "command -v \"$1\" >/dev/null 2>&1", "sh", toolName],
            WorkingDirectory = workingDirectory,
        }, ct);

        return probe.ExitCode != 0;
    }

    private bool IsConfirmedMissingTopLevelTool(SandboxExecResult result)
    {
        return result.ExitCode == 127 && _opts.TreatExit127AsMissingTool == true;
    }

    private AuditResult MissingToolResult(string toolName, string rawOutput)
    {
        var finding = new AuditFinding(
            AuditorName: Name,
            Severity: AuditSeverity.Info,
            Title: $"tool not installed in sandbox: {toolName} (auditor skipped — install the tool in MultipassExtraRuncmd)",
            Description: $"The auditor command was not run because '{toolName}' is not available in the audit sandbox.");
        return new AuditResult(false, [finding], RawOutput: rawOutput);
    }
}

public sealed record ShellCommandAuditorOptions
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Argv { get; init; }
    public string? ToolName { get; init; }
    public bool? TreatExit127AsMissingTool { get; init; }
    public IShellCommandResultClassifier? ResultClassifier { get; init; }
    public bool CanShortCircuitOnBlockingFinding { get; init; }
}
