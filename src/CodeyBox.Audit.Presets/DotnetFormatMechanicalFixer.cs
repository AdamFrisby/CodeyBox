using System.Text;
using CodeyBox.Core;
using CodeyBox.Audit.Presets.Presets;

namespace CodeyBox.Audit.Presets;

/// <summary>
/// Mechanical counterpart to the <c>csharp:format-check</c> auditor. It consumes
/// the preset-owned dotnet-format input prepared from the active C# format-check
/// auditor, removing only flags that make the command read-only.
/// </summary>
public sealed class DotnetFormatMechanicalFixer : IMechanicalFixer
{
    public const string FixerName = MechanicalFixerNames.DotnetFormat;
    internal const string FormatCheckAuditorName = "csharp:format-check";
    public const int OutputCaptureMaxBytes = 1024 * 1024;
    private const int MaxRawOutputChars = 16_000;
    private const int MaxExceptionOutputChars = 1_000;
    private const string TruncationMarker = "\n... output truncated.";

    public string Name => FixerName;
    public string Kind => "shell";
    public string CommitSubject => "chore: normalize (dotnet format)";

    public async Task<MechanicalFixerResult> ApplyAsync(
        ISandbox sandbox,
        string workingDirectory,
        MechanicalFixerContext context,
        CancellationToken ct = default)
    {
        var command = context.FindInput<DotnetFormatMechanicalFixerInput>();
        if (command is null || command.FormatCheckArgv.Count == 0)
        {
            return new MechanicalFixerResult(
                Changed: false,
                Summary: $"{FormatCheckAuditorName} is not active; {FixerName} skipped");
        }

        if (!TryToFixerArgv(command.FormatCheckArgv, out var formatArgv))
        {
            return new MechanicalFixerResult(
                Changed: false,
                Summary: $"{FormatCheckAuditorName} does not expose a writable dotnet-format command; {FixerName} skipped");
        }

        var before = await GitStatusAsync(sandbox, workingDirectory, ct);
        var beforePatch = await GitTrackedDiffAsync(sandbox, workingDirectory, ct);

        var discovery = await DiscoverProjectDirectoriesAsync(
            sandbox,
            workingDirectory,
            command.ProjectMarkerScript,
            ct);
        await EnsureMarkerDiscoveryDidNotModifyTrackedFilesAsync(
            sandbox,
            workingDirectory,
            beforePatch,
            ct);

        if (discovery.FailureSummary is not null)
        {
            return new MechanicalFixerResult(
                Changed: false,
                Summary: discovery.FailureSummary,
                RawOutput: discovery.RawOutput);
        }

        var projectDirectories = discovery.ProjectDirectories;

        if (projectDirectories.Count == 0)
        {
            return new MechanicalFixerResult(
                Changed: false,
                Summary: "no C# project marker found; dotnet format skipped");
        }

        var selectedDirectories = LanguageProjectDiscovery.SelectProjectDirectoriesToRun(
            "csharp",
            projectDirectories,
            out var skippedDueToLimit);
        if (skippedDueToLimit > 0)
        {
            return new MechanicalFixerResult(
                Changed: false,
                Summary: $"dotnet-format found {projectDirectories.Count} C# project directories and skipped normalization so {FormatCheckAuditorName} can report the preset directory cap");
        }

        var output = new StringBuilder();
        foreach (var projectDirectory in selectedDirectories)
        {
            var projectWorkingDirectory = LanguagePresetProjectDiscovery.ResolveWorkingDirectory(
                workingDirectory,
                projectDirectory);
            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = formatArgv,
                WorkingDirectory = projectWorkingDirectory,
                MaxStdoutBytes = OutputCaptureMaxBytes,
                MaxStderrBytes = OutputCaptureMaxBytes,
                KillOnOutputLimit = false,
            }, ct);

            AppendBoundedOutput(output, CombinedOutput(result));

            if (result.ExecutionUnavailable)
            {
                await RestorePreFixerTrackedChangesAsync(sandbox, workingDirectory, beforePatch, ct);
                throw new InvalidOperationException(
                    $"dotnet-format fixer could not execute formatter command in {projectDirectory}: {ExceptionSafeOutput(result)}");
            }

