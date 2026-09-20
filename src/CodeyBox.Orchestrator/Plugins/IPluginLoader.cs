namespace CodeyBox.Orchestrator;

/// <summary>
/// Discovers plugin assemblies, validates them, and returns the set of plugins
/// that should be registered with the DI container.
/// </summary>
public interface IPluginLoader
{
    /// <summary>
    /// Returns the list of plugins that were discovered and validated during
    /// host startup. Subsequent calls return the cached result without re-scanning.
    /// </summary>
    Task<IReadOnlyList<LoadedPlugin>> DiscoverAndLoadAsync(CancellationToken ct);

    /// <summary>
    /// Per-plugin discovery outcomes, including enabled-but-skipped entries.
    /// Used by the startup report so an operator can see that a plugin they
    /// enabled stayed unloaded (disabled, not allowlisted, version mismatch,
    /// or invalid tool declaration) before it fails inside a sandbox.
    /// </summary>
    IReadOnlyList<PluginDiscoveryStatus> GetDiscoveryStatuses();

    /// <summary>
    /// Per-configured-path discovery outcomes: for each assembly path, whether
    /// the file was found, whether it loaded, which plugin ids it contributed,
    /// and which host contracts they registered under. Path-level failures
    /// (missing file, unloadable assembly, stale host contracts, no plugin
    /// entry) appear here; per-candidate skips appear in
    /// <see cref="GetDiscoveryStatuses"/>.
    /// </summary>
    IReadOnlyList<PluginAssemblyReport> GetAssemblyReports();

    /// <summary>
    /// Validated external-tool requirements of every loaded (enabled +
    /// allowlisted) plugin, sorted by (plugin ID, binary) for a stable
    /// baseline identity. Disabled plugins contribute nothing.
    /// </summary>
    IReadOnlyList<PluginToolRequirement> GetEnabledPluginTools();
}
