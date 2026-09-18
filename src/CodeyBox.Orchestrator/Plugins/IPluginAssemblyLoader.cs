using System.Reflection;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Loads a plugin assembly for execution after metadata inspection has
/// approved it. The indirection exists so tests can prove a disabled plugin's
/// assembly is never loaded: the recording fake asserts zero calls, which is
/// the load-context-level evidence (no <c>PluginAssemblyLoadContext</c> is
/// ever created for the path).
/// </summary>
public interface IPluginAssemblyLoader
{
    /// <summary>
    /// Loads the assembly at <paramref name="absolutePath"/> for execution
    /// and returns it. Called at most once per path per discovery pass.
    /// </summary>
    Assembly Load(string absolutePath);

    /// <summary>Absolute paths this loader has loaded during this discovery pass.</summary>
    IReadOnlyList<string> LoadedPaths { get; }
}

/// <summary>
/// Production loader: one isolated <see cref="PluginAssemblyLoadContext"/>
/// per assembly, so plugin code stays out of the host's default context.
/// </summary>
public sealed class PluginAssemblyLoadContextLoader : IPluginAssemblyLoader
{
    private readonly List<string> _loadedPaths = [];

    public IReadOnlyList<string> LoadedPaths => _loadedPaths;

    public Assembly Load(string absolutePath)
    {
        var contextName = $"Plugin:{Path.GetFileNameWithoutExtension(absolutePath)}";
        var alc = new PluginAssemblyLoadContext(contextName, absolutePath);
        var assembly = alc.LoadFromAssemblyPath(absolutePath);
        _loadedPaths.Add(absolutePath);
        return assembly;
    }
}
