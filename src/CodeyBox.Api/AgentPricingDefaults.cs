using System.Text.Json;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Api;

/// <summary>
/// Loads the bundled <c>agent-pricing-defaults.json</c> file from the API
/// content root. Merge logic lives on <see cref="AgentPricingOptions"/> in the
/// orchestrator so hot-reload and cost calculation share the same domain rules.
/// </summary>
internal static class AgentPricingDefaults
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
    /// Throws <see cref="InvalidOperationException"/> when the file is missing,
    /// malformed, or contains invalid rates.
    /// </summary>
    public static BundledAgentPricing Load(string contentRootPath, ILogger log)
    {
        var path = Path.Combine(contentRootPath, FileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"AgentPricing: bundled defaults file not found at '{path}'");
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
                AgentPricingOptions.ValidateRateNotNull(rate, agentKey, modelKey, "bundled");
                if (rate.InputPerMillion < 0 || rate.CachedInputPerMillion < 0 || rate.OutputPerMillion < 0)
                    throw new InvalidOperationException(
                        $"AgentPricing: bundled rate has negative value for agent '{agentKey}' model '{modelKey}'");
            }
        }

        return parsed;
    }
}
