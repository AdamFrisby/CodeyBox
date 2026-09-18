namespace CodeyBox.Orchestrator;

/// <summary>
/// Checks whether an external binary required by an enabled plugin is
/// available on the host. Injected (not static) so startup-report tests can
/// substitute a fake; production searches <c>PATH</c>.
/// </summary>
public interface IPluginToolAvailabilityProbe
{
    /// <summary>
    /// Returns true when <paramref name="binaryName"/> resolves to an
    /// executable file on the host. Callers pass only validated bare names
    /// (<see cref="PluginToolRequirement.Binary"/>).
    /// </summary>
    bool IsAvailable(string binaryName);
}

/// <summary>
/// Production probe: searches the host <c>PATH</c> for an executable file
/// with the exact validated name. Exact-match only (never substring), a
/// bounded number of directories, no shell.
/// </summary>
public sealed class PathPluginToolAvailabilityProbe : IPluginToolAvailabilityProbe
{
    /// <summary>Maximum PATH directories inspected before giving up.</summary>
    public const int MaxPathDirectoriesInspected = 64;

    private readonly Func<string?> _pathReader;

    public PathPluginToolAvailabilityProbe()
        : this(static () => Environment.GetEnvironmentVariable("PATH"))
    {
    }

    internal PathPluginToolAvailabilityProbe(Func<string?> pathReader)
    {
        _pathReader = pathReader ?? throw new ArgumentNullException(nameof(pathReader));
    }

    public bool IsAvailable(string binaryName)
    {
        if (string.IsNullOrWhiteSpace(binaryName))
            return false;
        // Belt and braces: the loader only passes validated names, but this
        // probe is public and must be safe to call with anything.
        if (binaryName.IndexOfAny(['/', '\\', .. Path.GetInvalidFileNameChars()]) >= 0)
            return false;

        var path = _pathReader();
        if (string.IsNullOrEmpty(path))
            return false;

        var inspected = 0;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            if (++inspected > MaxPathDirectoriesInspected)
                return false;
            string candidate;
            try
            {
                candidate = Path.Combine(dir, binaryName);
            }
            catch (Exception)
            {
                continue;
            }
            if (IsExecutableFile(candidate))
                return true;
        }
        return false;
    }

    private static bool IsExecutableFile(string candidate)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(candidate);
        }
        catch (Exception)
        {
            return false;
        }
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
            return false;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var mode = File.GetUnixFileMode(candidate);
                if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                    return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
        return true;
    }
}
