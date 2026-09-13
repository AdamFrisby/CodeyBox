namespace CodeyBox.Core;

/// <summary>
/// Outcome of a single delegation turn. Kept as exact-match strings (never
/// substring-compared) so future agents cannot smuggle one outcome past a
/// check for another.
/// </summary>
public static class DelegationOutcomes
{
    /// <summary>The delegate committed changes; the item advanced to audit.</summary>
    public const string Completed = "completed";

    /// <summary>The delegate exited cleanly but committed nothing.</summary>
    public const string NoChanges = "no-changes";

    /// <summary>The delegate turn failed (agent error, timeout, infra).</summary>
    public const string Failed = "failed";
}

/// <summary>
/// First-class record of one delegation turn: what the delegate was told
/// (the composed brief), who ran it (agent + model), and what it did (the
/// resulting diff). Append-only; one row per completed turn.
/// </summary>
public sealed record DelegationEvent
{
    /// <summary>Unique row id (GUID string).</summary>
    public required string Id { get; init; }

    public required WorkItemId WorkItemId { get; init; }

    /// <summary>1-based turn number for the item. Matches the item's
    /// <see cref="WorkItem.DelegationAttempts"/> after the turn completes.</summary>
    public required int Attempt { get; init; }

    /// <summary>The exact brief text the delegate received (bounded by
    /// <c>CodeyBox:ConvergenceBrief</c> at compose time).</summary>
    public required string Brief { get; init; }

    /// <summary>Agent that ran the delegation turn.</summary>
    public required AgentKind Agent { get; init; }

    /// <summary>Model id observed for the turn, if any.</summary>
    public string? Model { get; init; }

    /// <summary>One of <see cref="DelegationOutcomes"/>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Human-readable reason: park message on no-change/failure, null on success.</summary>
    public string? Reason { get; init; }

    /// <summary><c>git diff --stat</c> of base vs work branch after the turn.</summary>
    public required string DiffStat { get; init; }

    /// <summary>Bounded full diff of base vs work branch after the turn.</summary>
    public required string ResultDiff { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}
