namespace CodeyBox.Admin.Model;

/// <summary>
/// The five stages an item passes through, in drawn order. There is one
/// work stage: "rework" is work happening again because audit sent it back,
/// which is a return edge with a count, not a box.
/// </summary>
public enum PipelineStage
{
    Plan,
    Work,
    Audit,
    Merge,
    Landed,
}

/// <summary>How a stage renders.</summary>
public enum StageStatus
{
    /// <summary>Not reached yet.</summary>
    NotReached,
    /// <summary>Visited and left.</summary>
    Visited,
    /// <summary>The item is here now.</summary>
    Current,
    /// <summary>The item is here now, but parked (quota, pause, backoff, operator input).</summary>
    Parked,
    /// <summary>The item failed terminally here.</summary>
    Failed,
    /// <summary>History for this stage is not obtainable — drawn as unknown, never as done.</summary>
    Unknown,
}

/// <summary>Why the item went backwards (or round again).</summary>
public enum StageLoopKind
{
    /// <summary>Audit sent it back to work — the merit cycle, the gate's fail path.</summary>
    Rework,
    /// <summary>Merge conflicts sent it round the merge circuit again.</summary>
    Conflict,
    /// <summary>Infrastructure: a re-pickup or an interrupted phase restarted. Never counted as rework.</summary>
    Interruption,
    /// <summary>An operator retried it out of a terminal failure.</summary>
    Retry,
}

/// <summary>What is known about the item's history.</summary>
public enum StageHistory
{
    /// <summary>State transitions were available: visits and loops are exact.</summary>
    Full,
    /// <summary>Only the current state is known; earlier stages are unknown, not assumed.</summary>
    StateOnly,
}

/// <summary>One stage node of the item.</summary>
public sealed record StageNode
{
    public required PipelineStage Stage { get; init; }

    public required string Label { get; init; }

    public required StageStatus Status { get; init; }

    /// <summary>Times the item entered this stage (0 when never, or unknown). Work's count is 1 + returns from audit.</summary>
    public int Visits { get; init; }

    /// <summary>One line of stage-specific evidence ("1/6 · 2 blk"); empty when none.</summary>
    public string Detail { get; init; } = string.Empty;
}

/// <summary>A return edge, drawn dotted with its count — subordinate to the spine.</summary>
public sealed record StageLoop
{
    public required PipelineStage From { get; init; }

    public required PipelineStage To { get; init; }

    public required StageLoopKind Kind { get; init; }

    public required int Count { get; init; }

    public required string Label { get; init; }
}

/// <summary>One stage change, in time order.</summary>
public sealed record StageHop
{
    public required DateTimeOffset At { get; init; }

    public required PipelineStage From { get; init; }

    public required PipelineStage To { get; init; }

    public StageLoopKind? Loop { get; init; }
}

/// <summary>
/// The zoomed-in level of meaning: one item on the circuit
/// <c>plan → work → audit ⇒ merge → landed</c>, where audit is a decision
/// gate whose pass path continues right and whose fail path is a dotted
/// return to work. Where it is now, what it already did, and every return
/// drawn as an annotated edge.
/// </summary>
public sealed record ItemStagePipeline
{
    public required string ItemId { get; init; }

    public required StageHistory History { get; init; }

    /// <summary>Operator note when history is partial ("timeline unavailable"); empty when full.</summary>
    public string HistoryNote { get; init; } = string.Empty;

    /// <summary>All five stages, in drawn order.</summary>
    public required IReadOnlyList<StageNode> Stages { get; init; }

    public IReadOnlyList<StageLoop> Loops { get; init; } = [];

    /// <summary>Chronological stage changes (bounded).</summary>
    public IReadOnlyList<StageHop> Hops { get; init; } = [];

    public required PipelineStage Current { get; init; }

    public required string CurrentState { get; init; }

    /// <summary>One-line reading of the journey so far. Never empty.</summary>
    public required string Summary { get; init; }
}

/// <summary>A state transition from the item's recorded timeline.</summary>
public sealed record StageTransition
{
    public required DateTimeOffset At { get; init; }

    public string? From { get; init; }

    public required string To { get; init; }

