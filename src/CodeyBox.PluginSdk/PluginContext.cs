using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.PluginSdk;

/// <summary>
/// Startup context passed to <see cref="IPluginInitializer.InitializeAsync"/>.
/// Contains both metadata about the plugin's place in the host and a reference
/// to the <see cref="IPluginHost"/> for callbacks that should outlive initialization.
/// </summary>
public sealed record PluginContext(
    /// <summary>Host API version in effect (e.g. <c>"1.0"</c>).</summary>
    string HostApiVersion,
    /// <summary>Plugin ID from <see cref="CodeyBoxPluginAttribute.Id"/>.</summary>
    string PluginId,
    /// <summary>Display name from <see cref="CodeyBoxPluginAttribute.DisplayName"/>.</summary>
    string PluginDisplayName,
    /// <summary>Host callbacks. Store this reference if you need it after initialization.</summary>
    IPluginHost Host)
{
    /// <summary>Convenience accessor — same as <see cref="IPluginHost.Logger"/>.</summary>
    public ILogger Logger => Host.Logger;

    /// <summary>Convenience accessor — same as <see cref="IPluginHost.ScopedConfig"/>.</summary>
    public IConfigurationSection ScopedConfig => Host.ScopedConfig;
}
