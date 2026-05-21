using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

public sealed class DotnetTestCommandResultClassifier : IShellCommandResultClassifier
{
    public AuditResult? ClassifyFailedCommand(ShellCommandResultContext context)
    {
        var parsed = DotnetTestOutputParser.Parse(context.AuditorName, context.CombinedOutput);
        if (parsed.ParsedFailureCount == 0)
            return null;

        if (!parsed.HasCommandFailureSignals)
            return new AuditResult(
                parsed.Findings.Count == 0,
                parsed.Findings,
                RawOutput: context.CombinedOutput);

        if (parsed.Findings.Count == 0)
            return null;

        return new AuditResult(
            false,
            [.. parsed.Findings, context.CommandFinding],
            RawOutput: context.CombinedOutput);
    }
}
