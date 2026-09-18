using System.Runtime.InteropServices;
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
/// How one proven-stale temp entry fared at the deletion sink. Internal so
/// tests can exercise the sink directly, bypassing the freshness probe.
/// </summary>
internal enum TempEntryDeleteOutcome
{
    Removed,
    SkippedSymlink,
    Failed,
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
/// <item>Deletion itself never traverses a link either: on Linux every
/// component is opened <c>O_NOFOLLOW</c> relative to a pinned directory
/// descriptor and removed with <c>unlinkat</c>, so a path swapped for a
/// symlink after the freshness probe fails with <c>ELOOP</c> and aborts the
/// entry instead of diverting deletion outside the temp root. Where native
/// descriptors are unavailable each node is re-validated immediately before
/// a non-recursive remove, and any link aborts the whole entry.</item>
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

    // Caps the deletion walk inside one candidate directory, so removing a
    // single enormous stale tree cannot stall startup either. Tripping the
    // cap aborts the entry (counted as an error) and leaves the remainder
    // for the next startup sweep.
    private const int DeletionVisitCap = 50_000;

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

        // Delete without traversing links. The freshness probe above is only a
        // staleness proof, never a deletion guard: DeleteProvenStaleEntry
        // re-validates every component at the sink, so a path swapped for a
        // symlink after the probe aborts the entry instead of diverting
        // deletion outside the temp root. (A UID-ownership gate was
        // considered and rejected: the sweep must reap leftovers from prior
        // deployments and test runs that may have run under a different UID;
        // link-safety, not ownership, is the guard.)
        switch (DeleteProvenStaleEntry(root, name, fullPath))
        {
            case TempEntryDeleteOutcome.Removed:
                summary.Removed++;
                break;
            case TempEntryDeleteOutcome.SkippedSymlink:
                summary.SkippedSymlink++;
                break;
            default:
                summary.Errors++;
                _log?.LogWarning(
                    "TempFileSweeper: failed to remove stale temp entry {Entry}; leaving it for the next sweep",
                    fullPath);
                break;
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

    /// <summary>
    /// Removes one entry already proven stale. Never follows a symbolic link:
    /// on Linux every component is addressed relative to a pinned directory
    /// descriptor (<c>O_NOFOLLOW</c> opens, <c>unlinkat</c> removes), so a
    /// swap after the freshness probe surfaces as <c>ELOOP</c> and aborts the
    /// entry; elsewhere each node is re-validated immediately before a
    /// non-recursive remove. Never throws: failures map to
    /// <see cref="TempEntryDeleteOutcome.Failed"/>.
    /// </summary>
    internal TempEntryDeleteOutcome DeleteProvenStaleEntry(string root, string entryName, string fullPath)
    {
        if (OperatingSystem.IsLinux() && Directory.Exists("/proc/self/fd"))
        {
            try
            {
                return DeleteLinuxNoFollow(root, entryName);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                or NativeSweepUnavailableException)
            {
                // No usable libc/statx (exotic runtime or ancient kernel):
                // fall through to the managed no-follow delete below.
            }
        }

        var visits = 0;
        return DeleteManagedNoFollow(root, fullPath, ref visits);
    }

    private TempEntryDeleteOutcome DeleteLinuxNoFollow(string root, string entryName)
    {
        string name;
        try
        {
            name = ValidateChildName(entryName);
        }
        catch (IOException)
        {
            return TempEntryDeleteOutcome.Failed;
        }

        // The root itself is the operator-configured temp directory, opened
        // without NOFOLLOW so a conventional /tmp symlink still sweeps; every
        // component beneath it is addressed relative to this descriptor, so a
        // swap can never redirect removal outside the root.
        var rootFd = NativeOpen(root, OpenReadOnly | OpenDirectory | OpenCloseOnExec);
        if (rootFd < 0)
        {
            ThrowIfNativeUnavailable(Marshal.GetLastPInvokeError());
            return TempEntryDeleteOutcome.Failed;
        }

        try
        {
            if (NativeStatx(rootFd, name, AtSymlinkNoFollow, StatxBasicStats, out var status) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                ThrowIfNativeUnavailable(error);
                if (error == ErrorNoEntry)
                    return TempEntryDeleteOutcome.Removed;
                return TempEntryDeleteOutcome.Failed;
            }

            switch (status.Mode & FileTypeMask)
            {
                case SymbolicLinkFileType:
                    return TempEntryDeleteOutcome.SkippedSymlink;
                case DirectoryFileType:
                {
                    var visits = 0;
                    var linkFound = false;
                    if (!RemoveDirectoryLinux(rootFd, name, ref visits, ref linkFound))
                        return linkFound ? TempEntryDeleteOutcome.SkippedSymlink : TempEntryDeleteOutcome.Failed;
                    return TempEntryDeleteOutcome.Removed;
                }
                default:
                    // Regular files, fifos, sockets, devices: unlinking the
                    // link itself can never reach a target, even if the entry
                    // is swapped after the statx above.
                    if (NativeUnlinkAt(rootFd, name, 0) != 0)
                    {
                        var error = Marshal.GetLastPInvokeError();
                        ThrowIfNativeUnavailable(error);
                        if (error == ErrorNoEntry)
                            return TempEntryDeleteOutcome.Removed;
                        return TempEntryDeleteOutcome.Failed;
                    }

                    return TempEntryDeleteOutcome.Removed;
            }
        }
        finally
        {
            NativeClose(rootFd);
        }
    }

    // Removes the directory `name` pinned under `parentFd`: opens it
    // O_NOFOLLOW (a swapped-in symlink fails ELOOP here, before anything
    // beneath it is touched), unlinks every child fd-relative, then removes
    // the now-empty directory itself. Enumeration goes through
    // /proc/self/fd, which the kernel resolves to the pinned directory
    // rather than re-resolving the untrusted path.
    private bool RemoveDirectoryLinux(int parentFd, string name, ref int visits, ref bool linkFound)
    {
        var fd = NativeOpenAt(parentFd, name, OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec);
        if (fd < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            ThrowIfNativeUnavailable(error);
            if (error == ErrorNoEntry)
                return true;
            if (error == ErrorLoop)
                linkFound = true;
            return false;
        }

        try
        {
            string[] children;
            try
            {
                children = [.. Directory
                    .EnumerateFileSystemEntries("/proc/self/fd/" + fd)
                    .Select(Path.GetFileName)
                    .Select(n => ValidateChildName(n!))];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            foreach (var child in children)
            {
                if (++visits > DeletionVisitCap)
                    return false;
                if (!RemoveChildLinux(fd, child, ref visits, ref linkFound))
                    return false;
            }
        }
        finally
        {
            NativeClose(fd);
        }

        if (NativeUnlinkAt(parentFd, name, AtRemoveDir) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            ThrowIfNativeUnavailable(error);
            return error == ErrorNoEntry;
        }

        return true;
    }

    private bool RemoveChildLinux(int dirFd, string child, ref int visits, ref bool linkFound)
    {
        if (NativeStatx(dirFd, child, AtSymlinkNoFollow, StatxBasicStats, out var status) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            ThrowIfNativeUnavailable(error);
            return error == ErrorNoEntry;
        }

        var kind = status.Mode & FileTypeMask;
        if (kind == DirectoryFileType)
            return RemoveDirectoryLinux(dirFd, child, ref visits, ref linkFound);
        if (kind == SymbolicLinkFileType)
        {
            linkFound = true;
            return false;
        }

        if (NativeUnlinkAt(dirFd, child, 0) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            ThrowIfNativeUnavailable(error);
            return error == ErrorNoEntry;
        }

        return true;
    }

    // Managed fallback for non-Linux hosts (and for Linux runtimes without a
    // usable libc/statx): bottom-up removal where every node is re-validated
    // immediately before its own mutation and only non-recursive removes are
    // used, so a link found at any point aborts the whole entry. This still
    // carries a check-then-act window around directory enumeration that only
    // pinned descriptors can close; the deployment target (/tmp on Linux)
    // always takes the descriptor path above.
    private static TempEntryDeleteOutcome DeleteManagedNoFollow(string root, string fullPath, ref int visits)
    {
        if (++visits > DeletionVisitCap)
            return TempEntryDeleteOutcome.Failed;

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Already gone converges to the goal; anything else is a failure.
            return !File.Exists(fullPath) && !Directory.Exists(fullPath)
                ? TempEntryDeleteOutcome.Removed
                : TempEntryDeleteOutcome.Failed;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return TempEntryDeleteOutcome.SkippedSymlink;
        if (!IsWithinRoot(root, fullPath))
            return TempEntryDeleteOutcome.Failed;

        if ((attributes & FileAttributes.Directory) == 0)
        {
            try
            {
                File.Delete(fullPath);
                return TempEntryDeleteOutcome.Removed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return !File.Exists(fullPath) && !Directory.Exists(fullPath)
                    ? TempEntryDeleteOutcome.Removed
                    : TempEntryDeleteOutcome.Failed;
            }
        }

        List<string> children;
        try
        {
            children = [.. Directory.EnumerateFileSystemEntries(fullPath)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TempEntryDeleteOutcome.Failed;
        }

        foreach (var child in children)
        {
            string childFull;
            try
            {
                childFull = Path.GetFullPath(child);
            }
            catch (Exception)
            {
                return TempEntryDeleteOutcome.Failed;
            }

            var childOutcome = DeleteManagedNoFollow(root, childFull, ref visits);
            if (childOutcome != TempEntryDeleteOutcome.Removed)
                return childOutcome;
        }

        try
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                return TempEntryDeleteOutcome.SkippedSymlink;
            Directory.Delete(fullPath, recursive: false);
            return TempEntryDeleteOutcome.Removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return !Directory.Exists(fullPath)
                ? TempEntryDeleteOutcome.Removed
                : TempEntryDeleteOutcome.Failed;
        }
    }

    private static bool IsWithinRoot(string root, string fullPath) =>
        fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || string.Equals(fullPath, root, StringComparison.Ordinal);

    private static string ValidateChildName(string? name)
    {
        if (string.IsNullOrEmpty(name)
            || name is "." or ".."
            || name.Contains('/')
            || name.Contains('\0'))
        {
            throw new IOException("A swept temp entry returned an invalid child name.");
        }

        return name;
    }

    private static void ThrowIfNativeUnavailable(int error)
    {
        // statx(2) exists only on Linux 4.11+; without it the descriptor
        // path cannot classify entries, so the caller falls back to the
        // managed no-follow delete.
        if (error == ErrorNoSys)
            throw new NativeSweepUnavailableException();
    }

    private sealed class NativeSweepUnavailableException : Exception;

    // Linux constants, mirroring CodeyBox.Sandbox.Incus.IncusSafeFile (the
    // established fd-relative no-follow seam): the sweeper cannot reference
    // that provider project without inverting the layer direction, so the
    // handful of values it needs is repeated here beside their only other
    // consumer.
    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int AtSymlinkNoFollow = 0x100;
    private const int AtRemoveDir = 0x200;
    private const uint StatxBasicStats = 0x7ff;
    private const ushort FileTypeMask = 0xF000;
    private const ushort DirectoryFileType = 0x4000;
    private const ushort SymbolicLinkFileType = 0xA000;
    private const int ErrorNoEntry = 2;
    private const int ErrorLoop = 40;
    private const int ErrorNoSys = 38;

    // Classic DllImport (rather than LibraryImport source generation) so this
    // project needs no AllowUnsafeBlocks; all signatures are blittable
    // primitives, which keeps the interop NativeAOT-safe.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int NativeOpenAt(
        int dirfd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int NativeUnlinkAt(
        int dirfd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int NativeStatx(
        int dirfd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out StatxMode status);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int NativeClose(int fd);

    // Minimal view of struct statx(2): the syscall always writes the full
    // 256-byte struct, so the buffer keeps its full size and only the mode
    // word (offset 28) is projected.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxMode
    {
        [FieldOffset(28)]
        internal ushort Mode;
    }
}
