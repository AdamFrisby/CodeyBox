namespace CodeyBox.HostProcess;

/// <summary>
/// Builds a minimal environment for host CLI probes that need PATH and home
/// config discovery without inheriting the full orchestrator environment.
/// </summary>
public static class MinimalHostProcessEnvironment
{
    /// <summary>
    /// PATH, HOME, and XDG_CONFIG_HOME when set on the host (for auth file discovery).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ForCliAuthDiscovery()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        CopyIfSet(env, "PATH");
        CopyIfSet(env, "HOME");
        CopyIfSet(env, "XDG_CONFIG_HOME");
        return env;
    }

    private static void CopyIfSet(Dictionary<string, string> env, string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrEmpty(value))
            env[key] = value;
    }
}
