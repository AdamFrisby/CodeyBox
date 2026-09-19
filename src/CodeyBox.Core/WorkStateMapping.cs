namespace CodeyBox.Core;

/// <summary>
/// Explicit, operator-visible declaration of how CodeyBox work-item states map
/// to an external tool's statuses. No external tool has an equivalent of the
/// CodeyBox state set, so the mapping is configuration — not a private
/// interpretation inside each provider. A state with no entry is reported as
/// unmapped (<see cref="UnmappedWorkItemState"/>) rather than guessed:
/// marking something Done because a mapping was missing is the worst
/// available failure.
/// </summary>
public sealed class WorkStateMapping
{
    private readonly IReadOnlyDictionary<WorkItemState, string> _map;

    private WorkStateMapping(IReadOnlyDictionary<WorkItemState, string> map) => _map = map;

    /// <summary>Empty mapping: every state reports unmapped.</summary>
    public static WorkStateMapping Empty { get; } =
        new(new Dictionary<WorkItemState, string>());

    /// <summary>Builds a mapping from an explicit declaration.</summary>
    public static WorkStateMapping From(IReadOnlyDictionary<WorkItemState, string> map) =>
        new(new Dictionary<WorkItemState, string>(map));

    /// <summary>
    /// Parses an operator declaration keyed by state name (e.g.
    /// <c>{ "Working": "in progress", "Done": "done" }</c>). Unknown state
    /// names throw <see cref="ArgumentException"/> at startup so a typo can
    /// never silently leave a state unmapped. Blank values are rejected for
    /// the same reason.
    /// </summary>
    public static WorkStateMapping Parse(IReadOnlyDictionary<string, string> declaration)
    {
        var map = new Dictionary<WorkItemState, string>();
        foreach (var (name, status) in declaration)
        {
            if (!Enum.TryParse<WorkItemState>(name, ignoreCase: false, out var state))
                throw new ArgumentException(
                    $"unknown work item state '{name}' in state mapping; valid names: {string.Join(", ", Enum.GetNames<WorkItemState>())}",
                    nameof(declaration));
            if (string.IsNullOrWhiteSpace(status))
                throw new ArgumentException(
                    $"state mapping for '{name}' must not be blank",
                    nameof(declaration));
            map[state] = status;
        }

        return new WorkStateMapping(map);
    }

    /// <summary>
    /// Maps <paramref name="state"/> to its declared external status.
    /// Returns false and sets <paramref name="unmapped"/> when the state has
    /// no declaration — the caller must report it as unmapped, never guess.
    /// </summary>
    public bool TryMap(WorkItemState state, out string? externalStatus) =>
        _map.TryGetValue(state, out externalStatus);

    /// <summary>Declared states, for operator visibility (capabilities/status endpoints).</summary>
    public IReadOnlyDictionary<WorkItemState, string> Declared => _map;
}

/// <summary>
/// Sentinel recording that a work item state has no declared external
/// mapping. Returned (not thrown) wherever a mapping is required so the
/// unmapped state is visible in the sync audit trail.
/// </summary>
public sealed record UnmappedWorkItemState(WorkItemState State)
{
    /// <summary>Human-readable explanation naming the state and the fix.</summary>
    public string Describe() =>
        $"work item state '{State}' has no declared external status mapping; " +
        "add one to the source's state mapping or leave the item unsynced";
}
