using System.Text;
using CodeyBox.Core;

namespace CodeyBox.E2bSandboxPlugin;

/// <summary>
/// Pure construction of the guest shell commands sent to the envd command
/// gateway. Merges spec and exec environments (exec wins), applies removals,
/// base64-encodes values so no quoting edge case can break out of the
/// generated preamble, establishes the working directory, pipes bounded
/// stdin, and appends the caller argv. All inputs are treated as untrusted;
/// every bound is enforced before the command is returned.
///
/// <para>Secret-bearing execs (<see cref="SandboxExec.EnvironmentContainsSecrets"/>)
/// must not use <see cref="Build"/>: it embeds every value in the command
/// string, which the hosted control plane may retain with the execution
/// record. Route those through <see cref="BuildEnvFileContent"/> (staged via
/// the envd file gateway) plus <see cref="BuildSourcingCommand"/>, so values
/// never enter host-visible command payloads.</para>
/// </summary>
public static class E2bShellCommand
{
    /// <summary>Builds the guest shell command for one exec.</summary>
    /// <exception cref="ArgumentException">Empty argv, oversized payload, or invalid env names.</exception>
    public static string Build(
        IReadOnlyDictionary<string, string> specEnvironment,
        SandboxExec exec,
        string workingDirectory,
        int maxEnvironmentBytes,
        int maxCommandBytes,
        int maxStdinBytes)
    {
        ArgumentNullException.ThrowIfNull(specEnvironment);
        ArgumentNullException.ThrowIfNull(exec);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ValidateArgv(exec);

        var (merged, removals) = MergeEnvironment(specEnvironment, exec);
        CheckEnvironmentBytes(merged, maxEnvironmentBytes, nameof(exec));
        var stdinBase64 = EncodeStdin(exec, maxStdinBytes);

        var builder = new StringBuilder();
        builder.Append("set -u; ");
        AppendExports(builder, merged);
        AppendUnsets(builder, removals);
        AppendWorkdirAndArgv(builder, exec.Argv, workingDirectory);

        var command = WrapStdin(builder.ToString(), stdinBase64);
        CheckCommandBytes(command, maxCommandBytes, nameof(exec));

        return command;
    }

    /// <summary>
    /// Merges the spec baseline with the exec overlay (exec wins) and applies
    /// <c>EnvironmentVariablesToUnset</c> removals. Validates argv and variable
    /// names; byte bounds are enforced by the callers that serialize the result.
    /// </summary>
    /// <exception cref="ArgumentException">Empty argv or invalid env names.</exception>
    public static (Dictionary<string, string> Merged, HashSet<string> Removals) MergeEnvironment(
        IReadOnlyDictionary<string, string> specEnvironment,
        SandboxExec exec)
    {
        ArgumentNullException.ThrowIfNull(specEnvironment);
        ArgumentNullException.ThrowIfNull(exec);
        ValidateArgv(exec);

        var merged = new Dictionary<string, string>(specEnvironment, StringComparer.Ordinal);
        if (exec.ExtraEnvironment is not null)
        {
            foreach (var (key, value) in exec.ExtraEnvironment)
            {
                SandboxEnvironmentVariableName.Validate(key, nameof(exec.ExtraEnvironment));
                merged[key] = value ?? string.Empty;
            }
        }

        foreach (var key in merged.Keys.ToList())
        {
            SandboxEnvironmentVariableName.Validate(key, nameof(specEnvironment));
        }

        var removals = new HashSet<string>(StringComparer.Ordinal);
        if (exec.EnvironmentVariablesToUnset.Count > 0)
        {
            var apply = new Action<string>(name => { removals.Add(name); });
            exec.ApplyEnvironmentRemovals(apply);
            foreach (var name in removals)
            {
                merged.Remove(name);
            }
        }

        return (merged, removals);
    }

