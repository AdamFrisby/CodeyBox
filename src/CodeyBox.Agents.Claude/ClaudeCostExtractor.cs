using System.Text.Json;
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

    public ModelRateConfig? DefaultPricing { get; } = new()
    {
        InputPerMillion = 15.0,
        CachedInputPerMillion = 1.50,
        OutputPerMillion = 75.0,
    };

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        if (string.IsNullOrWhiteSpace(agentStdout) && string.IsNullOrWhiteSpace(agentStderr))
            return null;

        var ndJson = TryParseNdJson(agentStdout);
        if (ndJson is not null) return ndJson;

        return AnthropicUsageParsing.TryParseHumanReadable(agentStdout)
            ?? AnthropicUsageParsing.TryParseHumanReadable(agentStderr);
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
                    // Fold the Anthropic usage envelope through the shared parser
                    // (cache-write folded into fresh input, cache-read kept
                    // separate, counters clamped/saturated) so this extractor and
                    // CrockCostExtractor apply one identical billing policy.
                    AnthropicUsageParsing.ExtractUsageCounts(
                        usage, out inputTokens, out outputTokens, out cachedTokens);
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

    /// <summary>
    /// Re-reads <c>cache_creation_input_tokens</c> from the same stream-json
    /// blob <see cref="TryExtract"/> consumes. <see cref="AgentCostSnapshot"/>
    /// (the cross-agent contract) folds cache-creation into the billable input
    /// bucket so cost rows charge correctly; <see cref="ClaudeSessionWorker"/>
    /// needs the breakdown separately to drive the ACP cache-warmth verification.
    /// Returns 0 when no recognisable <c>result</c>/<c>usage</c> event surfaces
    /// (older CLI, plain-text mode, etc.).
    /// </summary>
    public static int ExtractCacheCreationTokens(string? agentStdout)
    {
        if (string.IsNullOrWhiteSpace(agentStdout)) return 0;
        var first = agentStdout.AsSpan().TrimStart();
        if (first.IsEmpty || first[0] != '{') return 0;
        var total = 0;
        foreach (var line in agentStdout.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                if (typeProp.GetString() != "result") continue;
                if (!root.TryGetProperty("usage", out var usage)) continue;
                if (usage.TryGetProperty("cache_creation_input_tokens", out var cct)
                    && cct.TryGetInt32(out var cctv))
                {
                    total = cctv;
                }
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }
        return total;
    }

}
