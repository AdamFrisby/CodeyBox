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
}
