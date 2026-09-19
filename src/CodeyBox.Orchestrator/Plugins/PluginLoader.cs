using System.Reflection;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Discovers plugin assemblies from configured paths, validates them against
/// the allowlist, the enablement switch, and the host API version, and
/// registers their types into the DI container under the
/// <c>CodeyBox.Core</c> interfaces they implement.
///
/// <para>Discovery is kind-agnostic: the loader does not know about
/// <c>IAuditor</c>, <c>IUpstreamRemote</c>, etc. specifically. It registers any
/// exported type decorated with <see cref="CodeyBoxPluginAttribute"/> under
/// whatever Core interfaces it implements. The orchestrator's existing
/// <c>IEnumerable&lt;TInterface&gt;</c> injection pattern picks them up
/// automatically.</para>
///
/// <para>Enablement is enforced <em>before</em> loading: each assembly is
/// first inspected via metadata only
/// (<see cref="PluginAssemblyInspector"/>), and an assembly with no enabled +
/// allowlisted + version-compatible candidate is never loaded — no isolated
/// load context is created, no plugin code runs. An allowlisted-but-disabled
/// plugin stays unloaded.</para>
/// </summary>
public sealed class PluginLoader : IPluginLoader
{
    private readonly PluginOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PluginLoader> _logger;
    private readonly IPluginAssemblyLoader _assemblyLoader;
    private IReadOnlyList<LoadedPlugin>? _preloaded;
    private List<PluginDiscoveryStatus>? _statuses;

    public PluginLoader(
        PluginOptions options,
        IConfiguration configuration,
        ILogger<PluginLoader> logger,
        IReadOnlyList<LoadedPlugin>? preloaded = null,
        IPluginAssemblyLoader? assemblyLoader = null,
        IReadOnlyList<PluginDiscoveryStatus>? preloadedStatuses = null)
    {
        _options = options;
        _configuration = configuration;
        _logger = logger;
        _preloaded = preloaded;
        _assemblyLoader = assemblyLoader ?? new PluginAssemblyLoadContextLoader();
        _statuses = preloadedStatuses is null ? null : new List<PluginDiscoveryStatus>(preloadedStatuses);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<LoadedPlugin>> DiscoverAndLoadAsync(CancellationToken ct)
    {
        _preloaded ??= DiscoverPlugins();
        return Task.FromResult(_preloaded);
    }

    /// <inheritdoc/>
    public IReadOnlyList<PluginDiscoveryStatus> GetDiscoveryStatuses()
    {
        // Synchronous by design: discovery is a startup-time scan whose async
        // surface (DiscoverAndLoadAsync) is a cache hit.
        _ = DiscoverPlugins();
        if (_preloaded is not null && _statuses is null)
        {
            // Pre-seeded by the host (tests / pre-discovery capture): synthesize
            // statuses so the startup report still sees every loaded plugin.
            _statuses = _preloaded
                .Select(static p => new PluginDiscoveryStatus(
                    p.PluginId, p.DisplayName, p.AssemblyPath,
                    Enabled: true, Allowlisted: true, Loaded: true,
                    SkipReason: PluginSkipReason.None, p.RequiredTools ?? []))
                .ToList();
        }
        return _statuses ?? [];
    }

    /// <inheritdoc/>
    public IReadOnlyList<PluginToolRequirement> GetEnabledPluginTools()
    {
        var tools = new List<PluginToolRequirement>();
        foreach (var plugin in _preloaded ?? DiscoverPlugins())
        {
            if (plugin.RequiredTools is not null)
                tools.AddRange(plugin.RequiredTools);
        }
        // Deterministic order so the baseline identity is stable across restarts.
        tools.Sort(static (a, b) =>
        {
            var c = string.Compare(a.PluginId, b.PluginId, StringComparison.Ordinal);
            return c != 0 ? c : string.Compare(a.Binary, b.Binary, StringComparison.Ordinal);
        });
        return tools;
    }

    /// <summary>
    /// Synchronously discovers plugins from configured paths. Called at DI
    /// configuration time (before the container is built) so discovered types
    /// can be registered as singletons.
    /// </summary>
    internal IReadOnlyList<LoadedPlugin> DiscoverPlugins()
    {
        if (_preloaded is not null)
            return _preloaded;

        var result = new List<LoadedPlugin>();
        _statuses = [];

        foreach (var path in CollectAssemblyPaths())
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("Plugin assembly not found, skipping: {Path}", path);
                continue;
            }

            ScanAssembly(path, result, _statuses);
        }

