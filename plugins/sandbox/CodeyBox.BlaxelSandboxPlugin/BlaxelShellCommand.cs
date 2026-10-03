using System.Text;
using CodeyBox.Core;

namespace CodeyBox.BlaxelPlugin;

/// <summary>
/// Pure construction of the guest shell commands sent to Blaxel's <c>POST /process</c>
/// endpoint. Unlike command-string transports, Blaxel's process API carries the
/// environment natively (<c>ProcessRequest.env</c>), so this builder only renders
/// argv, the working directory, and bounded stdin — environment values never enter
/// the command string at all, secret-bearing or otherwise. All inputs are treated
/// as untrusted; every bound is enforced before the command is returned.
/// </summary>
public static class BlaxelShellCommand
{
    /// <summary>Builds the guest shell command for one exec (argv, workdir, stdin only).</summary>
    /// <exception cref="ArgumentException">Empty argv or oversized payload.</exception>
    public static string Build(
        SandboxExec exec,
        string workingDirectory,
        int maxCommandBytes,
        int maxStdinBytes)
    {
        ArgumentNullException.ThrowIfNull(exec);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ValidateArgv(exec);

        var stdinBase64 = EncodeStdin(exec, maxStdinBytes);

        var builder = new StringBuilder();
        builder.Append("set -u; ");
        AppendWorkdirAndArgv(builder, exec.Argv, workingDirectory);

        var command = WrapStdin(builder.ToString(), stdinBase64);
        CheckCommandBytes(command, maxCommandBytes, nameof(exec));

        return command;
    }

    /// <summary>
    /// Merges the spec baseline with the exec overlay (exec wins) and applies
    /// <c>EnvironmentVariablesToUnset</c> removals. The merged map is delivered
    /// natively via <c>ProcessRequest.env</c> — never interpolated into the
    /// command string — but names are still validated and the payload bound.
    /// </summary>
    /// <exception cref="ArgumentException">Empty argv, invalid env names, or oversized payload.</exception>
    public static Dictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string> specEnvironment,
        SandboxExec exec,
        int maxEnvironmentBytes)
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

        if (exec.EnvironmentVariablesToUnset.Count > 0)
        {
            exec.ApplyEnvironmentRemovals(name => merged.Remove(name));
        }

        var environmentBytes = 0L;
        foreach (var (key, value) in merged)
        {
            environmentBytes += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value);
            if (environmentBytes > maxEnvironmentBytes)
            {
                throw new ArgumentException(
                    $"Merged environment exceeds {maxEnvironmentBytes} bytes; refusing to build the exec request.",
                    nameof(exec));
            }
        }

        return merged;
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
