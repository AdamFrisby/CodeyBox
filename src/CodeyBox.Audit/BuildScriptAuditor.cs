using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Language-agnostic build gate driven by a project-owned repo-root
/// <c>build.sh</c>. Runs as a credential-free tool auditor; the project owns
/// what the script does, while CodeyBox only enforces the exit contract.
/// </summary>
public sealed class BuildScriptAuditor : IAuditor
{
    public const string AuditorName = "process:build-script";

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
            if (presence.ExitCode != 1 || IsCouldNotExecute(presence))
                throw UnavailableFromResult("could-not-verify: build.sh presence check could not run", presence);

            return context.BuildScriptRequired
                ? MissingRequiredResult()
                : MissingOptionalResult();
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
            }, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new BuildScriptAuditUnavailableException(
                $"could-not-verify: build.sh timed out after {FormatTimeout(timeout)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new BuildScriptAuditUnavailableException(
                $"could-not-verify: build.sh could not execute: {SingleLineSummary(ex.Message)}",
                ex);
        }

        var output = CombinedOutput(result);
        if (result.Success)
            return new AuditResult(true, [], RawOutput: output);

        if (IsCouldNotExecute(result))
            throw UnavailableFromResult("could-not-verify: build.sh could not execute", result);

        var description = $"build.sh exited with code {result.ExitCode}.";
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

    private static AuditResult MissingRequiredResult()
    {
        var finding = new AuditFinding(
            AuditorName,
            AuditSeverity.Error,
            "build.sh missing",
            "This project requires a repo-root build.sh for the build-script audit gate, but none was present on the work branch.");
        return new AuditResult(false, [finding], RawOutput: "required build.sh missing");
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
            throw new BuildScriptAuditUnavailableException(
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

    private static BuildScriptAuditUnavailableException UnavailableFromResult(
        string reason,
        SandboxExecResult result)
    {
        var output = CombinedOutput(result);
        var detail = string.IsNullOrWhiteSpace(output)
            ? string.Empty
            : $": {Tail(output.TrimEnd(), UnavailableOutputMaxChars)}";
        return new BuildScriptAuditUnavailableException(
            $"{reason} (exit {result.ExitCode}){detail}",
            result.ExitCode,
            output);
    }

    private static string CombinedOutput(SandboxExecResult result)
    {
        if (string.IsNullOrEmpty(result.Stderr)) return result.Stdout;
        if (string.IsNullOrEmpty(result.Stdout)) return result.Stderr;
        return result.Stdout + "\n" + result.Stderr;
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

public sealed class BuildScriptAuditUnavailableException : Exception
{
    public BuildScriptAuditUnavailableException(string message)
        : base(message) { }

    public BuildScriptAuditUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }

    public BuildScriptAuditUnavailableException(string message, int exitCode, string output)
        : base(message)
    {
        ExitCode = exitCode;
        Output = output;
    }

    public int? ExitCode { get; }
    public string? Output { get; }
}
