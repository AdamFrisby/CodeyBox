namespace CodeyBox.Core;

/// <summary>
/// Configuration for a <see cref="JobType.CheckAndAct"/> work item. The check
/// phase asks the agent a yes/no <see cref="Question"/> against the project
/// repo and parses a structured <see cref="CheckVerdict"/> from the agent's
/// stdout. When the verdict's <c>answer</c> equals <see cref="ActionableAnswer"/>,
/// the orchestrator enqueues <see cref="OnYes"/> as a normal work item against
/// the same project, parented (by <see cref="WorkItem.OriginCheckWorkItemId"/>)
/// to the check item that triggered it.
/// </summary>
public sealed record CheckAndActSpec
{
    /// <summary>The yes/no question the agent must answer against the repo.</summary>
    public required string Question { get; init; }

    /// <summary>
    /// Which boolean answer triggers the on-yes action. Defaults to <c>true</c>
    /// — the common "if vulnerable, fix" / "if missing, add" shape. Set to
    /// <c>false</c> to act on a "no" answer (e.g. "no tests cover X → write tests").
    /// </summary>
    public bool ActionableAnswer { get; init; } = true;

    /// <summary>
    /// Spec for the follow-up work item enqueued when the verdict matches
    /// <see cref="ActionableAnswer"/>. Required when the check item is created.
    /// </summary>
    public required OnYesActionSpec OnYes { get; init; }
}

/// <summary>
/// Spec for the follow-up work item the orchestrator enqueues when a
/// <see cref="JobType.CheckAndAct"/> verdict matches the actionable condition.
/// Mirrors the create-work-item API surface so the operator can fully describe
/// the remediation/follow-up task as data on the check item.
/// </summary>
public sealed record OnYesActionSpec
{
    /// <summary>Title of the follow-up work item (≤ 200 chars, no control chars).</summary>
    public required string Title { get; init; }

    /// <summary>Prompt for the follow-up work item (≤ 64 KB).</summary>
    public required string Prompt { get; init; }

    /// <summary>Optional minimum quality score for the routed agent member.</summary>
    public int? MinModelScore { get; init; }

    /// <summary>Optional dispatch priority for the follow-up.</summary>
    public int? Priority { get; init; }

    /// <summary>Optional explicit agent kind for the follow-up.</summary>
    public string? Agent { get; init; }

    /// <summary>Optional agent-class id for the follow-up.</summary>
    public string? AgentClassId { get; init; }

    /// <summary>
    /// Optional dependency list (UUIDs or namespaced/bare externalIds resolved
    /// at enqueue time against items in the same project).
    /// </summary>
    public IReadOnlyList<string>? DependsOn { get; init; }
}

/// <summary>
/// Structured verdict returned by a <see cref="JobType.CheckAndAct"/> agent.
/// Persisted on the check work item once the agent invocation completes so the
/// operator can audit what was asked, the agent's boolean answer, and the
/// evidence cited. Stored as JSON; rendered in the work-item DTO and timeline.
/// </summary>
public sealed record CheckVerdict
{
    /// <summary>The agent's boolean answer to the operator's question.</summary>
    public required bool Answer { get; init; }

    /// <summary>
    /// Human-readable evidence / justification the agent gave for its answer.
    /// May reference specific files, line numbers, or patterns observed in the
    /// repo. Used for the timeline summary and operator review; never used as
    /// machine-parsable data.
    /// </summary>
    public required string Evidence { get; init; }

    /// <summary>
    /// Optional confidence label (e.g. <c>"high"</c>, <c>"medium"</c>,
    /// <c>"low"</c>). Free-form; the orchestrator does not gate on this value.
    /// </summary>
    public string? Confidence { get; init; }
}
