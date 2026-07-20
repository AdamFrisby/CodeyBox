using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Crock;

/// <summary>
/// Anthropic Claude model ids CrockCode is known to accept. The CrockCode CLI
/// does not expose a <c>crock models</c> command at integration time (verified
/// against CrockCode 0.x, 2026-06), so this curated list is what
/// <see cref="CrockModelListProbe"/> returns. Unknown ids are not rejected —
/// the validator only warns so newer Claude releases that pre-date this list
/// still validate cleanly when an operator pins one.
///
/// <para>The list intentionally mirrors the Anthropic public model catalog
/// rather than enumerating every internal alias. CrockCode itself routes the
/// id through Anthropic's Message Batches API, so the canonical strings here
/// are the Anthropic-shipped ids.</para>
/// </summary>
public static class CrockKnownModels
{
    /// <summary>
    /// Canonical Anthropic Claude model ids CrockCode routes batches to. Kept
    /// in step with the rates seeded in <c>agent-pricing-defaults.json</c>
    /// under the <c>crock</c> rate bucket.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Opus tier — frontier reasoning.
        "claude-opus-4-7",
        "claude-opus-4-6",
        // Sonnet tier — balanced cost/quality.
        "claude-sonnet-4-6",
        // Haiku tier — cheap, low-latency-equivalent (still rides batches).
        "claude-haiku-4-5",
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
    /// for a crock member is not in <see cref="All"/>. Mirrors the equivalent
    /// hook in <c>CodeyBox.Agents.Antigravity.AntigravityKnownModels</c> /
    /// <c>CodeyBox.Agents.Gemini.GeminiKnownModels</c>: unknown ids are not
    /// rejected so a newer Anthropic model pre-dating this list still
    /// validates cleanly, but the warning catches typos before they surface as
    /// a runtime batch-rejection.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': Crock member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). CrockCode may accept newer Anthropic ids this list hasn't been " +
            "taught yet; double-check for a typo.";
        log.LogWarning(
            "AgentClass '{ClassId}': Crock member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "CrockCode may accept newer Anthropic ids this list hasn't been taught yet; double-check for a typo.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
