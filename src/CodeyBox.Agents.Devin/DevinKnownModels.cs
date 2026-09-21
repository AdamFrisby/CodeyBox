using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Seed list of devin model aliases driving the warn-only config validator.
///
/// <para>The authoritative catalog is account-scoped (<c>devin models
/// list</c> requires auth), so this is a curated seed of documented aliases,
/// not an enumeration: every entry has a provenance comment, and unknown
/// aliases are never rejected — the CLI may accept any alias the account's
/// model registry exposes, so validation only warns (mirroring
/// <c>GooseKnownModels</c>/<c>VibeKnownModels</c>).</para>
/// </summary>
public static class DevinKnownModels
{
    /// <summary>
    /// Curated seed of devin-accepted <c>--model</c> values.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Verified 2026-09-21 against `devin --help` (v3000.11.1), which lists
        // these exact examples for --model: "claude-sonnet-4",
        // "claude-opus-4.6", "opus", "codex".
        "claude-sonnet-4",
        "claude-opus-4.6",
        "opus",
        "codex",
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
    /// for a devin member is not in <see cref="All"/>. Unknown aliases are not
    /// rejected — the account's model registry may expose aliases beyond this
    /// seed — but the warning prompts operators to double-check a typo before
    /// a dispatch falls back to the account default model at resolution time.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': Devin member ModelId '{modelId}' is not in the known " +
            $"alias list ({string.Join(", ", All)}). Devin accepts any account-model-registry alias; " +
            "double-check for a typo or confirm with `devin models list`.";
        log.LogWarning(
            "AgentClass '{ClassId}': Devin member ModelId '{ModelId}' is not in the known alias list ({Known}). " +
            "Devin accepts any account-model-registry alias; double-check for a typo or confirm with `devin models list`.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
