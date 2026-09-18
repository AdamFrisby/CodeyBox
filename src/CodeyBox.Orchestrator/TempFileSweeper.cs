using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Outcome of one <see cref="TempFileSweeper"/> pass.
/// </summary>
public sealed class TempSweepSummary
{
    public int Scanned { get; set; }
    public int Removed { get; set; }

    /// <summary>
    /// Entries left alone because they were recently written — or because
    /// staleness could not be proven (enumeration error, freshness-probe
    /// visit budget hit, nested reparse point). Unprovable entries are
    /// treated as fresh: the sweep never removes what it cannot prove stale.
    /// </summary>
    public int SkippedFresh { get; set; }
    public int SkippedSymlink { get; set; }
    public int Errors { get; set; }

    /// <summary>
    /// True when <c>MaxEntriesPerSweep</c> tripped before the directory was
    /// fully enumerated — the next startup sweep resumes the remainder.
    /// </summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Bounded startup sweep over the system temp path. Removes top-level
/// <c>codeybox-*</c> entries whose newest write is older than the configured
/// threshold — the backlog previous deployments and test runs abandoned
/// (per-instance hook dirs, SQLite test databases, sandbox staging dirs).
///
/// <para>Defensive rules, each enforced at the point of deletion:</para>
/// <list type="bullet">
/// <item>Top-level entries only; the sweep never descends looking for
/// candidates, and a directory is removed only after a bounded recursive
/// freshness probe proves nothing under it was recently written.</item>
/// <item>Entries that are symlinks (or any other reparse point) are never
/// traversed and never deleted — the link target may live outside the temp
/// path.</item>
/// <item>Every candidate's canonical full path must stay inside the swept
/// temp root; anything else is skipped.</item>
/// <item>The live hooks suppression directory is not under the temp path
/// (it lives in the per-user application-data directory), so the sweep
/// needs no exclusion for it; legacy per-instance leftovers and the old
/// predictable shared name are ordinary candidates, reaped only once
/// proven stale.</item>
/// <item>Entries the sweep cannot prove stale (enumeration errors, freshness
/// probe hitting its visit budget) are left alone.</item>
/// </list>
/// </summary>
public sealed class TempFileSweeper
{
    /// <summary>Only top-level entries starting with this prefix are candidates.</summary>
    public const string EntryPrefix = "codeybox-";

    // Caps the recursive freshness probe inside one candidate directory, so a
    // single enormous tree cannot stall startup. Hitting the cap means "cannot
    // prove stale" — the entry is skipped, never removed.
    private const int FreshnessProbeVisitCap = 10_000;

    private readonly TimeProvider _time;
    private readonly ILogger<TempFileSweeper>? _log;

    public TempFileSweeper(TimeProvider? time = null, ILogger<TempFileSweeper>? log = null)
    {
        _time = time ?? TimeProvider.System;
        _log = log;
    }

    /// <summary>
    /// Runs one bounded sweep pass. Never throws: per-entry failures are
    /// counted in <see cref="TempSweepSummary.Errors"/> and enumeration
    /// failure of the root yields an empty summary with one error.
    /// </summary>
    public TempSweepSummary Sweep(TempSweepOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var summary = new TempSweepSummary();
        var root = CanonicalRoot(options.TempDirectory);
        if (root is null)
        {
            summary.Errors++;
            _log?.LogWarning("TempFileSweeper: cannot resolve the temp root; skipping sweep");
            return summary;
        }

        List<string> entries;
        try
        {
            entries = [.. Directory.EnumerateFileSystemEntries(root)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            summary.Errors++;
            _log?.LogWarning(ex, "TempFileSweeper: failed to enumerate temp root {Root}; skipping sweep", root);
            return summary;
        }

        var maxAge = options.EffectiveMaxAge;
        var maxEntries = options.MaxEntriesPerSweep <= 0 ? int.MaxValue : options.MaxEntriesPerSweep;
        var now = _time.GetUtcNow();

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (summary.Scanned >= maxEntries)
            {
                summary.Truncated = true;
                break;
            }

            try
            {
                InspectOne(entry, root, now, maxAge, summary);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                summary.Errors++;
                _log?.LogWarning(ex, "TempFileSweeper: failed to inspect temp entry {Entry}; skipping", entry);
            }
        }

        if (summary.Removed > 0 || summary.Errors > 0)
        {
            _log?.LogInformation(
                "TempFileSweeper: sweep completed — {Removed} removed, {Scanned} scanned, {SkippedFresh} fresh, {SkippedSymlink} symlinks, {Errors} errors, truncated={Truncated}",
                summary.Removed, summary.Scanned, summary.SkippedFresh,
                summary.SkippedSymlink, summary.Errors, summary.Truncated);
        }

        return summary;
    }

