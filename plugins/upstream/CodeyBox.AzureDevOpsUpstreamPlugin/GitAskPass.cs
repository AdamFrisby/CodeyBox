namespace CodeyBox.AzureDevOpsUpstreamPlugin;

// Minimal GIT_ASKPASS scope so host-side git HTTPS calls can authenticate
// with the PAT without embedding it in the remote URL (which would expose it
// in /proc cmdlines and logs). Kept inside the plugin because plugins may
// only depend on the SDK surface — CodeyBox.Git's GitCredentialHelper is a
// host internal and not referenceable from here.
//
// SECURITY: the script (mode 0700) prints only the values of environment
// variables the host git process already holds; the PAT itself never appears
// on argv, in files readable by others, or in exception text (callers scrub
// it). Disposing deletes the script directory.

internal sealed class GitAskPass : IDisposable
{
    private readonly string _dir;

    /// <summary>Environment additions to pass to the host git process.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }

    private GitAskPass(string dir, IReadOnlyDictionary<string, string> env)
    {
        _dir = dir;
        Environment = env;
    }

    /// <summary>Builds a 0700 askpass script answering with <paramref name="token"/>.</summary>
    /// <exception cref="ArgumentException">Token is empty.</exception>
    public static GitAskPass Create(string token)
    {
        if (string.IsNullOrEmpty(token))
            throw new ArgumentException("Token must not be empty.", nameof(token));

        var dir = Directory.CreateTempSubdirectory("codeybox-ado-askpass-");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var scriptPath = Path.Combine(dir.FullName, "askpass.sh");
        const string script = """
            #!/bin/sh
            case "$1" in
              Username*) printf '%s' "$CODEYBOX_ADO_GIT_USER" ;;
              *)         printf '%s' "$CODEYBOX_ADO_GIT_PASS" ;;
            esac
            """;
        File.WriteAllText(scriptPath, script);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return new GitAskPass(dir.FullName, new Dictionary<string, string>
        {
            ["GIT_ASKPASS"] = scriptPath,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["CODEYBOX_ADO_GIT_USER"] = "x-access-token",
            ["CODEYBOX_ADO_GIT_PASS"] = token,
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
