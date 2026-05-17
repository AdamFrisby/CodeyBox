namespace CodeyBox.Agents;

/// <summary>
/// Per-model price configuration. All rates are in USD per million tokens.
/// Lives in <c>CodeyBox.Agents</c> so per-provider libraries can declare their
/// own built-in fallback rate (via <see cref="IAgentCostExtractor.DefaultPricing"/>)
/// without the Orchestrator hardcoding provider-specific values.
/// </summary>
public sealed class ModelRateConfig
{
    public double InputPerMillion { get; set; }
    public double CachedInputPerMillion { get; set; }
    public double OutputPerMillion { get; set; }
}
