using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Prime;

/// <summary>
/// Seed list of prime-agent model ids driving the warn-only config validator
/// and the <see cref="PrimeModelListProbe"/>.
///
/// <para>Prime has no host-queryable model catalog that fits the startup
/// probe: <c>prime-agent model list</c> needs an authenticated provider plus
/// network (and emits a wide table, not machine-readable ids), and the
/// catalog refreshes automatically per provider — so the probe cannot
/// live-read it the way the opencode probe shells out to
/// <c>opencode models</c>. This list is therefore a curated seed, not an
/// enumeration: every entry has a provenance comment (live dispatch
/// verification or the CLI's own catalog output), and unknown ids are never
/// rejected — the CLI accepts provider-catalog ids far beyond this seed, so
/// validation only warns (mirroring <c>PiKnownModels</c> /
/// <c>AiderKnownModels</c>).</para>
/// </summary>
public static class PrimeKnownModels
{
    /// <summary>
    /// Curated seed of prime-agent-accepted model ids. Ids are the
    /// provider-catalog form for the configured <c>--provider</c> (the
    /// shipped default is <c>openrouter</c>, whose catalog uses
    /// OpenRouter-style ids). The runner passes the id to <c>--model</c>
    /// verbatim alongside <c>--provider</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Live-verified 2026-09-16 against prime-agent 0.9.5: `--provider
        // openrouter --model nvidia/nemotron-3.5-lightning:free` with a $0-
        // limit OpenRouter key completed a real run (answer + file edit)
        // with usage {input:1185, output:104, cacheRead:4352}.
        "nvidia/nemotron-3.5-lightning:free",
        // Prime's own `model list` catalog under --provider openrouter
        // (2026-09-16): shipped-member candidate in the haiku class,
        // matching the aider member's model family. Paid tier — needs a
        // funded key, unlike the :free row above.
        "anthropic/claude-haiku-4.5",
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
    /// for a prime member is not in <see cref="All"/>. Unknown ids are not
    /// rejected — prime routes any provider-catalog id beyond this seed —
    /// but the warning prompts operators to double-check typos before a
    /// dispatch fails at model-resolution time.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': Prime member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). Prime routes provider-catalog ids beyond this seed; " +
            "double-check for a typo.";
        log.LogWarning(
            "AgentClass '{ClassId}': Prime member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "Prime routes provider-catalog ids beyond this seed; double-check for a typo.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
