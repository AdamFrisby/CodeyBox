namespace CodeyBox.Majordomo;

/// <summary>
/// Operator-selected, hot-reloadable majordomo policy: the autonomy mode and
/// the blast-radius bounds. <see cref="MajordomoAuthorization.Decide"/> takes
/// the current options on every call, so a config reload changes behaviour
/// for subsequent calls without a restart.
///
/// Bounds reject invalid values at set time rather than silently weakening
/// the gate on a config typo.
/// </summary>
public sealed record MajordomoOptions
{
    /// <summary>Default per-turn cap on mutated items.</summary>
    public const int DefaultMaxMutatedItemsPerTurn = 8;

    /// <summary>
    /// Hard ceiling on <see cref="MaxMutatedItemsPerTurn"/> — configuration
    /// cannot raise the blast radius past this no matter what the operator
    /// sets.
    /// </summary>
    public const int MaxAllowedMutatedItemsPerTurn = 100;

    private MajordomoAutonomyMode _mode = MajordomoAutonomyMode.Proposed;

    /// <summary>
    /// Whether MUTATE tools execute immediately or produce operator proposals.
    /// Defaults to <see cref="MajordomoAutonomyMode.Proposed"/> so a fresh
    /// install is review-first until the operator opts into autonomy. A
    /// config-bound value outside <see cref="MajordomoAutonomyMode"/> is
    /// rejected at set time rather than silently read as Proposed.
    /// </summary>
    public MajordomoAutonomyMode Mode
    {
        get => _mode;
        init
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(
                    nameof(Mode), value,
                    $"Mode must be a defined {nameof(MajordomoAutonomyMode)}");
            _mode = value;
        }
    }

    private int _maxMutatedItemsPerTurn = DefaultMaxMutatedItemsPerTurn;

    /// <summary>
    /// Maximum number of work items mutated per majordomo turn, cumulative
    /// across calls. A single call may also not exceed it — a proposal the
    /// operator waves through is still a mutation, so the bound applies in
    /// both modes.
    /// </summary>
    public int MaxMutatedItemsPerTurn
    {
        get => _maxMutatedItemsPerTurn;
        init
        {
            if (value < 1 || value > MaxAllowedMutatedItemsPerTurn)
                throw new ArgumentOutOfRangeException(
                    nameof(MaxMutatedItemsPerTurn), value,
                    $"MaxMutatedItemsPerTurn must be within [1, {MaxAllowedMutatedItemsPerTurn}]");
            _maxMutatedItemsPerTurn = value;
        }
    }
}
