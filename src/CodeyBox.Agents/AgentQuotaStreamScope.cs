using System.Text.Json;

namespace CodeyBox.Agents;

/// <summary>
/// Helpers that scope quota-failure detection to the terminal agent error in a
/// buffered NDJSON stream. Long multi-turn runs can carry earlier quota-shaped
/// lines in the same stdout buffer; scanning the whole buffer false-positives
/// a later API 400 agent crash (e.g. Claude thinking-block modification) as
/// quota exhaustion.
/// </summary>
public static class AgentQuotaStreamScope
{
    /// <summary>
    /// Claude stream-json exposes HTTP status on terminal error results.
    /// </summary>
    internal const string ClaudeThinkingBlockSignature =
        "blocks in the latest assistant message cannot be modified";

    /// <summary>
    /// Returns true when the captured streams represent a non-quota agent/API
    /// crash (e.g. Claude 400 thinking-block modification). Must run before
    /// provider quota detectors so stale quota keywords in earlier NDJSON lines
    /// cannot false-positive the terminal failure.
    /// </summary>
    public static bool IsNonQuotaAgentApiCrash(string? stderr, string? stdout)
    {
        if (TryGetTerminalStreamError(stdout, out var terminal))
        {
            if (terminal.ApiErrorStatus is 400)
                return true;

            if (terminal.ApiErrorStatus is int status
                && status is >= 400 and < 500
                && status is not 429 and not 529)
            {
                return true;
            }

            if (ContainsThinkingBlockSignature(terminal.Message))
                return true;
        }

        if (!string.IsNullOrEmpty(stderr) && ContainsThinkingBlockSignature(stderr))
            return true;

        return false;
    }

    /// <summary>
    /// When stdout is NDJSON with a terminal error result, returns only that
    /// line so downstream detectors do not match quota patterns from earlier
    /// events in the same buffer. Otherwise returns <paramref name="stdout"/>
    /// unchanged.
    /// </summary>
    public static string? ScopeStdoutForQuotaDetection(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return stdout;

        return TryGetTerminalStreamError(stdout, out var terminal)
            ? terminal.RawLine
            : stdout;
    }

    internal static bool ContainsThinkingBlockSignature(string? text) =>
        !string.IsNullOrEmpty(text)
        && (text.Contains(ClaudeThinkingBlockSignature, StringComparison.OrdinalIgnoreCase)
            || text.Contains("`thinking`", StringComparison.Ordinal)
            || text.Contains("`redacted_thinking`", StringComparison.Ordinal));

    internal static bool TryGetTerminalStreamError(string? stdout, out TerminalStreamError terminal)
    {
        terminal = default!;
        if (string.IsNullOrWhiteSpace(stdout))
            return false;

        var first = stdout.AsSpan().TrimStart();
        if (first.IsEmpty || first[0] != '{')
            return false;

        TerminalStreamError? last = null;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            if (!TryParseErrorLine(line, out var parsed)) continue;
            last = parsed;
        }

        if (last is null)
            return false;

        terminal = last;
        return true;
    }

    private static bool TryParseErrorLine(string line, out TerminalStreamError terminal)
    {
        terminal = default!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)
                || typeProp.GetString() != "result")
            {
                return false;
            }

            var isError = false;
            if (root.TryGetProperty("is_error", out var isErrorProp)
                && isErrorProp.ValueKind == JsonValueKind.True)
            {
                isError = true;
            }

            if (root.TryGetProperty("subtype", out var subtypeProp)
                && string.Equals(subtypeProp.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                isError = true;
            }

            if (root.TryGetProperty("status", out var statusProp)
                && string.Equals(statusProp.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                isError = true;
            }

            if (!isError) return false;

            var message = ReadString(root, "result")
                ?? ReadString(root, "message");
            if (root.TryGetProperty("error", out var errorProp))
            {
                if (errorProp.ValueKind == JsonValueKind.Object)
                    message ??= ReadString(errorProp, "message");
                else if (errorProp.ValueKind == JsonValueKind.String)
                    message ??= errorProp.GetString();
            }

            int? apiStatus = null;
            if (root.TryGetProperty("api_error_status", out var statusElement))
            {
                apiStatus = statusElement.ValueKind switch
                {
                    JsonValueKind.Number when statusElement.TryGetInt32(out var code) => code,
                    JsonValueKind.String when int.TryParse(statusElement.GetString(), out var parsed) => parsed,
                    _ => null,
                };
            }

            terminal = new TerminalStreamError(line, message, apiStatus);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement node, string property)
        => node.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    internal sealed record TerminalStreamError(string RawLine, string? Message, int? ApiErrorStatus);
}
