using System.Text.Json;

namespace CodeyBox.Agents.DotNetOpencode;

/// <summary>
/// Pure extraction of dotnet-opencode's terminal run error from
/// <c>run --format json</c> output.
///
/// <para>Two framings, in priority order:</para>
/// <list type="number">
/// <item><description>A structured <c>{"type":"error",…,"error":{…}}</c>
/// frame on stdout (verified live: <c>provider.auth</c> /
/// <c>"Provider request failed with HTTP 401."</c> with numeric
/// <c>status</c>; <c>provider.invalid-request</c> /
/// <c>"No available model is present in the configured catalog."</c>;
/// <c>unknown</c> / ripgrep-missing). The first one wins.</description></item>
/// <item><description>Host/shell-level plaintext on stderr when the CLI never
/// started: <c>command not found</c> / exit 127 (binary absent),
/// <c>You must install or update .NET</c> (preview runtime missing),
/// ripgrep-missing, and the managed-service <c>listener address is already
/// in use</c> collision. These name an infrastructure cause so a broken
/// install is never recorded as the model declining to act.</description></item>
/// </list>
///
/// <para>Returns null when no terminal error is present. Never throws:
/// malformed lines are skipped so a half-written stream still yields whatever
/// terminal signal it contains. Output is capped at
/// <see cref="MaxDiagnosticChars"/> so the pipeline's audit/webhook sinks
/// never balloon on a verbose provider error body.</para>
/// </summary>
internal static class DotNetOpencodeTerminalDiagnoser
{
    internal const int MaxDiagnosticChars = 500;

    internal static string? TryExtractTerminalError(string? stdout, string? stderr)
    {
        if (TryExtractStructuredError(stdout) is { } structured)
            return structured;

        return TryExtractInfrastructureError(stderr);
    }

    private static string? TryExtractStructuredError(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith('{'))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (TryExtractErrorFrame(doc.RootElement) is { } error)
                    return error;
            }
            catch (JsonException)
            {
                // Half-written frames or ASP.NET hosting logs interleaved on
                // stdout in --standalone mode — keep scanning.
            }
        }

        return null;
    }

    private static string? TryExtractErrorFrame(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "error", StringComparison.Ordinal))
        {
            return null;
        }

        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return Truncate("dotnet-opencode run emitted an error frame with no error detail");

        var kind = error.TryGetProperty("type", out var errorType) && errorType.ValueKind == JsonValueKind.String
            ? errorType.GetString()
            : null;
        var message = error.TryGetProperty("message", out var errorMessage) && errorMessage.ValueKind == JsonValueKind.String
            ? errorMessage.GetString()
            : null;

        var combined = string.IsNullOrWhiteSpace(kind)
            ? message
            : string.IsNullOrWhiteSpace(message) ? $"dotnet-opencode error: {kind}" : $"{kind}: {message}";
        if (string.IsNullOrWhiteSpace(combined))
            return Truncate("dotnet-opencode run emitted an empty error frame");

        if (error.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number)
            combined += $" (status {status.GetRawText()})";

        return Truncate(combined!);
    }

    private static string? TryExtractInfrastructureError(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return null;

        foreach (var rawLine in stderr.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.Contains("command not found", StringComparison.OrdinalIgnoreCase)
                || line.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase)
                || line.Contains("exit 127", StringComparison.OrdinalIgnoreCase)
                || line.Contains("You must install or update .NET", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Ripgrep is unavailable", StringComparison.OrdinalIgnoreCase)
                || line.Contains("listener address is already in use", StringComparison.OrdinalIgnoreCase))
            {
                return Truncate(line);
            }
        }

        return null;
    }

    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxDiagnosticChars
            ? trimmed
            : trimmed[..MaxDiagnosticChars] + "…";
    }
}
