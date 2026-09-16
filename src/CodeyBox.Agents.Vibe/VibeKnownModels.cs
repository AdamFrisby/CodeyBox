using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Vibe;

/// <summary>
/// Seed list of vibe model aliases driving the warn-only config validator and
/// the <see cref="VibeModelListProbe"/>.
///
/// <para>Vibe has no host-queryable model catalog: models are guest-config
/// <c>[[models]]</c> aliases resolved inside the sandbox (the provider id
/// behind each alias never leaves the guest), so the probe cannot live-read
/// a catalog the way the opencode probe shells out to <c>opencode
/// models</c>. This list is therefore a curated seed of guest-config aliases,
/// not an enumeration: every entry has a provenance comment (live dispatch
/// verification), and unknown aliases are never rejected — the guest config
/// may define any alias, so validation only warns (mirroring
/// <c>GooseKnownModels</c>).</para>
/// </summary>
public static class VibeKnownModels
{
    /// <summary>
    /// Curated seed of vibe-accepted model aliases. These are guest
    /// <c>~/.vibe/config.toml</c> <c>[[models]]</c> aliases (passed via
    /// <c>VIBE_ACTIVE_MODEL</c>), NOT provider ids — the guest config maps
    /// each alias to its provider id.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Live-verified 2026-09-16 against vibe 2.25.4: guest alias
        // `nemotron-free` mapping to provider id
        // `nvidia/nemotron-3.5-lightning:free` on the `openrouter` provider
        // completed a programmatic `vibe -p` run (text answer) and a
        // tool-use run (created a file) with a $0-spend-limit OpenRouter key.
        "nemotron-free",
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
    /// for a vibe member is not in <see cref="All"/>. Unknown aliases are not
    /// rejected — the guest config may define any alias beyond this seed —
    /// but the warning prompts operators to double-check typos (and that the
    /// guest <c>config.toml</c> actually defines the alias) before a dispatch
    /// falls back to the guest default model at resolution time.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': Vibe member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). Vibe ModelIds are guest-config model aliases beyond this seed; " +
            "double-check for a typo and that the guest ~/.vibe/config.toml defines the alias.";
        log.LogWarning(
            "AgentClass '{ClassId}': Vibe member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "Vibe ModelIds are guest-config model aliases beyond this seed; double-check for a typo and that the guest ~/.vibe/config.toml defines the alias.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
