using System.Text;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.TartSandboxPlugin;

/// <summary>
/// Pure remote-shell construction for the Tart guest SSH transport. Every
/// value crossing into the remote command is single-quote escaped here — at
/// this sink — so future callers cannot smuggle shell metacharacters through
/// a new exec path. Secret values never reach these builders: they travel a
/// stdin-fed base64 file staged by a separate SSH call.
/// </summary>
public static class TartShellCommand
{
    /// <summary>Directory staging per-exec secret environment files.</summary>
    public const string SecretEnvStagingDirectory = "/tmp/.codeybox-exec-env";

    /// <summary>Directory staging per-exec guest status files.</summary>
    public const string ExecStatusDirectory = "/tmp/.codeybox-exec-status";

    /// <summary>
    /// Wraps a user command so its guest exit code lands in a status file as
    /// well as the process exit: an SSH client failure (exit 255 with no
    /// guest side effects) is otherwise indistinguishable from a guest
    /// `exit 255`. The reader trusts the status file, never the bare exit.
    /// </summary>
    public static string WrapWithStatusFile(string innerCommand, string statusFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(innerCommand);
        TartGuestPath.ValidateAbsolute(statusFile, nameof(statusFile));
        return $"{innerCommand}; __cb_code=$?; printf '%s' \"$__cb_code\" > {Quote(statusFile)}; exit \"$__cb_code\"";
    }

    /// <summary>Remote command reading a status file written by <see cref="WrapWithStatusFile"/>.</summary>
    public static string BuildReadStatusCommand(string statusFile)
    {
        TartGuestPath.ValidateAbsolute(statusFile, nameof(statusFile));
        return $"cat -- {Quote(statusFile)}";
    }

    /// <summary>Remote command removing a status or secret-env staging file (best effort).</summary>
    public static string BuildRemoveFileCommand(string guestPath)
    {
        TartGuestPath.ValidateAbsolute(guestPath, nameof(guestPath));
        return $"rm -f -- {Quote(guestPath)}";
    }

    /// <summary>Single-quote escapes one shell word.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// Builds the remote command for a guest exec with a non-secret
    /// environment. Validates names through
    /// <see cref="SandboxEnvironmentVariableName"/> and bounds the merged
    /// environment and the command before returning it.
    /// </summary>
    public static string Build(
        IReadOnlyDictionary<string, string> specEnvironment,
        SandboxExec exec,
        string workingDirectory,
        int maxEnvironmentBytes,
        int maxCommandBytes)
    {
        ArgumentNullException.ThrowIfNull(specEnvironment);
        ArgumentNullException.ThrowIfNull(exec);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var merged = new Dictionary<string, string>(specEnvironment, StringComparer.Ordinal);
        if (exec.ExtraEnvironment is not null)
        {
            foreach (var (key, value) in exec.ExtraEnvironment)
            {
                SandboxEnvironmentVariableName.Validate(key, nameof(SandboxExec.ExtraEnvironment));
                merged[key] = value ?? string.Empty;
            }
        }
        exec.ApplyEnvironmentRemovals(name => merged.Remove(name));

        var preamble = new StringBuilder();
        preamble.Append("mkdir -p -- ").Append(Quote(workingDirectory)).Append(" && cd -- ").Append(Quote(workingDirectory));
        var environmentBytes = 0;
        foreach (var (key, value) in merged)
        {
            SandboxEnvironmentVariableName.Validate(key, nameof(SandboxExec.ExtraEnvironment));
            environmentBytes += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value);
            if (environmentBytes > maxEnvironmentBytes)
                throw new InvalidOperationException($"Exec environment exceeds the {maxEnvironmentBytes}-byte bound.");
            preamble.Append(" && export ").Append(Quote(key)).Append('=').Append(Quote(value));
        }
        foreach (var name in exec.EnvironmentVariablesToUnset)
            preamble.Append(" && unset -- ").Append(Quote(name));

        preamble.Append(" && exec");
        foreach (var arg in exec.Argv)
            preamble.Append(' ').Append(Quote(arg ?? string.Empty));

