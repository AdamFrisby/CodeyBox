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

    /// <summary>Default proposal time-to-live: a day-old suggestion is stale reasoning.</summary>
    public static readonly TimeSpan DefaultProposalTimeToLive = TimeSpan.FromHours(24);

    /// <summary>Shortest accepted proposal time-to-live.</summary>
    public static readonly TimeSpan MinProposalTimeToLive = TimeSpan.FromMinutes(1);

    /// <summary>Longest accepted proposal time-to-live.</summary>
    public static readonly TimeSpan MaxProposalTimeToLive = TimeSpan.FromDays(30);

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

    private TimeSpan _proposalTimeToLive = DefaultProposalTimeToLive;

    /// <summary>
    /// How long a proposal stays approvable after it is queued. Approving
    /// past the deadline is refused and the proposal is marked expired, so a
    /// stale queue of suggestions cannot be approved long after the reasoning
    /// behind them stopped being true.
    /// </summary>
    public TimeSpan ProposalTimeToLive
    {
        get => _proposalTimeToLive;
        init
        {
            if (value < MinProposalTimeToLive || value > MaxProposalTimeToLive)
                throw new ArgumentOutOfRangeException(
                    nameof(ProposalTimeToLive), value,
                    $"ProposalTimeToLive must be within [{MinProposalTimeToLive}, {MaxProposalTimeToLive}]");
            _proposalTimeToLive = value;
        }
    }
}
