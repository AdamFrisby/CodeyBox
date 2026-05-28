using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

/// <summary>
/// Surfaces the effective per-(agent, model) pricing table used by
/// <see cref="AgentCostCalculator"/> plus bundled <c>_meta</c> (lastUpdated,
/// source URLs) so operators can see active rates and how stale the shipped
/// defaults are.
/// </summary>
internal static class AgentPricingEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/agent-pricing", (AgentPricingState pricingState) =>
            Results.Ok(BuildAgentPricingDto(pricingState)));
    }

    /// <summary>
    /// Maps the last applied merge through a cloned pricing snapshot so the HTTP
    /// response cannot mutate (or observe mutations of) live orchestrator state.
    /// </summary>
    private static object BuildAgentPricingDto(AgentPricingState pricingState)
    {
        var defaults = pricingState.Defaults;
        var merge = pricingState.LastMerge;
        var snapshot = AgentPricingOptions.CloneSnapshot(merge.Options);

        return new
        {
            meta = new
            {
                lastUpdated = defaults.Meta.LastUpdated,
                sources = defaults.Meta.Sources,
                notes = defaults.Meta.Notes,
                sourcePath = defaults.SourcePath,
                bundledFile = AgentPricingDefaults.FileName,
                counts = new
                {
                    bundled = merge.BundledRateCount,
                    operatorOverrides = merge.OperatorRateCount,
                    total = merge.TotalRateCount,
                    overlap = merge.OverlapCount,
                },
            },
            rates = snapshot.Rates,
            defaultRates = snapshot.DefaultRates,
        };
    }
}
