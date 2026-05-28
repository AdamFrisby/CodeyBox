using System.Text.Json;
using System.Text.Json.Serialization;
using CodeyBox.Agents;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Api;

/// <summary>
/// Loader for the bundled <c>agent-pricing-defaults.json</c> file shipped next
/// to the API binary. The file ships known published per-token rates for
/// providers that publish them (Claude, Codex/OpenAI, Gemini) so new
/// installations get cost reporting without the operator hand-populating
/// every entry from provider docs.
///
/// <para>
/// Subscription-only providers (Cursor, Copilot, opencode-go) are
/// intentionally excluded from the bundled rates — they do not publish
/// per-token economics, so any bundled number would be a guess. Operators who
/// want USD-equivalent reporting under those agents override per (model,
/// agent) in their own config.
/// </para>
///
/// <para>
/// Operator config from <c>CodeyBox:AgentPricing</c> always wins per
/// (agentKind, modelId). The bundled <c>_meta</c> (lastUpdated + source URLs)
/// is exposed via the <c>GET /agent-pricing</c> endpoint so operators can see
/// when the bundled file is stale relative to the provider's published rates.
/// </para>
///
/// <para>
/// On malformed JSON the loader throws — failing loud at startup is the
/// correct behaviour. Silently falling back to "no defaults" would make a
/// bundled-file typo invisible until cost reports go to zero.
/// </para>
/// </summary>
public static class AgentPricingDefaults
{
    public const string FileName = "agent-pricing-defaults.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads the bundled defaults file from <paramref name="contentRootPath"/>.
    /// Returns an empty bundle (no rates, empty meta) if the file is missing —
    /// callers can decide whether that is an error.
    /// Throws <see cref="InvalidOperationException"/> if the file is present
    /// but cannot be parsed or contains a negative rate.
    /// </summary>
    public static BundledAgentPricing Load(string contentRootPath, ILogger log)
    {
        var path = Path.Combine(contentRootPath, FileName);
        if (!File.Exists(path))
        {
            log.LogWarning(
                "AgentPricing: bundled defaults file not found at {Path}; cost estimates will rely on operator config and per-provider built-in fallbacks only",
                path);
            return new BundledAgentPricing { SourcePath = path };
        }

        BundledAgentPricing parsed;
        try
        {
            using var stream = File.OpenRead(path);
            parsed = JsonSerializer.Deserialize<BundledAgentPricing>(stream, JsonOpts)
                ?? throw new InvalidOperationException(
                    $"AgentPricing: bundled defaults file at '{path}' deserialized to null");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"AgentPricing: bundled defaults file at '{path}' is malformed: {ex.Message}", ex);
        }

        foreach (var (agentKey, modelMap) in parsed.Rates)
        {
            foreach (var (modelKey, rate) in modelMap)
            {
                if (rate is null)
                    throw new InvalidOperationException(
                        $"AgentPricing: bundled rate is null for agent '{agentKey}' model '{modelKey}'");
                if (rate.InputPerMillion < 0 || rate.CachedInputPerMillion < 0 || rate.OutputPerMillion < 0)
                    throw new InvalidOperationException(
                        $"AgentPricing: bundled rate has negative value for agent '{agentKey}' model '{modelKey}'");
            }
        }

        parsed.SourcePath = path;
        return parsed;
    }

    /// <summary>
    /// Produces a merged <see cref="AgentPricingOptions"/> snapshot.
    /// For each (agentKind, modelId), operator config wins over the bundled
    /// default. Operator <c>DefaultRates</c> are carried through unchanged
    /// (no bundled defaults at the agent-level fallback layer).
    /// The returned <see cref="MergedAgentPricing"/> carries the merged
    /// options plus diagnostic counts: total bundled entries, total operator
    /// entries (whether or not they overlapped), and the overlap count
    /// (operator entries that shadowed a bundled entry for the same key).
    /// </summary>
    public static MergedAgentPricing Merge(BundledAgentPricing bundled, AgentPricingOptions operatorOpts)
    {
        var merged = new AgentPricingOptions
        {
            DefaultRates = new Dictionary<string, ModelRateConfig>(operatorOpts.DefaultRates),
        };

        int bundledCount = 0;
        int operatorCount = 0;
        int overlapCount = 0;

        foreach (var (agentKey, modelMap) in bundled.Rates)
        {
            var copy = new Dictionary<string, ModelRateConfig>(modelMap.Count, StringComparer.Ordinal);
            foreach (var (modelKey, rate) in modelMap)
            {
                copy[modelKey] = rate;
                bundledCount++;
            }
            merged.Rates[agentKey] = copy;
        }

        // Apply operator overrides — operator wins per (agentKind, modelId).
        foreach (var (agentKey, modelMap) in operatorOpts.Rates)
        {
            if (!merged.Rates.TryGetValue(agentKey, out var bucket))
            {
                bucket = new Dictionary<string, ModelRateConfig>(StringComparer.Ordinal);
                merged.Rates[agentKey] = bucket;
            }
            foreach (var (modelKey, rate) in modelMap)
            {
                if (bucket.ContainsKey(modelKey))
                    overlapCount++;
                bucket[modelKey] = rate;
                operatorCount++;
            }
        }

        return new MergedAgentPricing(merged, bundledCount, operatorCount, overlapCount);
    }
}

/// <summary>Result of a merge — the merged options plus diagnostic counts.</summary>
public readonly record struct MergedAgentPricing(
    AgentPricingOptions Options,
    int BundledRateCount,
    int OperatorRateCount,
    int OverlapCount)
{
    /// <summary>Distinct (agent, model) rate entries after merging.</summary>
    public int TotalRateCount => BundledRateCount + OperatorRateCount - OverlapCount;
}

/// <summary>
/// Parsed shape of <c>agent-pricing-defaults.json</c>. Mirrors
/// <see cref="AgentPricingOptions"/> except the bundled file does not carry
/// agent-level <c>DefaultRates</c>; only per (agent, model) rates plus a
/// <c>_meta</c> block.
/// </summary>
public sealed class BundledAgentPricing
{
    [JsonPropertyName("_meta")]
    public BundledAgentPricingMeta Meta { get; set; } = new();

    public Dictionary<string, Dictionary<string, ModelRateConfig>> Rates { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>Absolute path the bundle was loaded from; populated by the loader.</summary>
    [JsonIgnore]
    public string SourcePath { get; set; } = "";
}

public sealed class BundledAgentPricingMeta
{
    public string LastUpdated { get; set; } = "";
    public Dictionary<string, string> Sources { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Notes { get; set; } = new(StringComparer.Ordinal);
}
