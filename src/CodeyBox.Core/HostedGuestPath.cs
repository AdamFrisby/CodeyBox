namespace CodeyBox.Core;

/// <summary>
/// Pure validation for guest-absolute POSIX paths in hosted sandboxes.
/// Shared by every hosted-sandbox plugin (E2B, Runloop): guest output is
/// untrusted, so every path crossing the boundary is validated here before
/// it reaches a filesystem or API sink. One implementation keeps quoting,
/// bound, and escape checks identical across providers.
/// </summary>
public static class HostedGuestPath
{
    /// <summary>Maximum guest path length accepted (characters).</summary>
    public const int MaxLength = 4096;

    /// <summary>Throws unless the path is absolute, bounded, and free of escapes.</summary>
    /// <exception cref="ArgumentException">The path is not a safe absolute guest path.</exception>
    public static void ValidateAbsolute(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path.Length > MaxLength)
        {
            throw new ArgumentException($"Guest path has invalid length ({path.Length}).", nameof(path));
        }

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Guest path must be absolute: '{path}'.", nameof(path));
        }

        if (path.Contains('\0'))
        {
            throw new ArgumentException("Guest path contains NUL.", nameof(path));
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment == "..")
            {
                throw new ArgumentException($"Guest path escapes its parent: '{path}'.", nameof(path));
            }
        }
    }

    /// <summary>
    /// Returns the relative path of <paramref name="absolute"/> under
    /// <paramref name="root"/>. Throws when the path escapes the root.
    /// </summary>
    /// <exception cref="ArgumentException">The path is outside the root.</exception>
    public static string GetRelativePath(string root, string absolute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(absolute);
        ValidateAbsolute(root);
        ValidateAbsolute(absolute);

        var normalizedRoot = root.TrimEnd('/');
        if (normalizedRoot.Length == 0)
        {
            throw new ArgumentException("Guest mount root must not be '/'.", nameof(root));
        }

        if (string.Equals(absolute, normalizedRoot, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var prefix = normalizedRoot + "/";
        if (!absolute.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Guest path '{absolute}' is outside mount '{root}'.", nameof(absolute));
        }

        return absolute.Substring(prefix.Length);
    }
}
