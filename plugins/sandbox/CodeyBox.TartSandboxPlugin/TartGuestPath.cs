namespace CodeyBox.TartSandboxPlugin;

/// <summary>
/// Guard at the filesystem sinks: guest paths must be absolute with no
/// parent-directory escapes, and host sync-back targets must canonicalize
/// under their owning directory. Mirrors the hosted providers' guest-path
/// helpers so a new caller cannot stage outside its mount.
/// </summary>
public static class TartGuestPath
{
    /// <summary>Validates an absolute guest path with no escapes.</summary>
    public static void ValidateAbsolute(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Guest path must be non-empty.", parameterName);
        if (path.IndexOf('\0') >= 0)
            throw new ArgumentException("Guest path must not contain NUL bytes.", parameterName);
        if (!path.StartsWith('/'))
            throw new ArgumentException("Guest path must be absolute.", parameterName);
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "." || segment == "..")
                throw new ArgumentException("Guest path must not contain '.' or '..' segments.", parameterName);
        }
    }

    /// <summary>
    /// Joins a validated guest directory with an untrusted relative entry
    /// (for example a `find -print0` row) and validates the result.
    /// </summary>
    public static string Join(string guestDirectory, string relativeEntry)
    {
        ValidateAbsolute(guestDirectory, nameof(guestDirectory));
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeEntry);
        if (relativeEntry.IndexOf('\0') >= 0)
            throw new ArgumentException("Guest entry must not contain NUL bytes.", nameof(relativeEntry));
        if (Path.IsPathRooted(relativeEntry))
            throw new ArgumentException("Guest entry must be relative.", nameof(relativeEntry));
        var combined = guestDirectory.TrimEnd('/') + "/" + relativeEntry.TrimStart('/');
        ValidateAbsolute(combined, nameof(relativeEntry));
        return combined;
    }

    /// <summary>
    /// Canonicalizes an untrusted host path and contains it under
    /// <paramref name="owningDirectory"/>: returns the full path when
    /// contained, throws otherwise. The guard runs at the write sink.
    /// </summary>
    public static string ContainHostPath(string owningDirectory, string relativeEntry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owningDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeEntry);
        var root = Path.GetFullPath(owningDirectory);
        var full = Path.GetFullPath(Path.Combine(root, relativeEntry));
        if (!full.Equals(root, StringComparison.Ordinal)
            && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Sync-back entry '{relativeEntry}' escapes its owning directory.");
        return full;
    }
}
