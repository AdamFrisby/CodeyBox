using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Antigravity;

/// <summary>
/// Display-name → canonical <c>--model</c> id mapping for the Google Antigravity
/// (<c>agy</c>) CLI's multi-model gateway. The CLI's <c>agy models</c> output
/// shows human display names (e.g. "Gemini 3.5 Flash (High Thinking)"); the
/// canonical strings here are what <c>--model</c> actually accepts. Each model
/// is a separate per-model quota bucket on Google's side, so the router models
/// each one as its own <see cref="AgentMembership"/> (the existing exhaustion
/// key is <c>(AgentKind, ModelId)</c>, which gives us per-model failover for
/// free — see <c>AgentClassRouter</c>).
///
/// <para>This list is a seed — operators can configure unknown ids and the
/// validator only warns (matching <c>CodeyBox.Agents.Gemini.GeminiKnownModels</c>).
/// The canonical strings shipped here were captured against agy v1.0.6 on
/// 2026-06-09; refresh as Google ships new gateway models. Per the work-item
/// note we deliberately keep numbers (quota sizes / pricing) config-driven —
/// the model identifiers themselves are needed for argv though.</para>
/// </summary>
public static class AntigravityKnownModels
{
    /// <summary>
    /// Canonical <c>--model</c> identifiers exposed by the multi-model
    /// gateway. Includes Google's own Gemini family alongside Anthropic Claude
    /// and OpenAI GPT-OSS models that route through the same subscription.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Gemini 3.5 Flash — three thinking levels share the gateway model id.
        "gemini-3.5-flash-low",
        "gemini-3.5-flash-medium",
        "gemini-3.5-flash-high",
        // Gemini 3.1 Pro — low and high thinking.
        "gemini-3.1-pro-low",
        "gemini-3.1-pro-high",
        // Anthropic via the same gateway.
        "claude-sonnet-4-6-thinking",
        "claude-opus-4-6-thinking",
        // OpenAI GPT-OSS via the same gateway.
        "gpt-oss-120b-medium",
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
    /// Logs a warning when the operator-configured <paramref name="modelId"/> for
    /// an Antigravity member is not in <see cref="All"/>. Unknown ids are not
    /// rejected — the CLI may accept newer ids this validator hasn't been taught
    /// yet — but the warning prompts operators to double-check typos before the
    /// quota probe quietly returns "unknown" at runtime. Mirrors the equivalent
    /// hook in <c>CodeyBox.Agents.Gemini.GeminiKnownModels</c>.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': Antigravity member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). Quota probes will treat this id as unknown unless the " +
            "model is in the live retrieveUserQuotaSummary response.";
        log.LogWarning(
            "AgentClass '{ClassId}': Antigravity member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "Quota probes will treat this id as unknown unless the model is in the live retrieveUserQuotaSummary response.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
