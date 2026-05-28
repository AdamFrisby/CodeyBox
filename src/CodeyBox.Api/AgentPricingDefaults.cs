using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

/// <summary>
/// Loads the bundled <c>agent-pricing-defaults.json</c> file from the API
/// content root. Merge logic lives in <see cref="AgentPricingMerge"/>; this
/// type only parses the host defaults artifact.
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
    public static AgentPricingDefaultsSnapshot Load(string contentRootPath)
    {
        var path = Path.Combine(contentRootPath, FileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"AgentPricing: bundled defaults file not found at '{path}'");
        }

        AgentPricingDefaultsFileDto parsed;
        try
        {
            using var stream = File.OpenRead(path);
            parsed = JsonSerializer.Deserialize<AgentPricingDefaultsFileDto>(stream, JsonOpts)
                ?? throw new InvalidOperationException(
                    $"AgentPricing: bundled defaults file at '{path}' deserialized to null");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"AgentPricing: bundled defaults file at '{path}' is malformed: {ex.Message}", ex);
        }

        if (parsed.Meta is null)
        {
            throw new InvalidOperationException(
                $"AgentPricing: bundled defaults file at '{path}' has null _meta");
        }

        if (parsed.Rates is null)
        {
            throw new InvalidOperationException(
                $"AgentPricing: bundled defaults file at '{path}' has null Rates");
        }

        var baseline = new AgentPricingOptions();
        foreach (var (agentKey, modelMap) in parsed.Rates)
        {
            var copy = new Dictionary<string, ModelRateConfig>(modelMap.Count, StringComparer.Ordinal);
            foreach (var (modelKey, rate) in modelMap)
            {
                AgentPricingOptions.ValidateRateNotNull(rate, agentKey, modelKey, "bundled");
                if (rate.InputPerMillion < 0 || rate.CachedInputPerMillion < 0 || rate.OutputPerMillion < 0)
                    throw new InvalidOperationException(
                        $"AgentPricing: bundled rate has negative value for agent '{agentKey}' model '{modelKey}'");
                copy[modelKey] = new ModelRateConfig
                {
                    InputPerMillion = rate.InputPerMillion,
                    CachedInputPerMillion = rate.CachedInputPerMillion,
                    OutputPerMillion = rate.OutputPerMillion,
                };
            }
            baseline.Rates[agentKey] = copy;
        }

        return new AgentPricingDefaultsSnapshot
        {
            Meta = parsed.Meta,
            SourcePath = path,
            Baseline = baseline,
        };
    }
}
