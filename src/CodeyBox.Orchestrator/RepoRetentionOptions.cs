namespace CodeyBox.Orchestrator;

/// <summary>
/// Configuration for work-item bare git repository retention and reaping.
/// Bound from <c>CodeyBox:RepoRetention</c>.
/// </summary>
public sealed class RepoRetentionOptions
{
    /// <summary>Enable or disable work-item clone retention and reaping. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long a completed/terminal work item's clone is retained before being reaped.
    /// Default 0 (reap immediately when entering a terminal state or during startup sweep).
    /// Can be set to a positive duration (e.g. 1 hour) to retain clones for debugging or manual review.
    /// </summary>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.Zero;

    /// <summary>Alias for <see cref="GracePeriod"/>.</summary>
    public TimeSpan GraceWindow
    {
        get => GracePeriod;
        set => GracePeriod = value;
    }

    /// <summary>
    /// Cadence for the periodic background sweep of stale/terminal clones.
    /// Default 15 minutes.
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(15);
}
