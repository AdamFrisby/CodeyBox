using System.Text;
using CodeyBox.Core;
using CodeyBox.Audit.Presets.Presets;

namespace CodeyBox.Audit.Presets;

/// <summary>
/// Mechanical counterpart to the <c>csharp:format-check</c> auditor. It reuses
/// the active C# format-check auditor's command and language marker discovery,
/// removing only flags that make the command read-only.
/// </summary>
public sealed class DotnetFormatMechanicalFixer : IMechanicalFixer
{
    public const string FixerName = MechanicalFixerNames.DotnetFormat;
    internal const string FormatCheckAuditorName = "csharp:format-check";
    private const int MaxRawOutputChars = 16_000;
    private const int MaxExceptionOutputChars = 1_000;

    public string Name => FixerName;
    public string Kind => "shell";

    public async Task<MechanicalFixerResult> ApplyAsync(
        ISandbox sandbox,
        string workingDirectory,
        MechanicalFixerContext context,
        CancellationToken ct = default)
    {
        var command = context.ShellCommands.FirstOrDefault(c =>
            c.Name.Equals(FormatCheckAuditorName, StringComparison.OrdinalIgnoreCase));
        if (command is null || command.Argv.Count == 0)
        {
            return new MechanicalFixerResult(
                Changed: false,
                Summary: $"{FormatCheckAuditorName} is not active; {FixerName} skipped");
        }

        if (!TryToFixerArgv(command.Argv, out var formatArgv))
        {
            return new MechanicalFixerResult(
                Changed: false,
                Summary: $"{FormatCheckAuditorName} does not invoke 'dotnet format'; {FixerName} skipped");
        }

        var discovery = await DiscoverProjectDirectoriesAsync(
            sandbox,
            workingDirectory,
            command.Metadata,
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

        var before = await GitStatusAsync(sandbox, workingDirectory, ct);
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
            }, ct);

            if (!string.IsNullOrWhiteSpace(result.Stdout))
                output.AppendLine(result.Stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(result.Stderr))
                output.AppendLine(result.Stderr.TrimEnd());

            if (!result.Success)
            {
                await DiscardTrackedChangesAsync(sandbox, workingDirectory, ct);
                return new MechanicalFixerResult(
                    Changed: false,
                    Summary: $"dotnet format exited {result.ExitCode} in {projectDirectory}; skipped normalization so audit can report the project error",
                    RawOutput: BoundedOutput(output.ToString()));
            }
        }

        var after = await GitStatusAsync(sandbox, workingDirectory, ct);
        var changed = !string.Equals(before, after, StringComparison.Ordinal);
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
            $"{FormatCheckAuditorName} must invoke 'dotnet format' for the {FixerName} fixer to reuse it.");
    }

    public static bool TryToFixerArgv(
        IReadOnlyList<string> formatCheckArgv,
        out IReadOnlyList<string> fixerArgv)
    {
        if (formatCheckArgv.Count < 2 ||
            !formatCheckArgv[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            !formatCheckArgv[1].Equals("format", StringComparison.OrdinalIgnoreCase))
        {
            fixerArgv = [];
            return false;
        }

        var argv = new List<string>(formatCheckArgv.Count);
        for (var i = 0; i < formatCheckArgv.Count; i++)
        {
            var arg = formatCheckArgv[i];
            if (arg.Equals("--verify-no-changes", StringComparison.OrdinalIgnoreCase))
                continue;
            if (arg.Equals("--report", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }
            if (arg.StartsWith("--report=", StringComparison.OrdinalIgnoreCase))
                continue;

            argv.Add(arg);
        }

        fixerArgv = argv;
        return true;
    }

    private static async Task<ProjectDirectoryDiscovery> DiscoverProjectDirectoriesAsync(
        ISandbox sandbox,
        string workingDirectory,
        ShellAuditorCommandMetadata? metadata,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(metadata?.MarkerScript))
            return new ProjectDirectoryDiscovery(["."]);

        var discovery = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", metadata.MarkerScript],
            WorkingDirectory = workingDirectory,
        }, ct);

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

    private static async Task DiscardTrackedChangesAsync(
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct)
    {
        var reset = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", workingDirectory, "reset", "--hard", "HEAD"],
        }, ct);
        if (!reset.Success)
        {
            throw new InvalidOperationException(
                $"dotnet-format fixer could not discard partial changes after command failure: {ExceptionSafeOutput(reset)}");
        }
    }

    private static string CombinedOutput(SandboxExecResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Stderr))
            return result.Stdout;
        if (string.IsNullOrWhiteSpace(result.Stdout))
            return result.Stderr;
        return result.Stdout + "\n" + result.Stderr;
    }

    private static string BoundedOutput(string output)
    {
        output = output.TrimEnd();
        return output.Length <= MaxRawOutputChars
            ? output
            : output[..MaxRawOutputChars] + "\n... output truncated.";
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
