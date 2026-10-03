namespace CodeyBox.Core;

/// <summary>
/// Single shared guard collecting host files for upload to hosted
/// (third-party) guest storage. Every hosted-sandbox plugin that stages a
/// workspace mount to the guest (E2B, Runloop) enumerates through this
/// helper so symlink handling cannot drift between copies: the staged tree
/// carries untrusted workspace content, and a planted symlink would otherwise
/// pull host files from outside the mount root into an upload bound for
/// infrastructure CodeyBox does not control.
/// </summary>
public static class HostedMountStaging
{
    /// <summary>
    /// Lists regular-file candidates under <paramref name="hostRoot"/> for
    /// staging. Refuses the mount (throws) when any entry inside the tree is
    /// a symlink, when an enumerated path escapes the root, or when the file
    /// count exceeds <paramref name="maxFileCount"/>. Enumeration itself
    /// never follows directory symlinks; callers must still call
    /// <see cref="ThrowIfSymlinked"/> immediately before opening each file,
    /// closing the swap-in race between listing and reading.
    /// </summary>
    /// <exception cref="InvalidOperationException">The mount is refused.</exception>
    public static List<(string HostFull, string Relative)> CollectStageFiles(
        string hostRoot,
        string providerLabel,
        int maxFileCount,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerLabel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileCount);

        RefuseSymlinkedEntries(hostRoot, providerLabel, ct);

        var files = new List<(string HostFull, string Relative)>();

        // The refusal walk above already rejected links, so skipping reparse
        // points here only guards against a link swapped into the tree
        // between the walk and this listing — it never follows one.
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var hostFile in Directory.EnumerateFiles(hostRoot, "*", enumeration))
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(hostFile);
            if (!full.StartsWith(hostRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Mount source escapes its root: '{hostFile}'.");
            }

            var relative = Path.GetRelativePath(hostRoot, full);
            if (relative.Split(Path.DirectorySeparatorChar).Any(static s => s == ".." || s.Length == 0))
            {
                throw new InvalidOperationException($"Mount source escapes its root: '{hostFile}'.");
            }

            ThrowIfSymlinked(hostRoot, full, relative, providerLabel);

            files.Add((full, relative));
            if (files.Count > maxFileCount)
            {
                throw new InvalidOperationException(
                    $"{providerLabel} mount staging exceeds {maxFileCount} files; refusing to stage.");
            }
        }

        return files;
    }

    /// <summary>
    /// Re-probes <paramref name="hostFull"/> immediately before opening it
    /// for upload: a symlink swapped into the host tree after
    /// <see cref="CollectStageFiles"/> listed it must still refuse the mount
    /// rather than upload the link target. Throws on refusal.
    /// </summary>
    /// <exception cref="InvalidOperationException">The path passes through a symlink.</exception>
    public static void ThrowIfSymlinked(string hostRoot, string hostFull, string relative, string providerLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostFull);
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerLabel);

        if (HostedMountSyncGuard.HostPathPassesThroughSymlink(hostRoot, hostFull))
        {
            throw new InvalidOperationException(
                $"{providerLabel} mount source '{relative}' passes through a symlink; refusing to stage.");
        }
    }

    private static void RefuseSymlinkedEntries(string hostRoot, string providerLabel, CancellationToken ct)
    {
        // The walk itself never follows links: it reads each entry's own
        // attributes (nofollow) and throws on the reparse point instead of
        // recursing through it, matching the sync-back posture — any symlink
        // is refused, not just outside-pointing ones.
        var pendingDirs = new Stack<string>();
        pendingDirs.Push(hostRoot);
        while (pendingDirs.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(pendingDirs.Pop()))
            {
                ct.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(
                        $"{providerLabel} mount source '{entry}' cannot be inspected; refusing to stage.", ex);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"{providerLabel} mount source '{Path.GetRelativePath(hostRoot, entry)}' is a symlink; refusing to stage.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirs.Push(entry);
                }
            }
        }
    }
}
