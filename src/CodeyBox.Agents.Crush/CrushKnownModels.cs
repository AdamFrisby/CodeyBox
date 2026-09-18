using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Crush;

/// <summary>
/// Seed list of Crush model ids driving the warn-only config validator and the
/// <see cref="CrushModelListProbe"/>.
///
/// <para>Crush has no host-queryable model catalog: the catalog is per
/// provider and server-side (the <c>crush models</c> listing is a static
/// registry of thousands of ids, not a live entitlement check), so the probe
/// cannot live-read it. This list is therefore a curated seed, not an
/// enumeration: every entry has a provenance comment (live dispatch
/// verification), and unknown ids are never rejected — validation only warns
/// (mirroring <c>ContinueKnownModels</c>). Ids are stored in the exact
/// <c>-m</c> form the runner passes: Crush accepts <c>model</c> or
/// <c>provider/model</c> to disambiguate, and the shipped OpenRouter member
/// uses the qualified form (verified: <c>crush models</c> lists it
/// natively).</para>
/// </summary>
public static class CrushKnownModels
{
    /// <summary>
    /// Curated seed of Crush-accepted model ids in <c>-m</c> form.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Live-verified 2026-09-18 against @charmland/crush 0.95.0:
        // `crush run -q -m openrouter/nvidia/nemotron-3.5-lightning:free`
        // with a $0-spend-limit OpenRouter key completed one-shot runs — a
        // plain reply, a piped-stdin prompt, and a seeded-bug repo-edit run
        // (exit 0, working-tree fix) — proving the model id passed CLI-side
        // resolution and the provider accepted the call.
        "openrouter/nvidia/nemotron-3.5-lightning:free",
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
    /// for a Crush member is not in <see cref="All"/>. Unknown ids are not
    /// rejected — the CLI resolves any id against its registry and fails
    /// closed at dispatch (<c>Failed to override models: … not found</c>)
    /// when the id is unknown — but the warning prompts operators to
    /// double-check typos before a dispatch fails. A $0-spend-limit
    /// OpenRouter key only serves ids ending <c>:free</c>; a paid id fails
    /// with <c>Key limit exceeded (total limit)</c>, which the detector parks
    /// as quota exhaustion.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': Crush member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). Crush resolves -m against its model registry at dispatch; " +
            "double-check for a typo.";
        log.LogWarning(
            "AgentClass '{ClassId}': Crush member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "Crush resolves -m against its model registry at dispatch; double-check for a typo.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
