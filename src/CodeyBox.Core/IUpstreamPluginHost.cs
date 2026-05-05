namespace CodeyBox.Core;

/// <summary>
/// Extended host interface for upstream remote plugins. Provides per-project
/// configuration access. The orchestrator's <c>PluginHost</c> implements both
/// this interface and <c>CodeyBox.PluginSdk.IPluginHost</c>.
///
/// <para>Upstream plugins that call <see cref="GetProjectUpstreamConfig"/> should
/// cast <c>context.Host</c> to <c>IUpstreamPluginHost</c> at
/// <c>IPluginInitializer.InitializeAsync</c> time and store the reference for use
/// in <c>CompleteAsync</c>. The cast always succeeds against the orchestrator host;
/// if it fails, the plugin is running in an unsupported host and should throw.</para>
/// </summary>
public interface IUpstreamPluginHost
{
    /// <summary>
    /// Returns the <c>Upstream.PluginConfig</c> key/value map for the specified
    /// project. This is the ONE sanctioned way for an upstream remote plugin to
    /// read its per-project settings (base URL, owner, repository, …) at runtime.
    /// Returns an empty dictionary when the project is unknown or has no
    /// <c>Upstream.PluginConfig</c> entries; never throws.
    /// </summary>
    IReadOnlyDictionary<string, string> GetProjectUpstreamConfig(ProjectId projectId);
}
