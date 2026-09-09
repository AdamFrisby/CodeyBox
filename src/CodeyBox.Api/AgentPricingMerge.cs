using CodeyBox.Agents;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

/// <summary>Result of merging bundled defaults with operator pricing at the API host.</summary>
internal readonly record struct MergedAgentPricing(
    AgentPricingOptions Options,
    int BundledRateCount,
    int OperatorRateCount,
    int OverlapCount)
{
    /// <summary>Distinct (agent, model) rate entries after merging.</summary>
    public int TotalRateCount => BundledRateCount + OperatorRateCount - OverlapCount;
}

/// <summary>
/// Merges bundled <c>agent-pricing-defaults.json</c> with operator
/// <c>CodeyBox:AgentPricing</c>. Operator entries win per (agentKind, modelId).
/// </summary>
internal static class AgentPricingMerge
{
    public static MergedAgentPricing Merge(AgentPricingOptions baseline, AgentPricingOptions operatorOpts)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(operatorOpts);

        var merged = new AgentPricingOptions
        {
            DefaultRates = new Dictionary<string, ModelRateConfig>(StringComparer.Ordinal),
        };

        // Bundled per-agent fallbacks first, then let operator entries win per agent kind.
        foreach (var (agentKey, rate) in baseline.DefaultRates)
        {
            AgentPricingOptions.ValidateRateNotNull(rate, agentKey, "(default)", "bundled");
            merged.DefaultRates[agentKey] = CloneRate(rate);
        }
        foreach (var (agentKey, rate) in operatorOpts.DefaultRates)
        {
            AgentPricingOptions.ValidateRateNotNull(rate, agentKey, "(default)", "operator");
            merged.DefaultRates[agentKey] = CloneRate(rate);
        }

        int bundledCount = 0;
        int operatorCount = 0;
        int overlapCount = 0;

        foreach (var (agentKey, modelMap) in baseline.Rates)
        {
            var copy = new Dictionary<string, ModelRateConfig>(modelMap.Count, StringComparer.Ordinal);
            foreach (var (modelKey, rate) in modelMap)
            {
                copy[modelKey] = CloneRate(rate);
                bundledCount++;
            }
            merged.Rates[agentKey] = copy;
        }

        foreach (var (agentKey, modelMap) in operatorOpts.Rates)
        {
            if (!merged.Rates.TryGetValue(agentKey, out var bucket))
            {
                bucket = new Dictionary<string, ModelRateConfig>(StringComparer.Ordinal);
                merged.Rates[agentKey] = bucket;
            }
            foreach (var (modelKey, rate) in modelMap)
            {
                AgentPricingOptions.ValidateRateNotNull(rate, agentKey, modelKey, "operator");
                if (bucket.ContainsKey(modelKey))
                    overlapCount++;
                bucket[modelKey] = CloneRate(rate);
                operatorCount++;
            }
        }

        return new MergedAgentPricing(merged, bundledCount, operatorCount, overlapCount);
    }

    private static ModelRateConfig CloneRate(ModelRateConfig rate) => new()
    {
        InputPerMillion = rate.InputPerMillion,
        CachedInputPerMillion = rate.CachedInputPerMillion,
        OutputPerMillion = rate.OutputPerMillion,
    };
}
