using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Orchestrator-side implementation of <see cref="IPluginHost"/>. Created per
/// plugin during initialization; plugins may hold a reference for their lifetime.
/// </summary>
internal sealed class PluginHost : IPluginHost
{
    public ILogger Logger { get; }
    public IConfigurationSection ScopedConfig { get; }

    public PluginHost(string pluginId, ILoggerFactory loggerFactory, IConfiguration configuration)
    {
        Logger = loggerFactory.CreateLogger($"Plugin:{pluginId}");
        ScopedConfig = configuration.GetSection($"CodeyBox:Plugins:{pluginId}");
    }
}
