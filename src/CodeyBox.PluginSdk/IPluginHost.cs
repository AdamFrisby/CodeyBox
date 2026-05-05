using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.PluginSdk;

/// <summary>
/// Host callbacks available to a plugin. Passed inside <see cref="PluginContext"/>
/// during initialization and may be stored for the plugin's lifetime.
///
/// <para>In v1 the host exposes only logging and scoped configuration. Plugins
/// that need agent credentials must declare them as DI dependencies resolved
/// from their own configuration section — the host does NOT grant blanket access
/// to <c>ICredentialProvider</c>.</para>
/// </summary>
public interface IPluginHost
{
    /// <summary>
    /// Logger pre-named for this plugin (<c>Plugin:&lt;plugin-id&gt;</c>).
    /// Use it instead of injecting <c>ILogger&lt;T&gt;</c> when you want the
    /// plugin ID in every log line regardless of the concrete type.
    /// </summary>
    ILogger Logger { get; }

    /// <summary>
    /// Configuration section scoped to <c>CodeyBox:Plugins:&lt;plugin-id&gt;</c>.
    /// Operators place plugin-specific settings under that key; the plugin reads
    /// them here without knowing its own ID.
    /// </summary>
    IConfigurationSection ScopedConfig { get; }
}
