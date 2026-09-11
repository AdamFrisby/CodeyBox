namespace CodeyBox.Orchestrator;

/// <summary>
/// Single source of truth for SQLite connection policy on the state database.
/// <c>busy_timeout</c> is per-connection SQLite state (default 0 = fail
/// immediately), so every connection — the work-item store writer, all of its
/// short-lived readers, the worker registry writer and readers, and the
/// maintenance connection — must apply the same value or routine WAL lock
/// contention surfaces as <c>SQLITE_BUSY</c> on whichever connection missed
/// it. The extended error-code constants below name the same lock-contention
/// codes everywhere instead of repeating bare numeric literals.
/// </summary>
internal static class SqliteDefaults
{
    /// <summary>
    /// Lock-wait budget applied to every connection opened against the state
    /// database. Operational default, not a hot knob: changing it requires a
    /// restart so every pooled connection picks it up consistently.
    /// </summary>
    public const int BusyTimeoutMilliseconds = 30000;

    /// <summary>SQLite primary result code for a locked table (<c>SQLITE_BUSY</c>).</summary>
    public const int SqliteBusy = 5;

    /// <summary>SQLite primary result code for a locked database (<c>SQLITE_LOCKED</c>).</summary>
    public const int SqliteLocked = 6;
}
