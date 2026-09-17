using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Cmd;

/// <summary>
/// Seed list of cmd model ids driving the warn-only config validator and the
/// <see cref="CmdModelListProbe"/>.
///
/// <para>Command Code has no host-queryable model catalog: the catalog is per
/// provider and server-side (OpenRouter alone lists hundreds of ids), so the
/// probe cannot live-read it. This list is therefore a curated seed, not an
/// enumeration: every entry has a provenance comment (live dispatch
/// verification), and unknown ids are never rejected — undeclared ids are
/// "sent anyway" (verified), so validation only warns (mirroring
/// <c>OmpKnownModels</c>). Ids are stored in the exact <c>-m</c> form the
/// runner passes: unlike omp (which strips the qualifier in
/// <c>message.model</c>), cmd echoes the full dispatch id in
/// <c>model_request_start.model</c>, so cost-attribution keys and the id in
/// <c>CodeyBox:AgentDefaults:cmd</c> use the qualified form.</para>
/// </summary>
public static class CmdKnownModels
{
    /// <summary>
    /// Curated seed of cmd-accepted model ids in <c>-m</c> form.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Live-verified 2026-09-17 against command-code 1.54.2:
        // `cmd --local-only -p --output-format json --no-session
        // --skip-onboarding --yolo -m
        // openrouter/nvidia/nemotron-3.5-lightning:free` with a
        // $0-spend-limit OpenRouter key completed one-shot runs (argv
        // prompt, piped-stdin prompt, and a file-creating repo-edit run),
        // proving the model id passed CLI-side resolution and the provider
        // accepted the call.
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
    /// for a cmd member is not in <see cref="All"/>. Unknown ids are not
    /// rejected — the CLI sends undeclared ids anyway — but the warning
    /// prompts operators to double-check typos before a dispatch fails at
    /// the provider with a 400. A $0-spend-limit OpenRouter key only serves
    /// ids ending <c>:free</c>; a paid id fails with
    /// <c>Key limit exceeded (total limit)</c>, which the detector parks as
    /// quota exhaustion.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': Cmd member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). Cmd sends undeclared ids anyway; " +
            "double-check for a typo.";
        log.LogWarning(
            "AgentClass '{ClassId}': Cmd member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "Cmd sends undeclared ids anyway; double-check for a typo.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
