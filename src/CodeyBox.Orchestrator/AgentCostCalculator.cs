using Microsoft.Extensions.Logging;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Pricing configuration loaded from the <c>AgentPricing</c> config section.
///
/// Structure:
/// <code>
/// "AgentPricing": {
///   "Rates": {
///     "claude": {
///       "claude-opus-4-7": { "inputPerMillion": 5.0, "cachedInputPerMillion": 0.50, "outputPerMillion": 25.0 },
///       "claude-sonnet-4-6": { ... }
///     }
///   },
///   "DefaultRates": {
///     "claude": { "inputPerMillion": 3.0, "cachedInputPerMillion": 0.30, "outputPerMillion": 15.0 }
///   }
/// }
/// </code>
/// </summary>
public sealed class AgentPricingOptions
{
    /// <summary>Per-agent, per-model rates. Key is agent kind; value is model-id → rate map.</summary>
    public Dictionary<string, Dictionary<string, ModelRateConfig>> Rates { get; set; } = [];

    /// <summary>Fallback rate per agent kind when the model is not in <see cref="Rates"/>.</summary>
    public Dictionary<string, ModelRateConfig> DefaultRates { get; set; } = [];

    public static void ValidateRateNotNull(ModelRateConfig? rate, string agentKey, string modelKey, string source)
    {
        if (rate is null)
            throw new InvalidOperationException(
                $"AgentPricing: {source} rate is null for agent '{agentKey}' model '{modelKey}'");
    }

    /// <summary>
    /// Deep-clones a pricing snapshot for defensive reads and reload swaps.
    /// </summary>
    public static AgentPricingOptions CloneSnapshot(AgentPricingOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clone = new AgentPricingOptions
        {
            DefaultRates = source.DefaultRates.ToDictionary(
                kv => kv.Key,
                kv => CloneRate(kv.Value),
                StringComparer.Ordinal),
        };
        foreach (var (agentKey, modelMap) in source.Rates)
        {
            var copy = new Dictionary<string, ModelRateConfig>(modelMap.Count, StringComparer.Ordinal);
            foreach (var (modelKey, rate) in modelMap)
                copy[modelKey] = CloneRate(rate);
            clone.Rates[agentKey] = copy;
        }
        return clone;
    }

    private static ModelRateConfig CloneRate(ModelRateConfig rate) => new()
    {
        InputPerMillion = rate.InputPerMillion,
        CachedInputPerMillion = rate.CachedInputPerMillion,
        OutputPerMillion = rate.OutputPerMillion,
    };
}

/// <summary>
/// Calculates estimated USD cost for a single agent invocation given a pricing configuration.
///
/// Lookup order:
///   1. <c>Rates[agentKind][modelId]</c> from configuration.
///   2. <c>DefaultRates[agentKind]</c> from configuration.
///   3. <c>Rates[agentKind][AgentDefaults[agentKind]]</c> from configuration.
///   4. <see cref="IAgentCostExtractor.DefaultPricing"/> owned by the per-provider library
///      (conservative built-in fallback the provider declares for itself).
///   5. Returns null (→ zero cost) when no source supplies a rate.
///
/// The orchestrator no longer hardcodes provider-specific rates — adding a new provider
/// only requires that provider's cost extractor to expose its own <c>DefaultPricing</c>.
///
/// For subscription plans, <c>estimatedUsd</c> is the equivalent pay-per-API value, not a
/// real charge. See docs/cost-reporting.md §"Subscription equivalent USD".
/// </summary>
public sealed class AgentCostCalculator
{
    // Volatile reference swap: replaced atomically by ApplyConfigReload so an
    // in-flight Calculate() sees either the old or new pricing snapshot but
    // never a torn intermediate. Per-Calculate() reads use a local copy so the
    // ResolveRate lookup is consistent for that single call.
    private volatile AgentPricingOptions _opts;
    private readonly IReadOnlyDictionary<AgentKind, IAgentCostExtractor> _extractors;
    private readonly AgentDefaultsSnapshot? _defaultModels;

    public AgentCostCalculator(
        AgentPricingOptions opts,
        IReadOnlyDictionary<AgentKind, IAgentCostExtractor>? extractors = null,
        AgentDefaultsSnapshot? defaultModels = null)
    {
        _opts = AgentPricingOptions.CloneSnapshot(opts);
        _extractors = extractors ?? new Dictionary<AgentKind, IAgentCostExtractor>();
        _defaultModels = defaultModels;
    }

    /// <summary>
    /// Swaps the held pricing snapshot. Called by the API host after a successful
    /// merge (startup or hot-reload). The new snapshot is
    /// validated by the same rules as the startup snapshot before the swap.
    /// </summary>
    public void ApplyConfigReload(AgentPricingOptions next)
    {
        foreach (var (agentKey, modelMap) in next.Rates)
        {
            foreach (var (modelKey, rate) in modelMap)
            {
                AgentPricingOptions.ValidateRateNotNull(rate, agentKey, modelKey, "reload");
                if (rate.InputPerMillion < 0 || rate.CachedInputPerMillion < 0 || rate.OutputPerMillion < 0)
                    throw new InvalidOperationException(
                        $"AgentPricing: negative rate for agent '{agentKey}' model '{modelKey}'");
            }
        }
        foreach (var (agentKey, rate) in next.DefaultRates)
        {
            AgentPricingOptions.ValidateRateNotNull(rate, agentKey, "(default)", "reload");
            if (rate.InputPerMillion < 0 || rate.CachedInputPerMillion < 0 || rate.OutputPerMillion < 0)
                throw new InvalidOperationException(
                    $"AgentPricing: negative default rate for agent '{agentKey}'");
        }
        _opts = AgentPricingOptions.CloneSnapshot(next);
    }

