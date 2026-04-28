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
          .AppendLine(" found issues with your previous changes. Please address every Error finding below.");
        sb.AppendLine();
        sb.AppendLine("**Before committing**, you MUST run the tool auditors yourself in /work and confirm they pass:");
        sb.AppendLine();
        sb.AppendLine("- `dotnet build CodeyBox.slnx /warnaserror` — must exit 0");
        sb.AppendLine("- `dotnet format --verify-no-changes CodeyBox.slnx` — must exit 0 (run `dotnet format` to fix and stage the result)");
        sb.AppendLine("- `gitleaks detect --source . --no-banner --no-color --redact` — must exit 0");
        sb.AppendLine("- `semgrep --config auto --error --quiet` — must exit 0");
        sb.AppendLine();
        sb.AppendLine("Re-running these BEFORE you commit is the cheapest way to converge — the orchestrator runs the same commands and will fail audit again if any of them exit non-zero. If a tool reports a finding you genuinely cannot fix (e.g. a false-positive in a third-party file), fix what you can and explain the residue in your commit message.");
        sb.AppendLine();
        sb.AppendLine("Make new commits — do not amend.");
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
