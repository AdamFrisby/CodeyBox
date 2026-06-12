namespace CodeyBox.Orchestrator;

/// <summary>
/// Shared SQLite connection teardown helpers for stores that own a
/// <see cref="Microsoft.Data.Sqlite.SqliteConnection"/>.
/// </summary>
internal static class SqliteConnectionDisposal
{
    /// <summary>
    /// Disposes a connection, tolerating the internal teardown-race exceptions
    /// that <c>Microsoft.Data.Sqlite.SqliteConnection.Close()</c> has been
    /// observed to throw intermittently when a still-in-flight async command
    /// races against connection disposal:
    /// <list type="bullet">
    ///   <item><see cref="NullReferenceException"/> from inside the driver's
    ///   connection close path.</item>
    ///   <item><see cref="InvalidOperationException"/> "Collection was modified;
    ///   enumeration operation may not execute" from
    ///   <c>SqliteCommand.DisposePreparedStatements</c> when an in-flight
    ///   finalize mutates the prepared-statement list mid-iteration.</item>
    /// </list>
    /// The connection is being discarded either way, so swallowing these races
    /// keeps the dispose contract clean; callers should still release every
    /// other owned resource in a <c>finally</c>. Exceptions that do not
    /// originate inside the SQLite driver bubble unchanged so unrelated bugs
    /// stay visible.
    /// </summary>
    internal static void DisposeTolerantOfTeardownRace(IDisposable connection)
    {
        try
        {
            connection.Dispose();
        }
        catch (NullReferenceException ex) when (IsSqliteTeardownRace(ex))
        {
            // Internal Sqlite teardown race; safe to ignore because the connection is being discarded.
        }
        catch (InvalidOperationException ex) when (IsSqliteTeardownRace(ex))
        {
            // SqliteCommand.DisposePreparedStatements race against an in-flight finalize.
        }
    }

    /// <summary>
    /// True iff <paramref name="ex"/> was thrown from inside the
    /// <c>Microsoft.Data.Sqlite</c> driver's dispose / close path. Narrowed by
    /// stack-trace inspection so unrelated <see cref="InvalidOperationException"/>
    /// instances still surface.
    /// </summary>
    private static bool IsSqliteTeardownRace(Exception ex)
    {
        var trace = ex.StackTrace;
        return trace is not null
            && trace.Contains("Microsoft.Data.Sqlite", StringComparison.Ordinal);
    }
}
