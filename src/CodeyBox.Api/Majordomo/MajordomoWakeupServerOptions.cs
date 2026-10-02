using CodeyBox.Majordomo;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// Operator configuration for majordomo self-wakeups, bound from the
/// <c>CodeyBox:Majordomo:Wakeup</c> section and hot-reloadable: the
/// coordinator reads the current value on every tick so a cadence change
/// applies without a restart.
/// </summary>
public sealed class MajordomoWakeupServerOptions
{
    /// <summary>Default scheduled cadence in seconds (15 minutes).</summary>
    public static readonly int DefaultWakeupIntervalSeconds = (int)MajordomoWakeupOptions.DefaultWakeupInterval.TotalSeconds;

    /// <summary>Shortest accepted cadence in seconds (1 minute).</summary>
    public static readonly int MinWakeupIntervalSeconds = (int)MajordomoWakeupOptions.MinWakeupInterval.TotalSeconds;

    /// <summary>Longest accepted cadence in seconds (24 hours).</summary>
    public static readonly int MaxWakeupIntervalSeconds = (int)MajordomoWakeupOptions.MaxWakeupInterval.TotalSeconds;

    /// <summary>Default burst-collapsing floor in seconds (5 minutes).</summary>
    public static readonly int DefaultMinTriggerIntervalSeconds = (int)MajordomoWakeupOptions.DefaultMinTriggerInterval.TotalSeconds;

    /// <summary>Shortest accepted floor in seconds (30 seconds).</summary>
    public static readonly int MinTriggerIntervalSecondsFloor = (int)MajordomoWakeupOptions.MinTriggerIntervalFloor.TotalSeconds;

    /// <summary>Longest accepted floor in seconds (1 hour).</summary>
    public static readonly int MaxTriggerIntervalSecondsFloor = (int)MajordomoWakeupOptions.MaxTriggerIntervalFloor.TotalSeconds;

    /// <summary>Whether scheduled and event wakeups fire at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Cadence between scheduled wakeups, in seconds. Between
    /// <see cref="MinWakeupIntervalSeconds"/> and
    /// <see cref="MaxWakeupIntervalSeconds"/> seconds.
    /// </summary>
    public int WakeupIntervalSeconds { get; set; } = DefaultWakeupIntervalSeconds;

    /// <summary>
    /// Minimum interval between event-triggered wakeups, in seconds: a burst
    /// of triggers inside this window produces one considered pass, not one
    /// wakeup per trigger. Between <see cref="MinTriggerIntervalSecondsFloor"/>
    /// and <see cref="MaxTriggerIntervalSecondsFloor"/> seconds.
    /// </summary>
    public int MinTriggerIntervalSeconds { get; set; } = DefaultMinTriggerIntervalSeconds;

    /// <summary>
    /// Purpose prompt carried by each scheduled tick — by default a
    /// queue-health pass. At most
    /// <see cref="MajordomoWakeupOptions.MaxPurposePromptChars"/> characters.
    /// </summary>
    public string PurposePrompt { get; set; } = MajordomoWakeupOptions.DefaultPurposePrompt;

    /// <summary>
    /// Bound on one wakeup report before it is stored, in characters. Between
    /// <see cref="MajordomoWakeupOptions.MinMaxReportChars"/> and
    /// <see cref="MajordomoWakeupOptions.MaxMaxReportChars"/>.
    /// </summary>
    public int MaxReportChars { get; set; } = MajordomoWakeupOptions.DefaultMaxReportChars;

    /// <summary>Builds the policy record the wakeup coordinator consumes.</summary>
    public MajordomoWakeupOptions ToWakeupOptions() => new()
    {
        Enabled = Enabled,
        WakeupInterval = TimeSpan.FromSeconds(WakeupIntervalSeconds),
        MinTriggerInterval = TimeSpan.FromSeconds(MinTriggerIntervalSeconds),
        PurposePrompt = PurposePrompt,
        MaxReportChars = MaxReportChars,
    };

    /// <summary>Hot-reload validator: returns the failure message or null.</summary>
    public static string? Validate(MajordomoWakeupServerOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (opts.WakeupIntervalSeconds < MinWakeupIntervalSeconds
            || opts.WakeupIntervalSeconds > MaxWakeupIntervalSeconds)
            return $"WakeupIntervalSeconds must be within [{MinWakeupIntervalSeconds}, {MaxWakeupIntervalSeconds}]";
        if (opts.MinTriggerIntervalSeconds < MinTriggerIntervalSecondsFloor
            || opts.MinTriggerIntervalSeconds > MaxTriggerIntervalSecondsFloor)
            return $"MinTriggerIntervalSeconds must be within [{MinTriggerIntervalSecondsFloor}, {MaxTriggerIntervalSecondsFloor}]";
        if (string.IsNullOrWhiteSpace(opts.PurposePrompt))
            return "PurposePrompt must be non-empty";
        if (opts.PurposePrompt.Length > MajordomoWakeupOptions.MaxPurposePromptChars)
            return $"PurposePrompt must be at most {MajordomoWakeupOptions.MaxPurposePromptChars} characters";
        if (opts.MaxReportChars < MajordomoWakeupOptions.MinMaxReportChars
            || opts.MaxReportChars > MajordomoWakeupOptions.MaxMaxReportChars)
            return $"MaxReportChars must be within [{MajordomoWakeupOptions.MinMaxReportChars}, {MajordomoWakeupOptions.MaxMaxReportChars}]";
        return null;
    }
}
