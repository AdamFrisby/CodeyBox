using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Goose;

/// <summary>
/// Seed list of goose model ids driving the warn-only config validator and
/// the <see cref="GooseModelListProbe"/>.
///
/// <para>Goose has no host-queryable model catalog: the catalog is per
/// provider (OpenRouter alone lists hundreds of ids) and refreshes
/// server-side, so the probe cannot live-read it. This list is therefore a
/// curated seed, not an enumeration: every entry has a provenance comment
/// (live dispatch verification), and unknown ids are never rejected — goose
/// accepts any provider-native id via <c>--model</c>, so validation only
/// warns (mirroring <c>PiKnownModels</c>).</para>
/// </summary>
public static class GooseKnownModels
{
    /// <summary>
    /// Curated seed of goose-accepted model ids. These are provider-native
    /// ids passed to <c>--model</c> alongside the matching
    /// <c>--provider</c> (see <c>CodeyBox:Goose:Provider</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Live-verified 2026-09-16 against goose 1.50.1: `--provider
        // openrouter --model nvidia/nemotron-3.5-lightning:free` with a
        // $0-spend-limit OpenRouter key completed a one-shot `goose run`
        // and edited a file, proving the model id passed CLI-side
        // resolution and the provider accepted the call.
        "nvidia/nemotron-3.5-lightning:free",
    };

    public static bool IsKnown(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        foreach (var m in All)
        {
            if (string.Equals(m, modelId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Logs a warning when the operator-configured <paramref name="modelId"/>
    /// for a goose member is not in <see cref="All"/>. Unknown ids are not
    /// rejected — goose accepts any provider-native id beyond this seed —
    /// but the warning prompts operators to double-check typos before a
    /// dispatch fails at model-resolution time.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': Goose member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). Goose accepts any provider-native id beyond this seed; " +
            "double-check for a typo.";
        log.LogWarning(
            "AgentClass '{ClassId}': Goose member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "Goose accepts any provider-native id beyond this seed; double-check for a typo.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
