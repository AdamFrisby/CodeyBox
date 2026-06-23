using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Crock;

/// <summary>
/// Extracts token counts from CrockCode CLI output for cost-attribution rows.
///
/// <para>CrockCode submits work to Anthropic's Message Batches API; the
/// terminal <c>crock status</c> output carries an Anthropic usage envelope
/// (<c>input_tokens</c> / <c>cache_creation_input_tokens</c> /
/// <c>cache_read_input_tokens</c> / <c>output_tokens</c>) — the same shape
/// <see cref="CodeyBox.Agents.Claude.ClaudeCostExtractor"/> consumes. This
/// extractor recognises that shape verbatim and also accepts a human-readable
/// footer that mirrors Claude's <c>"$cost (X input, Y output, Z cached tokens)"</c>
/// summary line — both shapes have been observed in CrockCode preview builds,
/// so we try the NDJSON path first and fall back to the footer pattern.</para>
///
/// <para><b>Default pricing.</b> The bundled per-model rates in
/// <c>agent-pricing-defaults.json</c> under the <c>crock</c> bucket are the
/// post-batch-discount effective rates (half of the on-demand
/// <c>/v1/messages</c> rate, since Anthropic applies the ~50% batch discount
/// at billing time). The defaults here mirror those rates so the cost
/// calculator has a sensible fallback for any model id the pricing file does
/// not enumerate.</para>
/// </summary>
public sealed class CrockCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Crock;

    /// <summary>
    /// Post-batch-discount fallback for any model id the pricing file does
    /// not enumerate. Half the Anthropic on-demand <c>/v1/messages</c>
    /// Opus-tier rate (the conservative top end among CrockCode's accepted
    /// model set), so spend never under-bills relative to the upper bound.
    /// </summary>
    public ModelRateConfig? DefaultPricing { get; } = new()
    {
        InputPerMillion = 2.50,
        CachedInputPerMillion = 0.25,
        OutputPerMillion = 12.50,
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

        int inputTokens = 0, outputTokens = 0, cachedTokens = 0;
        string? modelId = null;
        bool foundUsage = false;

        foreach (var line in stdout.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // CrockCode mirrors Anthropic's terminal "result" / "usage"
                // shape (the message batches API returns the same envelope as
                // /v1/messages). When the CLI surfaces a wrapper "type" field,
                // honour it; otherwise treat any object that carries a
                // "usage" property as the terminal envelope.
                JsonElement usage = default;
                bool hasUsage = false;
                if (root.TryGetProperty("type", out var typeProp)
                    && typeProp.ValueKind == JsonValueKind.String
                    && string.Equals(typeProp.GetString(), "result", StringComparison.Ordinal)
                    && root.TryGetProperty("usage", out var typedUsage))
                {
                    usage = typedUsage;
                    hasUsage = true;
                }
                else if (root.TryGetProperty("usage", out var bareUsage)
                    && bareUsage.ValueKind == JsonValueKind.Object)
                {
                    usage = bareUsage;
                    hasUsage = true;
                }

                if (hasUsage)
                {
                    var freshInput = usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv) ? itv : 0;
                    var cacheCreation = usage.TryGetProperty("cache_creation_input_tokens", out var cct) && cct.TryGetInt32(out var cctv) ? cctv : 0;
                    var cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var ct) && ct.TryGetInt32(out var ctv) ? ctv : 0;
                    inputTokens = freshInput + cacheCreation;
                    outputTokens = usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv) ? otv : 0;
                    cachedTokens = cacheRead;
                    foundUsage = true;
                }

                if (modelId is null && root.TryGetProperty("model", out var topModel)
                    && topModel.ValueKind == JsonValueKind.String)
                {
                    var raw = topModel.GetString();
                    modelId = raw is { Length: > 128 } ? raw[..128] : raw;
                }
                else if (modelId is null && root.TryGetProperty("message", out var msg)
                    && msg.ValueKind == JsonValueKind.Object
                    && msg.TryGetProperty("model", out var nestedModel)
                    && nestedModel.ValueKind == JsonValueKind.String)
                {
                    var raw = nestedModel.GetString();
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
                return new AgentCostSnapshot(TokenUsageAccounting.FreshInputTokens(input, cached), cached, output, null);
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
                return new AgentCostSnapshot(TokenUsageAccounting.FreshInputTokens(input, cached), cached, output, null);
        }

        return null;
    }

    private static int ParseTokenCount(string s)
    {
        var cleaned = s.Replace(",", "", StringComparison.Ordinal);
        return int.TryParse(cleaned, out var v) ? v : 0;
    }
}
