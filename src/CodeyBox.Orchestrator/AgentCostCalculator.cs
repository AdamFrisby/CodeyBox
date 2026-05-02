using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Per-model price configuration. All rates are in USD per million tokens.
/// </summary>
public sealed class ModelRateConfig
{
    public double InputPerMillion { get; set; }
    public double CachedInputPerMillion { get; set; }
    public double OutputPerMillion { get; set; }
}

/// <summary>
/// Pricing configuration loaded from the <c>AgentPricing</c> config section.
///
/// Structure:
/// <code>
/// "AgentPricing": {
///   "Rates": {
///     "claude": {
///       "claude-opus-4-7": { "inputPerMillion": 15.0, "cachedInputPerMillion": 1.50, "outputPerMillion": 75.0 },
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
}

/// <summary>
/// Calculates estimated USD cost for a single agent invocation given a pricing configuration.
///
/// Lookup order:
///   1. <c>Rates[agentKind][modelId]</c>
///   2. <c>DefaultRates[agentKind]</c>
///   3. Zero (unknown agent / missing pricing; no warning emitted here — callers decide)
///
/// For subscription plans, <c>estimatedUsd</c> is the equivalent pay-per-API value, not a
/// real charge. See docs/cost-reporting.md §"Subscription equivalent USD".
/// </summary>
public sealed class AgentCostCalculator
{
    private readonly AgentPricingOptions _opts;

    /// <summary>
    /// Conservative fallback rates used when no pricing config is available for an agent.
    /// Intentionally over-estimates to flag unexpectedly expensive runs.
    /// </summary>
    private static readonly Dictionary<string, ModelRateConfig> BuiltInFallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"]  = new() { InputPerMillion = 15.0, CachedInputPerMillion = 1.50, OutputPerMillion = 75.0 },
        ["codex"]   = new() { InputPerMillion = 5.0,  CachedInputPerMillion = 0.50, OutputPerMillion = 25.0 },
        ["gemini"]  = new() { InputPerMillion = 7.0,  CachedInputPerMillion = 0.70, OutputPerMillion = 21.0 },
        ["copilot"] = new() { InputPerMillion = 0.0,  CachedInputPerMillion = 0.0,  OutputPerMillion = 0.0  },
    };

    public AgentCostCalculator(AgentPricingOptions opts)
    {
        _opts = opts;
    }

    /// <summary>
    /// Calculates estimated USD cost. Returns 0 when token counts are zero.
    /// </summary>
    public decimal Calculate(AgentCostSnapshot snapshot, AgentKind kind)
    {
        if (snapshot.InputTokens == 0 && snapshot.OutputTokens == 0)
            return 0m;

        var rate = ResolveRate(kind, snapshot.ModelId);
        if (rate is null) return 0m;

        var billableInput = Math.Max(0, snapshot.InputTokens - snapshot.CachedInputTokens);
        var cost =
            (decimal)billableInput * (decimal)rate.InputPerMillion / 1_000_000m
            + (decimal)snapshot.CachedInputTokens * (decimal)rate.CachedInputPerMillion / 1_000_000m
            + (decimal)snapshot.OutputTokens * (decimal)rate.OutputPerMillion / 1_000_000m;

        return decimal.Round(cost, 6);
    }

    private ModelRateConfig? ResolveRate(AgentKind kind, string? modelId)
    {
        var agentKey = kind.Value;

        // 1. Model-specific rate
        if (!string.IsNullOrEmpty(modelId)
            && _opts.Rates.TryGetValue(agentKey, out var modelMap)
            && modelMap.TryGetValue(modelId, out var modelRate))
        {
            return modelRate;
        }

        // 2. Agent-level default from config
        if (_opts.DefaultRates.TryGetValue(agentKey, out var defaultRate))
            return defaultRate;

        // 3. Built-in fallback constant
        if (BuiltInFallbacks.TryGetValue(agentKey, out var builtin))
            return builtin;

        return null;
    }

    /// <summary>
    /// Validates the pricing config at startup. Emits a warning for each agent kind
    /// that has a registered runner but no pricing entry. Returns true if all known
    /// agents have at least some pricing configured.
    /// </summary>
    public static void ValidateAtStartup(
        AgentPricingOptions opts,
        IEnumerable<AgentKind> registeredAgents,
        ILogger log)
    {
        foreach (var kind in registeredAgents)
        {
            var key = kind.Value;
            var hasRates = opts.Rates.ContainsKey(key) || opts.DefaultRates.ContainsKey(key);
            if (!hasRates && BuiltInFallbacks.ContainsKey(key))
            {
                log.LogInformation(
                    "AgentPricing: no config for agent '{Agent}'; using built-in fallback rates", key);
            }
            else if (!hasRates)
            {
                log.LogWarning(
                    "AgentPricing: no rates configured for agent '{Agent}' and no built-in fallback; estimated_usd will be 0", key);
            }
        }

        // Validate that configured rates have non-negative values.
        foreach (var (agentKey, modelMap) in opts.Rates)
        {
            foreach (var (modelKey, rate) in modelMap)
            {
                if (rate.InputPerMillion < 0 || rate.CachedInputPerMillion < 0 || rate.OutputPerMillion < 0)
                    throw new InvalidOperationException(
                        $"AgentPricing: negative rate for agent '{agentKey}' model '{modelKey}'");
            }
        }
        foreach (var (agentKey, rate) in opts.DefaultRates)
        {
            if (rate.InputPerMillion < 0 || rate.CachedInputPerMillion < 0 || rate.OutputPerMillion < 0)
                throw new InvalidOperationException(
                    $"AgentPricing: negative default rate for agent '{agentKey}'");
        }
    }
}
