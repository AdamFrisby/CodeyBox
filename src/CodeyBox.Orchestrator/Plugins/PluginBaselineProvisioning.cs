using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Result of <see cref="PluginBaselineProvisioning.BuildContributions"/>:
/// what the baseline bake installs and verifies for the given
/// enabled-plugin tools, plus what had to be dropped to respect provider
/// caps.
/// </summary>
public sealed record PluginBaselineContributions(
    IReadOnlyList<BaselineVerificationCommand> VerificationCommands,
    IReadOnlyList<string> InstallCommands,
    IReadOnlyList<PluginToolRequirement> DroppedTools);

/// <summary>
/// Host-owned translation of enabled plugins' external-tool requirements into
/// sandbox baseline contributions. The plugin supplies only validated names
/// (<see cref="PluginToolRequirement"/>); every command below is constructed
/// by the host, so a plugin can never inject an arbitrary command through its
/// tool declaration.
/// </summary>
public static class PluginBaselineProvisioning
{

    /// <summary>
    /// Derives baseline contributions for <paramref name="tools"/> (already
    /// restricted to loaded, enabled plugins by the caller — typically
    /// <see cref="IPluginLoader.GetEnabledPluginTools"/>).
    /// </summary>
    /// <param name="tools">Validated requirements, any order (sorted internally).</param>
    /// <param name="existingVerificationCount">
    /// Verification commands already present (e.g. agent-CLI probes); plugin
    /// steps fill only the remaining headroom up to
    /// <see cref="BaselineProvisioningLimits.MaximumVerificationCommands"/>.
    /// </param>
    /// <remarks>
    /// Verification is a host-owned <c>sh -c</c> presence probe with the
    /// binary passed as <c>$1</c> — the declaration text is never
    /// interpolated into shell. Installation is a single host-constructed
    /// <c>apt-get</c> line over validated package names (charset-verified, so
    /// no element can split words or smuggle options). Callers append
    /// <see cref="PluginBaselineContributions.InstallCommands"/> after
    /// operator <c>ExtraRuncmd</c> (so operator repository setup runs first)
    /// and <see cref="PluginBaselineContributions.VerificationCommands"/>
    /// after existing verification commands. Both lists join the providers'
    /// baseline-identity hashes, so an enabled-set change is a baseline
    /// change and a stale baseline is detectable via the normal orphan/grace
    /// path.
    /// </remarks>
    public static PluginBaselineContributions BuildContributions(
        IReadOnlyList<PluginToolRequirement> tools,
        int existingVerificationCount = 0)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var ordered = tools
            .OrderBy(static t => t.PluginId, StringComparer.Ordinal)
            .ThenBy(static t => t.Binary, StringComparer.Ordinal)
            .Take(PluginToolRequirement.MaxTotalTools)
            .ToList();
        var dropped = tools.Count > ordered.Count
            ? tools
                .OrderBy(static t => t.PluginId, StringComparer.Ordinal)
                .ThenBy(static t => t.Binary, StringComparer.Ordinal)
                .Skip(ordered.Count)
                .ToList()
            : new List<PluginToolRequirement>();

        var verifications = new List<BaselineVerificationCommand>();
        var headroom = Math.Max(
            0,
            BaselineProvisioningLimits.MaximumVerificationCommands - Math.Max(0, existingVerificationCount));
        foreach (var tool in ordered)
        {
            if (verifications.Count >= headroom)
            {
                dropped.Add(tool);
                continue;
            }
            verifications.Add(BuildPresenceCheck(tool));
        }

        var installCommands = BuildInstallCommands(ordered);

        return new PluginBaselineContributions(verifications, installCommands, dropped);
    }

    private static BaselineVerificationCommand BuildPresenceCheck(PluginToolRequirement tool)
    {
        var hint = tool.InstallHint is null
            ? $"plugin '{tool.PluginId}' requires binary '{tool.Binary}' on sandbox PATH"
            : $"plugin '{tool.PluginId}' requires binary '{tool.Binary}' on sandbox PATH ({tool.InstallHint})";
        return new BaselineVerificationCommand(
            Label: $"plugin-tool:{tool.PluginId}:{tool.Binary}",
            Argv: ["sh", "-c", "command -v \"$1\" >/dev/null 2>&1", "codeybox-plugin-tool-check", tool.Binary],
            FailureHint: hint);
    }

    private static IReadOnlyList<string> BuildInstallCommands(IReadOnlyList<PluginToolRequirement> ordered)
    {
        var packages = ordered
            .Where(static t => t.AptPackage is not null)
            .Select(static t => t.AptPackage!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static p => p, StringComparer.Ordinal)
            .ToList();
        if (packages.Count == 0)
            return [];
        // Host-constructed from charset-validated package names only: each
        // element is a single lowercase word that cannot split, glob, or
        // smuggle an option (leading character is alnum by validation).
        return [$"apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y {string.Join(" ", packages)}"];
    }
}
