using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Deterministic, non-LLM approval control applied after the configured reviewer
/// panel and independent of it. Because the reviewer panel's pass verdict is
/// model output over less-trusted task/plan data (and so is forgeable via prompt
/// injection), this policy does not let that verdict be the sole PlanApproved
/// authority: it requires the canonical plan to reproduce a coverage-scaled
/// number of the operator task's distinctive terms before the state transition.
/// The operator-authored task is the trusted anchor; a forged pass on a plan
/// that does not actually address the task is rejected here regardless of the
/// panel verdict. It is a deterministic backstop, not a full soundness proof —
/// a plan that lexically covers the task can still be wrong; catching that
/// remains the (defence-in-depth) job of the reviewer panel and, ultimately,
/// operator oversight.
/// </summary>
internal static partial class PlanApprovalPolicy
{
    private const int MinimumTaskTokenLength = 4;

    /// <summary>
    /// Default fraction of the task's distinctive terms the plan must reproduce
    /// to be considered bound. Configurable via
    /// <c>CodeyBox:PipelineTuning:PlanTaskBindingCoverageRatio</c>. Scaling by a
    /// fraction (rather than a fixed count) keeps a one-line task bindable with a
    /// single term while forcing a substantive multi-requirement task to be
    /// covered by proportionally more of the plan — closing the "echo any one
    /// four-character token" bypass.
    /// </summary>
    public const double DefaultTaskBindingCoverage = 0.2;

    public static AuditFinding? ReviewTaskBinding(
        string originalPrompt,
        string planArtifact,
        string auditorName,
        double coverageRatio = DefaultTaskBindingCoverage)
    {
        var plan = PlanArtifactDocument.ParseCanonical(planArtifact);

        var bindingTerms = TaskBindingTerms(originalPrompt);
        if (bindingTerms.Count == 0)
            return null; // A term-less task (empty / punctuation-only) has nothing to bind to.

        var planText = string.Join('\n',
            [plan.Approach, .. plan.Files, .. plan.TestStrategy, .. plan.Risks, plan.SatisfiesTask]);
        var planTokens = Tokens(planText).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var covered = bindingTerms.Count(planTokens.Contains);
        var required = RequiredBoundTermCount(bindingTerms.Count, coverageRatio);
        if (covered >= required)
            return null;

        return new AuditFinding(
            auditorName,
            AuditSeverity.Error,
            "plan is not deterministically bound to the task",
            $"The canonical PLAN reproduces {covered} of the task's {bindingTerms.Count} distinctive term(s), " +
            $"but at least {required} are required to bind it to the requested task. Revise its approach or " +
            "satisfiesTask field so the planned work explicitly addresses the task's concrete obligations.",
            "PLAN:satisfiesTask");
    }

    // Distinct significant task terms used as the binding anchor. Short but
    // non-empty tasks (whose every token is under the significance length) fall
    // back to all distinct tokens so they are still bound rather than skipped.
    private static IReadOnlyCollection<string> TaskBindingTerms(string prompt)
    {
        var allTokens = Tokens(prompt).ToList();
        var significant = allTokens
            .Where(static token => token.Length >= MinimumTaskTokenLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (significant.Count > 0)
            return significant;

        return allTokens.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int RequiredBoundTermCount(int distinctTermCount, double coverageRatio)
    {
        var ratio = double.IsFinite(coverageRatio) && coverageRatio > 0
            ? coverageRatio
            : DefaultTaskBindingCoverage;
        var required = (int)Math.Ceiling(distinctTermCount * ratio);
        return Math.Clamp(required, 1, distinctTermCount);
    }

    private static IEnumerable<string> Tokens(string value) =>
        WordToken().Matches(value).Select(static match => match.Value);

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordToken();
}
