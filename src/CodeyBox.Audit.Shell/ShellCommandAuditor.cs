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

        if (result.Success)
            return new AuditResult(true, [], RawOutput: result.Stdout);

        var description = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;

        // Exit 127 means the shell couldn't find the command — i.e. the tool
        // isn't installed in the audit sandbox. That's an operator-level
        // configuration gap, not something the agent can fix by editing
        // code. Emit it as an INFO finding so it shows up in the report
        // (operator should install the tool and re-run audit) but doesn't
        // gate merge or trigger an unfixable rework loop.
        var severity = result.ExitCode == 127
            ? AuditSeverity.Info
            : AuditSeverity.Error;
        var title = result.ExitCode == 127
            ? $"tool not installed in sandbox: {_opts.Argv[0]} (auditor skipped — install the tool in MultipassExtraRuncmd)"
            : $"command exited {result.ExitCode}: {string.Join(' ', _opts.Argv)}";

        var finding = new AuditFinding(
            AuditorName: Name,
            Severity: severity,
            Title: title,
            Description: description.TrimEnd());
        var rawOutput = string.IsNullOrEmpty(result.Stderr)
            ? result.Stdout
            : result.Stderr + (string.IsNullOrEmpty(result.Stdout) ? "" : "\n" + result.Stdout);
        return new AuditResult(false, [finding], RawOutput: rawOutput);
    }
}

public sealed record ShellCommandAuditorOptions
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Argv { get; init; }
}
