using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

public sealed class DotnetFormatCommandResultClassifier : IShellCommandResultClassifier
{
    private const int MaxViolationLines = 40;

    public AuditResult? ClassifyFailedCommand(ShellCommandResultContext context)
    {
        if (context.Argv.Count < 2 ||
            !context.Argv[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            !context.Argv[1].Equals("format", StringComparison.OrdinalIgnoreCase) ||
            !context.Argv.Any(a => a.Equals("--verify-no-changes", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var lines = context.CombinedOutput
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsFormatViolationLine)
            .Take(MaxViolationLines + 1)
            .ToList();
        if (lines.Count == 0)
        {
            var fallbackDescription = "dotnet format reported that formatting verification failed, but did not emit parseable file-level violation lines. "
                + "Run `dotnet format --verbosity diagnostic` with the same SDK/baseline as the auditor, or let the configured mechanical fixer apply it before audit.";
            if (!string.IsNullOrWhiteSpace(context.CombinedOutput))
                fallbackDescription += "\n\n" + Truncate(context.CombinedOutput.Trim(), 4000);

            return new AuditResult(
                Passed: false,
                Findings:
                [
                    new AuditFinding(
                        AuditorName: context.AuditorName,
                        Severity: AuditSeverity.Error,
                        Title: "dotnet format verification failed",
                        Description: fallbackDescription),
                ],
                RawOutput: context.CombinedOutput);
        }

        var truncated = lines.Count > MaxViolationLines;
        if (truncated)
            lines = lines.Take(MaxViolationLines).ToList();

        var description = "Run `dotnet format` with the same SDK/baseline as the auditor, or let the configured mechanical fixer apply it before audit.\n\n"
            + string.Join('\n', lines);
        if (truncated)
            description += $"\n... additional dotnet format violations omitted after {MaxViolationLines} lines.";

        return new AuditResult(
            Passed: false,
            Findings:
            [
                new AuditFinding(
                    AuditorName: context.AuditorName,
                    Severity: AuditSeverity.Error,
                    Title: "dotnet format would change files",
                    Description: description),
            ],
            RawOutput: context.CombinedOutput);
    }

    private static bool IsFormatViolationLine(string line)
        => line.Contains(": error WHITESPACE:", StringComparison.OrdinalIgnoreCase) ||
           line.Contains(": error IDE", StringComparison.OrdinalIgnoreCase) ||
           line.Contains(": error CA", StringComparison.OrdinalIgnoreCase) ||
           line.Contains(": error CS", StringComparison.OrdinalIgnoreCase) ||
           line.Contains(": error ", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars
            ? value
            : value[..maxChars] + "\n... output truncated.";
}
