namespace CodeyBox.Majordomo;

/// <summary>
/// How much of the current turn's mutation budget has been spent. The runtime
/// owns the counter — it increments <see cref="MutatedItems"/> by the call's
/// <see cref="MajordomoMutateArgs.AffectedItemCount"/> only after a mutation
/// actually executes (never for reads, proposals, refusals, or dry-runs) —
/// and passes the running total into <see cref="MajordomoAuthorization.Decide"/>.
/// </summary>
public sealed record MajordomoTurnUsage
{
    /// <summary>A fresh turn: nothing mutated yet.</summary>
    public static readonly MajordomoTurnUsage None = new();

    private int _mutatedItems;

    /// <summary>Work items mutated so far this turn, across all executed calls.</summary>
    public int MutatedItems
    {
        get => _mutatedItems;
        init
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(MutatedItems), value, "MutatedItems must be >= 0");
            _mutatedItems = value;
        }
    }
}
