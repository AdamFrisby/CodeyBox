using System.Text;
using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Formats audit findings into a structured rework prompt for the agent.
/// The format is deliberately plain text so it works across agents that
/// have different prompt-shape preferences.
/// </summary>
public static class ReworkPromptBuilder
{
    public static string Build(
        string originalPrompt,
        IReadOnlyList<AuditFinding> findings,
        int iteration,
        int maxIterations,
        IReadOnlyList<WorkItemQuestion>? answeredQuestions = null,
        bool allowAgentQuestions = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Rework requested");
        sb.AppendLine();
        sb.Append("Audit iteration ").Append(iteration).Append(" of ").Append(maxIterations)
          .AppendLine(" found issues with your previous changes. Please address every Error-severity finding below, then commit. The orchestrator re-runs the full audit suite after your commit; it will fail with new findings if anything is still wrong, and you'll get another chance to address them.");
        sb.AppendLine();
        sb.AppendLine("Evaluate carefully whether a refactor is really required to address a finding. A small, targeted fix is usually sufficient to resolve the auditor's concern — only consider a refactor if that small fix would be papering over a larger architectural fault. Refactors are not forbidden, but they widen the diff, increase regression risk, and often introduce new findings; reach for one only when the smaller fix is clearly inadequate.");
        sb.AppendLine();
        sb.AppendLine("Make new commits — do not amend.");
        sb.AppendLine();
        sb.AppendLine("Every commit message MUST end with the following trailers, separated from the subject by a blank line:");
        sb.AppendLine();
        sb.AppendLine("    " + CodeyBoxTrailers.PromptRevisionTrailerKey + ": $CODEYBOX_PROMPT_REVISION");
        sb.AppendLine("    " + CodeyBoxTrailers.CoAuthoredBy);
        sb.AppendLine();
        sb.AppendLine("The `" + CodeyBoxTrailers.PromptRevisionTrailerKey + "` value MUST be the literal integer from the `CODEYBOX_PROMPT_REVISION` environment variable. The orchestrator audits this trailer to detect agents that finished against a stale prompt; missing or mismatched values are a blocking finding.");
        sb.AppendLine();

        if (allowAgentQuestions)
        {
            sb.AppendLine("You may still emit `<codeybox-question>` blocks if you hit genuine ambiguity:");
            sb.AppendLine();
            sb.AppendLine("    <codeybox-question id=\"q-001\">Question text here. State the decision and your default if no answer comes.</codeybox-question>");
            sb.AppendLine();
            sb.AppendLine("Then **continue working with your default**. Don't block. The id must be alphanumeric with hyphens/underscores only. A maximum of 10 questions per work item is enforced.");
            sb.AppendLine();
        }

        // Inject operator answers so the agent can apply them.
        var answered = answeredQuestions?.Where(q => q.State == "answered").ToList();
        if (answered is { Count: > 0 })
        {
            sb.AppendLine("## Operator answers to your questions");
            sb.AppendLine();
            sb.AppendLine("You asked the following question(s) and the operator has responded:");
            sb.AppendLine();
            foreach (var q in answered)
            {
                sb.Append("- **").Append(q.QuestionId).AppendLine("**");
                sb.AppendLine("  Question:");
                sb.AppendLine("  ```");
                foreach (var line in (q.QuestionText ?? string.Empty).Split('\n'))
                    sb.Append("  ").AppendLine(line.TrimEnd('\r'));
                sb.AppendLine("  ```");
                sb.AppendLine("  Answer:");
                sb.AppendLine("  ```");
                foreach (var line in (q.AnswerText ?? string.Empty).Split('\n'))
                    sb.Append("  ").AppendLine(line.TrimEnd('\r'));
                sb.AppendLine("  ```");
                sb.AppendLine();
            }
            sb.AppendLine("Apply these answers to your work.");
            sb.AppendLine();
        }

        var grouped = findings.GroupBy(f => f.AuditorName);
        foreach (var group in grouped)
        {
            sb.Append("### ").AppendLine(group.Key);
            foreach (var f in group.OrderByDescending(f => f.Severity))
            {
                sb.Append("- **").Append(f.Severity).Append("**: ").Append(f.Title);
                if (f.Location is not null) sb.Append(" (").Append(f.Location).Append(')');
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(f.Description))
                {
                    foreach (var line in f.Description.Split('\n'))
                        sb.Append("  ").AppendLine(line.TrimEnd('\r'));
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Original task");
        sb.AppendLine();
        sb.AppendLine(originalPrompt);
        return sb.ToString();
    }
}
