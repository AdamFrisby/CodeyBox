using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Omp;

/// <summary>
/// Seed list of omp model ids driving the warn-only config validator and the
/// <see cref="OmpModelListProbe"/>.
///
/// <para>OMP has no host-queryable model catalog: the catalog is per
/// provider and server-side (OpenRouter alone lists hundreds of ids), and
/// <c>omp models --json</c> returns an empty set without login state — so
/// the probe cannot live-read it. This list is therefore a curated seed,
/// not an enumeration: every entry has a provenance comment (live dispatch
/// verification), and unknown ids are never rejected — the CLI accepts fuzzy
/// patterns and <c>provider/id</c>-qualified ids far beyond this seed, so
/// validation only warns (mirroring <c>PiKnownModels</c>). The stream
/// reports <c>message.model</c> in bare provider-catalog form (the
/// <c>openrouter/</c> qualifier is stripped), so cost-attribution keys use
/// the bare form either way.</para>
/// </summary>
public static class OmpKnownModels
{
    /// <summary>
    /// Curated seed of omp-accepted model ids. Both the
    /// <c>openrouter/</c>-qualified and bare forms are accepted on the
    /// OpenRouter path (the provider env key disambiguates); prefer the bare
    /// form in operator config to match the <c>message.model</c> attribution
    /// key and the goose/autohand/vibe/cline convention.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Live-verified 2026-09-16 against omp 18.2.2: `-p --mode json
        // --no-session --model nvidia/nemotron-3.5-lightning:free` with a
        // $0-spend-limit OpenRouter key completed one-shot runs (argv
        // prompt, piped-stdin prompt, and a file-creating repo-edit run),
        // proving the model id passed CLI-side resolution and the provider
        // accepted the call.
        "nvidia/nemotron-3.5-lightning:free",
        // Same verification, openrouter/-qualified form: the run completed
        // and the stream reported message.model in the bare form above.
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
    /// for an omp member is not in <see cref="All"/>. Unknown ids are not
    /// rejected — omp accepts fuzzy patterns and provider-qualified ids
    /// beyond this seed — but the warning prompts operators to double-check
    /// typos before a dispatch fails at model-resolution time. A
    /// $0-spend-limit OpenRouter key only serves ids ending <c>:free</c>; a
    /// paid id fails with <c>Key limit exceeded (total limit)</c>, which the
    /// detector parks as quota exhaustion.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': Omp member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). Omp accepts fuzzy and provider-qualified ids beyond this seed; " +
            "double-check for a typo.";
        log.LogWarning(
            "AgentClass '{ClassId}': Omp member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "Omp accepts fuzzy and provider-qualified ids beyond this seed; double-check for a typo.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