    /// <summary>Worker that picked the item up, when the transition was a pickup.</summary>
    public int? WorkerId { get; init; }
}

/// <summary>One audit-progress row (current attempt or not; the builder does not partition).</summary>
public sealed record StageAuditIteration
{
    public int Iteration { get; init; }

    public int MaxIterations { get; init; }

    /// <summary>"complete", "incomplete", or "in_progress".</summary>
    public string Status { get; init; } = "complete";

    public int BlockingFindings { get; init; }
}

/// <summary>Everything the stage derivation reads. Null lists mean "not obtainable", not "empty".</summary>
public sealed record StageSnapshot
{
    public const int MaxTransitions = 4096;

    public required string ItemId { get; init; }

    public required string State { get; init; }

    /// <summary>Recorded state transitions, any order; null when the timeline could not be fetched.</summary>
    public IReadOnlyList<StageTransition>? Transitions { get; init; }

    /// <summary>Audit-progress rows; null when the surface could not be fetched.</summary>
    public IReadOnlyList<StageAuditIteration>? AuditIterations { get; init; }

    /// <summary>Agent that ran the work phase, when known.</summary>
    public string? WorkAgent { get; init; }
}

/// <summary>
/// Pure derivation of <see cref="ItemStagePipeline"/> from an item's recorded
/// state transitions. States map to stages by exact ordinal match; Reworking
/// is the work stage again. Parked states do not change stage; failed states
/// mark the stage the item failed from. A backward hop is a return edge:
/// auditing → reworking is the gate's fail path (<see cref="StageLoopKind.Rework"/>);
/// auditing → working and worker re-pickups are interruptions and are never
/// counted as rework; merging → reworking-for-conflict is the merge circuit;
/// a hop out of a terminal failure is an operator retry. When the timeline
/// is unavailable only the current stage is asserted — earlier stages render
/// as unknown, never as done.
/// </summary>
public static class ItemStagePipelineBuilder
{
    private static readonly IReadOnlyList<(PipelineStage Stage, string Label)> Order =
    [
        (PipelineStage.Plan, "Plan"),
        (PipelineStage.Work, "Work"),
        (PipelineStage.Audit, "Audit"),
        (PipelineStage.Merge, "Merge"),
        (PipelineStage.Landed, "Landed"),
    ];

