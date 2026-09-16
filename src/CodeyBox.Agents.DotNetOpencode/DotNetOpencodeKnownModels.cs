using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.DotNetOpencode;

/// <summary>
/// Seed list of dotnet-opencode model ids driving the warn-only config
/// validator and the <see cref="DotNetOpencodeModelListProbe"/>.
///
/// <para>dotnet-opencode has no host-queryable model catalog: the only
/// enumeration surface is the interactive TUI / authenticated server, so the
/// probe cannot live-read it. This list is therefore a curated seed, not an
/// enumeration: every entry has a provenance comment (live CLI-side
/// resolution against 0.1.0-ci.20260905083303.33955573552.1), and unknown ids
/// are never rejected — the CLI accepts any <c>provider/model</c> id the
/// backing provider serves, so validation only warns (mirroring
/// <c>PiKnownModels</c>).</para>
/// </summary>
public static class DotNetOpencodeKnownModels
{
    /// <summary>
    /// Curated seed of dotnet-opencode-accepted model ids. Prefer
    /// <c>provider/id</c>-qualified ids in operator config: that is the
    /// <c>--model</c> form the CLI documents (<c>provider/model#variant</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Live-verified 2026-09-16: `run --format json -m
        // anthropic/claude-haiku-4-5` with an opencode.json apiKey reached
        // api.anthropic.com and returned a provider HTTP 401, proving the
        // model id passed CLI-side resolution (credential stage comes first;
        // an unknown model id fails before any provider call).
        "anthropic/claude-haiku-4-5",
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
    /// for a dotnet-opencode member is not in <see cref="All"/>. Unknown ids
    /// are not rejected — the CLI accepts any provider-served
    /// <c>provider/model</c> id beyond this seed — but the warning prompts
    /// operators to double-check typos before a dispatch fails at
    /// model-resolution time.
    /// </summary>
    public static string? ValidateModelIdAgainstProviderList(
        string classId, string? modelId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (IsKnown(modelId)) return null;
        var message = $"AgentClass '{classId}': DotNetOpencode member ModelId '{modelId}' is not in the known " +
            $"provider list ({string.Join(", ", All)}). dotnet-opencode accepts provider/model ids beyond this seed; " +
            "double-check for a typo.";
        log.LogWarning(
            "AgentClass '{ClassId}': DotNetOpencode member ModelId '{ModelId}' is not in the known provider list ({Known}). " +
            "dotnet-opencode accepts provider/model ids beyond this seed; double-check for a typo.",
            classId, modelId, string.Join(", ", All));
        return message;
    }
}
