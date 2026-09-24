using System.Text.Json;

namespace CodeyBox.Agents.Unreal;

/// <summary>
/// Pure extraction of Unreal's terminal failure diagnostic from stdout JSON error events
/// and stderr output lines.
///
/// <para>On any error, <c>unreal-agent-runner</c> writes a structured error event
/// <c>{"type":"error","message":"..."}</c> to stdout and writes the prefixed error
/// message <c>unreal-agent-runner: ...</c> to stderr, then exits 1 (or 130 on SIGINT).
/// This diagnoser extracts the root cause so the orchestrator can classify failures
/// cleanly rather than reporting generic non-zero exits.</para>
/// </summary>
public static class UnrealTerminalDiagnoser
{
    public const int MaxDiagnosticChars = 500;
    public const string BinaryPrefix = "unreal-agent-runner:";

    public static string? TryExtractTerminalError(string? stdout, string? stderr)
    {
        // 1. Try to extract from stdout structured JSON error frame
        if (TryExtractFromStdout(stdout) is { } stdoutError)
            return stdoutError;

        // 2. Try to extract from stderr lines
        if (TryExtractFromStderr(stderr) is { } stderrError)
            return stderrError;

        // 3. Scan stdout line-by-line for any prefixed or JSON error lines as fallback
        if (TryExtractFromStderr(stdout) is { } fallbackError)
            return fallbackError;

        return null;
    }

    private static string? TryExtractFromStdout(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        var reader = new StringReader(stdout);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("type", out var typeProp)
                    && typeProp.ValueKind == JsonValueKind.String
                    && string.Equals(typeProp.GetString(), "error", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("message", out var msgProp)
                    && msgProp.ValueKind == JsonValueKind.String)
                {
                    var msg = msgProp.GetString();
                    if (!string.IsNullOrWhiteSpace(msg))
                        return Truncate(msg.Trim());
                }
            }
            catch (JsonException)
            {
                // Non-JSON or malformed lines are skipped
            }
        }

        return null;
    }

    private static string? TryExtractFromStderr(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return null;

        var reader = new StringReader(stderr);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            if (trimmed.StartsWith(BinaryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var msg = trimmed[BinaryPrefix.Length..].Trim();
                if (!string.IsNullOrEmpty(msg))
                    return Truncate(msg);
            }
        }

        return null;
    }

    private static string Truncate(string message)
    {
        if (message.Length <= MaxDiagnosticChars)
            return message;
        return message[..MaxDiagnosticChars];
    }
}
