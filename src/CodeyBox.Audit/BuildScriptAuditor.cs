using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Language-agnostic build gate driven by a project-owned repo-root
/// <c>build.sh</c>. Runs as a credential-free tool auditor; the project owns
/// what the script does, while CodeyBox only enforces the exit contract.
/// </summary>
public sealed class BuildScriptAuditor : IAuditor, IAuditSandboxIsolation
{
    public const string AuditorName = "process:build-script";
    public const int OutputCaptureMaxBytes = 1024 * 1024;

    private const int CommandCannotExecuteExitCode = 126;
    private const int CommandNotFoundExitCode = 127;
    private const int FindingOutputMaxChars = 16 * 1024;
    private const int UnavailableOutputMaxChars = 4 * 1024;

    private readonly Func<BuildScriptAuditorOptions> _optionsAccessor;

    public BuildScriptAuditor()
        : this(() => new BuildScriptAuditorOptions()) { }

    public BuildScriptAuditor(BuildScriptAuditorOptions options)
        : this(() => options) { }

    public BuildScriptAuditor(Func<BuildScriptAuditorOptions> optionsAccessor)
        => _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));

    public string Name => AuditorName;
    public string Kind => "shell";
    public AuditCapabilities Required => AuditCapabilities.None;
    public bool RequiresFreshSandbox => true;

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);

        var presence = await ExecOrUnavailableAsync(
            sandbox,
            new SandboxExec
            {
                Argv = ["sh", "-c", "test -f ./build.sh"],
                WorkingDirectory = workingDirectory,
            },
            "check for ./build.sh",
            ct);

        if (presence.ExitCode != 0)
        {
            if (presence.ExitCode != 1 || IsCouldNotExecute(presence) || IsProviderExecutionFailure(presence))
                throw UnavailableFromResult("could-not-verify: build.sh presence check could not run", presence);

            if (context.BuildScriptRequired)
                return MissingRequiredResult("This project requires a repo-root build.sh for the build-script audit gate, but none was present on the work branch.");

            if (await BaseBranchHasBuildScriptAsync(sandbox, workingDirectory, context.BaseBranch, ct))
                return MissingRequiredResult("The base branch contains a repo-root build.sh, but it was missing on the work branch. Restore the script or intentionally disable the build-script auditor in project configuration.");

            return MissingOptionalResult();
        }

        var timeout = ResolveTimeout();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        SandboxExecResult result;
        try
        {
            result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "./build.sh"],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = OutputCaptureMaxBytes,
                MaxStderrBytes = OutputCaptureMaxBytes,
            }, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new AuditUnavailableException(
                $"could-not-verify: build.sh timed out after {FormatTimeout(timeout)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AuditUnavailableException(
                $"could-not-verify: build.sh could not execute: {SingleLineSummary(ex.Message)}",
                ex);
        }

        var output = CombinedOutput(result);
        if (result.Success)
            return new AuditResult(true, [], RawOutput: output);

        if (IsProviderExecutionFailure(result))
            throw UnavailableFromResult("could-not-verify: build.sh could not execute", result);

        var description = result.OutputLimitExceeded
            ? $"build.sh output exceeded the per-stream capture limit ({OutputCaptureMaxBytes} bytes) and was terminated. Last observed exit code: {result.ExitCode}."
            : $"build.sh exited with code {result.ExitCode}.";
        if (!string.IsNullOrWhiteSpace(output))
            description += "\n\n" + Tail(output.TrimEnd(), FindingOutputMaxChars);

        return new AuditResult(false,
            [
                new AuditFinding(
                    AuditorName: Name,
                    Severity: AuditSeverity.Error,
                    Title: "build failed",
                    Description: description),
            ],
            RawOutput: output);
    }

    private static AuditResult MissingOptionalResult()
        => new(true, [], RawOutput: "build.sh absent; auditor skipped");

    private static AuditResult MissingRequiredResult(string description)
    {
        var finding = new AuditFinding(
            AuditorName,
            AuditSeverity.Error,
            "build.sh missing",
            description);
        return new AuditResult(false, [finding], RawOutput: "required build.sh missing");
    }

    private async Task<bool> BaseBranchHasBuildScriptAsync(
        ISandbox sandbox,
        string workingDirectory,
        string baseBranch,
        CancellationToken ct)
    {
        var baseRef = BaseBranchRef(baseBranch);
        var verifyRef = await ExecOrUnavailableAsync(
            sandbox,
            new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "rev-parse", "--verify", "--quiet", $"{baseRef}^{{commit}}"],
            },
            $"resolve base branch {baseRef}",
            ct);
        if (!verifyRef.Success)
            throw UnavailableFromResult($"could-not-verify: base branch {baseRef} could not be resolved", verifyRef);

        var basePresence = await ExecOrUnavailableAsync(
            sandbox,
            new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "cat-file", "-e", $"{baseRef}:build.sh"],
            },
            $"check base branch {baseRef} for build.sh",
            ct);
        if (basePresence.ExitCode == 0)
            return true;
        if (basePresence.ExitCode == 128 && !IsProviderExecutionFailure(basePresence))
            return false;

        throw UnavailableFromResult($"could-not-verify: base branch {baseRef} build.sh presence check could not run", basePresence);
    }

    private async Task<SandboxExecResult> ExecOrUnavailableAsync(
        ISandbox sandbox,
        SandboxExec exec,
        string operation,
        CancellationToken ct)
    {
        try
        {
            return await sandbox.ExecAsync(exec, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AuditUnavailableException(
                $"could-not-verify: {operation} failed: {SingleLineSummary(ex.Message)}",
                ex);
        }
    }

    private TimeSpan ResolveTimeout()
    {
        var options = _optionsAccessor() ?? new BuildScriptAuditorOptions();
        var seconds = options.TimeoutSeconds <= 0
            ? BuildScriptAuditorOptions.DefaultTimeoutSeconds
            : options.TimeoutSeconds;
        return TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    private static bool IsCouldNotExecute(SandboxExecResult result)
        => result.ExitCode is CommandCannotExecuteExitCode or CommandNotFoundExitCode;

    private static bool IsProviderExecutionFailure(SandboxExecResult result)
    {
        if (result.ExitCode == 0)
            return false;

        var stderr = result.Stderr ?? string.Empty;
        return stderr.StartsWith("multipass transient daemon error after ", StringComparison.Ordinal)
            || stderr.StartsWith("multipass daemon unreachable after ", StringComparison.Ordinal);
    }

    private static AuditUnavailableException UnavailableFromResult(
        string reason,
        SandboxExecResult result)
    {
        var output = CombinedOutput(result);
        var detail = string.IsNullOrWhiteSpace(output)
            ? string.Empty
            : $": {Tail(output.TrimEnd(), UnavailableOutputMaxChars)}";
        return new AuditUnavailableException(
            $"{reason} (exit {result.ExitCode}){detail}",
            result.ExitCode,
            output);
    }

    private static string CombinedOutput(SandboxExecResult result)
    {
        var stdout = result.Stdout;
        if (result.StdoutLimitExceeded)
            stdout += $"\n[stdout truncated after {OutputCaptureMaxBytes} bytes]";
        var stderr = result.Stderr;
        if (result.StderrLimitExceeded)
            stderr += $"\n[stderr truncated after {OutputCaptureMaxBytes} bytes]";

        if (string.IsNullOrEmpty(stderr)) return stdout;
        if (string.IsNullOrEmpty(stdout)) return stderr;
        return stdout + "\n" + stderr;
    }

    private static string BaseBranchRef(string baseBranch)
    {
        var branch = baseBranch.Trim();
        if (branch.StartsWith("refs/remotes/", StringComparison.Ordinal))
            return branch;
        if (branch.StartsWith("refs/heads/", StringComparison.Ordinal))
            branch = branch["refs/heads/".Length..];
        if (branch.StartsWith("origin/", StringComparison.Ordinal))
            return $"refs/remotes/{branch}";
        return $"refs/remotes/origin/{branch}";
    }

    private static string Tail(string text, int maxChars)
        => text.Length <= maxChars ? text : text[^maxChars..];

    private static string SingleLineSummary(string message)
        => message.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    private static string FormatTimeout(TimeSpan timeout)
        => timeout.TotalMinutes >= 1
            ? $"{timeout.TotalMinutes:0.##} minutes"
            : $"{timeout.TotalSeconds:0.##} seconds";
}

public sealed class BuildScriptAuditorOptions
{
    public const int DefaultTimeoutSeconds = 30 * 60;

    /// <summary>
    /// Per-invocation ceiling for <c>./build.sh</c>. Defaults to 30 minutes.
    /// Read at run time by the auditor so config reloads affect later audits.
    /// </summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
}
