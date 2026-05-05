using System.Reflection;
using System.Runtime.Loader;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Isolated load context for a single plugin assembly. Each plugin gets its
/// own named context so log output and future unload support can identify it.
///
/// Core and SDK assemblies are deliberately NOT loaded here — they must come
/// from the host's default context so that type-identity checks succeed when
/// the orchestrator casts plugin instances to Core interface types.
/// </summary>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string name, string pluginAssemblyPath)
        : base(name, isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // CodeyBox.Core and CodeyBox.PluginSdk must resolve from the host ALC
        // so that `plugin is IAuditor` checks pass without type-identity mismatches.
        if (assemblyName.Name is "CodeyBox.Core" or "CodeyBox.PluginSdk")
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }
}
