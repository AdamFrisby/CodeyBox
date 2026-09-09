using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Antigravity;

/// <summary>
/// Extracts token counts from the Google Antigravity CLI (<c>agy --print</c>).
/// agy follows a Claude-shaped one-shot output format: a terminal
/// <c>{"type":"result", ..., "usage": {...}}</c> NDJSON envelope when invoked
/// with the structured output flag, plus a human-readable footer like
/// "Total cost: $0.07 (12,345 input, 678 output, 5,000 cached tokens)" when
/// run in plain mode. The extractor accepts either shape so the same code
/// path covers both invocation styles.
///
/// <para>The CLI's structured output names vary by gateway model — Gemini-
/// backed runs emit <c>input_tokens</c>/<c>output_tokens</c> plus
/// <c>cached_input_tokens</c>; Claude-backed runs reuse Anthropic's
/// <c>input_tokens</c> / <c>cache_creation_input_tokens</c> /
/// <c>cache_read_input_tokens</c> shape. Both are recognised here so a
/// per-model member that points at either backend reports cost correctly.</para>
/// </summary>
public sealed class AntigravityCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Antigravity;

    /// <summary>
    /// Conservative seed pricing for the gateway. Antigravity's request-based
    /// quota model (≈20 req/day Free; weekly refresh on AI Pro with up to a
    /// 7-day lockout) means token-precision billing is operator-config
    /// territory anyway — this default keeps cost reporting non-zero until
    /// the operator wires <c>CodeyBox:AgentPricing</c> for the gateway model
    /// ids they actually use. See docs/operating/costs.md.
    /// </summary>
    public ModelRateConfig? DefaultPricing { get; } = new()
    {
        InputPerMillion = 5.0,
        CachedInputPerMillion = 0.5,
        OutputPerMillion = 15.0,
    };

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

                if (modelId is null
                    && root.TryGetProperty("model", out var topModelOnAnyLine)
                    && topModelOnAnyLine.ValueKind == JsonValueKind.String)
                {
                    var raw = topModelOnAnyLine.GetString();
                    modelId = raw is { Length: > 128 } ? raw[..128] : raw;
                }

                if (type == "result" && root.TryGetProperty("usage", out var usage))
                {
                    // Anthropic-shape names (claude-* gateway models): fresh
                    // input + cache-creation count toward billing-input;
                    // cache-read is the cached bucket.
                    var freshInput = ReadInt(usage, "input_tokens");
                    var cacheCreation = ReadInt(usage, "cache_creation_input_tokens");
                    var cacheRead = ReadInt(usage, "cache_read_input_tokens");

                    if (freshInput == 0 && cacheCreation == 0 && cacheRead == 0)
                    {
                        // Gemini-shape names (gemini-* gateway models): a flat
                        // cached_input_tokens alongside input/output. Google's
                        // convention is that promptTokenCount INCLUDES cached,
                        // so subtract to recover the fresh-input billing bucket
                        // (mirrors GeminiStreamParser / TokenUsageAccounting).
                        var promptTotal = ReadInt(usage, "prompt_tokens", "promptTokenCount");
                        cacheRead = ReadInt(usage, "cached_input_tokens", "cachedInputTokenCount");
                        freshInput = TokenUsageAccounting.FreshInputTokens(promptTotal, cacheRead);
                    }

                    inputTokens = freshInput + cacheCreation;
                    outputTokens = ReadInt(usage, "output_tokens", "candidatesTokenCount", "completion_tokens");
                    cachedTokens = cacheRead;
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

    private static int ReadInt(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var v))
                return v;
        }
        return 0;
    }

    private static AgentCostSnapshot? TryParseHumanReadable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var inputM = InputPattern.Match(text);
        var outputM = OutputPattern.Match(text);
        if (!inputM.Success || !outputM.Success) return null;

        var input = ParseTokenCount(inputM.Groups[1].Value);
        var output = ParseTokenCount(outputM.Groups[1].Value);
        var cachedM = CachedPattern.Match(text);
        var cached = cachedM.Success ? ParseTokenCount(cachedM.Groups[1].Value) : 0;
        if (input == 0 && output == 0) return null;

        return new AgentCostSnapshot(
            TokenUsageAccounting.FreshInputTokens(input, cached),
            cached,
            output,
            null);
    }

    private static int ParseTokenCount(string s)
    {
        var cleaned = s.Replace(",", "", StringComparison.Ordinal);
        return int.TryParse(cleaned, out var v) ? v : 0;
    }
}
