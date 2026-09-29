using System.Diagnostics;

namespace CodeyBox.GitLabUpstreamPlugin;

// Host-side git authentication for the plugin. Plain git has no
// GIT_USERNAME/GIT_PASSWORD convention, so (like the orchestrator's own
// GitCredentialHelper, which this plugin cannot reference across the plugin
// load-context boundary) credentials travel via a short-lived GIT_ASKPASS
// script: the token lives only in process environment, never on argv, and
// the script directory is removed on dispose. GitLab answers the username
// prompt with any non-blank value, so the script returns "oauth2" for the
// username prompt and the token for the password prompt.

internal sealed class GitLabGitAuthScope : IDisposable
{
    private readonly string _directory;
    private bool _disposed;

    public IReadOnlyDictionary<string, string> Environment { get; }

    private GitLabGitAuthScope(string directory, IReadOnlyDictionary<string, string> environment)
    {
        _directory = directory;
        Environment = environment;
    }

    public static GitLabGitAuthScope Create(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return new GitLabGitAuthScope(string.Empty, new Dictionary<string, string>());

        var directory = Directory.CreateTempSubdirectory("codeybox-gitlab-askpass-").FullName;
        var scriptPath = Path.Combine(directory, "askpass.sh");
        File.WriteAllText(
            scriptPath,
            "#!/bin/sh\ncase \"$1\" in\n*Username*) printf '%s' \"oauth2\" ;;\n*) printf '%s' \"$CODEYBOX_GITLAB_GIT_PASS\" ;;\nesac\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new GitLabGitAuthScope(directory, new Dictionary<string, string>
        {
            ["GIT_ASKPASS"] = scriptPath,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["CODEYBOX_GITLAB_GIT_PASS"] = token,
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (string.IsNullOrEmpty(_directory))
            return;
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}

// Minimal git runner for the release-sync merge path (merge one upstream
// branch into another). Mirrors the built-in generic-git's clone/fetch/
// merge/push sequence; it lives here because IUpstreamRemote callers only
// reach this provider through the contract, which carries no project
// context, so the merge cannot be delegated to a per-project remote.
internal static class GitLabGitRunner
{
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string workdir,
        IReadOnlyDictionary<string, string> extraEnv,
        CancellationToken ct,
        params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        foreach (var (key, value) in extraEnv)
            psi.EnvironmentVariables[key] = value;

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, stdout, stderr);
    }
}
