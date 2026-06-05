namespace CodeyBox.Core;

/// <summary>
/// Token usage and estimated cost for a single agent invocation within a pipeline phase.
/// One row is written per agent CLI call; tool auditors (which have no token cost) produce no rows.
/// </summary>
public sealed record WorkItemCost
{
    public required string Id { get; init; }
    public required string WorkItemId { get; init; }

    /// <summary>work | rework | audit | merge</summary>
    public required string Phase { get; init; }

    /// <summary>Audit/rework iteration number; null for work and merge phases.</summary>
    public int? Iteration { get; init; }

    /// <summary>claude | codex | gemini | copilot</summary>
    public required string AgentKind { get; init; }

    /// <summary>Routed agent instance, e.g. "claude/acct-a"; null for legacy/default rows.</summary>
    public string? AgentInstanceId { get; init; }

    /// <summary>
    /// Model identifier reported by the CLI when usage data is parsed; elapsed
    /// fallback rows use the dispatched/resolved model id. Null when unknown.
    /// </summary>
    public string? ModelId { get; init; }

    public required int InputTokens { get; init; }
    public int CachedInputTokens { get; init; }
    public required int OutputTokens { get; init; }

    /// <summary>
    /// Equivalent pay-per-API cost in USD. On subscription plans this is the
    /// notional value of the same workload at API rates, not a real charge.
    /// </summary>
    public double EstimatedUsd { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }

    /// <summary>Any additional agent-specific fields captured verbatim from CLI output.</summary>
    public string RawMetadataJson { get; init; } = "{}";
}
