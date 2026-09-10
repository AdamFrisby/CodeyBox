using Microsoft.Data.Sqlite;

namespace CodeyBox.Tests;

/// <summary>
/// Isolated temp directory for tests that need filesystem scratch space
/// (SQLite databases, sandbox workspaces, seed repos).
///
/// Replaces the old pattern of dropping uniquely-named files directly into
/// the shared temp directory and deleting only the well-known file on
/// teardown — which leaked SQLite <c>-wal</c>/<c>-shm</c> companions (a WAL
/// pair outlives a carelessly-closed connection) and left whole directories
/// behind whenever a test failed before its cleanup line ran.
///
/// Ownership rule: dispose every connection or handle rooted in
/// <see cref="DirectoryPath"/> (e.g. <c>SqliteWorkItemStore.Dispose</c>) BEFORE
/// disposing the scratch directory, otherwise the recursive delete retries
/// against an open handle and the leak recurs. xUnit calls
/// <see cref="IDisposable.Dispose"/> even when the test body throws, so
/// cleanup is deterministic on both pass and fail.
/// </summary>
internal sealed class TestScratchDirectory : IDisposable
{
    /// <summary>
    /// Environment variable overriding the scratch root. Must be an absolute
    /// path when set. Unset (the norm) falls back to a per-user subdirectory
    /// of the standard temp-path API so the suite never hardcodes a
    /// machine-specific location and never sweeps the shared temp root.
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

    private static readonly string[] SqliteCompanionSuffixes = ["-wal", "-shm", "-journal"];

    private const int DeleteAttempts = 5;
    private const int DeleteRetryDelayMilliseconds = 50;

    private int _disposed;

    private TestScratchDirectory(string path)
    {
        DirectoryPath = path;
    }

    /// <summary>Absolute path of the scratch directory.</summary>
    internal string DirectoryPath { get; }

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
        if (System.IO.Path.IsPathRooted(fileName))
            throw new ArgumentException("Database file name must be a bare file name, not a rooted path.", nameof(fileName));
        if (fileName.IndexOfAny(new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar }) >= 0)
            throw new ArgumentException("Database file name must not contain directory separators.", nameof(fileName));
        if (!string.Equals(fileName, System.IO.Path.GetFileName(fileName), StringComparison.Ordinal))
            throw new ArgumentException("Database file name must be a bare file name.", nameof(fileName));

        var combined = System.IO.Path.GetFullPath(System.IO.Path.Combine(DirectoryPath, fileName));
        var containedPrefix = DirectoryPath.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? DirectoryPath
            : DirectoryPath + System.IO.Path.DirectorySeparatorChar;
        if (!combined.StartsWith(containedPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Database file name escapes the scratch directory.", nameof(fileName));
        return combined;
    }

    /// <summary>
    /// Removes stale files and directories from previous runs. Only entries
    /// directly under <paramref name="root"/> whose names carry a managed
    /// prefix and whose last write is older than <paramref name="maxAge"/>
    /// are removed. Symbolic links and other reparse points are never
    /// followed: the link itself is removed without recursion. Best-effort:
    /// IO failures are swallowed so a wedged leftover can never fail test
    /// setup, but programming errors still throw.
    /// </summary>
    internal static void SweepStale(string root, TimeSpan maxAge)
    {
        string canonicalRoot;
        try
        {
            canonicalRoot = System.IO.Path.GetFullPath(root);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - maxAge;
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(canonicalRoot);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
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

                // Canonicalize-then-contain: never touch anything that does
                // not resolve directly beneath the sweep root.
                string fullEntry;
                try
                {
                    fullEntry = System.IO.Path.GetFullPath(entry);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                var containedPrefix = canonicalRoot.EndsWith(System.IO.Path.DirectorySeparatorChar)
                    ? canonicalRoot
                    : canonicalRoot + System.IO.Path.DirectorySeparatorChar;
                if (!fullEntry.StartsWith(containedPrefix, StringComparison.Ordinal))
                    continue;

                // No-follow: a symlinked entry is removed as a link, never
                // recursed into, so a planted link cannot redirect the
                // recursive delete at an arbitrary host path.
                if (IsSymbolicLink(entry))
                {
                    DeleteLinkNoFollow(entry);
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    if (Directory.GetLastWriteTimeUtc(entry) > cutoff.UtcDateTime)
                        continue;
                    // Re-check immediately before the sink: the entry could
                    // have been swapped for a link between the first check
                    // and the delete (TOCTOU). Never recurse into a link.
                    if (IsSymbolicLink(entry))
                    {
                        DeleteLinkNoFollow(entry);
                        continue;
                    }

                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    if (File.GetLastWriteTimeUtc(entry) > cutoff.UtcDateTime)
                        continue;
                    if (IsSymbolicLink(entry))
                    {
                        DeleteLinkNoFollow(entry);
                        continue;
                    }

                    File.Delete(entry);
                    if (name.EndsWith(".db", StringComparison.Ordinal))
                        DeleteSqliteCompanions(entry);
                }
            }
            catch (IOException)
            {
                // Stale-sweep is self-healing hygiene, never a test gate.
            }
            catch (UnauthorizedAccessException)
            {
                // Locked or privileged leftover; a later run retries it.
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
        catch (IOException)
        {
            // Pool clearing is opportunistic; fall through to the delete.
        }
        catch (UnauthorizedAccessException)
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

        foreach (var suffix in SqliteCompanionSuffixes)
        {
            try
            {
                File.Delete(dbPath + suffix);
            }
            catch (IOException)
            {
                // Best-effort companion cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort companion cleanup.
            }
        }
    }

    /// <summary>
    /// Resolves the scratch root: <see cref="ScratchRootVariable"/> when set
    /// to an absolute path (canonicalized), else a per-user subdirectory of
    /// the standard temp-path API so the stale sweep never operates directly
    /// on the shared temp root.
    /// </summary>
    internal static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable(ScratchRootVariable);
        if (!string.IsNullOrWhiteSpace(configured) && System.IO.Path.IsPathRooted(configured))
        {
            try
            {
                return System.IO.Path.GetFullPath(configured);
            }
            catch (IOException)
            {
                // Fall through to the default root.
            }
            catch (UnauthorizedAccessException)
            {
                // Fall through to the default root.
            }
            catch (ArgumentException)
            {
                // Malformed override; fall through to the default root.
            }
            catch (NotSupportedException)
            {
                // Malformed override; fall through to the default root.
            }
        }

        return DefaultRoot();
    }

    internal static string DefaultRoot()
    {
        var user = Environment.UserName ?? string.Empty;
        var safe = new string(user.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (string.IsNullOrEmpty(safe))
            safe = "shared";
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codeybox-tests-{safe}"));
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

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null)
                return true;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            if (new DirectoryInfo(path).LinkTarget is not null)
                return true;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteLinkNoFollow(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        // A directory symlink may survive File.Delete on some runtimes;
        // remove the link itself, never recursively.
        try
        {
            if (Directory.Exists(path) && IsSymbolicLink(path))
                Directory.Delete(path, recursive: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Never recurse into a link planted at our own path: remove the link
        // itself instead of following it.
        if (IsSymbolicLink(DirectoryPath))
        {
            DeleteLinkNoFollow(DirectoryPath);
            return;
        }

        for (var attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                    Directory.Delete(DirectoryPath, recursive: true);
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
