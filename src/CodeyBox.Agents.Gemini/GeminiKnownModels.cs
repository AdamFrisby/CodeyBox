using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Gemini;

/// <summary>
/// Static list of the Gemini model ids the operator-curated quota probes and
/// validators know about. Operators can still configure other ids — validation
/// warns rather than rejects — but unknown ids will not be live-probed when the
/// auto sentinel fans out.
///
/// The list intentionally tracks the Code Assist subscription "bucket list"
/// (the set of models the per-model retrieveUserQuota response returns), not
/// every model the CLI can run.
/// </summary>
public static class GeminiKnownModels
{
    /// <summary>The "auto" sentinel — opts into ModelRouterService picking per-turn.</summary>
    public const string AutoSentinel = "auto";

    /// <summary>Currently-known Code Assist bucket-list models.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        "gemini-2.5-pro",
        "gemini-2.5-flash",
        "gemini-3-pro-preview",
        "gemini-3-flash-preview",
    };

    /// <summary>True if the given ModelId is the auto sentinel (case-insensitive).</summary>
    public static bool IsAuto(string? modelId) =>
        modelId is not null && string.Equals(modelId, AutoSentinel, StringComparison.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="modelId"/> is in <see cref="All"/>.</summary>
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
    /// <paramref name="agent"/> is not in that agent's known provider list.
    /// Unknown ids are not rejected — the CLI may accept newer model names this
    /// validator hasn't been taught yet — but the warning prompts operators to
    /// double-check typos before the quota probe quietly returns "unknown" at
    /// runtime. Returns the warning message that was logged (or null when no
    /// warning was emitted) so tests can assert against the exact text.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, AgentKind agent, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        // Copilot's CLI ignores --model entirely; skip validation so an inert
        // ModelId doesn't generate noise.
        if (agent == AgentKind.Copilot) return null;

        if (agent == AgentKind.Gemini)
        {
            if (IsAuto(modelId)) return null;
            if (IsKnown(modelId)) return null;
            var message = $"AgentClass '{classId}': Gemini member ModelId '{modelId}' is not in the known " +
                $"provider list ({string.Join(", ", All)}). Quota probes will treat this id as unknown " +
                "unless the model is in the live bucket-list response.";
            log.LogWarning(
                "AgentClass '{ClassId}': Gemini member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
                "Quota probes will treat this id as unknown unless the model is in the live bucket-list response.",
                classId, modelId, string.Join(", ", All));
            return message;
        }

        // Other agents (claude/codex) currently have no static registry —
        // quota probe shapes there are stable enough that typos surface as
        // PerModel cache misses rather than silent fall-back.
        return null;
    }
}
