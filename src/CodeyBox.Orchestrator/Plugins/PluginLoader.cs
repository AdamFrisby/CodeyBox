using System.Reflection;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Discovers plugin assemblies from configured paths, validates them against
/// the allowlist and host API version, and registers their types into the DI
/// container under the <c>CodeyBox.Core</c> interfaces they implement.
///
/// <para>Discovery is kind-agnostic: the loader does not know about
/// <c>IAuditor</c>, <c>IUpstreamRemote</c>, etc. specifically. It registers any
/// exported type decorated with <see cref="CodeyBoxPluginAttribute"/> under
/// whatever Core interfaces it implements. The orchestrator's existing
/// <c>IEnumerable&lt;TInterface&gt;</c> injection pattern picks them up
/// automatically.</para>
/// </summary>
public sealed class PluginLoader : IPluginLoader
{
    private readonly PluginOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PluginLoader> _logger;
    private IReadOnlyList<LoadedPlugin>? _preloaded;

    public PluginLoader(
        PluginOptions options,
        IConfiguration configuration,
        ILogger<PluginLoader> logger,
        IReadOnlyList<LoadedPlugin>? preloaded = null)
    {
        _options = options;
        _configuration = configuration;
        _logger = logger;
        _preloaded = preloaded;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<LoadedPlugin>> DiscoverAndLoadAsync(CancellationToken ct)
    {
        _preloaded ??= DiscoverPlugins();
        return Task.FromResult(_preloaded);
    }

    /// <summary>
    /// Synchronously discovers plugins from configured paths. Called at DI
    /// configuration time (before the container is built) so discovered types
    /// can be registered as singletons.
    /// </summary>
    internal IReadOnlyList<LoadedPlugin> DiscoverPlugins()
    {
        var result = new List<LoadedPlugin>();

        foreach (var path in CollectAssemblyPaths())
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("Plugin assembly not found, skipping: {Path}", path);
                continue;
            }

            ScanAssembly(path, result);
        }

