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
    /// Opus-tier rate (the conservative top end <em>within CrockCode's
    /// curated Anthropic-Claude set</em>). Operators pinning an unknown
    /// model id from a pricier family (e.g. a future frontier model
    /// released after this list was curated) should configure an explicit
    /// per-model rate in <c>agent-pricing-defaults.json</c> rather than
    /// relying on this fallback — the fallback only guarantees the upper
    /// bound for ids the curated set already covers.
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

        // Prefer the typed `type:"result"` envelope (Anthropic's terminal
        // billable totals); fall back to the bare `usage` shape only when
        // no result envelope is seen. ClaudeCostExtractor uses the same
        // ordering precisely because a per-message partial `usage` line
        // emitted AFTER the result envelope would otherwise clobber the
        // final totals.
        int resultInput = 0, resultOutput = 0, resultCached = 0;
        bool sawResult = false;
        int bareInput = 0, bareOutput = 0, bareCached = 0;
        bool sawBareUsage = false;
        string? modelId = null;

        foreach (var line in stdout.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var isResult = root.TryGetProperty("type", out var typeProp)
                    && typeProp.ValueKind == JsonValueKind.String
                    && string.Equals(typeProp.GetString(), "result", StringComparison.Ordinal);

                if (isResult && root.TryGetProperty("usage", out var typedUsage))
                {
                    ExtractUsageCounts(typedUsage, out var input, out var output, out var cached);
                    resultInput = input;
                    resultOutput = output;
                    resultCached = cached;
                    sawResult = true;
                }
                else if (!isResult
                    && root.TryGetProperty("usage", out var bareUsage)
                    && bareUsage.ValueKind == JsonValueKind.Object)
                {
                    ExtractUsageCounts(bareUsage, out var input, out var output, out var cached);
                    bareInput = input;
                    bareOutput = output;
                    bareCached = cached;
                    sawBareUsage = true;
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

        if (sawResult)
            return new AgentCostSnapshot(resultInput, resultCached, resultOutput, modelId);
        if (sawBareUsage)
            return new AgentCostSnapshot(bareInput, bareCached, bareOutput, modelId);
        return null;
    }

    private static void ExtractUsageCounts(
        JsonElement usage, out int inputTokens, out int outputTokens, out int cachedTokens)
    {
        var freshInput = usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv) ? itv : 0;
        var cacheCreation = usage.TryGetProperty("cache_creation_input_tokens", out var cct) && cct.TryGetInt32(out var cctv) ? cctv : 0;
        var cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var crt) && crt.TryGetInt32(out var crtv) ? crtv : 0;
        inputTokens = freshInput + cacheCreation;
        outputTokens = usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv) ? otv : 0;
        cachedTokens = cacheRead;
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
