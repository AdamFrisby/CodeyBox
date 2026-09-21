namespace CodeyBox.Admin.Model;

/// <summary>A state change the map can make to an item.</summary>
public sealed record MapItemAction
{
    /// <summary>Stable key: addDependent, retryWork, retryAudit, cancel, delegate.</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }

    /// <summary>Destructive or irreversible — the UI must confirm before acting.</summary>
    public bool Confirm { get; init; }

    /// <summary>Renders in the danger tone.</summary>
    public bool Danger { get; init; }
}

/// <summary>
/// Which mutations an item admits, from its state and activity alone. The
/// map is meant to be where the operator acts, so the rules live here, pure
/// and tested, not scattered across buttons.
/// </summary>
public static class MapItemActions
{
    public static readonly MapItemAction AddDependent = new() { Key = "addDependent", Label = "Add dependent" };
    public static readonly MapItemAction RetryWork = new() { Key = "retryWork", Label = "Retry from work", Confirm = true };
    public static readonly MapItemAction RetryAudit = new() { Key = "retryAudit", Label = "Retry from audit", Confirm = true };
    public static readonly MapItemAction Cancel = new() { Key = "cancel", Label = "Cancel", Confirm = true, Danger = true };
    public static readonly MapItemAction Delegate = new() { Key = "delegate", Label = "Delegate a repair turn", Confirm = true };
    public static readonly MapItemAction Answer = new() { Key = "answer", Label = "Answer question" };

    /// <summary>
    /// Actions for an item in <paramref name="state"/>. A dependent may be
    /// added to anything that can still land (not failed, not cancelled);
    /// retries and delegation apply to terminal failures; cancel to anything
    /// not yet terminal.
    /// </summary>
    public static IReadOnlyList<MapItemAction> For(string? state)
    {
        var s = state ?? string.Empty;
        var actions = new List<MapItemAction>();
        var failed = ItemStates.IsFailedTerminal(s);
        var cancelled = string.Equals(s, "Cancelled", StringComparison.Ordinal);
        var terminal = ItemStates.IsTerminal(s);
        if (string.Equals(s, "NeedsOperatorInput", StringComparison.Ordinal))
        {
            actions.Add(Answer);
        }
        if (!failed && !cancelled)
        {
            actions.Add(AddDependent);
        }
        if (failed || cancelled)
        {
            actions.Add(RetryWork);
            actions.Add(RetryAudit);
        }
        if (failed)
        {
            actions.Add(Delegate);
        }
        if (!terminal)
        {
            actions.Add(Cancel);
        }
        return actions;
    }
}
