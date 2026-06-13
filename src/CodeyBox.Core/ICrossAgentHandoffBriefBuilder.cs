namespace CodeyBox.Core;

/// <summary>
/// Builds the condensed cross-agent handoff brief — the short summary of what
/// the prior agent attempted, what state the work branch is in, and what is
/// left to do — that the <see cref="CrossAgentHandoffPromptPreprocessor"/>
/// injects when the orchestrator falls over from one <see cref="AgentKind"/>
/// to another mid-work-item.
/// <para>
/// Splitting the construction of the brief out of the preprocessor lets the
/// foundation that owns the inputs (host-captured stream of the prior agent
/// plus the branch diff against base) compute the brief however it likes —
/// e.g. via a text-only LLM summarisation, deterministic templating, or a
/// hybrid — without the preprocessor having to know any of that.
/// </para>
/// </summary>
public interface ICrossAgentHandoffBriefBuilder
{
    /// <summary>
    /// Returns the brief for the cross-agent fallback identified by
    /// <paramref name="ctx"/> (current agent) and <paramref name="priorAgent"/>
    /// (the agent the orchestrator just spilled away from). Returning
    /// <c>null</c> or whitespace tells the preprocessor to skip the injection
    /// — for example when there is no useful prior state to summarise.
    /// </summary>
    Task<string?> BuildAsync(PromptContext ctx, AgentKind priorAgent, CancellationToken ct = default);
}
