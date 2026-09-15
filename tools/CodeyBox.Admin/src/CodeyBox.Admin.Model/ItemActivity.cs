namespace CodeyBox.Admin.Model;

/// <summary>Derived answer to "is this item moving?".</summary>
public enum ActivityKind
{
    /// <summary>In flight and progressing (work, audit, merge, planning turns).</summary>
    Running,
    /// <summary>Queued but a dependency has not reached Done. Names the blocker.</summary>
    BlockedByDependency,
    /// <summary>Queued but its agent is paused, benched, or quota-exhausted.</summary>
    BlockedByAgentAvailability,
    /// <summary>Queued, dispatchable, but no free worker slot right now.</summary>
    WaitingForSlot,
    /// <summary>Queued and dispatchable with a free slot waiting.</summary>
    Ready,
    /// <summary>
    /// Parked by the orchestrator (quota reset, agent resume, transient retry,
    /// operator input). Not failed; resumes on its own except operator input.
    /// </summary>
    Parked,
    /// <summary>Terminally failed (Failed, AuditFailed, conflict, abandoned).</summary>
    Failed,
    /// <summary>Terminally resolved (Done, NoActionRequired).</summary>
    Succeeded,
    /// <summary>Terminally cancelled by an operator or cascade.</summary>
    Cancelled,
    /// <summary>State string the model does not recognise (surface drift).</summary>
    Unknown,
}

/// <summary>A dependency currently gating a queued item.</summary>
public sealed record ActivityBlocker
{
    public required string Id { get; init; }

    /// <summary>Blocker's state name, or "absent" when outside the snapshot.</summary>
    public required string State { get; init; }
}

/// <summary>Per-item activity verdict.</summary>
public sealed record ItemActivity
{
    public required string ItemId { get; init; }

    public required ActivityKind Kind { get; init; }

    /// <summary>One-line operator reading, naming blockers where relevant.</summary>
    public required string Summary { get; init; }

    public IReadOnlyList<ActivityBlocker> Blockers { get; init; } = [];

    /// <summary>Parked-state name (e.g. "WaitingForQuotaReset"), when parked.</summary>
    public string? ParkReason { get; init; }
}

/// <summary>
/// Derives <see cref="ItemActivity"/> for every item in a snapshot. Evaluation
/// order mirrors the dispatcher's own pickup gate (dependency gate, then
/// agent availability, then worker slots) so the screen agrees with the
/// orchestrator about why a Queued item is not starting.
/// </summary>
public static class ActivityAnalyzer
{
    // Closed mirror of the orchestrator's WorkItemState names lives in
    // ItemStates (single source of truth); compared by exact ordinal match —
    // never substring — per the trust-boundary rules.

    /// <summary>
    /// Derives the activity verdict for one item. Pure: reads only
    /// <paramref name="snapshot"/> and <paramref name="options"/>.
    /// </summary>
    public static ItemActivity Analyze(
        AdminWorkItem item,
        FleetSnapshot snapshot,
        AdminModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new AdminModelOptions();

        var state = item.State ?? string.Empty;

        if (ItemStates.Failed.Contains(state))
        {
            return Activity(item.Id, ActivityKind.Failed, $"Failed in {state}.");
        }
        if (state == "Cancelled")
        {
            return Activity(item.Id, ActivityKind.Cancelled, "Cancelled.");
        }
        if (ItemStates.Succeeded.Contains(state))
        {
            return Activity(item.Id, ActivityKind.Succeeded, $"Resolved as {state}.");
        }
        if (ItemStates.Parked.Contains(state))
        {
            return Activity(item.Id, ActivityKind.Parked, ParkedSummary(state), ParkReason: state);
        }
        if (!ItemStates.IsQueued(state))
        {
            return ItemStates.KnownInFlight.Contains(state)
                ? Activity(item.Id, ActivityKind.Running, $"Running ({state}).")
                : Activity(item.Id, ActivityKind.Unknown, $"Unrecognised state '{state}'.");
        }

        var blockers = FindBlockers(item, snapshot);
        if (blockers.Count > 0)
        {
            var names = string.Join(", ", blockers.Select(b => $"{b.Id} ({b.State})"));
            return Activity(
                item.Id,
                ActivityKind.BlockedByDependency,
                $"Blocked by {names}.",
                blockers);
        }

        var availability = CheckAgentAvailability(item, snapshot, options);
        if (availability is not null)
        {
            return Activity(item.Id, ActivityKind.BlockedByAgentAvailability, availability);
        }

        return HasFreeSlot(item, snapshot)
            ? Activity(item.Id, ActivityKind.Ready, "Queued and dispatchable.")
            : Activity(item.Id, ActivityKind.WaitingForSlot, "Waiting for a free worker slot.");
    }

