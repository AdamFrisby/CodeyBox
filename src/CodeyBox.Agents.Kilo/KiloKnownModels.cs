using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Kilo;

/// <summary>
/// Seed list of kilo model ids driving the warn-only config validator and
/// the <see cref="KiloModelListProbe"/>.
///
/// <para>Kilo has no host-queryable model catalog: the catalog is per
/// provider and server-side (OpenRouter alone lists hundreds of ids), so the
/// probe cannot live-read it. This list is therefore a curated seed, not an
/// enumeration: every entry has a provenance comment (live dispatch
/// verification), and unknown ids are never rejected — validation only warns
/// (mirroring <c>AutohandKnownModels</c>). Ids are stored in the exact
/// <c>-m provider/model</c> form the runner passes: kilo resolves the
/// dispatch model against the <c>models</c> map seeded into the guest
/// <c>kilo.jsonc</c> (see <see cref="KiloConfigBuilder"/>), so the id here
/// and the id in <c>CodeyBox:AgentDefaults:kilo</c> must agree.</para>
/// </summary>
public static class KiloKnownModels
{
    /// <summary>
    /// Curated seed of kilo-accepted model ids in <c>-m</c> form.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Live-verified 2026-09-16 against @kilocode/cli 7.7.2:
        // `kilo run --auto --format json -m
        // openai-compatible/nvidia/nemotron-3.5-lightning:free` with a
        // $0-spend-limit OpenRouter key completed a one-shot run (both via
        // argv prompt and via piped-stdin prompt), proving the model id
        // passed CLI-side resolution and the provider accepted the call.
        "openai-compatible/nvidia/nemotron-3.5-lightning:free",
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
    /// for a kilo member is not in <see cref="All"/>. Unknown ids are not
    /// rejected — the CLI accepts any id present in the seeded guest-config
    /// <c>models</c> map — but the warning prompts operators to
    /// double-check typos before a dispatch fails at model-resolution time
    /// with <c>Model not found</c>. A $0-spend-limit OpenRouter key only
    /// serves ids ending <c>:free</c>; a paid id fails with
    /// <c>Key limit exceeded (total limit)</c>, which the detector parks as
    /// quota exhaustion.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': Kilo member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). Kilo resolves -m against the guest-config models map; " +
            "double-check for a typo.";
        log.LogWarning(
            "AgentClass '{ClassId}': Kilo member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "Kilo resolves -m against the guest-config models map; double-check for a typo.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
