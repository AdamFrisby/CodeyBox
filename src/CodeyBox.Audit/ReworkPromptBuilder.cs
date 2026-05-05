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
        IReadOnlyList<WorkItemQuestion>? answeredQuestions = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Rework requested");
        sb.AppendLine();
        sb.Append("Audit iteration ").Append(iteration).Append(" of ").Append(maxIterations)
          .AppendLine(" found issues with your previous changes. Please address every Error-severity finding below, then commit. The orchestrator re-runs the full audit suite after your commit; it will fail with new findings if anything is still wrong, and you'll get another chance to address them.");
        sb.AppendLine();
        sb.AppendLine("Make new commits — do not amend.");
        sb.AppendLine();
        sb.AppendLine("Every commit message MUST end with the following trailer, separated from the subject by a blank line:");
        sb.AppendLine();
        sb.AppendLine("    " + CodeyBoxTrailers.CoAuthoredBy);
        sb.AppendLine();

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
                sb.Append("- **").Append(q.QuestionId).Append("**: \"").Append(q.QuestionText).AppendLine("\"");
                sb.Append("  Answer: \"").Append(q.AnswerText).AppendLine("\"");
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
