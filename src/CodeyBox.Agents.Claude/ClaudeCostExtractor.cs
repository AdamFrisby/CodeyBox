using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Extracts token counts from Claude Code CLI output.
///
/// Tries two formats in order:
/// 1. NDJSON stream-json (--output-format stream-json): reads the final "result" event's
///    "usage" object and the "model" field from any "assistant" event.
/// 2. Human-readable footer: matches patterns like
///    "Total cost: $0.12 (12,345 input, 678 output, 5,000 cached tokens)"
///    or "Total input tokens: 12,345" / "Total output tokens: 678".
/// Returns null when neither format yields token counts.
/// </summary>
public sealed class ClaudeCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Claude;

    private static readonly Regex InputPattern = new(
        @"(\d[\d,]*)\s+input\s+tokens?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OutputPattern = new(
        @"(\d[\d,]*)\s+output\s+tokens?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CachedPattern = new(
        @"(\d[\d,]*)\s+cached(?:\s+input)?\s+tokens?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TotalInputPattern = new(
        @"(?:Total\s+)?[Ii]nput\s+tokens?[:\s]+(\d[\d,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TotalOutputPattern = new(
        @"(?:Total\s+)?[Oo]utput\s+tokens?[:\s]+(\d[\d,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TotalCachedPattern = new(
        @"[Cc]ache(?:d)?\s+(?:input\s+)?tokens?[:\s]+(\d[\d,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        if (string.IsNullOrWhiteSpace(agentStdout) && string.IsNullOrWhiteSpace(agentStderr))
            return null;

        var ndJson = TryParseNdJson(agentStdout);
        if (ndJson is not null) return ndJson;

        return TryParseHumanReadable(agentStdout) ?? TryParseHumanReadable(agentStderr);
    }

    private static AgentCostSnapshot? TryParseNdJson(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;

        var first = stdout.AsSpan().TrimStart();
        if (first.IsEmpty || first[0] != '{') return null;

        int inputTokens = 0, outputTokens = 0, cachedTokens = 0;
        string? modelId = null;
        bool foundUsage = false;

        foreach (var line in stdout.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString();

                if (type == "result" && root.TryGetProperty("usage", out var usage))
                {
                    inputTokens = usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv) ? itv : 0;
                    outputTokens = usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv) ? otv : 0;
                    cachedTokens = usage.TryGetProperty("cache_read_input_tokens", out var ct) && ct.TryGetInt32(out var ctv) ? ctv : 0;
                    foundUsage = true;
                }
                else if (type == "assistant" && modelId is null
                    && root.TryGetProperty("message", out var msg)
                    && msg.TryGetProperty("model", out var m))
                {
                    var raw = m.GetString();
                    modelId = raw is { Length: > 128 } ? raw[..128] : raw;
                }
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }

        if (!foundUsage) return null;
        return new AgentCostSnapshot(inputTokens, cachedTokens, outputTokens, modelId);
    }

    private static AgentCostSnapshot? TryParseHumanReadable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var inputM = InputPattern.Match(text);
        var outputM = OutputPattern.Match(text);
        if (inputM.Success && outputM.Success)
        {
            var input = ParseTokenCount(inputM.Groups[1].Value);
            var output = ParseTokenCount(outputM.Groups[1].Value);
            var cachedM = CachedPattern.Match(text);
            var cached = cachedM.Success ? ParseTokenCount(cachedM.Groups[1].Value) : 0;
            if (input > 0 || output > 0)
                return new AgentCostSnapshot(input, cached, output, null);
        }

        var tiM = TotalInputPattern.Match(text);
        var toM = TotalOutputPattern.Match(text);
        if (tiM.Success && toM.Success)
        {
            var input = ParseTokenCount(tiM.Groups[1].Value);
            var output = ParseTokenCount(toM.Groups[1].Value);
            var tcM = TotalCachedPattern.Match(text);
            var cached = tcM.Success ? ParseTokenCount(tcM.Groups[1].Value) : 0;
            if (input > 0 || output > 0)
                return new AgentCostSnapshot(input, cached, output, null);
        }

        return null;
    }

    private static int ParseTokenCount(string s)
    {
        var cleaned = s.Replace(",", "", StringComparison.Ordinal);
        return int.TryParse(cleaned, out var v) ? v : 0;
    }
}
