using System.ComponentModel;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Maps host process start failures to opencode CLI-not-found semantics.
/// </summary>
internal static class OpencodeCliErrors
{
    /// <summary>Linux/macOS ENOENT from <see cref="System.Diagnostics.Process.Start"/>.</summary>
    private const int LinuxEnoent = 2;

    public static bool IsCliNotFound(Exception ex) =>
        ex is FileNotFoundException
        || (ex is Win32Exception w32 && w32.NativeErrorCode == LinuxEnoent);

    public static bool IsCliNotFoundExitCode(int exitCode) => exitCode == 127;
}
