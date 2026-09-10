namespace CodeyBox.Orchestrator;

/// <summary>
/// Bounds SQLite freelist growth on the state database. Deleting and
/// updating rows leaves freelist pages inside the file; without a periodic
/// VACUUM the file grows without bound (observed: 2.1 GB file with ~1.58 GB
/// reclaimable freelist against ~1.3 MB of live row content).
/// Bind under <c>CodeyBox:SqliteMaintenance</c>.
/// </summary>
public sealed class SqliteMaintenanceOptions
{
    /// <summary>Master switch. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often the freelist is inspected. Sampled fresh each iteration so
    /// edits apply without a restart. Must be positive. Default 6 hours.
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// VACUUM runs only when freelist pages reach this count. Must be at
    /// least 1. Default 100,000 pages (~400 MB at a 4 KiB page size).
    /// </summary>
    public long FreelistPageThreshold { get; set; } = 100_000;

    /// <summary>
    /// Command timeout for the VACUUM itself; rewriting a gigabyte-scale
    /// file takes minutes. Must be positive. Default 45 seconds.
    /// </summary>
    /// <remarks>
    /// This timeout is also the maintenance gate-hold budget (plus a small
    /// inspection allowance): while a VACUUM holds the write gate, dispatch
    /// pickup waits are absorbed as expected maintenance backoff instead of
    /// counting toward stuck-holder escalation, but only while the hold stays
    /// inside this budget. Keep this value at or below
    /// <c>SqliteWriteGate:AcquisitionTimeout</c> x
    /// <c>WorkerPool:MaxConsecutiveDispatchGateTimeoutsBeforeEscalation</c> —
    /// a larger value fails startup validation. Raising it for a larger
    /// database requires widening that escalation window to match, which is
    /// an explicit decision to tolerate a longer dispatch stall.
    /// </remarks>
    public TimeSpan VacuumTimeout { get; set; } = TimeSpan.FromSeconds(45);

    public void Validate()
    {
        if (CheckInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:SqliteMaintenance:CheckInterval must be positive");
        if (FreelistPageThreshold < 1)
            throw new InvalidOperationException("CodeyBox:SqliteMaintenance:FreelistPageThreshold must be >= 1");
        if (VacuumTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:SqliteMaintenance:VacuumTimeout must be positive");
    }
}
