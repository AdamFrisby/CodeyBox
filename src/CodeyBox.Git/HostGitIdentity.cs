using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Git;

public sealed record HostGitIdentity(string Name, string Email);

/// <summary>
/// Reads the operator's git identity from the host's global git config.
/// Used once at startup to propagate the operator's real name/email into
/// sandbox commits, replacing the synthetic "CodeyBox &lt;codeybox@local&gt;" identity.
/// </summary>
public static class HostGitIdentityReader
{
    /// <param name="log">Optional logger. Warnings at startup only; email is never logged (PII).</param>
    /// <param name="homeDir">Override for $HOME, used in tests to point at a synthetic .gitconfig.</param>
    public static HostGitIdentity? Read(ILogger? log = null, string? homeDir = null)
    {
        var name = RunGitConfig("user.name", homeDir, log);
        var email = RunGitConfig("user.email", homeDir, log);

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(email))
        {
            log?.LogWarning(
                "No git identity configured on host; commits will be authored as " +
                "CodeyBox <codeybox@local>. Run `git config --global user.name` and " +
                "`git config --global user.email` to propagate your identity into sandboxes.");
            return null;
        }

        log?.LogInformation("Host git identity resolved: {Name}", name);
        return new HostGitIdentity(name, email);
    }

    private static string? RunGitConfig(string key, string? homeDir, ILogger? log = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("config");
            psi.ArgumentList.Add("--global");
            psi.ArgumentList.Add(key);
            if (homeDir is not null)
                psi.EnvironmentVariables["HOME"] = homeDir;

            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5_000))
            {
                try { p.Kill(); } catch { }
                return null;
            }
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "Failed to read git config {Key} (git may not be installed or accessible)", key);
            return null;
        }
    }
}