    /// <summary>
    /// Calculates estimated USD cost. Returns 0 when token counts are zero.
    /// </summary>
    public decimal Calculate(AgentCostSnapshot snapshot, AgentKind kind)
    {
        if (snapshot.InputTokens == 0 && snapshot.CachedInputTokens == 0 && snapshot.OutputTokens == 0)
            return 0m;

        var opts = _opts;
        var rate = ResolveRate(opts, kind, snapshot.ModelId);
        if (rate is null) return 0m;

        var billableInput = Math.Max(0, snapshot.InputTokens);
        var cost =
            (decimal)billableInput * (decimal)rate.InputPerMillion / 1_000_000m
            + (decimal)snapshot.CachedInputTokens * (decimal)rate.CachedInputPerMillion / 1_000_000m
            + (decimal)snapshot.OutputTokens * (decimal)rate.OutputPerMillion / 1_000_000m;

        return decimal.Round(cost, 6);
    }

    private ModelRateConfig? ResolveRate(AgentPricingOptions opts, AgentKind kind, string? modelId)
    {
        var agentKey = kind.Value;

        // 1. Model-specific rate from config.
        if (!string.IsNullOrEmpty(modelId)
            && opts.Rates.TryGetValue(agentKey, out var modelMap)
            && modelMap.TryGetValue(modelId, out var modelRate))
        {
            return modelRate;
        }

        // 2. Operator-supplied agent-level default from config.
        if (opts.DefaultRates.TryGetValue(agentKey, out var defaultRate))
            return defaultRate;

        // 3. AgentDefaults-derived model rate from config. This covers
        // structured streams or human-readable footers that carry token counts
        // but omit the model id, while keeping the fallback generic per agent.
        var defaultModelId = _defaultModels?.GetDefault(agentKey);
        if (!string.IsNullOrEmpty(defaultModelId)
            && opts.Rates.TryGetValue(agentKey, out var defaultModelMap)
            && defaultModelMap.TryGetValue(defaultModelId, out var defaultModelRate))
        {
            return defaultModelRate;
        }

        // 4. Per-provider built-in fallback (owned by the provider's cost extractor).
        if (_extractors.TryGetValue(kind, out var extractor) && extractor.DefaultPricing is { } providerDefault)
            return providerDefault;

        return null;
    }

    /// <summary>
    /// Validates the pricing config at startup. Emits a warning for each agent kind
    /// that has a registered runner but no pricing entry. Throws
    /// <see cref="InvalidOperationException"/> if any configured rate value is negative.
    /// </summary>
    public static void ValidateAtStartup(
        AgentPricingOptions opts,
        IEnumerable<AgentKind> registeredAgents,
        IReadOnlyDictionary<AgentKind, IAgentCostExtractor> extractors,
        ILogger log)
    {
        foreach (var kind in registeredAgents)
        {
            var key = kind.Value;
            var hasRates = opts.Rates.ContainsKey(key) || opts.DefaultRates.ContainsKey(key);
            var hasProviderDefault = extractors.TryGetValue(kind, out var extractor)
                && extractor.DefaultPricing is not null;
            if (!hasRates && hasProviderDefault)
            {
                log.LogWarning(
                    "AgentPricing: no config for agent '{Agent}'; using provider built-in fallback rates", key);
            }
            else if (!hasRates)
            {
                log.LogWarning(
                    "AgentPricing: no rates configured for agent '{Agent}' and no provider fallback; estimated_usd will be 0", key);
            }
        }

        // Validate that configured rates have non-negative values.
        foreach (var (agentKey, modelMap) in opts.Rates)
        {
            foreach (var (modelKey, rate) in modelMap)
            {
                AgentPricingOptions.ValidateRateNotNull(rate, agentKey, modelKey, "configured");
                if (rate.InputPerMillion < 0 || rate.CachedInputPerMillion < 0 || rate.OutputPerMillion < 0)
                    throw new InvalidOperationException(
                        $"AgentPricing: negative rate for agent '{agentKey}' model '{modelKey}'");
            }
        }
        foreach (var (agentKey, rate) in opts.DefaultRates)
        {
            AgentPricingOptions.ValidateRateNotNull(rate, agentKey, "(default)", "configured");
            if (rate.InputPerMillion < 0 || rate.CachedInputPerMillion < 0 || rate.OutputPerMillion < 0)
                throw new InvalidOperationException(
                    $"AgentPricing: negative default rate for agent '{agentKey}'");
        }
    }
}
