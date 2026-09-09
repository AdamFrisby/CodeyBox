using System.Text.Json.Serialization;
using CodeyBox.Agents;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

/// <summary>
/// Operator-visible metadata from the shipped
/// <c>agent-pricing-defaults.json</c> <c>_meta</c> block.
/// </summary>
internal sealed class AgentPricingDefaultsMeta
{
    public string LastUpdated { get; set; } = "";

    public Dictionary<string, string> Sources { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Notes { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Bundled defaults loaded from <c>agent-pricing-defaults.json</c> at startup.
/// The baseline <see cref="AgentPricingOptions"/> is merged with operator config
/// in the API composition root; orchestration only sees merged snapshots.
/// </summary>
internal sealed class AgentPricingDefaultsSnapshot
{
    public AgentPricingDefaultsMeta Meta { get; init; } = new();

    /// <summary>Absolute path to the loaded defaults file.</summary>
    public string SourcePath { get; init; } = "";

    /// <summary>
    /// Per-(agent, model) rates plus per-agent unknown-model <c>DefaultRates</c>
    /// fallbacks, both from the bundled file.
    /// </summary>
    public AgentPricingOptions Baseline { get; init; } = new();
}

/// <summary>JSON on-disk shape for <c>agent-pricing-defaults.json</c>.</summary>
internal sealed class AgentPricingDefaultsFileDto
{
    [JsonPropertyName("_meta")]
    public AgentPricingDefaultsMeta Meta { get; set; } = new();

    public Dictionary<string, Dictionary<string, ModelRateConfig>> Rates { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Per-agent unknown-model fallback rate, applied when a model id is absent
    /// from that agent's <see cref="Rates"/> bucket. Key is agent kind.
    /// </summary>
    public Dictionary<string, ModelRateConfig> DefaultRates { get; set; } =
        new(StringComparer.Ordinal);
}

/// <summary>
/// Tracks the last successfully applied merge (startup or hot-reload) so
/// <c>GET /agent-pricing</c> matches <see cref="AgentCostCalculator"/>.
/// </summary>
internal sealed class AgentPricingState
{
    private readonly Lock _sync = new();
    private MergedAgentPricing _lastMerge;

    public AgentPricingDefaultsSnapshot Defaults { get; }

    /// <summary>
    /// Last applied merge snapshot; use under <see cref="_sync"/> via this
    /// property or <see cref="ApplySuccessfulMerge"/>.
    /// </summary>
    public MergedAgentPricing LastMerge
    {
        get { lock (_sync) return _lastMerge; }
    }

    public AgentPricingState(AgentPricingDefaultsSnapshot defaults, MergedAgentPricing initial)
    {
        Defaults = defaults;
        _lastMerge = initial;
    }

    /// <summary>
    /// Atomically records the merged snapshot and updates the calculator so
    /// cost calculation and <c>GET /agent-pricing</c> always share one view.
    /// </summary>
    public void ApplySuccessfulMerge(MergedAgentPricing merge, AgentCostCalculator calculator)
    {
        ArgumentNullException.ThrowIfNull(calculator);
        lock (_sync)
        {
            calculator.ApplyConfigReload(merge.Options);
            _lastMerge = merge;
        }
    }
}
