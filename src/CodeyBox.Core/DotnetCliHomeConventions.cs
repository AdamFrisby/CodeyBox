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

    public static void ApplyIfDotnetInvocation(
        IReadOnlyList<string> argv,
        string workingDirectory,
        IDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(environment);
        if (!IsDotnetInvocation(argv) || environment.ContainsKey("DOTNET_CLI_HOME"))
            return;

        environment["DOTNET_CLI_HOME"] = ResolvePath(workingDirectory);
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
