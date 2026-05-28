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
        app.MapGet("/agent-pricing", (
            AgentCostCalculator calculator,
            AgentPricingState pricingState) =>
        {
            var effective = calculator.GetEffectivePricing();
            var defaults = pricingState.Defaults;
            var stats = pricingState.LastMerge;

            return Results.Ok(new
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
                        bundled = stats.BundledRateCount,
                        operatorOverrides = stats.OperatorRateCount,
                        total = stats.TotalRateCount,
                        overlap = stats.OverlapCount,
                    },
                },
                rates = effective.Rates,
                defaultRates = effective.DefaultRates,
            });
        });
    }
}
