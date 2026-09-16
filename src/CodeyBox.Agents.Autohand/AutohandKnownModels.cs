using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Autohand;

/// <summary>
/// Seed list of autohand model ids driving the warn-only config validator and
/// the <see cref="AutohandModelListProbe"/>.
///
/// <para>Autohand has no host-queryable model catalog: the catalog is per
/// provider (OpenRouter alone lists hundreds of ids) and refreshes
/// server-side, so the probe cannot live-read it. This list is therefore a
/// curated seed, not an enumeration: every entry has a provenance comment
/// (live dispatch verification), and unknown ids are never rejected — the CLI
/// accepts any provider-native id via <c>--model</c>, so validation only
/// warns (mirroring <c>GooseKnownModels</c>).</para>
/// </summary>
public static class AutohandKnownModels
{
    /// <summary>
    /// Curated seed of autohand-accepted model ids. These are provider-native
    /// ids passed to <c>--model</c> alongside the matching provider in the
    /// guest config (see <c>CodeyBox:Autohand:Provider</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Live-verified 2026-09-16 against autohand-cli 0.9.7: `--provider
        // openrouter` (guest config) with `--model
        // nvidia/nemotron-3.5-lightning:free` and a $0-spend-limit OpenRouter
        // key completed a one-shot bare headless run and edited a file,
        // proving the model id passed CLI-side resolution and the provider
        // accepted the call.
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
    /// for an autohand member is not in <see cref="All"/>. Unknown ids are not
    /// rejected — the CLI accepts any provider-native id beyond this seed —
    /// but the warning prompts operators to double-check typos before a
    /// dispatch fails at model-resolution time. A $0-spend-limit OpenRouter
    /// key only serves ids ending <c>:free</c>; a paid id fails with
    /// <c>Key limit exceeded (total limit)</c>, which the detector parks as
    /// quota exhaustion.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': Autohand member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). Autohand accepts any provider-native id beyond this seed; " +
            "double-check for a typo.";
        log.LogWarning(
            "AgentClass '{ClassId}': Autohand member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "Autohand accepts any provider-native id beyond this seed; double-check for a typo.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