    /// <summary>Derives verdicts for every item in the snapshot (bounded).</summary>
    public static IReadOnlyDictionary<string, ItemActivity> AnalyzeAll(
        FleetSnapshot snapshot,
        AdminModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var result = new Dictionary<string, ItemActivity>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items ?? [])
        {
            if (item is null || string.IsNullOrEmpty(item.Id) || result.ContainsKey(item.Id))
            {
                continue;
            }
            result[item.Id] = Analyze(item, snapshot, options);
            if (result.Count >= FleetSnapshot.MaxItems)
            {
                break;
            }
        }
        return result;
    }

    private static ItemActivity Activity(
        string itemId,
        ActivityKind kind,
        string summary,
        IReadOnlyList<ActivityBlocker>? blockers = null,
        string? ParkReason = null) => new()
        {
            ItemId = itemId,
            Kind = kind,
            Summary = summary,
            Blockers = blockers ?? [],
            ParkReason = ParkReason,
        };

    private static string ParkedSummary(string state) => state switch
    {
        "NeedsOperatorInput" => "Parked: waiting for operator input.",
        "WaitingForQuotaReset" => "Parked: waiting for agent quota reset.",
        "WaitingForAgentResume" => "Parked: waiting for an operator to resume the agent.",
        "WaitingForTransientRetry" => "Parked: backing off before a transient retry.",
        _ => $"Parked ({state}).",
    };

    private static IReadOnlyList<ActivityBlocker> FindBlockers(
        AdminWorkItem item, FleetSnapshot snapshot)
    {
        var deps = item.DependsOn;
        if (deps is null || deps.Count == 0)
        {
            return [];
        }
        Dictionary<string, string>? statesById = null;
        var blockers = new List<ActivityBlocker>();
        foreach (var dep in deps)
        {
            if (string.IsNullOrEmpty(dep))
            {
                continue;
            }
            statesById ??= BuildStateMap(snapshot);
            if (statesById.TryGetValue(dep, out var depState))
            {
                if (!ItemStates.DependencySatisfying.Contains(depState))
                {
                    blockers.Add(new ActivityBlocker { Id = dep, State = depState });
                }
            }
            else if (!item.DependsOnSatisfied)
            {
                blockers.Add(new ActivityBlocker { Id = dep, State = "absent" });
            }
            if (blockers.Count >= 32)
            {
                break;
            }
        }
        return blockers;
    }

    private static Dictionary<string, string> BuildStateMap(FleetSnapshot snapshot)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var candidate in snapshot.Items ?? [])
        {
            if (candidate is null || string.IsNullOrEmpty(candidate.Id) || map.ContainsKey(candidate.Id))
            {
                continue;
            }
            map[candidate.Id] = candidate.State ?? string.Empty;
            if (++count >= FleetSnapshot.MaxItems)
            {
                break;
            }
        }
        return map;
    }

    private static string? CheckAgentAvailability(
        AdminWorkItem item, FleetSnapshot snapshot, AdminModelOptions options)
    {
        foreach (var agent in snapshot.Agents ?? [])
        {
            if (agent is null || agent.Agent is null)
            {
                continue;
            }
            if (!string.Equals(agent.Agent, item.Agent, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (agent.Paused)
            {
                var reason = string.IsNullOrWhiteSpace(agent.PauseReason) ? "paused by an operator" : agent.PauseReason;
                return $"Blocked: agent {item.Agent} is {reason}.";
            }
            if (agent.Available == false)
            {
                var reason = string.IsNullOrWhiteSpace(agent.UnavailableReason)
                    ? "unavailable"
                    : agent.UnavailableReason;
                return $"Blocked: agent {item.Agent} is {reason}.";
            }
            if (agent.QuotaAvailablePct.HasValue
                && agent.QuotaAvailablePct.Value <= options.QuotaExhaustedAtOrBelowPct)
            {
                var reset = agent.QuotaResetAt.HasValue
                    ? $" Reset expected {agent.QuotaResetAt.Value:u}."
                    : string.Empty;
                return $"Blocked: agent {item.Agent} is out of quota ({agent.QuotaAvailablePct.Value}%).{reset}";
            }
            return null;
        }
        return null;
    }

    private static bool HasFreeSlot(AdminWorkItem item, FleetSnapshot snapshot)
    {
        var workers = snapshot.Workers;
        if (workers is null)
        {
            return true;
        }
        if (workers.GlobalMaxConcurrent > 0 && workers.GlobalRunning >= workers.GlobalMaxConcurrent)
        {
            return false;
        }
        if (workers.PerAgentCaps is not null
            && workers.PerAgentCaps.TryGetValue(item.Agent ?? string.Empty, out var cap)
            && cap > 0
            && workers.PerAgentRunning is not null
            && workers.PerAgentRunning.TryGetValue(item.Agent ?? string.Empty, out var running)
            && running >= cap)
        {
            return false;
        }
        return true;
    }
}
