using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Hosted service that runs after the DI container is built. Emits
/// <c>plugin.loaded</c> audit events and calls
/// <see cref="IPluginInitializer.InitializeAsync"/> on every plugin type that
/// opts into lifecycle callbacks. Plugins implementing
/// <see cref="IAsyncDisposable"/> are disposed automatically by the DI container
/// at shutdown.
/// </summary>
internal sealed class PluginInitializationService : IHostedService
{
    private readonly IPluginLoader _loader;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginInitializationService> _logger;

    public PluginInitializationService(
        IPluginLoader loader,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        ILogger<PluginInitializationService> logger)
    {
        _loader = loader;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var plugins = await _loader.DiscoverAndLoadAsync(cancellationToken);

        foreach (var plugin in plugins)
        {
            AuditLog.PluginLoaded(plugin.PluginId, plugin.DisplayName, plugin.AssemblyPath);

            foreach (var type in plugin.RegisteredTypes)
                await InitializeTypeAsync(plugin, type, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task InitializeTypeAsync(LoadedPlugin plugin, Type type, CancellationToken ct)
    {
        if (!typeof(IPluginInitializer).IsAssignableFrom(type))
            return;

        // Resolve via the first Core interface the type implements.
        // The instance is a DI singleton so it's the same object everywhere.
        var coreAssembly = typeof(IAuditor).Assembly;
        var coreInterface = type.GetInterfaces()
            .FirstOrDefault(i => i.Assembly == coreAssembly);

        if (coreInterface is null)
        {
            _logger.LogWarning(
                "Plugin {PluginId}: {TypeName} implements IPluginInitializer but no Core interface — cannot resolve from DI, skipping init",
                plugin.PluginId, type.Name);
            return;
        }

        object instance;
        try
        {
            instance = _serviceProvider.GetRequiredService(coreInterface);
        }
        catch (Exception ex)
        {
            AuditLog.PluginInitializationFailed(plugin.PluginId, ex);
            _logger.LogError(ex, "Plugin {PluginId}: failed to resolve {TypeName} from DI", plugin.PluginId, type.Name);
            throw;
        }

        if (instance is not IPluginInitializer initializer)
            return;

        var host = new PluginHost(plugin.PluginId, _loggerFactory, _configuration);
        var context = new PluginContext(
            HostApiVersion: CodeyBoxApiVersion.Current,
            PluginId: plugin.PluginId,
            PluginDisplayName: plugin.DisplayName,
            Host: host);

        try
        {
            await initializer.InitializeAsync(context, ct);
            _logger.LogInformation("Plugin {PluginId}: initialization complete", plugin.PluginId);
        }
        catch (Exception ex)
        {
            AuditLog.PluginInitializationFailed(plugin.PluginId, ex);
            _logger.LogError(ex, "Plugin {PluginId}: initialization failed", plugin.PluginId);
            throw;
        }
    }
}