    public static ItemStagePipeline Build(StageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var state = snapshot.State ?? string.Empty;
        var currentByState = StageOf(state);
        var parked = ItemStates.Parked.Contains(state);
        var failed = ItemStates.IsFailedTerminal(state);

        var visits = new Dictionary<PipelineStage, int>();
        var loops = new Dictionary<(PipelineStage, PipelineStage, StageLoopKind), int>();
        var hops = new List<StageHop>();
        PipelineStage? cursor = null;

        var transitions = snapshot.Transitions;
        if (transitions is not null)
        {
            foreach (var t in transitions
                         .Where(t => t is not null && !string.IsNullOrEmpty(t.To))
                         .OrderBy(t => t.At)
                         .Take(StageSnapshot.MaxTransitions))
            {
                var to = StageOf(t.To);
                if (to is null)
                {
                    // Parked, failed, cancelled: the item stays where it was.
                    continue;
                }
                if (cursor is null)
                {
                    cursor = to;
                    visits[to.Value] = 1;
                    continue;
                }
                if (to == cursor)
                {
                    if (ItemStates.IsFailedTerminal(t.From))
                    {
                        // An operator retry back into the same stage.
                        Count(loops, (cursor.Value, cursor.Value, StageLoopKind.Retry));
                        hops.Add(new StageHop { At = t.At, From = cursor.Value, To = to.Value, Loop = StageLoopKind.Retry });
                        visits[to.Value] = visits.GetValueOrDefault(to.Value) + 1;
                    }
                    else if (string.Equals(t.From, "Working", StringComparison.Ordinal)
                        && string.Equals(t.To, "Working", StringComparison.Ordinal)
                        && t.WorkerId.HasValue)
                    {
                        Count(loops, (PipelineStage.Work, PipelineStage.Work, StageLoopKind.Interruption));
                        hops.Add(new StageHop { At = t.At, From = cursor.Value, To = to.Value, Loop = StageLoopKind.Interruption });
                    }
                    else if (string.Equals(t.To, "ReworkingForConflict", StringComparison.Ordinal))
                    {
                        Count(loops, (PipelineStage.Merge, PipelineStage.Merge, StageLoopKind.Conflict));
                        hops.Add(new StageHop { At = t.At, From = cursor.Value, To = to.Value, Loop = StageLoopKind.Conflict });
                    }
                    continue;
                }
                var backward = Index(to.Value) < Index(cursor.Value);
                StageLoopKind? kind = null;
                if (backward)
                {
                    kind = ItemStates.IsFailedTerminal(t.From) ? StageLoopKind.Retry
                        : cursor == PipelineStage.Audit && to == PipelineStage.Work && string.Equals(t.To, "Reworking", StringComparison.Ordinal) ? StageLoopKind.Rework
                        : StageLoopKind.Interruption;
                    Count(loops, (cursor.Value, to.Value, kind.Value));
                }
                hops.Add(new StageHop { At = t.At, From = cursor.Value, To = to.Value, Loop = kind });
                visits[to.Value] = visits.GetValueOrDefault(to.Value) + 1;
                cursor = to;
            }
        }

        var history = transitions is null ? StageHistory.StateOnly : StageHistory.Full;
        var current = currentByState ?? cursor ?? PipelineStage.Work;

        var stages = new List<StageNode>(Order.Count);
        foreach (var (stage, label) in Order)
        {
            var count = visits.GetValueOrDefault(stage);
            StageStatus status;
            if (stage == current)
            {
                status = failed ? StageStatus.Failed : parked ? StageStatus.Parked : StageStatus.Current;
            }
            else if (history == StageHistory.StateOnly)
            {
                status = Index(stage) < Index(current) ? StageStatus.Unknown : StageStatus.NotReached;
            }
            else
            {
                status = count > 0 ? StageStatus.Visited : StageStatus.NotReached;
            }
            stages.Add(new StageNode
            {
                Stage = stage,
                Label = label,
                Status = status,
                Visits = history == StageHistory.StateOnly ? (stage == current ? 1 : 0) : Math.Max(count, stage == current ? 1 : 0),
                Detail = Detail(stage, status, snapshot),
            });
        }

        var loopList = loops
            .OrderBy(kv => Index(kv.Key.Item1))
            .ThenBy(kv => Index(kv.Key.Item2))
            .ThenBy(kv => kv.Key.Item3)
            .Select(kv => new StageLoop
            {
                From = kv.Key.Item1,
                To = kv.Key.Item2,
                Kind = kv.Key.Item3,
                Count = kv.Value,
                Label = LoopLabel(kv.Key.Item3, kv.Value),
            })
            .ToList();

        return new ItemStagePipeline
        {
            ItemId = snapshot.ItemId,
            History = history,
            HistoryNote = history == StageHistory.StateOnly
                ? "Timeline unavailable — only the current stage is known; earlier stages are not assumed."
                : string.Empty,
            Stages = stages,
            Loops = loopList,
            Hops = hops,
            Current = current,
            CurrentState = state,
            Summary = Summarize(stages, loopList, history, state, parked, failed),
        };
    }

    /// <summary>Stage for a lifecycle state; null for states that do not move the item (parked, failed, cancelled).</summary>
    public static PipelineStage? StageOf(string? state) => state switch
    {
        "Planning" or "PlanReview" or "PlanApproved" => PipelineStage.Plan,
        "Queued" or "Working" or "WorkComplete" or "Delegating" or "Reworking" => PipelineStage.Work,
        "Auditing" or "AuditPassed" => PipelineStage.Audit,
        "Merging" or "Merged" or "UpstreamPushing" or "ReworkingForConflict" => PipelineStage.Merge,
        "Done" or "NoActionRequired" => PipelineStage.Landed,
        _ => null,
    };

    private static int Index(PipelineStage stage) => (int)stage;

    private static void Count<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull =>
        counts[key] = counts.GetValueOrDefault(key) + 1;

