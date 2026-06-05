using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Codex;

/// <summary>
/// Extracts token counts from the OpenAI Codex CLI output.
///
/// Tries two formats:
/// 1. JSON output (codex exec --json): looks for {"usage":{"input_tokens":N,"cached_input_tokens":C,"output_tokens":M}}
///    and the OpenAI usage shape {"usage":{"prompt_tokens":N,"prompt_tokens_details":{"cached_tokens":C},"completion_tokens":M}}.
/// 2. Human-readable patterns: "Usage: N input, M output tokens".
/// </summary>
public sealed class CodexCostExtractor : IAgentCostExtractor
{
    private const int MaxUsageSearchDepth = 8;

    public AgentKind Kind => AgentKind.Codex;

    public ModelRateConfig? DefaultPricing { get; } = new()
    {
        InputPerMillion = 5.0,
        CachedInputPerMillion = 0.50,
        OutputPerMillion = 25.0,
    };

    private static readonly Regex UsagePromptPattern = new(
        @"[Pp]rompt\s+tokens?[:\s]+(\d[\d,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UsageCompletionPattern = new(
        @"[Cc]ompletion\s+tokens?[:\s]+(\d[\d,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UsageCachedPattern = new(
        @"[Cc]ache(?:d)?\s+(?:input\s+)?tokens?[:\s]+(\d[\d,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InputPattern = new(
        @"(\d[\d,]*)\s+input\s+tokens?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OutputPattern = new(
        @"(\d[\d,]*)\s+output\s+tokens?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CachedPattern = new(
        @"(\d[\d,]*)\s+cached(?:\s+input)?\s+tokens?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        if (string.IsNullOrWhiteSpace(agentStdout) && string.IsNullOrWhiteSpace(agentStderr))
            return null;

        var jsonResult = TryParseJsonOutput(agentStdout) ?? TryParseJsonOutput(agentStderr);
        if (jsonResult is not null) return jsonResult;

        return TryParseHumanReadable(agentStdout) ?? TryParseHumanReadable(agentStderr);
    }

    private static AgentCostSnapshot? TryParseJsonOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (text.IndexOf("usage", StringComparison.OrdinalIgnoreCase) < 0) return null;

        foreach (var line in text.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.Contains("usage", StringComparison.OrdinalIgnoreCase)) continue;
            if (!line.StartsWith('{') && !line.Contains("{")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var snapshot = ExtractFromDoc(doc.RootElement);
                if (snapshot is not null) return snapshot;
            }
            catch (JsonException) { }
        }

        try
        {
            using var doc = JsonDocument.Parse(text.Trim());
            return ExtractFromDoc(doc.RootElement);
        }
        catch (JsonException) { }

        return null;
    }

    private static AgentCostSnapshot? ExtractFromDoc(JsonElement root)
    {
        if (!TryGetUsage(root, out var usage, depth: 0)) return null;

        int totalInput = 0, output = 0;
        string? modelId = null;

        if (usage.TryGetProperty("prompt_tokens", out var pt) && TryGetNonNegativeInt32(pt, out var ptv)) totalInput = ptv;
        else if (usage.TryGetProperty("input_tokens", out var it) && TryGetNonNegativeInt32(it, out var itv)) totalInput = itv;

        if (usage.TryGetProperty("completion_tokens", out var ct) && TryGetNonNegativeInt32(ct, out var ctv)) output = ctv;
        else if (usage.TryGetProperty("output_tokens", out var ot) && TryGetNonNegativeInt32(ot, out var otv)) output = otv;

        var cached = TryReadCachedInputTokens(usage);
        var freshInput = Math.Max(0, totalInput - cached);

        if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
        {
            var raw = m.GetString();
            modelId = raw is { Length: > 128 } ? raw[..128] : raw;
        }

        if (totalInput == 0 && cached == 0 && output == 0) return null;
        return new AgentCostSnapshot(freshInput, cached, output, modelId);
    }

    private static bool TryGetUsage(JsonElement root, out JsonElement usage, int depth)
    {
        if (depth > MaxUsageSearchDepth)
        {
            usage = default;
            return false;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("usage", out usage)) return true;
            if (root.TryGetProperty("token_usage", out usage)) return true;
            if (root.TryGetProperty("total_token_usage", out usage)) return true;

            if (root.TryGetProperty("payload", out var payload) && TryGetUsage(payload, out usage, depth + 1)) return true;
            if (root.TryGetProperty("item", out var item) && TryGetUsage(item, out usage, depth + 1)) return true;
            if (root.TryGetProperty("info", out var info) && TryGetUsage(info, out usage, depth + 1)) return true;
        }

        usage = default;
        return false;
    }

    private static bool TryGetNonNegativeInt32(JsonElement element, out int value)
    {
        if (element.TryGetInt32(out value) && value >= 0)
            return true;

        value = 0;
        return false;
    }

    private static int TryReadCachedInputTokens(JsonElement usage)
    {
        if (usage.TryGetProperty("cached_input_tokens", out var cachedInput)
            && TryGetNonNegativeInt32(cachedInput, out var cachedInputValue))
            return cachedInputValue;

        if (usage.TryGetProperty("prompt_tokens_details", out var promptDetails)
            && promptDetails.TryGetProperty("cached_tokens", out var promptCached)
            && TryGetNonNegativeInt32(promptCached, out var promptCachedValue))
            return promptCachedValue;

        if (usage.TryGetProperty("input_tokens_details", out var inputDetails)
            && inputDetails.TryGetProperty("cached_tokens", out var inputCached)
            && TryGetNonNegativeInt32(inputCached, out var inputCachedValue))
            return inputCachedValue;

        if (usage.TryGetProperty("cache_read_input_tokens", out var cacheRead)
            && TryGetNonNegativeInt32(cacheRead, out var cacheReadValue))
            return cacheReadValue;

        if (usage.TryGetProperty("cached_tokens", out var cached)
            && TryGetNonNegativeInt32(cached, out var cachedValue))
            return cachedValue;

        return 0;
    }

    private static AgentCostSnapshot? TryParseHumanReadable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var promptM = UsagePromptPattern.Match(text);
        var completionM = UsageCompletionPattern.Match(text);
        if (promptM.Success && completionM.Success)
        {
            var cached = TryParseCachedTokens(text);
            var input = Math.Max(0, ParseTokenCount(promptM.Groups[1].Value) - cached);
            var output = ParseTokenCount(completionM.Groups[1].Value);
            return new AgentCostSnapshot(input, cached, output, null);
        }

        var inputM = InputPattern.Match(text);
        var outputM = OutputPattern.Match(text);
        if (inputM.Success && outputM.Success)
        {
            var cached = TryParseCachedTokens(text);
            var input = Math.Max(0, ParseTokenCount(inputM.Groups[1].Value) - cached);
            var output = ParseTokenCount(outputM.Groups[1].Value);
            if (input > 0 || cached > 0 || output > 0)
                return new AgentCostSnapshot(input, cached, output, null);
        }

        return null;
    }

    private static int TryParseCachedTokens(string text)
    {
        var label = UsageCachedPattern.Match(text);
        if (label.Success)
            return ParseTokenCount(label.Groups[1].Value);

        var compact = CachedPattern.Match(text);
        return compact.Success ? ParseTokenCount(compact.Groups[1].Value) : 0;
    }

    private static int ParseTokenCount(string s)
    {
        var cleaned = s.Replace(",", "", StringComparison.Ordinal);
        return int.TryParse(cleaned, out var v) ? v : 0;
    }
}
