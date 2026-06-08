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

        return $$"""
            ## Cross-agent handoff

            This work item was previously handled by **{{priorAgent.Value.Value}}** and is now being routed to **{{ctx.AgentKind.Value}}** as a fallback. The orchestrator has condensed what the prior agent did and the state of the work branch so you can continue without redoing finished work.

            --- BEGIN HANDOFF BRIEF ---
            {{brief.Trim()}}
            --- END HANDOFF BRIEF ---

            ## Agent prompt

            {{prompt}}
            """;
    }

    /// <summary>
    /// Returns the <see cref="AgentKind"/> of the most recent involvement
    /// entry whose agent differs from <paramref name="currentAgent"/>. The
    /// store's <c>ListByWorkItemAsync</c> contract is oldest-first, so we
    /// scan in reverse. A null return means there is no prior cross-agent
    /// record — either it's the first invocation or every prior entry is the
    /// same kind as the current invocation (a same-agent rework).
    /// </summary>
    private static AgentKind? ResolvePriorAgentKind(
        IReadOnlyList<AgentInvolvement> history,
        AgentKind currentAgent)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var entry = history[i];
            if (!entry.AgentKind.Equals(currentAgent))
                return entry.AgentKind;
        }

        return null;
    }
}
