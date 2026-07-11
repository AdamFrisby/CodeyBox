using System.Collections.ObjectModel;
using CodeyBox.Core;

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
    /// Validates a runner-scoped credential environment and returns an immutable
    /// snapshot containing only variables the runner declares as direct CLI
    /// inputs. Every credential name must be classified as exactly one of direct
    /// or file-backed; file-backed payload and destination metadata remain on the
    /// stdin materialisation path and are never copied into ambient process state.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SelectDirectCredentialEnvironment(
        AgentCredential credential,
        IAgentRunner runner,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(runner);
        if (credential.Agent != runner.Kind)
        {
            throw new ArgumentException(
                $"Credential belongs to agent '{credential.Agent.Value}', not '{runner.Kind.Value}'.",
                parameterName);
        }
        if (credential.EnvironmentVariables.Count == 0)
            return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
        if (runner is not IAgentCredentialEnvironmentPolicy policy)
        {
            throw new ArgumentException(
                $"Agent runner '{runner.Kind.Value}' does not declare a credential environment policy.",
                parameterName);
        }

        var direct = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in credential.EnvironmentVariables)
        {
            ValidateCredentialEnvironmentVariable(name, parameterName);
            var isDirect = policy.DirectCredentialEnvironmentVariables.Contains(name);
            var isFileBacked = policy.FileBackedCredentialEnvironmentVariables.Contains(name);
            if (isDirect == isFileBacked)
            {
                throw new ArgumentException(
                    $"Credential environment variable '{name}' must be classified as exactly one of direct or file-backed by runner '{runner.Kind.Value}'.",
                    parameterName);
            }
            if (isDirect)
                direct.Add(name, value);
        }
        return new ReadOnlyDictionary<string, string>(direct);
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
