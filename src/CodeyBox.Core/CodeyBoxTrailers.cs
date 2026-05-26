using System.Text;
using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// Commit-message trailers stamped on commits the orchestrator produces.
/// Trailers follow git/RFC-5322 conventions: each is a single line of
/// <c>Key: value</c> with no embedded newlines, grouped at the end of the
/// commit message separated from the subject/body by a blank line.
///
/// <para>
/// Schema:
///   <list type="bullet">
///     <item><description><c>CodeyBox-WorkItem: &lt;id&gt;</c> — the full work-item id (always present).</description></item>
///     <item><description><c>CodeyBox-Agent: &lt;agent&gt;[/&lt;model&gt;]</c> — the final agent/model that produced the work (always present).</description></item>
///     <item><description><c>CodeyBox-Prompt-Revision: &lt;N&gt;</c> — the prompt revision that was active when the iteration was dispatched. Present when the orchestrator dispatched the iteration with a known revision.</description></item>
///     <item><description><c>CodeyBox-Fallbacks: from→to (×N reason); …</c> — emitted only when fallback events occurred this run.</description></item>
///     <item><description><c>Co-Authored-By: CodeyBox &lt;noreply@codeybox.invalid&gt;</c> — terminal co-author trailer (always present).</description></item>
///   </list>
/// </para>
/// </summary>
public static class CodeyBoxTrailers
{
    public const string CoAuthoredBy = "Co-Authored-By: CodeyBox <noreply@codeybox.invalid>";
    public const string WorkItemTrailerKey = "CodeyBox-WorkItem";
    public const string AgentTrailerKey = "CodeyBox-Agent";
    public const string PromptRevisionTrailerKey = "CodeyBox-Prompt-Revision";
    public const string FallbacksTrailerKey = "CodeyBox-Fallbacks";

    /// <summary>
    /// Env-var name passed into the agent's sandbox carrying the prompt revision
    /// that was active when the current iteration was dispatched. The agent
    /// echoes this value back as the <see cref="PromptRevisionTrailerKey"/>
    /// trailer on every commit; <c>process:prompt-revision-trailer</c> verifies
    /// the trailer matches. Single shared constant so the orchestrator (writer),
    /// the audit module (verifier), and the rework-prompt template (reader)
    /// reference the same symbol — a future rename touches one place.
    /// </summary>
    public const string PromptRevisionEnvVar = "CODEYBOX_PROMPT_REVISION";

    private static readonly Regex CollapseWhitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Build the trailer block to append to a CodeyBox-emitted commit message.
    /// Lines are joined with '\n', no leading or trailing newline; the final
    /// line is always the canonical <see cref="CoAuthoredBy"/> trailer.
    /// <see cref="FallbacksTrailerKey"/> is included only when at least one
    /// fallback record was provided.
    /// </summary>
    public static string Compose(
        WorkItemId workItemId,
        AgentKind finalAgent,
        string? finalModel = null,
        IReadOnlyList<AgentFallbackRecord>? fallbackHistory = null,
        int? promptRevisionAtDispatch = null)
    {
        var sb = new StringBuilder();
        sb.Append(WorkItemTrailerKey).Append(": ").Append(workItemId).Append('\n');

        sb.Append(AgentTrailerKey).Append(": ").Append(SanitizeOneLine(finalAgent.Value));
        var model = SanitizeOneLine(finalModel ?? string.Empty);
        if (model.Length > 0)
            sb.Append('/').Append(model);
        sb.Append('\n');

        if (promptRevisionAtDispatch is { } rev)
            sb.Append(PromptRevisionTrailerKey).Append(": ").Append(rev).Append('\n');

        var fallbackLine = ComposeFallbackSummary(fallbackHistory);
        if (fallbackLine is not null)
            sb.Append(FallbacksTrailerKey).Append(": ").Append(fallbackLine).Append('\n');

        sb.Append(CoAuthoredBy);
        return sb.ToString();
    }

    /// <summary>
    /// Summarise fallback events as a single RFC-5322-safe line, or null if
    /// nothing happened. Groups by <c>(fromAgent, toAgent)</c>; each group
    /// contributes <c>from→to (×N reason)</c> using the most-common reason
    /// (ties broken ordinally). A null <c>ToAgent</c> renders as
    /// <c>(exhausted)</c> — the all-members-exhausted park event.
    /// </summary>
    public static string? ComposeFallbackSummary(IReadOnlyList<AgentFallbackRecord>? records)
    {
        if (records is null || records.Count == 0) return null;

        var groups = records
            .GroupBy(r => (FromAgent: r.FromAgent.Value, ToAgent: r.ToAgent?.Value))
            .Select(g => new
            {
                From = g.Key.FromAgent,
                To = g.Key.ToAgent ?? "(exhausted)",
                Count = g.Count(),
                Reason = MostCommonReason(g.Select(r => r.Reason)),
            })
            .OrderBy(x => x.From, StringComparer.Ordinal)
            .ThenBy(x => x.To, StringComparer.Ordinal)
            .ToList();

        if (groups.Count == 0) return null;
        return string.Join("; ", groups.Select(g => $"{g.From}→{g.To} (×{g.Count} {g.Reason})"));
    }

    private static string MostCommonReason(IEnumerable<string> reasons)
    {
        var top = reasons
            .Select(SanitizeOneLine)
            .Where(r => r.Length > 0)
            .GroupBy(r => r, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault();
        return top ?? "unspecified";
    }

    private static string SanitizeOneLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return CollapseWhitespace.Replace(s, " ").Trim();
    }
}
