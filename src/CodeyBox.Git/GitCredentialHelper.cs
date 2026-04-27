using System.Runtime.InteropServices;

namespace CodeyBox.Git;

/// <summary>
/// Helpers for invoking git with a credential without putting it on argv.
///
/// Git accepts credentials via:
///   - URL embedded (https://user:token@host/...) — visible in /proc/*/cmdline
///     and ps output. We avoid this.
///   - GIT_ASKPASS pointing to an executable that prints the credential.
///     We write a tiny script to a tmp file with mode 0700 that prints the
///     value of an env var the orchestrator set; the script and env both
///     vanish at the end of the scope.
/// </summary>
public static class GitCredentialHelper
{
    /// <summary>
    /// Builds a temporary askpass script and the env additions to use with it.
    /// Disposing the returned scope removes the script.
    /// </summary>
    public static AskPassScope CreateAskPassFor(string token, string username = "x-access-token")
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("AskPass scope is implemented for Unix only.");

        var dir = Directory.CreateTempSubdirectory("codeybox-askpass-");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var scriptPath = Path.Combine(dir.FullName, "askpass.sh");

        // The script reads CODEYBOX_GIT_PASS for the password and
        // CODEYBOX_GIT_USER for the username. Git invokes it with the
        // prompt as argv[1]; we branch on whether it's asking for username
        // or password.
        var script = """
            #!/bin/sh
            case "$1" in
              Username*) printf '%s' "$CODEYBOX_GIT_USER" ;;
              *)         printf '%s' "$CODEYBOX_GIT_PASS" ;;
            esac
            """;
        File.WriteAllText(scriptPath, script);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var env = new Dictionary<string, string>
        {
            ["GIT_ASKPASS"] = scriptPath,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["CODEYBOX_GIT_USER"] = username,
            ["CODEYBOX_GIT_PASS"] = token,
        };
        return new AskPassScope(dir.FullName, env);
    }
}

public sealed class AskPassScope : IDisposable
{
    private readonly string _dir;
    public IReadOnlyDictionary<string, string> Environment { get; }

    internal AskPassScope(string dir, IReadOnlyDictionary<string, string> env)
    {
        _dir = dir;
        Environment = env;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }
}
