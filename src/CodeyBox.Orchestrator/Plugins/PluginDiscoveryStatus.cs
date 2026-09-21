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

    /// <summary>Configured assembly path did not exist on disk.</summary>
    FileMissing,

    /// <summary>Assembly metadata could not be inspected (not a managed assembly or unreadable).</summary>
    InspectionFailed,

    /// <summary>Assembly passed the metadata gate but could not be loaded for execution.</summary>
    LoadFailed,

    /// <summary>Assembly loaded or inspected cleanly but contains no <c>[CodeyBoxPlugin]</c> type.</summary>
    NoPluginEntry,

    /// <summary>
    /// Assembly was built against a different version of the host contracts
    /// (<c>CodeyBox.Core</c>/<c>CodeyBox.PluginSdk</c>) and cannot bind to this host.
    /// Rebuild the plugin against the current host.
    /// </summary>
    StaleHostContracts,

    /// <summary>Plugin type loaded but its <c>IPluginInitializer</c> threw during startup.</summary>
    InitializationFailed,
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
    IReadOnlyList<PluginToolRequirement> RequiredTools,
    IReadOnlyList<string>? Contracts = null,
    string? Detail = null);

/// <summary>
/// Per-configured-path discovery outcome. One entry per assembly path the
/// operator configured (via <c>AssemblyPaths</c> or found through
/// <c>PackageDirectories</c>): whether the file existed, whether it loaded,
/// which plugin ids it contributed, and which host contracts those plugins
/// registered under. Path-level failures (missing file, unloadable assembly,
/// stale host contracts, no plugin entry) are reported here because there is
/// no plugin candidate to attach a <see cref="PluginDiscoveryStatus"/> to.
/// </summary>
public sealed record PluginAssemblyReport(
    string AssemblyPath,
    bool Found,
    bool Loaded,
    IReadOnlyList<string> PluginIds,
    IReadOnlyList<string> Contracts,
    PluginSkipReason SkipReason,
    string? Detail = null);