        return result;
    }

    // Plugins must not shadow the agent runner: doing so would let an allowlisted
    // plugin intercept all agent execution for the host, contradicting the threat
    // model in docs/plugins.md.
    //
    // ICredentialProvider is intentionally NOT blocked. Plugins that implement it
    // are registered as normal DI singletons and inserted into the credential chain
    // between the built-in OAuth-file and env-var providers. The chain order is
    // BUILT-IN-OAUTH → PLUGINS → BUILT-IN-ENV. See docs/credential-plugins.md for
    // the full rationale and per-project priority override semantics.
    private static readonly HashSet<Type> _blockedInterfaces =
    [
        typeof(IAgentRunner),
    ];

    /// <summary>
    /// Registers each loaded plugin's types into <paramref name="services"/>
    /// under the <c>CodeyBox.Core</c> interfaces they implement.
    ///
    /// <para>Each plugin type is registered once as a concrete singleton, with
    /// forwarding factories for each allowed interface. This ensures multi-interface
    /// plugins share a single instance regardless of which interface is resolved.
    /// </para>
    /// </summary>
    internal void RegisterPlugins(IServiceCollection services, IReadOnlyList<LoadedPlugin> plugins)
    {
        var coreAssembly = typeof(IAuditor).Assembly;

        foreach (var plugin in plugins)
        {
            foreach (var type in plugin.RegisteredTypes)
            {
                var allCoreInterfaces = type.GetInterfaces()
                    .Where(i => i.Assembly == coreAssembly)
                    .ToList();

                foreach (var blocked in allCoreInterfaces.Where(i => _blockedInterfaces.Contains(i)))
                {
                    _logger.LogWarning(
                        "Plugin {PluginId}: type {TypeName} implements restricted interface {InterfaceName}; " +
                        "registration blocked to protect host security boundaries",
                        plugin.PluginId, type.Name, blocked.Name);
                }

                var coreInterfaces = allCoreInterfaces
                    .Where(i => !_blockedInterfaces.Contains(i))
                    .ToList();

                if (coreInterfaces.Count == 0)
                {
                    _logger.LogWarning(
                        "Plugin {PluginId}: type {TypeName} has no registerable CodeyBox.Core interfaces; nothing registered",
                        plugin.PluginId, type.Name);
                    continue;
                }

                // Register the concrete type once as the canonical singleton so that all
                // interface resolutions share the same instance. Without this, each
                // AddSingleton(iface, type) call produces a separate instance, and
                // IPluginInitializer.InitializeAsync would only run on the first one.
                services.AddSingleton(type);

                foreach (var iface in coreInterfaces)
                {
                    var capturedType = type;
                    services.AddSingleton(iface, sp => sp.GetRequiredService(capturedType));
                    _logger.LogDebug(
                        "Plugin {PluginId}: registered {TypeName} as {InterfaceName}",
                        plugin.PluginId, type.Name, iface.Name);
                }
            }
        }
    }

    private void ScanAssembly(string absolutePath, List<LoadedPlugin> result)
    {
        Assembly assembly;
        try
        {
            var contextName = $"Plugin:{Path.GetFileNameWithoutExtension(absolutePath)}";
            var alc = new PluginAssemblyLoadContext(contextName, absolutePath);
            assembly = alc.LoadFromAssemblyPath(absolutePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load plugin assembly: {Path}", absolutePath);
            return;
        }

        foreach (var type in GetCandidateTypes(assembly))
        {
            var attr = type.GetCustomAttribute<CodeyBoxPluginAttribute>()!;

            if (!IsAllowed(attr.Id))
            {
                AuditLog.PluginSkippedNotAllowlisted(attr.Id, absolutePath);
                _logger.LogInformation(
                    "Plugin {PluginId} not in Allowlist; skipping (path: {Path})", attr.Id, absolutePath);
                continue;
            }

            if (!CodeyBoxApiVersion.Satisfies(attr.MinHostApiVersion))
            {
                AuditLog.PluginSkippedApiVersion(attr.Id, attr.MinHostApiVersion, CodeyBoxApiVersion.Current);
                _logger.LogError(
                    "Plugin {PluginId} requires host API {Required} but host provides {Current}; skipping",
                    attr.Id, attr.MinHostApiVersion, CodeyBoxApiVersion.Current);
                continue;
            }

            result.Add(new LoadedPlugin(attr.Id, attr.DisplayName, absolutePath, [type]));
            _logger.LogInformation(
                "Plugin discovered: {PluginId} ({DisplayName}) from {Path}",
                attr.Id, attr.DisplayName, absolutePath);
        }
    }

    private IEnumerable<Type> GetCandidateTypes(Assembly assembly)
    {
        IEnumerable<Type> exported;
        try
        {
            exported = assembly.GetExportedTypes();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate types in assembly {Assembly}", assembly.FullName);
            return [];
        }

        return exported.Where(t =>
            !t.IsAbstract
            && t.IsClass
            && t.GetCustomAttribute<CodeyBoxPluginAttribute>() is not null);
    }

    private bool IsAllowed(string pluginId)
    {
        if (_options.Allowlist.Count == 0)
            return false;

        if (_options.Allowlist.Contains("*", StringComparer.OrdinalIgnoreCase))
            return true;

        return _options.Allowlist.Contains(pluginId, StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<string> CollectAssemblyPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();

        foreach (var p in _options.AssemblyPaths)
        {
            var full = Path.GetFullPath(p);
            if (seen.Add(full)) paths.Add(full);
        }

        foreach (var dir in _options.PackageDirectories)
        {
            if (!Directory.Exists(dir))
            {
                _logger.LogWarning("Plugin package directory not found: {Dir}", dir);
                continue;
            }

            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                var full = Path.GetFullPath(dll);
                if (seen.Add(full)) paths.Add(full);
            }
        }

        return paths;
    }
}
