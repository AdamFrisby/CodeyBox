namespace CodeyBox.Majordomo;

/// <summary>
/// Operator-selected, hot-reloadable policy for majordomo self-wakeups: the
/// scheduled cadence, the event burst-collapsing floor, and the purpose
/// prompt each scheduled tick carries. Every bound rejects invalid values at
/// set time rather than silently weakening on a config typo.
/// </summary>
public sealed record MajordomoWakeupOptions
{
    /// <summary>Default scheduled cadence between wakeups.</summary>
    public static readonly TimeSpan DefaultWakeupInterval = TimeSpan.FromMinutes(15);

    /// <summary>Shortest accepted scheduled cadence.</summary>
    public static readonly TimeSpan MinWakeupInterval = TimeSpan.FromMinutes(1);

    /// <summary>Longest accepted scheduled cadence.</summary>
    public static readonly TimeSpan MaxWakeupInterval = TimeSpan.FromHours(24);

    /// <summary>Default floor collapsing a burst of event triggers into one wakeup.</summary>
    public static readonly TimeSpan DefaultMinTriggerInterval = TimeSpan.FromMinutes(5);

    /// <summary>Shortest accepted trigger floor.</summary>
    public static readonly TimeSpan MinTriggerIntervalFloor = TimeSpan.FromSeconds(30);

    /// <summary>Longest accepted trigger floor.</summary>
    public static readonly TimeSpan MaxTriggerIntervalFloor = TimeSpan.FromHours(1);

    /// <summary>
    /// Default purpose prompt carried by each scheduled tick: a queue-health
    /// pass over failures, stuck items, quota blocks, and operator attention.
    /// </summary>
    public const string DefaultPurposePrompt =
        "Queue-health pass: what failed since last time, what is stuck, what is quota-blocked, what needs the operator.";

    /// <summary>Longest accepted purpose prompt, in characters.</summary>
    public const int MaxPurposePromptChars = 4000;

    /// <summary>Default bound on one wakeup report stored in the conversation.</summary>
    public const int DefaultMaxReportChars = 8000;

    /// <summary>Smallest accepted wakeup report bound.</summary>
    public const int MinMaxReportChars = 1024;

    /// <summary>Largest accepted wakeup report bound.</summary>
    public const int MaxMaxReportChars = 65536;

    private TimeSpan _wakeupInterval = DefaultWakeupInterval;
    private TimeSpan _minTriggerInterval = DefaultMinTriggerInterval;
    private string _purposePrompt = DefaultPurposePrompt;
    private int _maxReportChars = DefaultMaxReportChars;

    /// <summary>Whether scheduled and event wakeups fire at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Cadence between scheduled wakeups.</summary>
    public TimeSpan WakeupInterval
    {
        get => _wakeupInterval;
        init
        {
            if (value < MinWakeupInterval || value > MaxWakeupInterval)
                throw new ArgumentOutOfRangeException(
                    nameof(WakeupInterval), value,
                    $"WakeupInterval must be within [{MinWakeupInterval}, {MaxWakeupInterval}]");
            _wakeupInterval = value;
        }
    }

    /// <summary>
    /// Minimum interval between any two wakeups fired by event triggers: a
    /// burst of failures inside this window produces one considered pass,
    /// not one wakeup per failure.
    /// </summary>
    public TimeSpan MinTriggerInterval
    {
        get => _minTriggerInterval;
        init
        {
            if (value < MinTriggerIntervalFloor || value > MaxTriggerIntervalFloor)
                throw new ArgumentOutOfRangeException(
                    nameof(MinTriggerInterval), value,
                    $"MinTriggerInterval must be within [{MinTriggerIntervalFloor}, {MaxTriggerIntervalFloor}]");
            _minTriggerInterval = value;
        }
    }

    /// <summary>Purpose prompt carried by each scheduled tick.</summary>
    public string PurposePrompt
    {
        get => _purposePrompt;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (value.Length > MaxPurposePromptChars)
                throw new ArgumentOutOfRangeException(
                    nameof(PurposePrompt), value.Length,
                    $"PurposePrompt must be at most {MaxPurposePromptChars} characters");
            _purposePrompt = value;
        }
    }

    /// <summary>
    /// Bound on one wakeup report before it is stored: longer reports are
    /// truncated with a marker so one verbose pass cannot grow the state
    /// database at wakeup rate.
    /// </summary>
    public int MaxReportChars
    {
        get => _maxReportChars;
        init
        {
            if (value < MinMaxReportChars || value > MaxMaxReportChars)
                throw new ArgumentOutOfRangeException(
                    nameof(MaxReportChars), value,
                    $"MaxReportChars must be within [{MinMaxReportChars}, {MaxMaxReportChars}]");
            _maxReportChars = value;
        }
    }

    /// <summary>Hot-reload validator: returns the failure message or null.</summary>
    public static string? Validate(MajordomoWakeupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.WakeupInterval < MinWakeupInterval || options.WakeupInterval > MaxWakeupInterval)
            return $"WakeupInterval must be within [{MinWakeupInterval}, {MaxWakeupInterval}]";
        if (options.MinTriggerInterval < MinTriggerIntervalFloor || options.MinTriggerInterval > MaxTriggerIntervalFloor)
            return $"MinTriggerInterval must be within [{MinTriggerIntervalFloor}, {MaxTriggerIntervalFloor}]";
        if (string.IsNullOrWhiteSpace(options.PurposePrompt))
            return "PurposePrompt must be non-empty";
        if (options.PurposePrompt.Length > MaxPurposePromptChars)
            return $"PurposePrompt must be at most {MaxPurposePromptChars} characters";
        if (options.MaxReportChars < MinMaxReportChars || options.MaxReportChars > MaxMaxReportChars)
            return $"MaxReportChars must be within [{MinMaxReportChars}, {MaxMaxReportChars}]";
        return null;
    }
}
