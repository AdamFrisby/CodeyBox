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
            var hasProjectFailure = HasProjectFailureLine(context.CombinedOutput);
            var fallbackTitle = hasProjectFailure
                ? "dotnet format command failed"
                : "dotnet format verification failed";
            var fallbackDescription = hasProjectFailure
                ? "dotnet format failed before it could report deterministic formatting violations. Fix the project, restore, or compiler errors shown below; the configured mechanical fixer intentionally skips these failures so the normal audit/rework loop can diagnose them."
                : "dotnet format reported that formatting verification failed, but did not emit parseable file-level violation lines. "
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
                        Title: fallbackTitle,
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
           line.Contains(": error CA", StringComparison.OrdinalIgnoreCase);

    private static bool HasProjectFailureLine(string output)
        => output.Contains(": error CS", StringComparison.OrdinalIgnoreCase) ||
           output.Contains(": error MSB", StringComparison.OrdinalIgnoreCase) ||
           output.Contains(": error NU", StringComparison.OrdinalIgnoreCase) ||
           output.Contains("Unable to load the service index", StringComparison.OrdinalIgnoreCase) ||
           output.Contains("Restore failed", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars
            ? value
            : value[..maxChars] + "\n... output truncated.";
}
