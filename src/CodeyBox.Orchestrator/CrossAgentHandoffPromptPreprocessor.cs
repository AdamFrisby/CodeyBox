using System.Text.RegularExpressions;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Built-in preprocessor that injects a condensed handoff brief whenever the
/// orchestrator dispatches the current invocation against a different
/// <see cref="AgentKind"/> than the one that ran the prior agent-involvement
/// entry for the same work item — i.e. the invocation is a cross-agent
/// fallback (quota, paused, smoke, or timeout spill) rather than a same-agent
/// rework.
/// <para>
/// The brief itself is built by <see cref="ICrossAgentHandoffBriefBuilder"/>;
/// this preprocessor only detects the cross-agent transition and injects
/// whatever text the builder returns. No-op when either the involvement
/// store or the brief builder is not wired, when the work item has no prior
/// agent record (first invocation), when the prior agent kind matches the
/// current one (same-agent rework / retry), or when the builder returns a
/// null/whitespace brief.
/// </para>
/// </summary>
public sealed class CrossAgentHandoffPromptPreprocessor : IAgentPromptPreprocessor
{
    private const int MaxBriefChars = 32 * 1024;

    // Captures any line that looks like one of OUR fence delimiters or
    // section headers — `---` runs and `##` markdown headings — so a
    // builder that emits attacker-controlled text cannot break out of
    // the BEGIN/END fences or impersonate the "## Agent prompt" header
    // that follows. We neutralise rather than drop so a redacted brief
    // is still informative.
    private static readonly Regex StructuralLine = new(
        @"^[ \t]*(---+.*|##+\s.*)$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IAgentInvolvementStore? _involvement;
    private readonly ICrossAgentHandoffBriefBuilder? _briefBuilder;
    private readonly ILogger<CrossAgentHandoffPromptPreprocessor> _log;

    public CrossAgentHandoffPromptPreprocessor(
        ILogger<CrossAgentHandoffPromptPreprocessor> log,
        IAgentInvolvementStore? involvement = null,
        ICrossAgentHandoffBriefBuilder? briefBuilder = null)
    {
        _involvement = involvement;
        _briefBuilder = briefBuilder;
        _log = log;
    }

    public int Order => AgentPromptPreprocessorOrder.BuiltInFirst + 200;

    public async Task<string> ProcessAsync(PromptContext ctx, string prompt, CancellationToken ct = default)
    {
        if (_involvement is null || _briefBuilder is null)
            return prompt;

        IReadOnlyList<AgentInvolvement> history;
        try
        {
            history = await _involvement.ListByWorkItemAsync(ctx.ItemId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                ex,
                "Involvement store failed for work item {WorkItemId}; cross-agent handoff brief skipped",
                ctx.ItemId);
            return prompt;
        }

        var priorAgent = ResolvePriorAgentKind(history, ctx.AgentKind);
        if (priorAgent is null)
            return prompt;

        string? brief;
        try
        {
            brief = await _briefBuilder.BuildAsync(ctx, priorAgent.Value, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                ex,
                "Cross-agent handoff brief builder failed for work item {WorkItemId} ({PriorAgent} -> {CurrentAgent}); prompt left unchanged",
                ctx.ItemId,
                priorAgent.Value.Value,
                ctx.AgentKind.Value);
            return prompt;
        }

        if (string.IsNullOrWhiteSpace(brief))
            return prompt;

        var sanitisedBrief = NeutraliseStructuralDelimiters(LimitBriefText(brief.Trim()));

        return $$"""
            ## Cross-agent handoff

            This work item was previously handled by **{{priorAgent.Value.Value}}** and is now being routed to **{{ctx.AgentKind.Value}}** as a fallback. The orchestrator has condensed what the prior agent did and the state of the work branch so you can continue without redoing finished work.

            --- BEGIN HANDOFF BRIEF ---
            {{sanitisedBrief}}
            --- END HANDOFF BRIEF ---

            ## Agent prompt

            {{prompt}}
            """;
    }

    /// <summary>
    /// Caps the brief at <see cref="MaxBriefChars"/> UTF-16 code units. The
    /// builder is supposed to produce a condensed brief; an oversized one
    /// risks blowing the agent's context window. UTF-16 surrogate pairs are
    /// kept whole so the truncated prefix is a valid string.
    /// </summary>
    private static string LimitBriefText(string brief)
    {
        if (brief.Length <= MaxBriefChars)
            return brief;

        var cut = MaxBriefChars;
        if (cut > 0 && char.IsHighSurrogate(brief[cut - 1]))
            cut--;

        return brief[..cut] + $"\n\n[Handoff brief truncated by CodeyBox at {MaxBriefChars / 1024} KiB.]";
    }

    /// <summary>
    /// Defuses attacker-controlled lines that would otherwise close the
    /// <c>--- BEGIN/END HANDOFF BRIEF ---</c> fence or impersonate the
    /// following <c>## Agent prompt</c> header. Lines that look structural
    /// get a single zero-width-space prefix so they render visibly the same
    /// to a human reader but no longer match the fence/header shape.
    /// </summary>
    private static string NeutraliseStructuralDelimiters(string text) =>
        StructuralLine.Replace(text, "​$&");

    /// <summary>
    /// Returns the <see cref="AgentKind"/> of the immediate predecessor in
    /// the involvement trail when it differs from <paramref name="currentAgent"/>.
    /// PipelineRunner records the current invocation's in-progress row
    /// (<see cref="AgentInvolvement.EndedAt"/> == null) before invoking the
    /// agent runner — and therefore before this preprocessor reads the
    /// trail — so we skip in-progress rows to locate the true predecessor.
    /// <para>
    /// The brief fires only when that immediate finalized predecessor's kind
    /// differs from the current invocation, i.e. a genuine cross-agent
    /// fallback. A same-agent rework / retry must not inject a brief even
    /// if an earlier entry in the trail happens to be a different kind —
    /// the prior cross-agent transition was already handled at its own
    /// boundary.
    /// </para>
    /// </summary>
    private static AgentKind? ResolvePriorAgentKind(
        IReadOnlyList<AgentInvolvement> history,
        AgentKind currentAgent)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var entry = history[i];
            if (entry.EndedAt is null)
                continue;

            return entry.AgentKind.Equals(currentAgent) ? null : entry.AgentKind;
        }

        return null;
    }
}