            if (result.ExitCode != 0)
            {
                await RestorePreFixerTrackedChangesAsync(sandbox, workingDirectory, beforePatch, ct);
                return new MechanicalFixerResult(
                    Changed: false,
                    Summary: $"dotnet format exited {result.ExitCode} in {projectDirectory}; skipped normalization so audit can report the project error",
                    RawOutput: BoundedOutput(output.ToString()));
            }
        }

        var after = await GitStatusAsync(sandbox, workingDirectory, ct);
        var afterPatch = await GitTrackedDiffAsync(sandbox, workingDirectory, ct);
        var changed = !string.Equals(before, after, StringComparison.Ordinal) ||
                      !string.Equals(beforePatch, afterPatch, StringComparison.Ordinal);
        return new MechanicalFixerResult(
            Changed: changed,
            Summary: changed
                ? $"dotnet format normalized {selectedDirectories.Count} C# project director{(selectedDirectories.Count == 1 ? "y" : "ies")}"
                : "dotnet format made no changes",
            RawOutput: output.Length == 0 ? null : output.ToString());
    }

    public static IReadOnlyList<string> ToFixerArgv(IReadOnlyList<string> formatCheckArgv)
    {
        if (TryToFixerArgv(formatCheckArgv, out var argv))
            return argv;

        throw new InvalidOperationException(
            $"{FormatCheckAuditorName} must invoke 'dotnet format' directly for the {FixerName} fixer to reuse it.");
    }

    public static bool TryToFixerArgv(
        IReadOnlyList<string> formatCheckArgv,
        out IReadOnlyList<string> fixerArgv)
    {
        if (formatCheckArgv.Count == 0)
        {
            fixerArgv = [];
            return false;
        }

        if (IsLiteralDotnetFormatCommand(formatCheckArgv))
        {
            fixerArgv = StripReadOnlyFormatArgs(formatCheckArgv, out _);
            return true;
        }

        fixerArgv = [];
        return false;
    }

    private static IReadOnlyList<string> StripReadOnlyFormatArgs(
        IReadOnlyList<string> formatCheckArgv,
        out bool removedReadOnlyFlag)
    {
        var argv = new List<string>(formatCheckArgv.Count);
        removedReadOnlyFlag = false;
        for (var i = 0; i < formatCheckArgv.Count; i++)
        {
            var arg = formatCheckArgv[i];
            if (arg.Equals("--verify-no-changes", StringComparison.OrdinalIgnoreCase))
            {
                removedReadOnlyFlag = true;
                continue;
            }

            if (arg.Equals("--report", StringComparison.OrdinalIgnoreCase))
            {
                removedReadOnlyFlag = true;
                i++;
                continue;
            }

            if (arg.StartsWith("--report=", StringComparison.OrdinalIgnoreCase))
            {
                removedReadOnlyFlag = true;
                continue;
            }

            argv.Add(arg);
        }

        return argv;
    }

    private static bool IsLiteralDotnetFormatCommand(IReadOnlyList<string> argv)
        => argv.Count >= 2 &&
           IsDotnetExecutable(argv[0]) &&
           argv[1].Equals("format", StringComparison.OrdinalIgnoreCase);

    private static bool IsDotnetExecutable(string command)
    {
        var fileName = Path.GetFileName(command);
        return fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ProjectDirectoryDiscovery> DiscoverProjectDirectoriesAsync(
        ISandbox sandbox,
        string workingDirectory,
        string? markerScript,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(markerScript))
            return new ProjectDirectoryDiscovery(["."]);

        var discovery = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", markerScript],
            WorkingDirectory = workingDirectory,
            MaxStdoutBytes = OutputCaptureMaxBytes,
            MaxStderrBytes = OutputCaptureMaxBytes,
            KillOnOutputLimit = false,
        }, ct);

        if (discovery.OutputLimitExceeded)
        {
            var rawOutput = BoundedOutput(CombinedOutput(discovery));
            return new ProjectDirectoryDiscovery(
                [],
                $"dotnet-format marker discovery exceeded the {OutputCaptureMaxBytes} byte output cap; skipped normalization so {FormatCheckAuditorName} can report the discovery failure",
                rawOutput);
        }

        if (discovery.ExitCode != 0)
        {
            var rawOutput = BoundedOutput(CombinedOutput(discovery));
            return new ProjectDirectoryDiscovery(
                [],
                $"dotnet-format marker discovery exited {discovery.ExitCode}; skipped normalization so {FormatCheckAuditorName} can report the discovery failure",
                rawOutput);
        }

        return new ProjectDirectoryDiscovery(
            LanguagePresetProjectDiscovery.ParseProjectDirectories(discovery.Stdout));
    }

    private static async Task<string> GitStatusAsync(
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", workingDirectory, "status", "--porcelain=v1", "-z", "--untracked-files=no"],
        }, ct);
        if (!result.Success)
            throw new InvalidOperationException(
                $"failed to read git status for mechanical fixer: {ExceptionSafeOutput(result)}");
        return result.Stdout;
    }

    private static async Task EnsureMarkerDiscoveryDidNotModifyTrackedFilesAsync(
        ISandbox sandbox,
        string workingDirectory,
        string beforePatch,
        CancellationToken ct)
    {
        var afterPatch = await GitTrackedDiffAsync(sandbox, workingDirectory, ct);
        if (string.Equals(beforePatch, afterPatch, StringComparison.Ordinal))
        {
            return;
        }

        await RestorePreFixerTrackedChangesAsync(sandbox, workingDirectory, beforePatch, ct);
        throw new InvalidOperationException(
            "dotnet-format marker discovery modified tracked files; rolled back marker side effects and skipped normalization");
    }

    private static async Task<string> GitTrackedDiffAsync(
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", workingDirectory, "diff", "--binary", "--full-index", "HEAD", "--"],
            MaxStdoutBytes = MechanicalEditLimits.PatchCaptureMaxBytes,
            MaxStderrBytes = MechanicalEditLimits.GitDiagnosticCaptureMaxBytes,
        }, ct);
        if (result.OutputLimitExceeded)
            throw new InvalidOperationException(
                $"tracked diff for mechanical fixer exceeded the {MechanicalEditLimits.PatchCaptureMaxBytes} byte output cap; skipped normalization");
        if (!result.Success)
            throw new InvalidOperationException(
                $"failed to read git diff for mechanical fixer: {ExceptionSafeOutput(result)}");
        return result.Stdout;
    }

    private static async Task RestorePreFixerTrackedChangesAsync(
        ISandbox sandbox,
        string workingDirectory,
        string beforePatch,
        CancellationToken ct)
    {
        // Reset the formatter's partial output, then reapply the tracked diff
        // that existed before this fixer ran so earlier fixers keep their edits.
        var reset = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", workingDirectory, "reset", "--hard", "HEAD"],
        }, ct);
        if (!reset.Success)
        {
            throw new InvalidOperationException(
                $"dotnet-format fixer could not discard partial changes after command failure: {ExceptionSafeOutput(reset)}");
        }

        if (string.IsNullOrWhiteSpace(beforePatch))
            return;

        const string patchPath = "/tmp/codeybox-dotnet-format-before.patch";
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "cat > \"$0\"", patchPath],
            Stdin = beforePatch,
        }, ct);
        if (!write.Success)
        {
            throw new InvalidOperationException(
                $"dotnet-format fixer could not materialize pre-existing changes after command failure: {ExceptionSafeOutput(write)}");
        }

        var apply = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", workingDirectory, "apply", "--whitespace=nowarn", patchPath],
        }, ct);
        if (!apply.Success)
        {
            throw new InvalidOperationException(
                $"dotnet-format fixer could not restore pre-existing changes after command failure: {ExceptionSafeOutput(apply)}");
        }
    }

    private static string CombinedOutput(SandboxExecResult result)
    {
        var stdout = result.Stdout;
        if (result.StdoutLimitExceeded)
            stdout += $"\n[stdout truncated after {OutputCaptureMaxBytes} bytes]";
        var stderr = result.Stderr;
        if (result.StderrLimitExceeded)
            stderr += $"\n[stderr truncated after {OutputCaptureMaxBytes} bytes]";

        if (string.IsNullOrWhiteSpace(stderr))
            return stdout;
        if (string.IsNullOrWhiteSpace(stdout))
            return stderr;
        return stdout + "\n" + stderr;
    }

    private static void AppendBoundedOutput(StringBuilder output, string rawPart)
    {
        if (string.IsNullOrWhiteSpace(rawPart) || output.Length >= MaxRawOutputChars)
            return;

        rawPart = rawPart.TrimEnd();
        var separatorLength = output.Length == 0 ? 0 : Environment.NewLine.Length;
        var remaining = MaxRawOutputChars - output.Length - separatorLength;
        if (remaining <= 0)
            return;

        if (separatorLength > 0)
            output.AppendLine();
        if (rawPart.Length <= remaining)
        {
            output.Append(rawPart);
            return;
        }

        var contentChars = Math.Max(0, remaining - TruncationMarker.Length);
        if (contentChars > 0)
            output.Append(rawPart.AsSpan(0, contentChars));
        output.Append(TruncationMarker.AsSpan(0, Math.Min(TruncationMarker.Length, remaining - contentChars)));
    }

    private static string BoundedOutput(string output)
    {
        output = output.TrimEnd();
        return output.Length <= MaxRawOutputChars
            ? output
            : output[..Math.Max(0, MaxRawOutputChars - TruncationMarker.Length)] + TruncationMarker;
    }

    private static string ExceptionSafeOutput(SandboxExecResult result)
    {
        var output = CombinedOutput(result)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\n', ' ')
            .Trim();
        if (string.IsNullOrEmpty(output))
            return "(no output)";
        return output.Length <= MaxExceptionOutputChars
            ? output
            : output[..MaxExceptionOutputChars] + " ... output truncated.";
    }

    private sealed record ProjectDirectoryDiscovery(
        IReadOnlyList<string> ProjectDirectories,
        string? FailureSummary = null,
        string? RawOutput = null);
}
