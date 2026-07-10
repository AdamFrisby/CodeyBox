namespace CodeyBox.Sandbox;

/// <summary>
/// Guards environment names before they reach sandbox process or shell-file
/// sinks.
/// </summary>
public static class SandboxEnvironmentVariablePolicy
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "BASH_ENV",
        "CDPATH",
        "DYLD_INSERT_LIBRARIES",
        "DYLD_LIBRARY_PATH",
        "ENV",
        "GLOBIGNORE",
        "HOME",
        "IFS",
        "LD_LIBRARY_PATH",
        "LD_PRELOAD",
        "NODE_OPTIONS",
        "PATH",
        "PERL5LIB",
        "PYTHONPATH",
        "RUBYLIB",
        "SHELL",
    };

    public static void ValidateCredentialEnvironmentVariable(string name, string parameterName)
    {
        SandboxCredentialFileWriter.ValidateEnvironmentVariableName(name, parameterName);
        if (ReservedNames.Contains(name))
            throw new ArgumentException($"Credential environment variable is reserved: {name}", parameterName);
    }

    public static void ValidateForSandboxEnvironment(string name, string parameterName)
    {
        SandboxCredentialFileWriter.ValidateEnvironmentVariableName(name, parameterName);
    }

    /// <summary>
    /// Serialises a general sandbox environment for dot-sourcing by a POSIX
    /// shell. Values are single-quoted and NUL is rejected before any file is
    /// written. Credential-specific reserved-name checks are intentionally not
    /// applied here because trusted sandbox specs may set variables such as
    /// PATH.
    /// </summary>
    public static string BuildShellEnvironmentFileContent(IReadOnlyDictionary<string, string> environment)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var (name, value) in environment)
        {
            ValidateForSandboxEnvironment(name, nameof(environment));
            if (value.Contains('\0'))
                throw new ArgumentException(
                    $"Sandbox environment value for '{name}' contains a NUL byte.",
                    nameof(environment));
            builder.Append(name)
                .Append('=')
                .Append(ShellQuote(value))
                .Append('\n');
        }
        return builder.ToString();
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
