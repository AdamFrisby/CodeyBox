namespace CodeyBox.BlaxelPlugin;

/// <summary>
/// Pure validation of guest-absolute paths used by the Blaxel provider.
/// Rejects relative paths, escapes above the intended root, and embedded
/// NULs before any value reaches the sandbox API: guest paths arrive from
/// specs and from untrusted guest listings, so the guard sits at the sink.
/// </summary>
public static class BlaxelGuestPath
{
    /// <summary>Validates that <paramref name="path"/> is an absolute, normalised guest path.</summary>
    /// <exception cref="ArgumentException">Relative, empty, or escaping path.</exception>
    public static void ValidateAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Guest path must be non-empty.", nameof(path));
        }

        if (path.Contains('\0'))
        {
            throw new ArgumentException("Guest path must not contain NUL.", nameof(path));
        }

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Guest path '{path}' must be absolute.", nameof(path));
        }

        foreach (var segment in path.Split('/'))
        {
            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Guest path '{path}' must not escape its root.", nameof(path));
            }
        }
    }

    /// <summary>Returns <paramref name="path"/> relative to <paramref name="root"/>.</summary>
    /// <exception cref="ArgumentException">Path is outside the root.</exception>
    public static string GetRelativePath(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateAbsolute(root);
        ValidateAbsolute(path);

        var normalisedRoot = root.TrimEnd('/');
        if (string.Equals(path, normalisedRoot, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var prefix = normalisedRoot + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Guest path '{path}' is outside root '{root}'.", nameof(path));
        }

        return path.Substring(prefix.Length);
    }
}
