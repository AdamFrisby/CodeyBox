using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

public sealed class DotnetTestCommandResultClassifier : IAuditResultClassifier
{
    public AuditResult? ClassifyFailedCommand(AuditResultClassificationContext context)
    {
        var parsed = DotnetTestOutputParser.Parse(context.AuditorName, context.CombinedOutput);
        if (parsed.ParsedFailureCount == 0)
            return null;

        if (!parsed.HasCommandFailureSignals)
            return new AuditResult(
                parsed.Findings.Count == 0,
                parsed.Findings,
                RawOutput: context.CombinedOutput)
            {
                BuildTestGateEvidenceVerified = parsed.Findings.Count > 0 ? null : false,
            };

        if (parsed.Findings.Count == 0)
            return null;

        return new AuditResult(
            false,
            [.. parsed.Findings, context.CommandFinding],
            RawOutput: context.CombinedOutput);
    }
}
