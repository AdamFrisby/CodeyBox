using Microsoft.Data.Sqlite;

namespace CodeyBox.Tests;

/// <summary>
/// Isolated temp directory for tests that need filesystem scratch space
/// (SQLite databases, sandbox workspaces, seed repos).
///
/// Replaces the old pattern of dropping uniquely-named files directly into
/// <see cref="Path.GetTempPath"/> and deleting only the well-known file on
/// teardown — which leaked SQLite <c>-wal</c>/<c>-shm</c> companions (a WAL
/// pair outlives a carelessly-closed connection) and left whole directories
/// behind whenever a test failed before its cleanup line ran.
///
/// Ownership rule: dispose every connection or handle rooted in
/// <see cref="Path"/> (e.g. <c>SqliteWorkItemStore.Dispose</c>) BEFORE
/// disposing the scratch directory, otherwise the recursive delete retries
/// against an open handle and the leak recurs. xUnit calls
/// <see cref="IDisposable.Dispose"/> even when the test body throws, so
/// cleanup is deterministic on both pass and fail.
/// </summary>
internal sealed class TestScratchDirectory : IDisposable
{
    /// <summary>
    /// Environment variable overriding the scratch root. Must be an absolute
    /// path when set. Unset (the norm) falls back to the standard temp-path
    /// API so the suite never hardcodes a machine-specific location.
    /// </summary>
    internal const string ScratchRootVariable = "CODEYBOX_TEST_SCRATCH_ROOT";

    /// <summary>
    /// Environment variable overriding <see cref="DefaultStaleMaxAgeHours"/>.
    /// </summary>
    internal const string StaleMaxAgeHoursVariable = "CODEYBOX_TEST_SCRATCH_MAX_AGE_HOURS";

    /// <summary>
    /// Default age after which a leftover entry from a previous run is
    /// considered stale and removed on setup. Entries from the currently
    /// running suite are seconds old, so a day-scale threshold never races
    /// with live fixtures while still letting an existing install self-heal.
    /// Unit: hours.
    /// </summary>
    internal const double DefaultStaleMaxAgeHours = 24;

    /// <summary>
    /// Only entries carrying these prefixes are ever swept. The sweep must
    /// never touch unrelated temp content owned by other processes.
    /// </summary>
    internal static readonly string[] ManagedPrefixes = ["codeybox-", "cb-"];

    private const int DeleteAttempts = 5;
    private const int DeleteRetryDelayMilliseconds = 50;

    private int _disposed;

    private TestScratchDirectory(string path)
    {
        Path = path;
    }

    /// <summary>Absolute path of the scratch directory.</summary>
    internal string Path { get; }

    /// <summary>
    /// Creates a uniquely-named scratch directory under the configured root
    /// and sweeps stale leftovers from previous runs first.
    /// </summary>
    /// <param name="prefix">Short lowercase prefix; must start with a managed prefix.</param>
    /// <param name="staleMaxAge">
    /// Staleness threshold for the setup sweep. Defaults to
    /// <see cref="DefaultStaleMaxAgeHours"/> (overridable via
    /// <see cref="StaleMaxAgeHoursVariable"/>).
    /// </param>
    internal static TestScratchDirectory Create(string prefix, TimeSpan? staleMaxAge = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (!ManagedPrefixes.Any(p => prefix.StartsWith(p, StringComparison.Ordinal)))
            throw new ArgumentException(
                $"Scratch prefix '{prefix}' must start with one of: {string.Join(", ", ManagedPrefixes)}.",
                nameof(prefix));

        var root = ResolveRoot();
        Directory.CreateDirectory(root);
        SweepStale(root, staleMaxAge ?? ResolveStaleMaxAge());

        var path = System.IO.Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TestScratchDirectory(path);
    }

    /// <summary>Returns a database file path inside the scratch directory.</summary>
    internal string DbPath(string fileName = "store.db")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return System.IO.Path.Combine(Path, fileName);
    }

    /// <summary>
    /// Removes stale files and directories from previous runs. Only entries
    /// under <paramref name="root"/> whose names carry a managed prefix and
    /// whose last write is older than <paramref name="maxAge"/> are removed.
    /// Best-effort: failures are swallowed so a wedged leftover can never
    /// fail test setup.
    /// </summary>
    internal static void SweepStale(string root, TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(root);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                var name = System.IO.Path.GetFileName(entry);
                if (!ManagedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
                    continue;
                if (Directory.Exists(entry))
                {
                    if (Directory.GetLastWriteTimeUtc(entry) > cutoff.UtcDateTime)
                        continue;
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    if (File.GetLastWriteTimeUtc(entry) > cutoff.UtcDateTime)
                        continue;
                    File.Delete(entry);
                    if (name.EndsWith(".db", StringComparison.Ordinal))
                        DeleteSqliteCompanions(entry);
                }
            }
            catch (Exception)
            {
                // Stale-sweep is self-healing hygiene, never a test gate.
            }
        }
    }

    /// <summary>
    /// Clears the shared SQLite connection pool. Pooled handles keep
    /// <c>-wal</c>/<c>-shm</c> alive briefly after the owning connection is
    /// disposed; clearing makes the sidecars unlinkable. Transparent to
    /// other tests — the pool re-establishes on next use. Best-effort.
    /// </summary>
    internal static void ClearSqlitePools()
    {
        try
        {
            SqliteConnection.ClearAllPools();
        }
        catch (Exception)
        {
            // Pool clearing is opportunistic; fall through to the delete.
        }
    }

    /// <summary>
    /// Deletes a SQLite database's sidecar files (<c>-wal</c>, <c>-shm</c>,
    /// <c>-journal</c>). Call after disposing the owning connection; pooled
    /// handles are cleared first so the companions are actually unlinkable.
    /// Best-effort.
    /// </summary>
    internal static void DeleteSqliteCompanions(string dbPath)
    {
        ClearSqlitePools();

        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            try
            {
                File.Delete(dbPath + suffix);
            }
            catch (Exception)
            {
                // Best-effort companion cleanup.
            }
        }
    }

    /// <summary>
    /// Resolves the scratch root: <see cref="ScratchRootVariable"/> when set
    /// to an absolute path, else the standard temp-path API.
    /// </summary>
    internal static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable(ScratchRootVariable);
        if (!string.IsNullOrWhiteSpace(configured) && System.IO.Path.IsPathRooted(configured))
            return configured;
        return System.IO.Path.GetTempPath();
    }

    internal static TimeSpan ResolveStaleMaxAge()
    {
        var raw = Environment.GetEnvironmentVariable(StaleMaxAgeHoursVariable);
        if (double.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var hours)
            && hours > 0)
            return TimeSpan.FromHours(hours);
        return TimeSpan.FromHours(DefaultStaleMaxAgeHours);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        for (var attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < DeleteAttempts - 1)
            {
                // A pooled SQLite handle can hold -wal/-shm briefly after the
                // owning connection was disposed; clear the pools and retry
                // rather than leaking the directory.
                ClearSqlitePools();
            }
            catch (UnauthorizedAccessException) when (attempt < DeleteAttempts - 1)
            {
                ClearSqlitePools();
            }

            Thread.Sleep(DeleteRetryDelayMilliseconds);
        }
    }
}
