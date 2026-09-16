using System.Globalization;
using System.Text.RegularExpressions;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Aider;

/// <summary>
/// Best-effort token-count extractor for aider's one-shot stdout.
///
/// <para>Every run ends with aider's own accounting line (verified against aider
/// 0.86.2 live runs):
/// <c>Tokens: 766 sent, 905 received.</c> — with optional cache segments,
/// <c>Tokens: 1.2k sent, 300 cache hit, 500 received.</c>, and an optional cost
/// trailer, <c>Cost: $0.012 message, $0.012 session.</c>, when the model carries
/// cost metadata (free-tier OpenRouter models print the tokens line only). The
/// extractor parses the LAST tokens line: a run has exactly one, but last-wins
/// keeps the totals honest if a retry ever emits two. The dispatch model id
/// rides on the run header as <c>Model: &lt;id&gt; with … edit format</c> and is
/// recorded for per-model rate lookup.</para>
///
/// <para>Token-count scale suffixes follow aider's own <c>format_tokens</c>:
/// plain below 1000, one-decimal <c>k</c> below 10000, rounded <c>k</c> above.
/// The dollar <c>Cost:</c> trailer is provider-billed spend, not a token count,
/// and is intentionally not parsed — per-token rates ship in
/// <c>agent-pricing-defaults.json</c> (or as operator overrides under
/// <c>CodeyBox:AgentPricing</c>) keyed by the model id this extractor
/// records.</para>
///
/// <para>No <see cref="DefaultPricing"/> is shipped: aider fronts many providers
/// with unrelated per-token economics, so no single fallback rate is honest.
/// Unrated models cost $0 with a startup warning (see
/// <c>AgentCostCalculator.ValidateAtStartup</c>) — mirroring the
/// Cursor/Copilot subscription-path posture. Returns null (unknown, never a
/// zero that looks like data) when no tokens line is present.</para>
/// </summary>
public sealed partial class AiderCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Aider;

    public ModelRateConfig? DefaultPricing { get; } = null;

    // Cap on the model id recorded for cost attribution. Bounded so a
    // pathological header line can't blow past the schema column width on the
    // downstream cost-summary table.
    private const int MaxModelIdLength = 128;

    // aider format_tokens: "591" | "1.2k" | "12k". One shared number shape for
    // the sent / cache-hit / received segments.
    private const string CountPattern = @"(?<n>\d+(?:\.\d+)?)\s*(?<s>k)?";

    private static readonly Regex TokensLinePattern = TokensLineRegex();
    private static readonly Regex CacheHitPattern = CacheHitRegex();
    private static readonly Regex ReceivedPattern = ReceivedRegex();
    private static readonly Regex ModelHeaderPattern = ModelHeaderRegex();

    [GeneratedRegex(
        @"Tokens:\s*" + CountPattern + @"\s*sent",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokensLineRegex();

    [GeneratedRegex(
        CountPattern + @"\s*cache\s*hit",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CacheHitRegex();

    [GeneratedRegex(
        CountPattern + @"\s*received",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReceivedRegex();

    [GeneratedRegex(
        @"^Model:\s*(?<id>\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModelHeaderRegex();

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        try
        {
            var fromStdout = ScanStream(agentStdout);
            var fromStderr = ScanStream(agentStderr);
            // Prefer the stream with the larger reported total — a retry may
            // leave a truncated accounting line on one stream while the other
            // holds the full run.
            if (fromStdout is null) return fromStderr;
            if (fromStderr is null) return fromStdout;
            var stdoutTotal = fromStdout.InputTokens + fromStdout.CachedInputTokens + fromStdout.OutputTokens;
            var stderrTotal = fromStderr.InputTokens + fromStderr.CachedInputTokens + fromStderr.OutputTokens;
            return stderrTotal > stdoutTotal ? fromStderr : fromStdout;
        }
        catch (Exception)
        {
            // Contract: implementations must never throw.
            return null;
        }
    }

    private readonly record struct ScannedUsage(int Input, int Cached, int Output);

    private static AgentCostSnapshot? ScanStream(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string? modelId = null;
        ScannedUsage? latest = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var modelMatch = ModelHeaderPattern.Match(line);
            if (modelMatch.Success)
            {
                var raw = modelMatch.Groups["id"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(raw))
                    modelId = raw.Length > MaxModelIdLength ? raw[..MaxModelIdLength] : raw;
            }

            if (!TokensLinePattern.IsMatch(line))
                continue;

            var sent = ParseCount(TokensLinePattern.Match(line));
            var cacheHit = ParseCount(CacheHitPattern.Match(line));
            var received = ParseCount(ReceivedPattern.Match(line));

            // aider's "sent" counter already includes cache-write tokens
            // (message_tokens_sent += prompt_tokens + cache_write_tokens), so
            // the cache-write segment — when present — is never added again.
            // "sent" also includes cache hits (litellm prompt_tokens is the
            // total), while the cost calculator charges the cached bucket
            // separately, so split hits out to avoid double counting.
            var cached = Math.Min(cacheHit, sent);
            var input = sent - cached;
            latest = new ScannedUsage(input, cached, received);
        }

        if (latest is not { } found)
            return null;
        if (found.Input == 0 && found.Cached == 0 && found.Output == 0)
            return null;
        return new AgentCostSnapshot(found.Input, found.Cached, found.Output, modelId);
    }

    private static int ParseCount(Match match)
    {
        if (!match.Success)
            return 0;
        if (!double.TryParse(
            match.Groups["n"].Value,
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out var value))
            return 0;
        if (match.Groups["s"].Success)
            value *= 1000;
        if (value < 0 || value > int.MaxValue)
            return 0;
        return (int)value;
    }
}
