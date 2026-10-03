namespace CodeyBox.Core;

/// <summary>
/// Single shared guard mapping an untrusted guest file listing back to a host
/// write during writable-mount sync-back. Every hosted-sandbox plugin that
/// syncs guest bytes to the host (E2B, Runloop) resolves and writes through
/// this helper so the prefix check, no-follow symlink probe, and
/// re-check-immediately-before-write cannot drift between copies.
/// </summary>
public static class HostedMountSyncGuard
{
    /// <summary>
    /// Resolves one untrusted <paramref name="guestFile"/> entry under
    /// <paramref name="mountGuestPath"/> to its host destination under
    /// <paramref name="mountHostRoot"/>. Returns false with a refusal reason
    /// when the guest path escapes its mount, the host target escapes the
    /// mount root, or any host path component is a symlink.
    /// </summary>
    public static bool TryResolveHostFile(
        string mountHostRoot,
        string mountGuestPath,
        string guestFile,
        out string hostFile,
        out string refusalReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountHostRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(mountGuestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(guestFile);

        string relative;
        try
        {
            relative = HostedGuestPath.GetRelativePath(mountGuestPath, guestFile);
        }
        catch (ArgumentException)
        {
            hostFile = string.Empty;
            refusalReason = $"guest path escapes its mount: {guestFile}";
            return false;
        }

        if (string.IsNullOrEmpty(relative))
        {
            hostFile = string.Empty;
            refusalReason = $"guest path is the mount root: {guestFile}";
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(mountHostRoot, relative));
        if (!candidate.StartsWith(mountHostRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(candidate, mountHostRoot, StringComparison.Ordinal))
        {
            hostFile = string.Empty;
            refusalReason = $"host path escapes its mount root for {guestFile}";
            return false;
        }

        if (HostPathPassesThroughSymlink(mountHostRoot, candidate))
        {
            hostFile = string.Empty;
            refusalReason = $"host path passes through a symlink for {guestFile}";
            return false;
        }

        hostFile = candidate;
        refusalReason = string.Empty;
        return true;
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="hostFile"/>, re-checking
    /// the symlink probe immediately before touching the host filesystem: the
    /// earlier probe ran before a network round-trip, so a symlink swapped into
    /// the host tree in between must still be refused rather than followed by
    /// <c>CreateDirectory</c>/<c>WriteAllBytesAsync</c>. Returns null on success,
    /// otherwise a refusal or write-failure reason. Cancellation still throws.
    /// </summary>
    public static async Task<string?> WriteFileGuardedAsync(
        string mountHostRoot,
        string hostFile,
        string guestFile,
        byte[] bytes,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountHostRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(guestFile);
        ArgumentNullException.ThrowIfNull(bytes);

        if (HostPathPassesThroughSymlink(mountHostRoot, hostFile))
        {
            return $"host path passes through a symlink for {guestFile}";
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(hostFile)!);
            await File.WriteAllBytesAsync(hostFile, bytes, ct).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"write-back {hostFile}: {ex.GetType().Name}";
        }
    }

    /// <summary>
    /// True when any component of <paramref name="hostFile"/> from the file
    /// itself up to (excluding) <paramref name="mountRoot"/> is a symlink.
    /// Uses lstat semantics so dangling links are caught too, and treats
    /// unreadable components as unsafe.
    /// </summary>
    public static bool HostPathPassesThroughSymlink(string mountRoot, string hostFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostFile);

        var current = hostFile;
        while (true)
        {
            if (string.Equals(current, mountRoot, StringComparison.Ordinal))
            {
                return false;
            }

            if (IsSymlinkNoFollow(current))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Length >= current.Length)
            {
                return true;
            }

            current = parent;
            if (!current.Equals(mountRoot, StringComparison.Ordinal)
                && !current.StartsWith(mountRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return true;
            }
        }
    }

    private static bool IsSymlinkNoFollow(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                return true;
            }

            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
