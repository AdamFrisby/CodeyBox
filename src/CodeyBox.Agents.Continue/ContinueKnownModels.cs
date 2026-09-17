using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Continue;

/// <summary>
/// Seed list of Continue model ids driving the warn-only config validator and
/// the <see cref="ContinueModelListProbe"/>.
///
/// <para>Continue has no host-queryable model catalog: the catalog is per
/// provider and server-side (OpenRouter alone lists hundreds of ids), so the
/// probe cannot live-read it. This list is therefore a curated seed, not an
/// enumeration: every entry has a provenance comment (live dispatch
/// verification), and unknown ids are never rejected — validation only warns
/// (mirroring <c>KiloKnownModels</c>). Ids are stored in the exact form the
/// runner writes to the guest <c>config.yaml</c> <c>model:</c> field (see
/// <see cref="ContinueConfigBuilder"/>): the raw provider-catalog id with no
/// qualifier, because Continue resolves the dispatch model from the first
/// <c>models[]</c> entry rather than a <c>provider/model</c> flag.</para>
/// </summary>
public static class ContinueKnownModels
{
    /// <summary>
    /// Curated seed of Continue-accepted model ids in guest-config
    /// <c>model:</c> form.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Live-verified 2026-09-16 against @continuedev/cli 1.5.47:
        // `cn --print --auto` with a single-entry guest config.yaml
        // (provider openrouter, this model id) and a $0-spend-limit
        // OpenRouter key completed one-shot runs — a plain reply, a
        // file-creation run (exit 0, change merged), and a two-entry config
        // proving first-entry-wins selection.
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
    /// for a Continue member is not in <see cref="All"/>. Unknown ids are not
    /// rejected — the CLI forwards any id to the provider — but the warning
    /// prompts operators to double-check typos before a dispatch fails at
    /// provider time. A $0-spend-limit OpenRouter key only serves ids ending
    /// <c>:free</c>; a paid id fails with <c>Key limit exceeded</c>, which
    /// the detector parks as quota exhaustion.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': Continue member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). Continue forwards any id to the provider; " +
            "double-check for a typo.";
        log.LogWarning(
            "AgentClass '{ClassId}': Continue member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "Continue forwards any id to the provider; double-check for a typo.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