    private void InspectOne(string entry, string root, DateTimeOffset now, TimeSpan maxAge, TempSweepSummary summary)
    {
        var name = Path.GetFileName(entry);
        if (!name.StartsWith(EntryPrefix, StringComparison.Ordinal))
            return;

        summary.Scanned++;

        // Canonicalize-then-contain: the enumerated path must resolve inside
        // the swept root. Enumerated entries always do, but the check is kept
        // adjacent to the sink so future callers cannot bypass it.
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(entry);
        }
        catch (Exception ex)
        {
            summary.Errors++;
            _log?.LogWarning(ex, "TempFileSweeper: cannot resolve temp entry {Entry}; skipping", entry);
            return;
        }

        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(fullPath, root, StringComparison.Ordinal))
        {
            summary.Errors++;
            _log?.LogWarning("TempFileSweeper: temp entry {Entry} resolves outside the temp root; skipping", entry);
            return;
        }

        // Never follow symlinks:Attr reads the link itself. A reparse point
        // is skipped outright — deleting the link would be safe, but leaving
        // it is safer and a stale link costs one inode.
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            summary.Errors++;
            _log?.LogWarning(ex, "TempFileSweeper: cannot stat temp entry {Entry}; skipping", fullPath);
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            summary.SkippedSymlink++;
            return;
        }

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (!TryGetNewestWriteUtc(fullPath, isDirectory, out var newestWrite))
        {
            // Freshness unprovable (I/O error or visit budget hit) — leave it.
            summary.SkippedFresh++;
            return;
        }

        if (now - newestWrite < maxAge)
        {
            summary.SkippedFresh++;
            return;
        }

        // Re-check the reparse-point bit immediately before the delete so a
        // path swapped for a symlink after the freshness probe is not
        // traversed. A residual TOCTOU window is inherent to /tmp sweeping;
        // the temp path is a trusted-local-operator surface, same as the
        // rest of the host layout CodeyBox manages.
        try
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                summary.SkippedSymlink++;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            summary.Errors++;
            return;
        }

        try
        {
            if (isDirectory)
                Directory.Delete(fullPath, recursive: true);
            else
                File.Delete(fullPath);
            summary.Removed++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            summary.Errors++;
            _log?.LogWarning(ex, "TempFileSweeper: failed to remove stale temp entry {Entry}; skipping", fullPath);
        }
    }

    private static string? CanonicalRoot(string? configured)
    {
        var raw = string.IsNullOrWhiteSpace(configured) ? Path.GetTempPath() : configured;
        string root;
        try
        {
            root = Path.GetFullPath(raw);
        }
        catch (Exception)
        {
            return null;
        }

        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    // Newest write under the entry: the entry's own mtime for files, or the
    // max mtime over a bounded recursive walk for directories (a directory's
    // own mtime only moves when children are added/removed, not when file
    // contents change, so it cannot prove staleness alone). Returns false
    // when staleness cannot be proven.
    private bool TryGetNewestWriteUtc(string fullPath, bool isDirectory, out DateTimeOffset newestWrite)
    {
        newestWrite = default;
        try
        {
            if (!isDirectory)
            {
                newestWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero);
                return true;
            }

            var newest = Directory.GetLastWriteTimeUtc(fullPath);
            var visits = 0;
            var stack = new Stack<string>();
            stack.Push(fullPath);
            while (stack.Count > 0)
            {
                string current;
                try
                {
                    current = stack.Pop();
                    foreach (var child in Directory.EnumerateFileSystemEntries(current))
                    {
                        if (++visits > FreshnessProbeVisitCap)
                            return false;
                        FileAttributes childAttributes;
                        try
                        {
                            childAttributes = File.GetAttributes(child);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            return false;
                        }

                        // Never descend through a nested link.
                        if ((childAttributes & FileAttributes.ReparsePoint) != 0)
                            return false;
                        DateTime childWrite;
                        try
                        {
                            childWrite = (childAttributes & FileAttributes.Directory) != 0
                                ? Directory.GetLastWriteTimeUtc(child)
                                : File.GetLastWriteTimeUtc(child);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            return false;
                        }

                        if (childWrite > newest)
                            newest = childWrite;
                        if ((childAttributes & FileAttributes.Directory) != 0)
                            stack.Push(child);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }

            newestWrite = new DateTimeOffset(newest, TimeSpan.Zero);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