    /// <summary>
    /// Renders the merged environment as a sourceable shell script for staging
    /// via the envd file gateway. Values travel base64-encoded, so the raw
    /// secret never appears in the file as a bare literal either.
    /// </summary>
    /// <exception cref="ArgumentException">Merged environment exceeds the byte budget.</exception>
    public static string BuildEnvFileContent(
        IReadOnlyDictionary<string, string> merged,
        IReadOnlySet<string> removals,
        int maxEnvironmentBytes)
    {
        ArgumentNullException.ThrowIfNull(merged);
        ArgumentNullException.ThrowIfNull(removals);
        CheckEnvironmentBytes(merged, maxEnvironmentBytes, nameof(merged));

        var builder = new StringBuilder();
        foreach (var (key, value) in merged.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
        {
            SandboxEnvironmentVariableName.Validate(key, nameof(merged));
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            builder.Append("export ");
            builder.Append(key);
            builder.Append("=$(printf %s '");
            builder.Append(encoded);
            builder.Append("'|base64 -d)\n");
        }

        if (removals.Count > 0)
        {
            builder.Append("unset --");
            foreach (var name in removals.OrderBy(static n => n, StringComparer.Ordinal))
            {
                SandboxEnvironmentVariableName.Validate(name, nameof(removals));
                builder.Append(' ');
                builder.Append(name);
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the guest shell command for a secret-bearing exec. Sources the
    /// staged env file, deletes it before running argv (so secrets do not
    /// linger on the guest disk past process start), then behaves like
    /// <see cref="Build"/>. The command carries only the env-file path —
    /// never a secret value — so the hosted execution record stays clean.
    /// </summary>
    /// <exception cref="ArgumentException">Empty argv, oversized payload, or invalid guest path.</exception>
    public static string BuildSourcingCommand(
        string envFilePath,
        SandboxExec exec,
        string workingDirectory,
        int maxCommandBytes,
        int maxStdinBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envFilePath);
        ArgumentNullException.ThrowIfNull(exec);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ValidateArgv(exec);
        E2bGuestPath.ValidateAbsolute(envFilePath);

        var stdinBase64 = EncodeStdin(exec, maxStdinBytes);

        var quotedEnvFile = Quote(envFilePath);
        var builder = new StringBuilder();
        builder.Append("set -u; . ");
        builder.Append(quotedEnvFile);
        builder.Append(" || exit 127; rm -f -- ");
        builder.Append(quotedEnvFile);
        builder.Append("; ");
        AppendWorkdirAndArgv(builder, exec.Argv, workingDirectory);

        var command = WrapStdin(builder.ToString(), stdinBase64);
        CheckCommandBytes(command, maxCommandBytes, nameof(exec));

        return command;
    }

    private static void ValidateArgv(SandboxExec exec)
    {
        if (exec.Argv.Count == 0)
        {
            throw new ArgumentException("Exec requires at least one argv entry.", nameof(exec));
        }

        foreach (var arg in exec.Argv)
        {
            if (string.IsNullOrEmpty(arg))
            {
                throw new ArgumentException("Exec argv entries must be non-empty.", nameof(exec));
            }
        }
    }

    private static void CheckEnvironmentBytes(
        IReadOnlyDictionary<string, string> merged,
        int maxEnvironmentBytes,
        string paramName)
    {
        var environmentBytes = 0L;
        foreach (var (key, value) in merged)
        {
            environmentBytes += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value);
            if (environmentBytes > maxEnvironmentBytes)
            {
                throw new ArgumentException(
                    $"Merged environment exceeds {maxEnvironmentBytes} bytes; refusing to build the exec command.",
                    paramName);
            }
        }
    }

    private static string EncodeStdin(SandboxExec exec, int maxStdinBytes)
    {
        if (string.IsNullOrEmpty(exec.Stdin))
        {
            return string.Empty;
        }

        var stdinBytes = Encoding.UTF8.GetByteCount(exec.Stdin);
        if (stdinBytes > maxStdinBytes)
        {
            throw new ArgumentException(
                $"Exec stdin exceeds {maxStdinBytes} bytes; refusing to build the exec command.",
                nameof(exec));
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(exec.Stdin));
    }

    private static void AppendExports(StringBuilder builder, IReadOnlyDictionary<string, string> merged)
    {
        foreach (var (key, value) in merged.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            builder.Append("export ");
            builder.Append(key);
            builder.Append("=$(printf %s '");
            builder.Append(encoded);
            builder.Append("'|base64 -d); ");
        }
    }

    private static void AppendUnsets(StringBuilder builder, IReadOnlySet<string> removals)
    {
        if (removals.Count == 0)
        {
            return;
        }

        builder.Append("unset --");
        foreach (var name in removals.OrderBy(static n => n, StringComparer.Ordinal))
        {
            builder.Append(' ');
            builder.Append(name);
        }

        builder.Append("; ");
    }

    private static void AppendWorkdirAndArgv(
        StringBuilder builder,
        IReadOnlyList<string> argv,
        string workingDirectory)
    {
        var quotedWorkdir = Quote(workingDirectory);
        builder.Append("mkdir -p -- ");
        builder.Append(quotedWorkdir);
        builder.Append(" && cd -- ");
        builder.Append(quotedWorkdir);
        builder.Append(" || exit 127; ");

        builder.Append(string.Join(' ', argv.Select(Quote)));
    }

    private static string WrapStdin(string command, string stdinBase64) =>
        string.IsNullOrEmpty(stdinBase64)
            ? command
            : $"printf %s '{stdinBase64}'|base64 -d|{{ {command}; }}";

    private static void CheckCommandBytes(string command, int maxCommandBytes, string paramName)
    {
        if (Encoding.UTF8.GetByteCount(command) > maxCommandBytes)
        {
            throw new ArgumentException(
                $"Built exec command exceeds {maxCommandBytes} bytes; refusing to send it.",
                paramName);
        }
    }

    /// <summary>POSIX single-quote escaping for one shell word.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
