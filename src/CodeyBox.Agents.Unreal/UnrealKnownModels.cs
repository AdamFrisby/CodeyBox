using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Unreal;

/// <summary>
/// Seed list of Unreal model IDs driving the warn-only config validator and the
/// <see cref="UnrealModelListProbe"/>.
///
/// <para>Unreal Labs' unreal-agent harness supports multiple backend providers
/// (OpenAI, OpenRouter, Fireworks, Ollama). The model can be specified either via
/// the request's JSON <c>model</c> field or via <c>UNREAL_HARNESS_LLM_MODEL</c>.
/// The upstream default model is <c>gpt-6-astra</c> on OpenAI; on OpenRouter,
/// qualified model IDs such as <c>openrouter/nvidia/nemotron-3.5-lightning:free</c>
/// or <c>nvidia/nemotron-3.5-lightning:free</c> are used. Unknown model IDs are
/// not rejected — validation only logs a warning so operators catch typos early
/// (mirroring Crush and Continue).</para>
/// </summary>
public static class UnrealKnownModels
{
    public const string DefaultModel = "gpt-6-astra";
    public const string FreeModel = "openrouter/nvidia/nemotron-3.5-lightning:free";

    /// <summary>
    /// Curated seed of Unreal-accepted model IDs.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        DefaultModel,
        FreeModel,
        "nvidia/nemotron-3.5-lightning:free",
        "gpt-5.5",
        "openai/gpt-5.5",
    ];

    public static bool IsKnown(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;
        foreach (var m in All)
        {
            if (string.Equals(m, modelId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Logs a warning when the operator-configured <paramref name="modelId"/>
    /// for an Unreal member is not in <see cref="All"/>.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        if (IsKnown(modelId))
            return null;

        var warning =
            $"AgentClass '{classId}': Unreal model '{modelId}' is not in the curated known-models seed " +
            $"({string.Join(", ", All)}). If this is a valid provider model ID, dispatch may succeed; " +
            "otherwise double-check the spelling.";
        log.LogWarning("{Warning}", warning);
        return warning;
    }
}
