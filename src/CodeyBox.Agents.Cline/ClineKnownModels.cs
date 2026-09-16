using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Cline;

/// <summary>
/// Seed list of cline model ids driving the warn-only config validator and
/// the <see cref="ClineModelListProbe"/>.
///
/// <para>Cline has no host-queryable model catalog: the catalog is per
/// provider (OpenRouter alone lists hundreds of ids) and refreshes
/// server-side, so the probe cannot live-read it. This list is therefore a
/// curated seed, not an enumeration: every entry has a provenance comment
/// (live dispatch verification), and unknown ids are never rejected — the
/// CLI accepts any provider-native id via <c>-m/--model</c>, so validation
/// only warns (mirroring <c>GooseKnownModels</c>).</para>
/// </summary>
public static class ClineKnownModels
{
    /// <summary>
    /// Curated seed of cline-accepted model ids. These are provider-native
    /// ids passed to <c>-m/--model</c> alongside the matching provider in
    /// <c>-P/--provider</c> (see <c>CodeyBox:Cline:Provider</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Live-verified 2026-09-16 against cline 3.0.62: `-P openrouter`
        // with `-m nvidia/nemotron-3.5-lightning:free` and a $0-spend-limit
        // OpenRouter key completed one-shot --json runs — a bare reply and
        // a file-creating tool run — proving the model id passed CLI-side
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
    /// for a cline member is not in <see cref="All"/>. Unknown ids are not
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
        var message = $"AgentClass '{classId}': Cline member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). Cline accepts any provider-native id beyond this seed; " +
            "double-check for a typo.";
        log.LogWarning(
            "AgentClass '{ClassId}': Cline member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "Cline accepts any provider-native id beyond this seed; double-check for a typo.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
