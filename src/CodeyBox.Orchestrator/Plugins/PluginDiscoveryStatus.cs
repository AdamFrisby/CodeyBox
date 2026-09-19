namespace CodeyBox.Orchestrator;

/// <summary>
/// Why a discovered plugin candidate was not loaded.
/// </summary>
public enum PluginSkipReason
{
    /// <summary>Not skipped — the plugin loaded.</summary>
    None,

    /// <summary>Not named in <c>Plugins:Enabled</c> (and no <c>"*"</c> wildcard).</summary>
    Disabled,

    /// <summary>Not named in <c>Plugins:Allowlist</c> (and no <c>"*"</c> wildcard).</summary>
    NotAllowlisted,

    /// <summary>Requires a newer host API than this host provides.</summary>
    ApiVersionMismatch,

    /// <summary>A tool declaration failed validation; the plugin fails closed.</summary>
    InvalidToolDeclaration,
}

/// <summary>
/// Per-plugin discovery outcome, retained by the loader for the startup
/// report (enabled/disabled state, unmet tools) and operator diagnostics.
/// </summary>
public sealed record PluginDiscoveryStatus(
    string PluginId,
    string DisplayName,
    string AssemblyPath,
    bool Enabled,
    bool Allowlisted,
    bool Loaded,
    PluginSkipReason SkipReason,
    IReadOnlyList<PluginToolRequirement> RequiredTools);
