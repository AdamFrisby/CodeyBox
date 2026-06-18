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
    public const string FixerName = "dotnet-format";
    internal const string FormatCheckAuditorName = "csharp:format-check";

    public string Name => FixerName;
    public string Kind => "shell";

    public async Task<MechanicalFixerResult> ApplyAsync(
        ISandbox sandbox,
        string workingDirectory,
        MechanicalFixerContext context,
        CancellationToken ct = default)
    {
        var auditor = context.Auditors.FirstOrDefault(a =>
            a.Name.Equals(FormatCheckAuditorName, StringComparison.OrdinalIgnoreCase));
        if (auditor is not IShellAuditorArgvProvider argvProvider || argvProvider.Argv.Count == 0)
        {
            return new MechanicalFixerResult(
                Changed: false,
                Summary: $"{FormatCheckAuditorName} is not active; {FixerName} skipped");
        }

        var formatArgv = ToFixerArgv(argvProvider.Argv);
        var projectDirectories = await DiscoverProjectDirectoriesAsync(
            sandbox,
            workingDirectory,
            auditor as ILanguagePresetAuditorMetadata,
            ct);

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
            throw new InvalidOperationException(
                $"dotnet-format fixer found {projectDirectories.Count} C# project directories; refusing to run because the audit preset limit is {LanguageProjectDiscovery.MaxProjectDirectoriesToRun}.");
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
                throw new InvalidOperationException(
                    $"dotnet-format fixer command failed (exit {result.ExitCode}) in {projectDirectory}: {string.Join(' ', formatArgv)}\n{result.Stderr}{result.Stdout}");
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
        if (formatCheckArgv.Count < 2 ||
            !formatCheckArgv[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            !formatCheckArgv[1].Equals("format", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{FormatCheckAuditorName} must invoke 'dotnet format' for the {FixerName} fixer to reuse it.");
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

        return argv;
    }

    private static async Task<IReadOnlyList<string>> DiscoverProjectDirectoriesAsync(
        ISandbox sandbox,
        string workingDirectory,
        ILanguagePresetAuditorMetadata? metadata,
        CancellationToken ct)
    {
        if (metadata is null)
            return ["."];

        var discovery = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", metadata.MarkerScript],
            WorkingDirectory = workingDirectory,
        }, ct);

        if (discovery.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet-format fixer could not discover C# project markers. Discovery exited {discovery.ExitCode}: {discovery.Stderr}");
        }

        return LanguagePresetProjectDiscovery.ParseProjectDirectories(discovery.Stdout);
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
            throw new InvalidOperationException($"failed to read git status for mechanical fixer: {result.Stderr}");
        return result.Stdout;
    }
}
