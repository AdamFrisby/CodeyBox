using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Pi;

/// <summary>
/// Seed list of pi model ids driving the warn-only config validator and the
/// <see cref="PiModelListProbe"/>.
///
/// <para>Pi has no host-queryable model catalog: <c>pi --list-models</c> needs
/// an authenticated provider and network, and the catalog refreshes
/// automatically per provider — so the probe cannot live-read it the way the
/// opencode probe shells out to <c>opencode models</c>. This list is therefore
/// a curated seed, not an enumeration: every entry has a provenance comment
/// (live dispatch verification or pi's own <c>--help</c>/README examples), and
/// unknown ids are never rejected — the CLI accepts fuzzy patterns and
/// <c>provider/id</c>-qualified ids far beyond this seed, so validation only
/// warns (mirroring <c>AntigravityKnownModels</c>).</para>
/// </summary>
public static class PiKnownModels
{
    /// <summary>
    /// Curated seed of pi-accepted model ids. Prefer
    /// <c>provider/id</c>-qualified ids in operator config: pi's default
    /// provider is google, so a bare id can resolve against the wrong
    /// provider catalog.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Live-verified 2026-09-14 against pi 0.85.1: `--provider anthropic
        // --model claude-haiku-4-5` with a bogus key reached api.anthropic.com
        // and returned a provider 401, proving the model id passed CLI-side
        // resolution.
        "anthropic/claude-haiku-4-5",
        // pi --help model examples (provider-qualified form).
        "openai/gpt-4o",
        "openai/gpt-4o-mini",
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
    /// for a pi member is not in <see cref="All"/>. Unknown ids are not
    /// rejected — pi accepts fuzzy patterns and provider-qualified ids beyond
    /// this seed — but the warning prompts operators to double-check typos
    /// before a dispatch fails at model-resolution time.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': Pi member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). Pi accepts fuzzy and provider-qualified ids beyond this seed; " +
            "double-check for a typo.";
        log.LogWarning(
            "AgentClass '{ClassId}': Pi member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "Pi accepts fuzzy and provider-qualified ids beyond this seed; double-check for a typo.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
