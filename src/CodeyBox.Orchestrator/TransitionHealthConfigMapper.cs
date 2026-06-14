namespace CodeyBox.Orchestrator;

/// <summary>
/// Converts the configuration-binding shape into the immutable
/// <see cref="TransitionHealthOptions"/> the orchestrator consumes. Clamping
/// happens here so the snapshot never carries an out-of-range value.
/// </summary>
public static class TransitionHealthConfigMapper
{
    /// <summary>Floor for the rolling window: 5 minutes.</summary>
    public static readonly TimeSpan MinWindow = TimeSpan.FromMinutes(5);

    /// <summary>Ceiling for the rolling window: 30 days.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(30);

    /// <summary>Floor for the optional MaxTransitions cap.</summary>
    public const int MinMaxTransitions = 50;

    /// <summary>Ceiling for the optional MaxTransitions cap.</summary>
    public const int MaxMaxTransitions = 100_000;

    public static TransitionHealthOptions ToOptions(bool enabled, double windowHours, int? maxTransitions)
    {
        var window = TimeSpan.FromHours(double.IsFinite(windowHours) && windowHours > 0
            ? windowHours
            : 24.0);
        if (window < MinWindow) window = MinWindow;
        if (window > MaxWindow) window = MaxWindow;

        int? cap = maxTransitions;
        if (cap is { } c)
        {
            if (c < MinMaxTransitions) cap = MinMaxTransitions;
            else if (c > MaxMaxTransitions) cap = MaxMaxTransitions;
        }

        return new TransitionHealthOptions
        {
            Enabled = enabled,
            Window = window,
            MaxTransitions = cap,
        };
    }
}