    private static string Detail(PipelineStage stage, StageStatus status, StageSnapshot snapshot)
    {
        // Terse on purpose: a stage detail has one stage-pitch of room in the
        // bubble, and counts that a return edge already carries are not repeated.
        switch (stage)
        {
            case PipelineStage.Work:
                return string.IsNullOrWhiteSpace(snapshot.WorkAgent) ? string.Empty : snapshot.WorkAgent.Trim();
            case PipelineStage.Audit:
            {
                var rows = snapshot.AuditIterations;
                if (rows is null)
                {
                    return status is StageStatus.NotReached or StageStatus.Unknown ? string.Empty : "progress n/a";
                }
                var complete = rows.Where(r => r is not null && r.Iteration > 0
                    && string.Equals(r.Status, "complete", StringComparison.OrdinalIgnoreCase)).ToList();
                var running = rows.FirstOrDefault(r => r is not null && r.Iteration > 0
                    && string.Equals(r.Status, "in_progress", StringComparison.OrdinalIgnoreCase));
                var max = rows.Where(r => r is not null).Select(r => r.MaxIterations).DefaultIfEmpty(0).Max();
                if (running is not null)
                {
                    return max > 0 ? $"{running.Iteration}/{max} running" : $"#{running.Iteration} running";
                }
                if (complete.Count == 0)
                {
                    return status is StageStatus.Current or StageStatus.Parked ? "no verdict" : string.Empty;
                }
                var last = complete.OrderBy(r => r.Iteration).Last();
                var budget = max > 0 ? $"{complete.Count}/{max}" : $"{complete.Count}";
                return $"{budget} · {last.BlockingFindings} blk";
            }
            default:
                return string.Empty;
        }
    }

    private static string LoopLabel(StageLoopKind kind, int count) => kind switch
    {
        StageLoopKind.Rework => $"rework ×{count}",
        StageLoopKind.Conflict => $"conflict ×{count}",
        StageLoopKind.Retry => $"retried ×{count}",
        _ => $"interrupted ×{count}",
    };

    private static string Summarize(
        IReadOnlyList<StageNode> stages,
        IReadOnlyList<StageLoop> loops,
        StageHistory history,
        string state,
        bool parked,
        bool failed)
    {
        var current = stages.First(s => s.Status is StageStatus.Current or StageStatus.Parked or StageStatus.Failed);
        var nth = current.Visits > 1 ? $", {Ordinal(current.Visits)} time" : string.Empty;
        var where = failed ? $"failed at {current.Label.ToLowerInvariant()} ({state})"
            : parked ? $"parked at {current.Label.ToLowerInvariant()} ({state})"
            : $"at {current.Label.ToLowerInvariant()} ({state}{nth})";
        if (history == StageHistory.StateOnly)
        {
            return $"Now {where}; history unavailable.";
        }
        var visited = stages.Where(s => s.Status == StageStatus.Visited).Select(s => s.Label.ToLowerInvariant()).ToList();
        var path = visited.Count == 0 ? "straight in" : string.Join(" → ", visited);
        var rework = loops.Where(l => l.Kind == StageLoopKind.Rework).Sum(l => l.Count);
        var interruptions = loops.Where(l => l.Kind == StageLoopKind.Interruption).Sum(l => l.Count);
        var conflicts = loops.Where(l => l.Kind == StageLoopKind.Conflict).Sum(l => l.Count);
        var retries = loops.Where(l => l.Kind == StageLoopKind.Retry).Sum(l => l.Count);
        var notes = new List<string>();
        if (retries > 0)
        {
            notes.Add($"{retries} operator retr{(retries == 1 ? "y" : "ies")}");
        }
        if (rework > 0)
        {
            notes.Add($"audit sent it back ×{rework}");
        }
        if (conflicts > 0)
        {
            notes.Add($"{conflicts} conflict round{(conflicts == 1 ? "" : "s")}");
        }
        if (interruptions > 0)
        {
            notes.Add($"{interruptions} interruption{(interruptions == 1 ? "" : "s")}");
        }
        var tail = notes.Count == 0 ? "no returns" : string.Join(", ", notes);
        return $"Now {where}. Path: {path}. {char.ToUpperInvariant(tail[0])}{tail[1..]}.";
    }

    private static string Ordinal(int n) => n switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{n}th",
    };
}
