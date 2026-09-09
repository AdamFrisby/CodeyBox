using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Agents;

/// <summary>
/// Single source of truth for parsing the Anthropic token-usage envelope
/// (<c>input_tokens</c> / <c>cache_creation_input_tokens</c> /
/// <c>cache_read_input_tokens</c> / <c>output_tokens</c>) and the human-readable
/// cost footer shared by every Anthropic-billed agent CLI (Claude Code, CrockCode).
///
/// <para>Both CLIs emit the same billing envelope because CrockCode submits to
/// Anthropic's Message Batches API, which mirrors <c>/v1/messages</c>. The two
/// extractors keep their own NDJSON event walk (the stream shapes differ per CLI),
/// but the field-fold and footer policy live here so they cannot diverge — a
/// forged/overflowing counter or a new cache bucket is handled identically for
/// every Anthropic extractor.</para>
/// </summary>
public static class AnthropicUsageParsing
{
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

    /// <summary>
    /// Folds an Anthropic <c>usage</c> object into the billing buckets used by
    /// <see cref="AgentCostSnapshot"/>.
    ///
    /// <para>Terminal agent stdout is less-trusted dependency output, so every
    /// counter is clamped non-negative and the fresh+cache-creation sum saturates
    /// rather than wrapping — a forged negative or an Int32-overflowing pair cannot
    /// produce a small/negative total that would false-pass a downstream budget
    /// gate.</para>
    ///
    /// <para>Cache-creation (write) tokens are folded into the fresh-input bucket
    /// and therefore billed at the base input rate; Anthropic charges a 1.25x-2x
    /// premium on cache writes that <c>ModelRateConfig</c> has no bucket for, so
    /// the resulting spend is a conservative estimate, not exact billing.
    /// Cache-read tokens stay in the separate cached bucket.</para>
    /// </summary>
    public static void ExtractUsageCounts(
        JsonElement usage, out int inputTokens, out int outputTokens, out int cachedTokens)
    {
        var freshInput = ReadNonNegativeInt(usage, "input_tokens");
        var cacheCreation = ReadNonNegativeInt(usage, "cache_creation_input_tokens");
        var cacheRead = ReadNonNegativeInt(usage, "cache_read_input_tokens");
        var output = ReadNonNegativeInt(usage, "output_tokens");

        inputTokens = SaturatingAdd(freshInput, cacheCreation);
        outputTokens = output;
        cachedTokens = cacheRead;
    }

    private static int ReadNonNegativeInt(JsonElement usage, string propertyName)
        => usage.TryGetProperty(propertyName, out var el) && el.TryGetInt32(out var v) && v > 0 ? v : 0;

    private static int SaturatingAdd(int a, int b)
    {
        var sum = (long)a + b;
        return sum > int.MaxValue ? int.MaxValue : (int)sum;
    }

    /// <summary>
    /// Parses a human-readable cost footer, e.g.
    /// <c>"12,345 input tokens, 678 output tokens, 5,000 cached tokens"</c> or the
    /// labelled <c>"Total input tokens: 12,345"</c> / <c>"Total output tokens: 678"</c>
    /// form. The reported input total includes cached tokens, so the fresh
    /// (non-cache-read) input bucket is <c>total - cached</c> via
    /// <see cref="TokenUsageAccounting.FreshInputTokens"/>. Returns null when no
    /// input+output pair is recognisable.
    /// </summary>
    public static AgentCostSnapshot? TryParseHumanReadable(string? text)
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
