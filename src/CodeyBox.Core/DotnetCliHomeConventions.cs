namespace CodeyBox.Core;

/// <summary>
/// Repo-local <c>DOTNET_CLI_HOME</c> for sandboxes where the user-level
/// <c>~/.nuget/NuGet</c> path is missing or not writable (common in agent
/// environments with a root-owned <c>~/.nuget</c> parent). Pair with
/// <see cref="Directory.Build.props"/> <c>RestoreConfigFile</c> and repo
/// <c>NuGet.Config</c> so restore does not depend on the host home directory.
/// </summary>
public static class DotnetCliHomeConventions
{
    public const string DirectoryName = ".dotnet-cli-home";

    public static string ResolvePath(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        return Path.Combine(workingDirectory, DirectoryName);
    }

    /// <summary>
    /// Stamps <c>DOTNET_CLI_HOME</c> on a sandbox (or exec) environment when
    /// the caller has not already set it. Use at sandbox creation so every
    /// child process — shell scripts, <c>build.sh</c>, and direct
    /// <c>dotnet</c> auditors — inherits a writable CLI home under the work
    /// tree instead of probing root-owned <c>~/.nuget</c>.
    /// </summary>
    public static void ApplyIfAbsent(
        IDictionary<string, string> environment,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.ContainsKey("DOTNET_CLI_HOME"))
            return;

        environment["DOTNET_CLI_HOME"] = ResolvePath(workingDirectory);
    }

    /// <summary>
    /// Stamps a writable CLI home on the environment of a single
    /// <c>dotnet</c> invocation. Pins BOTH <c>DOTNET_CLI_HOME</c> and
    /// <c>HOME</c> to the repo-local home: some NuGet builds derive the
    /// user-level config directory (<c>~/.nuget/NuGet</c>) from <c>$HOME</c>
    /// and IGNORE <c>DOTNET_CLI_HOME</c>, so pinning only the latter still lets
    /// restore probe a root-owned <c>~/.nuget</c> and abort with "Failed to read
    /// NuGet.Config ... denied" even though <c>DOTNET_CLI_HOME</c> is writable.
    /// Matching <c>HOME</c> to the same directory lands every NuGet resolution
    /// strategy on one writable home. This mirrors
    /// <c>SandboxRequiredBuildVerifier.DotnetCliHomeSelectionScript</c> and
    /// <c>build.sh</c>, which pin both for the same reason.
    /// <para>Unlike <see cref="ApplyIfAbsent"/> (used at sandbox creation, where
    /// overriding <c>HOME</c> would disturb sibling git/tool steps that need the
    /// caller's home), this is safe to override <c>HOME</c> because callers apply
    /// it to the per-command environment of the dotnet exec ONLY — see
    /// <c>ShellCommandAuditor</c>. Existing <c>HOME</c>/<c>DOTNET_CLI_HOME</c>
    /// values the caller set are respected.</para>
    /// </summary>
    public static void ApplyIfDotnetInvocation(
        IReadOnlyList<string> argv,
        string workingDirectory,
        IDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(environment);
        if (!IsDotnetInvocation(argv))
            return;

        ApplyIfAbsent(environment, workingDirectory);

        // Point HOME at the same CLI home resolved above so a $HOME-derived
        // NuGet user-config directory cannot fall back to a root-owned ~/.nuget.
        if (!environment.ContainsKey("HOME")
            && environment.TryGetValue("DOTNET_CLI_HOME", out var cliHome)
            && !string.IsNullOrEmpty(cliHome))
        {
            environment["HOME"] = cliHome;
        }
    }

    public static bool IsDotnetInvocation(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0)
            return false;

        var tool = argv[0];
        if (string.Equals(tool, "dotnet", StringComparison.Ordinal))
            return true;

        return tool.EndsWith("/dotnet", StringComparison.Ordinal)
            || tool.EndsWith("\\dotnet", StringComparison.Ordinal);
    }
}
