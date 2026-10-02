namespace CodeyBox.Majordomo;

/// <summary>
/// Operator-selected, hot-reloadable bounds for the durable majordomo
/// conversation and the context assembled from it. Every bound rejects
/// invalid values at set time rather than silently weakening on a typo.
/// </summary>
public sealed record MajordomoHistoryOptions
{
    /// <summary>Default per-row cap on stored entry text.</summary>
    public const int DefaultMaxEntryChars = 16384;

    /// <summary>Smallest accepted <see cref="MaxEntryChars"/>.</summary>
    public const int MinMaxEntryChars = 1024;

    /// <summary>Largest accepted <see cref="MaxEntryChars"/>.</summary>
    public const int MaxMaxEntryChars = 262144;

    /// <summary>Default cap on stored conversation rows before compaction.</summary>
    public const int DefaultMaxEntries = 2000;

    /// <summary>Smallest accepted <see cref="MaxEntries"/>.</summary>
    public const int MinMaxEntries = 100;

    /// <summary>Largest accepted <see cref="MaxEntries"/>.</summary>
    public const int MaxMaxEntries = 100000;

    /// <summary>Default total bound on one assembled context, in characters.</summary>
    public const int DefaultMaxContextChars = 12000;

    /// <summary>Smallest accepted <see cref="MaxContextChars"/>.</summary>
    public const int MinMaxContextChars = 1000;

    /// <summary>Largest accepted <see cref="MaxContextChars"/>.</summary>
    public const int MaxMaxContextChars = 200000;

    /// <summary>Default cap on recent entries replayed verbatim.</summary>
    public const int DefaultMaxRecentEntries = 50;

    /// <summary>Smallest accepted <see cref="MaxRecentEntries"/>.</summary>
    public const int MinMaxRecentEntries = 1;

    /// <summary>Largest accepted <see cref="MaxRecentEntries"/>.</summary>
    public const int MaxMaxRecentEntries = 500;

    /// <summary>Default budget for the summary section of one assembled context.</summary>
    public const int DefaultMaxSummaryChars = 2000;

    /// <summary>Default budget for the fleet section of one assembled context.</summary>
    public const int DefaultMaxFleetChars = 2000;

    /// <summary>Smallest accepted summary/fleet budget (0 omits the section).</summary>
    public const int MinSectionChars = 0;

    /// <summary>Largest accepted summary/fleet budget.</summary>
    public const int MaxSectionChars = 16000;

    private int _maxEntryChars = DefaultMaxEntryChars;
    private int _maxEntries = DefaultMaxEntries;
    private int _maxContextChars = DefaultMaxContextChars;
    private int _maxRecentEntries = DefaultMaxRecentEntries;
    private int _maxSummaryChars = DefaultMaxSummaryChars;
    private int _maxFleetChars = DefaultMaxFleetChars;

    /// <summary>
    /// Per-row cap on stored entry text. Longer payloads are truncated with
    /// a marker before they are persisted, so one huge tool result cannot
    /// grow the state database at request rate.
    /// </summary>
    public int MaxEntryChars
    {
        get => _maxEntryChars;
        init
        {
            if (value < MinMaxEntryChars || value > MaxMaxEntryChars)
                throw new ArgumentOutOfRangeException(
                    nameof(MaxEntryChars), value,
                    $"MaxEntryChars must be within [{MinMaxEntryChars}, {MaxMaxEntryChars}]");
            _maxEntryChars = value;
        }
    }

    /// <summary>
    /// Cap on stored conversation rows. Appending past it compacts the
    /// oldest excess into the deterministic summary (summarised, never
    /// dropped) inside the same write.
    /// </summary>
    public int MaxEntries
    {
        get => _maxEntries;
        init
        {
            if (value < MinMaxEntries || value > MaxMaxEntries)
                throw new ArgumentOutOfRangeException(
                    nameof(MaxEntries), value,
                    $"MaxEntries must be within [{MinMaxEntries}, {MaxMaxEntries}]");
            _maxEntries = value;
        }
    }

    /// <summary>
    /// Total bound on one assembled context, in characters. Assembly never
    /// returns more than this, no matter how far history has grown past it.
    /// </summary>
    public int MaxContextChars
    {
        get => _maxContextChars;
        init
        {
            if (value < MinMaxContextChars || value > MaxMaxContextChars)
                throw new ArgumentOutOfRangeException(
                    nameof(MaxContextChars), value,
                    $"MaxContextChars must be within [{MinMaxContextChars}, {MaxMaxContextChars}]");
            _maxContextChars = value;
        }
    }

    /// <summary>Cap on recent entries replayed verbatim in one assembly.</summary>
    public int MaxRecentEntries
    {
        get => _maxRecentEntries;
        init
        {
            if (value < MinMaxRecentEntries || value > MaxMaxRecentEntries)
                throw new ArgumentOutOfRangeException(
                    nameof(MaxRecentEntries), value,
                    $"MaxRecentEntries must be within [{MinMaxRecentEntries}, {MaxMaxRecentEntries}]");
            _maxRecentEntries = value;
        }
    }

    /// <summary>
    /// Budget for the summary section of one assembled context. Zero omits
    /// the section (covered entries are then reported as a count line).
    /// </summary>
    public int MaxSummaryChars
    {
        get => _maxSummaryChars;
        init
        {
            if (value < MinSectionChars || value > MaxSectionChars)
                throw new ArgumentOutOfRangeException(
                    nameof(MaxSummaryChars), value,
                    $"MaxSummaryChars must be within [{MinSectionChars}, {MaxSectionChars}]");
            _maxSummaryChars = value;
        }
    }

    /// <summary>
    /// Budget for the live fleet section of one assembled context. Zero
    /// omits the section.
    /// </summary>
    public int MaxFleetChars
    {
        get => _maxFleetChars;
        init
        {
            if (value < MinSectionChars || value > MaxSectionChars)
                throw new ArgumentOutOfRangeException(
                    nameof(MaxFleetChars), value,
                    $"MaxFleetChars must be within [{MinSectionChars}, {MaxSectionChars}]");
            _maxFleetChars = value;
        }
    }

    /// <summary>Hot-reload validator: returns the failure message or null.</summary>
    public static string? Validate(MajordomoHistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxEntryChars < MinMaxEntryChars || options.MaxEntryChars > MaxMaxEntryChars)
            return $"MaxEntryChars must be within [{MinMaxEntryChars}, {MaxMaxEntryChars}]";
        if (options.MaxEntries < MinMaxEntries || options.MaxEntries > MaxMaxEntries)
            return $"MaxEntries must be within [{MinMaxEntries}, {MaxMaxEntries}]";
        if (options.MaxContextChars < MinMaxContextChars || options.MaxContextChars > MaxMaxContextChars)
            return $"MaxContextChars must be within [{MinMaxContextChars}, {MaxMaxContextChars}]";
        if (options.MaxRecentEntries < MinMaxRecentEntries || options.MaxRecentEntries > MaxMaxRecentEntries)
            return $"MaxRecentEntries must be within [{MinMaxRecentEntries}, {MaxMaxRecentEntries}]";
        if (options.MaxSummaryChars < MinSectionChars || options.MaxSummaryChars > MaxSectionChars)
            return $"MaxSummaryChars must be within [{MinSectionChars}, {MaxSectionChars}]";
        if (options.MaxFleetChars < MinSectionChars || options.MaxFleetChars > MaxSectionChars)
            return $"MaxFleetChars must be within [{MinSectionChars}, {MaxSectionChars}]";
        return null;
    }
}
