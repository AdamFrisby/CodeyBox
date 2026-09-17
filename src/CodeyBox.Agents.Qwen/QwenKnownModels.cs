using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Qwen;

/// <summary>
/// Seed list of qwen model ids driving the warn-only config validator and
/// the <see cref="QwenModelListProbe"/>.
///
/// <para>Qwen has no host-queryable model catalog: the catalog is per
/// provider and server-side (OpenRouter alone lists hundreds of ids), so
/// the probe cannot live-read it. This list is therefore a curated seed,
/// not an enumeration: every entry has a provenance comment (live dispatch
/// verification), and unknown ids are never rejected — the CLI accepts any
/// provider-catalog id beyond this seed, so validation only warns
/// (mirroring <c>OmpKnownModels</c>). The stream reports the dispatch id
/// verbatim in <c>message.model</c>, so cost-attribution keys use the same
/// form.</para>
/// </summary>
public static class QwenKnownModels
{
    /// <summary>
    /// Curated seed of qwen-accepted model ids, in the exact form passed to
    /// <c>-m/--model</c> and reported back in <c>message.model</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Live-verified 2026-09-17 against qwen 0.24.0:
        // `--approval-mode yolo --auth-type openai -m
        // nvidia/nemotron-3.5-lightning:free --output-format stream-json`
        // with a $0-spend-limit OpenRouter key completed one-shot runs
        // (positional prompt, piped-stdin prompt, and JSON-array output),
        // proving the model id passed CLI-side resolution and the provider
        // accepted the call.
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
    /// for a qwen member is not in <see cref="All"/>. Unknown ids are not
    /// rejected — qwen accepts any provider-catalog id beyond this seed —
    /// but the warning prompts operators to double-check typos before a
    /// dispatch fails at model-resolution time. A $0-spend-limit OpenRouter
    /// key only serves ids ending <c>:free</c>; a paid id fails with
    /// <c>Key limit exceeded (total limit)</c>, which the detector parks as
    /// quota exhaustion.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': Qwen member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). The CLI accepts ids beyond this seed, " +
            "so the dispatch proceeds — double-check for typos.";
        log.LogWarning(
            "AgentClass '{ClassId}': Qwen member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "The CLI accepts ids beyond this seed, so the dispatch proceeds — double-check for typos.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
