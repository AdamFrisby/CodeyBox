using System.Text;
using CodeyBox.Core;

namespace CodeyBox.RunloopPlugin;

/// <summary>
/// Pure construction of the single shell command string sent to Runloop's
/// <c>execute_async</c> endpoint. Merges spec and exec environments (exec wins),
/// applies removals, base64-encodes values so no quoting edge case can break
/// out of the generated preamble, establishes the working directory, pipes
/// bounded stdin, and appends the caller argv. All inputs are treated as
/// untrusted; every bound is enforced before the command is returned.
/// </summary>
public static class RunloopShellCommand
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

        var environmentBytes = 0L;
        foreach (var (key, value) in merged)
        {
            environmentBytes += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value);
            if (environmentBytes > maxEnvironmentBytes)
            {
                throw new ArgumentException(
                    $"Merged environment exceeds {maxEnvironmentBytes} bytes; refusing to build the exec command.",
                    nameof(exec));
            }
        }

        var stdinBase64 = string.Empty;
        if (!string.IsNullOrEmpty(exec.Stdin))
        {
            var stdinBytes = Encoding.UTF8.GetByteCount(exec.Stdin);
            if (stdinBytes > maxStdinBytes)
            {
                throw new ArgumentException(
                    $"Exec stdin exceeds {maxStdinBytes} bytes; refusing to build the exec command.",
                    nameof(exec));
            }

            stdinBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(exec.Stdin));
        }

        var builder = new StringBuilder();
        builder.Append("set -u; ");
        foreach (var (key, value) in merged.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            builder.Append("export ");
            builder.Append(key);
            builder.Append("=$(printf %s '");
            builder.Append(encoded);
            builder.Append("'|base64 -d); ");
        }

        if (removals.Count > 0)
        {
            builder.Append("unset --");
            foreach (var name in removals.OrderBy(static n => n, StringComparer.Ordinal))
            {
                builder.Append(' ');
                builder.Append(name);
            }

            builder.Append("; ");
        }

        var quotedWorkdir = Quote(workingDirectory);
        builder.Append("mkdir -p -- ");
        builder.Append(quotedWorkdir);
        builder.Append(" && cd -- ");
        builder.Append(quotedWorkdir);
        builder.Append(" || exit 127; ");

        builder.Append(string.Join(' ', exec.Argv.Select(Quote)));

        var command = builder.ToString();
        if (!string.IsNullOrEmpty(stdinBase64))
        {
            command = $"printf %s '{stdinBase64}'|base64 -d|{{ {command}; }}";
        }

        if (Encoding.UTF8.GetByteCount(command) > maxCommandBytes)
        {
            throw new ArgumentException(
                $"Built exec command exceeds {maxCommandBytes} bytes; refusing to send it.",
                nameof(exec));
        }

        return command;
    }

    /// <summary>POSIX single-quote escaping for one shell word.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