        var command = preamble.ToString();
        if (Encoding.UTF8.GetByteCount(command) > maxCommandBytes)
            throw new InvalidOperationException($"Exec command exceeds the {maxCommandBytes}-byte bound.");
        return command;
    }

    /// <summary>
    /// Builds the remote command sourcing a staged secret-environment file,
    /// deleting it before argv runs so a concurrent guest listing never sees
    /// it. The file itself was staged by a separate stdin-fed SSH call, so
    /// neither the file content nor its base64 form appears here.
    /// </summary>
    public static string BuildSourced(
        SandboxExec exec,
        string workingDirectory,
        string stagedEnvFile,
        int maxCommandBytes)
    {
        ArgumentNullException.ThrowIfNull(exec);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        TartGuestPath.ValidateAbsolute(stagedEnvFile, nameof(stagedEnvFile));

        var preamble = new StringBuilder();
        preamble.Append("mkdir -p -- ").Append(Quote(workingDirectory)).Append(" && cd -- ").Append(Quote(workingDirectory));
        preamble.Append(" && . ").Append(Quote(stagedEnvFile));
        preamble.Append(" && rm -f -- ").Append(Quote(stagedEnvFile));
        foreach (var name in exec.EnvironmentVariablesToUnset)
            preamble.Append(" && unset -- ").Append(Quote(name));
        preamble.Append(" && exec");
        foreach (var arg in exec.Argv)
            preamble.Append(' ').Append(Quote(arg ?? string.Empty));

        var command = preamble.ToString();
        if (Encoding.UTF8.GetByteCount(command) > maxCommandBytes)
            throw new InvalidOperationException($"Exec command exceeds the {maxCommandBytes}-byte bound.");
        return command;
    }

    /// <summary>
    /// Builds the `export` preamble lines for a secret environment file: one
    /// `export 'NAME'='value'` line per entry, each name validated. The caller
    /// base64-encodes the result and pipes it over SSH stdin — never argv.
    /// </summary>
    public static string BuildSecretEnvFile(
        IReadOnlyDictionary<string, string> specEnvironment,
        SandboxExec exec,
        int maxEnvironmentBytes)
    {
        ArgumentNullException.ThrowIfNull(specEnvironment);
        ArgumentNullException.ThrowIfNull(exec);

        var merged = new Dictionary<string, string>(specEnvironment, StringComparer.Ordinal);
        if (exec.ExtraEnvironment is not null)
        {
            foreach (var (key, value) in exec.ExtraEnvironment)
            {
                SandboxEnvironmentVariableName.Validate(key, nameof(SandboxExec.ExtraEnvironment));
                merged[key] = value ?? string.Empty;
            }
        }
        exec.ApplyEnvironmentRemovals(name => merged.Remove(name));

        var file = new StringBuilder();
        var environmentBytes = 0;
        foreach (var (key, value) in merged)
        {
            SandboxEnvironmentVariableName.Validate(key, nameof(SandboxExec.ExtraEnvironment));
            environmentBytes += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value);
            if (environmentBytes > maxEnvironmentBytes)
                throw new InvalidOperationException($"Exec environment exceeds the {maxEnvironmentBytes}-byte bound.");
            file.Append("export ").Append(Quote(key)).Append('=').Append(Quote(value)).Append('\n');
        }
        return file.ToString();
    }

    /// <summary>
    /// Remote command writing a file from a base64 stream on stdin. The path
    /// is the only thing quoted into the command; content arrives on stdin.
    /// </summary>
    public static string BuildWriteFileCommand(string guestPath)
    {
        TartGuestPath.ValidateAbsolute(guestPath, nameof(guestPath));
        var directory = guestPath.Contains('/', StringComparison.Ordinal)
            ? guestPath[..guestPath.LastIndexOf('/')]
            : "/";
        if (directory.Length == 0)
            directory = "/";
        return $"umask 077 && mkdir -p -- {Quote(directory)} && base64 -d > {Quote(guestPath)}";
    }

    /// <summary>Remote command reading a file as base64 on stdout.</summary>
    public static string BuildReadFileCommand(string guestPath)
    {
        TartGuestPath.ValidateAbsolute(guestPath, nameof(guestPath));
        return $"base64 {Quote(guestPath)}";
    }

    /// <summary>Remote command listing regular files under a directory, NUL-separated.</summary>
    public static string BuildListFilesCommand(string guestDirectory)
    {
        TartGuestPath.ValidateAbsolute(guestDirectory, nameof(guestDirectory));
        return $"find {Quote(guestDirectory)} -type f -print0";
    }

    /// <summary>Remote command creating a directory (and parents).</summary>
    public static string BuildMkdirCommand(string guestDirectory)
    {
        TartGuestPath.ValidateAbsolute(guestDirectory, nameof(guestDirectory));
        return $"mkdir -p -- {Quote(guestDirectory)}";
    }

    /// <summary>
    /// Parses `tart list --format json`: a top-level array (or an object with
    /// a `vms`/`items` array) of objects carrying a name and a state. Unknown
    /// extra fields are ignored so additive CLI changes do not break listing.
    /// </summary>
    public static IReadOnlyList<TartVmListEntry> ParseListJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        JsonElement array = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("vms", out var vms) && vms.ValueKind == JsonValueKind.Array)
                array = vms;
            else if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                array = items;
            else
                throw new JsonException("tart list JSON has no array or vms/items property.");
        }
        if (array.ValueKind != JsonValueKind.Array)
            throw new JsonException("tart list JSON is not an array.");

        var entries = new List<TartVmListEntry>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var name = GetFirstString(item, "name", "Name", "vm", "VM");
            var state = GetFirstString(item, "state", "State", "status", "Status");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            entries.Add(new TartVmListEntry(name.Trim(), (state ?? string.Empty).Trim()));
        }
        return entries;
    }

    /// <summary>
    /// Fallback parser for plain `tart list` text: the first whitespace-separated
    /// token of each non-header line is the VM name. Header lines (starting
    /// with "name"/"source" or a dash rule) are skipped.
    /// </summary>
    public static IReadOnlyList<TartVmListEntry> ParseListText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<TartVmListEntry>();
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            var first = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(first))
                continue;
            if (first.StartsWith('-') || string.Equals(first, "name", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "source", StringComparison.OrdinalIgnoreCase))
                continue;
            var rest = line[first.Length..].Trim();
            var state = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
            entries.Add(new TartVmListEntry(first, state));
        }
        return entries;
    }

    private static string? GetFirstString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }
}

/// <summary>One VM row from `tart list`.</summary>
public sealed record TartVmListEntry(string Name, string State);
