using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Deterministic approval policy applied after the configured reviewer panel.
/// It does not attempt subjective architecture review; it independently
/// requires an explicit lexical binding between the canonical plan and task so
/// a model's syntactically valid pass verdict is not the sole PlanApproved
/// authority.
/// </summary>
internal static partial class PlanApprovalPolicy
{
    private const int MinimumTaskTokenLength = 4;

    public static AuditFinding? ReviewTaskBinding(
        string originalPrompt,
        string planArtifact,
        string auditorName)
    {
        var plan = PlanArtifactDocument.ParseCanonical(planArtifact);
        var taskTokens = Tokens(originalPrompt)
            .Where(static token => token.Length >= MinimumTaskTokenLength)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (taskTokens.Count == 0)
            return null;

        var planText = string.Join('\n',
            [plan.Approach, .. plan.Files, .. plan.TestStrategy, .. plan.Risks, plan.SatisfiesTask]);
        if (Tokens(planText).Any(taskTokens.Contains))
            return null;

        return new AuditFinding(
            auditorName,
            AuditSeverity.Error,
            "plan is not deterministically bound to the task",
            "The canonical PLAN does not repeat any substantive task term. Revise its approach or satisfiesTask field so the planned work is explicitly tied to the requested task.",
            "PLAN:satisfiesTask");
    }

    private static IEnumerable<string> Tokens(string value) =>
        WordToken().Matches(value).Select(static match => match.Value);

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordToken();
}
