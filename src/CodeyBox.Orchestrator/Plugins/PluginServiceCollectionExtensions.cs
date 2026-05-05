using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// DI wiring for the plugin foundation. Call
/// <see cref="AddCodeyBoxPlugins"/> from the host's service-configuration
/// phase (before <c>builder.Build()</c>) so that plugin types are registered
/// before the container is frozen.
/// </summary>
public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// Discovers plugin assemblies from <c>CodeyBox:Plugins</c> configuration,
    /// registers their types under the <c>CodeyBox.Core</c> interfaces they
    /// implement, and registers the <see cref="IPluginLoader"/> service and
    /// startup initializer.
    /// </summary>
    /// <returns>
    /// The list of plugins discovered during this call. Callers may capture this
    /// list to avoid calling <see cref="IPluginLoader.DiscoverAndLoadAsync"/> later
    /// (e.g. inside a DI factory, where async blocking is unsafe).
    /// </returns>
    public static IReadOnlyList<LoadedPlugin> AddCodeyBoxPlugins(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var opts = configuration.GetSection("CodeyBox:Plugins").Get<PluginOptions>() ?? new PluginOptions();

        // Discovery runs synchronously before the container is built so plugin
        // types can be registered as singletons now. A NullLogger is used here
        // because the real ILogger<T> is not yet available; the runtime
        // IPluginLoader instance (registered below) uses the proper logger.
        var tempLoader = new PluginLoader(opts, configuration, NullLogger<PluginLoader>.Instance);
        var discovered = tempLoader.DiscoverPlugins();
        tempLoader.RegisterPlugins(services, discovered);

        // Register the runtime IPluginLoader so hosted services and tests can
        // query what was loaded. Pre-seed it with the already-discovered list
        // so DiscoverAndLoadAsync is a cheap cache hit at runtime.
        services.AddSingleton<IPluginLoader>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PluginLoader>>();
            return new PluginLoader(opts, configuration, logger, preloaded: discovered);
        });

        services.AddHostedService<PluginInitializationService>();

        return discovered;
    }
}
