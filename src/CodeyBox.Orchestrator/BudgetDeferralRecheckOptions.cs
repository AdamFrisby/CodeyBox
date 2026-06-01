namespace CodeyBox.Orchestrator;

/// <summary>
/// Recheck intervals for per-project budget-cap deferrals. When a pickup
/// attempt hits the hourly/daily/concurrent ceiling or the project queue is
/// paused, the item is deferred for this configured interval before it is
/// reconsidered. Bound from <c>CodeyBox:BudgetDeferralRecheck</c> and
/// hot-reloaded via <see cref="BudgetDeferralRecheckSnapshot"/>.
/// </summary>
public sealed class BudgetDeferralRecheckOptions
{
    /// <summary>
    /// Recheck interval for items deferred by a paused project queue.
    /// Default 60 seconds.
    /// </summary>
    public TimeSpan PausedProjectRecheck { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Recheck interval for items deferred by the per-project hourly cap.
    /// Default 5 minutes.
    /// </summary>
    public TimeSpan HourlyLimitRecheck { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Recheck interval for items deferred by the per-project daily cap.
    /// Default 1 hour.
    /// </summary>
    public TimeSpan DailyLimitRecheck { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Recheck interval for items deferred by the per-project concurrent cap.
    /// Default 60 seconds.
    /// </summary>
    public TimeSpan ConcurrentLimitRecheck { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Shared, swappable holder for the current <see cref="BudgetDeferralRecheckOptions"/>.
/// Registered as a DI singleton so <see cref="OrchestratorService"/> reads
/// through the same reference the hot-reload coordinator writes to.
/// Mirrors the <see cref="AgentConcurrencySnapshot"/> pattern.
/// </summary>
public sealed class BudgetDeferralRecheckSnapshot
{
    private BudgetDeferralRecheckOptions _current;

    public BudgetDeferralRecheckSnapshot(BudgetDeferralRecheckOptions initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>
    /// Current snapshot. Volatile read so a concurrent <see cref="Replace"/>
    /// cannot tear the reference. Callers should bind once into a local for
    /// any compound read.
    /// </summary>
    public BudgetDeferralRecheckOptions Current => Volatile.Read(ref _current);

    /// <summary>
    /// Atomically publishes <paramref name="next"/> as the new snapshot.
    /// In-flight reads observe either the old or the new reference, never a
    /// partial state.
    /// </summary>
    public void Replace(BudgetDeferralRecheckOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next);
    }
}
