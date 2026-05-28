using Microsoft.Extensions.Options;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

/// <summary>
/// Surfaces the merged per-(agent, model) pricing table plus the bundled
/// <c>_meta</c> (lastUpdated + source URLs) so operators can see what
/// effective rates the cost calculator is using and how stale the bundled
/// file is relative to the provider's published rates.
///
/// <para>
/// The merge is re-computed on each request rather than cached so a hot
/// edit to <c>codeybox-extra.json</c> reflects immediately, matching the
/// hot-reload contract for <c>CodeyBox:AgentPricing</c>.
/// </para>
/// </summary>
internal static class AgentPricingEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/agent-pricing", (
            BundledAgentPricing bundled,
            IOptionsMonitor<CodeyBoxOptions> monitor) =>
        {
            var operatorOpts = monitor.CurrentValue.AgentPricing;
            var merged = AgentPricingOptions.Merge(bundled, operatorOpts);

            return Results.Ok(new
            {
                meta = new
                {
                    lastUpdated = bundled.Meta.LastUpdated,
                    sources = bundled.Meta.Sources,
                    notes = bundled.Meta.Notes,
                    bundledFile = AgentPricingDefaults.FileName,
                    counts = new
                    {
                        bundled = merged.BundledRateCount,
                        operatorOverrides = merged.OperatorRateCount,
                        total = merged.TotalRateCount,
                        overlap = merged.OverlapCount,
                    },
                },
                rates = merged.Options.Rates,
                defaultRates = merged.Options.DefaultRates,
            });
        });
    }
}
