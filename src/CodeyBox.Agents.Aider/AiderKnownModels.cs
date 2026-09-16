using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Aider;

/// <summary>
/// Seed list of aider model ids driving the warn-only config validator and the
/// <see cref="AiderModelListProbe"/>.
///
/// <para>Aider's live catalog (<c>aider --list-models &lt;query&gt;</c>) needs a
/// partial-match query argument, prints an interactive OpenRouter onboarding
/// prompt when no model or key is configured, and emits thousands of rows — so
/// it cannot back a host-side startup probe the way the opencode probe shells
/// out to <c>opencode models</c>. This list is therefore a curated seed, not an
/// enumeration: every entry was live-verified against aider 0.86.2, and unknown
/// ids are never rejected — aider accepts any litellm-routed
/// <c>provider/id</c> id far beyond this seed, so validation only warns
/// (mirroring <c>PiKnownModels</c>).</para>
/// </summary>
public static class AiderKnownModels
{
    /// <summary>
    /// Curated seed of aider-accepted model ids. Prefer
    /// <c>openrouter/…</c>-qualified ids in operator config when routing
    /// through the shipped OpenRouter credential mapping: aider's own startup
    /// default is <c>gpt-4o</c>, which needs an OpenAI key the sandbox may not
    /// carry.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Live-verified 2026-09-16 against aider 0.86.2: full one-shot run
        // (--message-file /dev/stdin) producing a file edit and the
        // `Tokens: …` accounting line.
        "openrouter/nvidia/nemotron-3.5-lightning:free",
        // Live-verified 2026-09-16 against aider 0.86.2: `--exit` model
        // resolution accepted the id (unknown-id warning names this exact
        // dotted form in its "Did you mean" list).
        "openrouter/anthropic/claude-haiku-4.5",
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
    /// for an aider member is not in <see cref="All"/>. Unknown ids are not
    /// rejected — aider routes any litellm provider id beyond this seed — but
    /// the warning prompts operators to double-check typos before a dispatch
    /// fails at model-resolution time.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': Aider member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). Aider accepts litellm-routed provider ids beyond this seed; " +
            "double-check for a typo.";
        log.LogWarning(
            "AgentClass '{ClassId}': Aider member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "Aider accepts litellm-routed provider ids beyond this seed; double-check for a typo.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
