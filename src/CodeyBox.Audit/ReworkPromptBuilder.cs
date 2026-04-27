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
    public static string Build(string originalPrompt, IReadOnlyList<AuditFinding> findings, int iteration, int maxIterations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Rework requested");
        sb.AppendLine();
        sb.Append("Audit iteration ").Append(iteration).Append(" of ").Append(maxIterations)
          .AppendLine(" found issues with your previous changes. Please address every error below, then ensure all auditors would pass on a re-run. Make new commits — do not amend.");
        sb.AppendLine();

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