        _preloaded = result;
        return result;
    }

    // Plugins must not shadow the agent runner: doing so would let an allowlisted
    // plugin intercept all agent execution for the host, contradicting the threat
    // model in docs/extending/plugins.md.
    //
    // ICredentialProvider is intentionally NOT blocked. Plugins that implement it
    // are registered as normal DI singletons and inserted into the credential chain
    // between the built-in OAuth-file and env-var providers. The chain order is
    // BUILT-IN-OAUTH → PLUGINS → BUILT-IN-ENV. See docs/extending/credential-plugins.md for
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

    private void ScanAssembly(string absolutePath, List<LoadedPlugin> result, List<PluginDiscoveryStatus> statuses)
    {
        // Phase 1 — metadata only. No plugin code runs here, so the enablement
        // and allowlist gates are enforced before the assembly is loadable.
        var candidates = PluginAssemblyInspector.Inspect(absolutePath, _logger);

        var loadable = new List<PluginMetadataCandidate>();
        foreach (var candidate in candidates)
        {
            var status = ClassifyCandidate(candidate, absolutePath);
            statuses.Add(status);
            switch (status.SkipReason)
            {
                case PluginSkipReason.None:
                    loadable.Add(candidate);
                    break;
                case PluginSkipReason.Disabled:
                    AuditLog.PluginSkippedDisabled(candidate.PluginId, absolutePath);
                    _logger.LogInformation(
                        "Plugin {PluginId} is disabled (not in Plugins:Enabled); skipping without loading {Path}",
                        candidate.PluginId, absolutePath);
                    break;
                case PluginSkipReason.NotAllowlisted:
                    AuditLog.PluginSkippedNotAllowlisted(candidate.PluginId, absolutePath);
                    _logger.LogInformation(
                        "Plugin {PluginId} not in Allowlist; skipping (path: {Path})", candidate.PluginId, absolutePath);
                    break;
                case PluginSkipReason.ApiVersionMismatch:
                    AuditLog.PluginSkippedApiVersion(candidate.PluginId, candidate.MinHostApiVersion, CodeyBoxApiVersion.Current);
                    _logger.LogError(
                        "Plugin {PluginId} requires host API {Required} but host provides {Current}; skipping",
                        candidate.PluginId, candidate.MinHostApiVersion, CodeyBoxApiVersion.Current);
                    break;
                case PluginSkipReason.InvalidToolDeclaration:
                    // Logged at validation time with the reason.
                    break;
            }
        }

        if (loadable.Count == 0)
            return;

        // Phase 2 — load once, then resolve the approved candidates by metadata
        // type name and re-validate from live attributes (the metadata read is
        // only a loading gate; live attributes are authoritative).
        Assembly assembly;
        try
        {
            assembly = _assemblyLoader.Load(absolutePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load plugin assembly: {Path}", absolutePath);
            return;
        }

        foreach (var candidate in loadable)
        {
            var type = assembly.GetType(candidate.TypeFullName);
            if (type is null)
            {
                _logger.LogError(
                    "Plugin {PluginId}: metadata type {TypeName} not found after loading {Path}; skipping",
                    candidate.PluginId, candidate.TypeFullName, absolutePath);
                continue;
            }

            var attr = type.GetCustomAttribute<CodeyBoxPluginAttribute>();
            if (attr is null || !string.Equals(attr.Id, candidate.PluginId, StringComparison.Ordinal))
            {
                _logger.LogError(
                    "Plugin metadata/live attribute mismatch for {TypeName} in {Path}; skipping",
                    candidate.TypeFullName, absolutePath);
                continue;
            }

            // Re-check the gates against live data: the file could have changed
            // between inspection and load.
            if (!_options.IsEnabled(attr.Id) || !IsAllowed(attr.Id) ||
                !CodeyBoxApiVersion.Satisfies(attr.MinHostApiVersion))
            {
                _logger.LogWarning(
                    "Plugin {PluginId} no longer passes enablement/allowlist/version gates after load; skipping",
                    attr.Id);
                continue;
            }

            if (!TryReadToolRequirements(attr.Id, type, out var tools))
                continue;

            if (tools.Count > PluginToolRequirement.MaxToolsPerPlugin)
            {
                _logger.LogWarning(
                    "Plugin {PluginId} declares {Count} tools (max {Max}); extras are dropped",
                    attr.Id, tools.Count, PluginToolRequirement.MaxToolsPerPlugin);
                tools = tools.Take(PluginToolRequirement.MaxToolsPerPlugin).ToList();
            }

            result.Add(new LoadedPlugin(attr.Id, attr.DisplayName, absolutePath, [type], tools));
            _logger.LogInformation(
                "Plugin discovered: {PluginId} ({DisplayName}) from {Path}",
                attr.Id, attr.DisplayName, absolutePath);
        }
    }

    private PluginDiscoveryStatus ClassifyCandidate(PluginMetadataCandidate candidate, string absolutePath)
    {
        var enabled = _options.IsEnabled(candidate.PluginId);
        var allowed = IsAllowed(candidate.PluginId);
        var versionOk = CodeyBoxApiVersion.Satisfies(candidate.MinHostApiVersion);

        PluginSkipReason reason = PluginSkipReason.None;
        IReadOnlyList<PluginToolRequirement> tools = [];
        if (!enabled)
            reason = PluginSkipReason.Disabled;
        else if (!allowed)
            reason = PluginSkipReason.NotAllowlisted;
        else if (!versionOk)
            reason = PluginSkipReason.ApiVersionMismatch;
        else if (!TryValidateToolDeclarations(candidate, out tools))
            reason = PluginSkipReason.InvalidToolDeclaration;

        return new PluginDiscoveryStatus(
            candidate.PluginId, candidate.DisplayName, absolutePath,
            enabled, allowed, reason == PluginSkipReason.None, reason, tools);
    }

    private bool TryValidateToolDeclarations(
        PluginMetadataCandidate candidate,
        out IReadOnlyList<PluginToolRequirement> tools)
    {
        var validated = new List<PluginToolRequirement>();
        foreach (var decl in candidate.ToolDeclarations)
        {
            if (!PluginToolRequirement.TryCreate(
                    candidate.PluginId, decl.Binary, decl.AptPackage, decl.InstallHint,
                    out var requirement, out var error))
            {
                AuditLog.PluginSkippedInvalidTool(candidate.PluginId, error ?? "invalid tool declaration");
                _logger.LogError(
                    "Plugin {PluginId} declares an invalid external tool ({Error}); " +
                    "failing closed — plugin not loaded. Fix the CodeyBoxPluginRequiresTool declaration.",
                    candidate.PluginId, error);
                tools = [];
                return false;
            }
            validated.Add(requirement!);
        }
        tools = validated;
        return true;
    }

    private bool TryReadToolRequirements(
        string pluginId,
        Type type,
        out IReadOnlyList<PluginToolRequirement> tools)
    {
        // CustomAttributeData, not GetCustomAttribute<T>: instantiating the
        // attribute would run plugin-referenced constructors during discovery.
        var validated = new List<PluginToolRequirement>();
        foreach (var attr in CustomAttributeData.GetCustomAttributes(type))
        {
            if (attr.AttributeType.FullName != PluginAssemblyInspector.RequiresToolAttributeFullName)
                continue;
            var binary = attr.ConstructorArguments.Count >= 1
                ? attr.ConstructorArguments[0].Value as string
                : null;
            string? aptPackage = null;
            string? installHint = null;
            foreach (var named in attr.NamedArguments)
            {
                if (named.MemberName == nameof(CodeyBoxPluginRequiresToolAttribute.AptPackage))
                    aptPackage = named.TypedValue.Value as string;
                else if (named.MemberName == nameof(CodeyBoxPluginRequiresToolAttribute.InstallHint))
                    installHint = named.TypedValue.Value as string;
            }
            if (!PluginToolRequirement.TryCreate(pluginId, binary, aptPackage, installHint, out var requirement, out var error))
            {
                AuditLog.PluginSkippedInvalidTool(pluginId, error ?? "invalid tool declaration");
                _logger.LogError(
                    "Plugin {PluginId} declares an invalid external tool ({Error}); failing closed — plugin not loaded",
                    pluginId, error);
                tools = [];
                return false;
            }
            validated.Add(requirement!);
        }
        tools = validated;
        return true;
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
