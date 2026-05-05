using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Orchestrator-side implementation of <see cref="IPluginHost"/>. Created per
/// plugin during initialization; plugins may hold a reference for their lifetime.
/// </summary>
internal sealed class PluginHost : IPluginHost, IUpstreamPluginHost
{
    private readonly Func<ProjectId, IReadOnlyDictionary<string, string>> _projectConfigResolver;

    public ILogger Logger { get; }
    public IConfigurationSection ScopedConfig { get; }

    public PluginHost(
        string pluginId,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        Func<ProjectId, IReadOnlyDictionary<string, string>>? projectConfigResolver = null)
    {
        Logger = loggerFactory.CreateLogger($"Plugin:{pluginId}");
        ScopedConfig = configuration.GetSection($"CodeyBox:Plugins:{pluginId}");
        _projectConfigResolver = projectConfigResolver ?? (_ => new Dictionary<string, string>());
    }

    public IReadOnlyDictionary<string, string> GetProjectUpstreamConfig(ProjectId projectId)
        => _projectConfigResolver(projectId);
}
