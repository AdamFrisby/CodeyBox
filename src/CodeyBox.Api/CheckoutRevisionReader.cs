using System.Text;

namespace CodeyBox.Api;

/// <summary>
/// Resolves the currently checked-out commit by reading <c>.git/HEAD</c>
/// (and the ref it points at) directly from disk. File reads only — no
/// subprocess — so the check stays cheap enough to run at every startup and
/// on every configuration reload. Never throws: any failure (no checkout,
/// unreadable files, unexpected layout) yields
/// <see cref="DeployConsistency.UnknownRevision"/> so the consistency check
/// reports unknown instead of blocking startup.
/// </summary>
internal static class CheckoutRevisionReader
{
    private const int MaxWalkDepth = 12;
    private const int MaxSmallFileBytes = 4096;
    private const int MaxPackedRefsBytes = 262144;

    public static string ReadCheckoutRevision(string? startDirectory = null)
    {
        try
        {
            var start = string.IsNullOrWhiteSpace(startDirectory)
                ? AppContext.BaseDirectory
                : startDirectory;
            var startFull = Path.GetFullPath(start);
            if (!Directory.Exists(startFull))
                startFull = Path.GetDirectoryName(startFull) ?? startFull;

            var gitDir = FindGitDir(startFull);
            if (gitDir is null)
                return DeployConsistency.UnknownRevision;

            return ResolveHead(gitDir);
        }
        catch (Exception)
        {
            // Checkout probing must never prevent startup.
            return DeployConsistency.UnknownRevision;
        }
    }

    private static string? FindGitDir(string startFull)
    {
        var dir = startFull;
        for (var depth = 0; depth < MaxWalkDepth; depth++)
        {
            var dotGit = Path.Combine(dir, ".git");
            try
            {
                if (Directory.Exists(dotGit))
                    return dotGit;
                if (File.Exists(dotGit))
                {
                    var gitDir = ResolveGitFile(dotGit, dir);
                    if (gitDir is not null)
                        return gitDir;
                }
            }
            catch (Exception)
            {
                return null;
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent is null || string.Equals(parent, dir, StringComparison.Ordinal))
                return null;
            dir = parent;
        }

        return null;
    }

    private static string? ResolveGitFile(string gitFilePath, string containingDir)
    {
        // Worktree / submodule pointer: "gitdir: <path>".
        var firstLine = ReadFirstLineCapped(gitFilePath, MaxSmallFileBytes);
        if (firstLine is null || !firstLine.StartsWith("gitdir:", StringComparison.Ordinal))
            return null;

        var target = firstLine["gitdir:".Length..].Trim();
        if (string.IsNullOrWhiteSpace(target))
            return null;

        var combined = Path.IsPathFullyQualified(target)
            ? target
            : Path.Combine(containingDir, target);
        var full = Path.GetFullPath(combined);
        return Directory.Exists(full) ? full : null;
    }

    private static string ResolveHead(string gitDir)
    {
        var head = ReadFirstLineCapped(Path.Combine(gitDir, "HEAD"), MaxSmallFileBytes);
        if (string.IsNullOrWhiteSpace(head))
            return DeployConsistency.UnknownRevision;

        head = head.Trim();
        const string RefPrefix = "ref:";
        if (head.StartsWith(RefPrefix, StringComparison.Ordinal))
        {
            var refPath = head[RefPrefix.Length..].Trim();
            var sha = ReadRef(gitDir, refPath) ?? ReadPackedRef(gitDir, refPath);
            return ToRevisionOrUnknown(sha);
        }

        return ToRevisionOrUnknown(head);
    }

    private static string? ReadRef(string gitDir, string refPath)
    {
        if (!IsContainedRefPath(refPath))
            return null;

        var full = Path.GetFullPath(Path.Combine(gitDir, refPath));
        if (!IsWithinDirectory(gitDir, full))
            return null;

        if (File.Exists(full))
        {
            var sha = ReadFirstLineCapped(full, MaxSmallFileBytes);
            if (IsCommitSha(sha))
                return sha!.Trim();
        }

        // Worktree checkouts keep refs in the main checkout; commondir points there.
        var commonDir = ReadFirstLineCapped(Path.Combine(gitDir, "commondir"), MaxSmallFileBytes)?.Trim();
        if (!string.IsNullOrWhiteSpace(commonDir))
        {
            var baseDir = Path.IsPathFullyQualified(commonDir)
                ? commonDir
                : Path.GetFullPath(Path.Combine(gitDir, commonDir));
            if (Directory.Exists(baseDir))
            {
                var altFull = Path.GetFullPath(Path.Combine(baseDir, refPath));
                if (IsWithinDirectory(baseDir, altFull) && File.Exists(altFull))
                {
                    var sha = ReadFirstLineCapped(altFull, MaxSmallFileBytes);
                    if (IsCommitSha(sha))
                        return sha!.Trim();
                }

                var packed = ReadPackedRef(baseDir, refPath);
                if (packed is not null)
                    return packed;
            }
        }

        return null;
    }

    private static string? ReadPackedRef(string gitDir, string refPath)
    {
        var packedRefs = Path.Combine(gitDir, "packed-refs");
        if (!File.Exists(packedRefs))
            return null;

        var content = ReadFileCapped(packedRefs, MaxPackedRefsBytes);
        if (content is null)
            return null;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('^'))
                continue;
            var space = trimmed.IndexOf(' ');
            if (space <= 0)
                continue;
            var sha = trimmed[..space];
            var name = trimmed[(space + 1)..].Trim();
            if (string.Equals(name, refPath, StringComparison.Ordinal) && IsCommitSha(sha))
                return sha.Trim();
        }

        return null;
    }

    private static bool IsContainedRefPath(string refPath) =>
        !string.IsNullOrWhiteSpace(refPath)
        && !Path.IsPathFullyQualified(refPath)
        && !refPath.Contains("..", StringComparison.Ordinal);

    private static bool IsWithinDirectory(string directory, string candidate)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, StringComparison.Ordinal);
    }

    private static bool IsCommitSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var sha = value.Trim();
        if (sha.Length is not (40 or 64))
            return false;
        foreach (var c in sha)
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
                return false;
        }

        return true;
    }

    private static string ToRevisionOrUnknown(string? sha) =>
        IsCommitSha(sha) ? sha!.Trim().ToLowerInvariant() : DeployConsistency.UnknownRevision;

    private static string? ReadFirstLineCapped(string path, int maxBytes)
    {
        var content = ReadFileCapped(path, maxBytes);
        if (content is null)
            return null;
        var newline = content.IndexOf('\n');
        return (newline < 0 ? content : content[..newline]).TrimEnd('\r');
    }

    private static string? ReadFileCapped(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0)
                return string.Empty;
            var take = (int)Math.Min(stream.Length, maxBytes);
            var buffer = new byte[take];
            var read = 0;
            while (read < take)
            {
                var n = stream.Read(buffer, read, take - read);
                if (n == 0)
                    break;
                read += n;
            }

            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
