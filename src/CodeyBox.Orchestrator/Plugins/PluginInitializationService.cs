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
///
/// <para>Also emits the plugin startup report: which plugins are enabled and
/// loaded, which stayed unloaded (and why), and which enabled plugins declare
/// external tools missing from the host — so an operator sees that an enabled
/// plugin will fail before it fails inside a sandbox.</para>
/// </summary>
internal sealed class PluginInitializationService : IHostedService
{
    private readonly IPluginLoader _loader;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginInitializationService> _logger;
    private readonly IPluginToolAvailabilityProbe _toolProbe;
    private readonly bool _failOnInitializationError;
    private readonly int _startupReportMaxEntries;
    private readonly List<PluginInitializationFailure> _initializationFailures = [];

    /// <summary>
    /// Unmet external-tool requirements observed by the last
    /// <see cref="StartAsync"/> run. Exposed for tests and diagnostics.
    /// </summary>
    public IReadOnlyList<PluginToolRequirement> UnmetToolRequirements { get; private set; } = [];

    /// <summary>
    /// Plugin types whose initialisation threw during the last
    /// <see cref="StartAsync"/> run. Recorded (in addition to the error log
    /// and audit event) so the administrative surface can report them.
    /// </summary>
    public IReadOnlyList<PluginInitializationFailure> InitializationFailures => _initializationFailures;

    public PluginInitializationService(
        IPluginLoader loader,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        ILogger<PluginInitializationService> logger,
        IPluginToolAvailabilityProbe? toolProbe = null,
        bool? failOnInitializationError = null,
        int? startupReportMaxEntries = null)
    {
        _loader = loader;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _toolProbe = toolProbe ?? new PathPluginToolAvailabilityProbe();
        // Failure policy is explicit and configurable (see PluginOptions):
        // fail closed on initialisation errors by default. An explicit
        // constructor override wins (tests), then configuration, then the default.
        _failOnInitializationError = failOnInitializationError
            ?? configuration.GetSection("CodeyBox:Plugins").GetValue<bool?>("FailOnInitializationError")
            ?? new PluginOptions().FailOnInitializationError;
        _startupReportMaxEntries = startupReportMaxEntries
            ?? configuration.GetSection("CodeyBox:Plugins").GetValue<int?>("StartupReportMaxEntries")
            ?? new PluginOptions().StartupReportMaxEntries;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var plugins = await _loader.DiscoverAndLoadAsync(cancellationToken);

        try
        {
            foreach (var plugin in plugins)
            {
                AuditLog.PluginLoaded(plugin.PluginId, plugin.DisplayName, plugin.AssemblyPath);

                foreach (var type in plugin.RegisteredTypes)
                    await InitializeTypeAsync(plugin, type, cancellationToken);
            }
        }
        catch when (_failOnInitializationError)
        {
            // Fatal path still reports: the discovery outcome and the recorded
            // initialisation failure are logged before the host aborts, so an
            // operator never faces a dead host with no reason in the log.
            ReportStartupState(plugins);
            throw;
        }

        ReportStartupState(plugins);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Logs the enabled/disabled/loaded state of every discovered plugin and
    /// probes the external tools required by loaded plugins, recording the
    /// unmet ones on <see cref="UnmetToolRequirements"/>. A missing binary is
    /// a loud startup warning — never a silent bake failure later.
    ///
    /// <para>Discovery itself runs before the container is built (with no real
    /// logger), so this report replays the full discovery outcome — per-plugin
    /// statuses <em>and</em> per-path assembly reports — with the runtime
    /// logger. A configured plugin that never became a candidate (missing
    /// file, unloadable assembly, stale contracts, no plugin entry) is
    /// reported here, never silently absent.</para>
    /// </summary>
    private void ReportStartupState(IReadOnlyList<LoadedPlugin> plugins)
    {
        foreach (var status in _loader.GetDiscoveryStatuses())
        {
            if (status.Loaded)
            {
                var contracts = status.Contracts is { Count: > 0 }
                    ? $" [{string.Join(", ", status.Contracts)}]"
                    : string.Empty;
                _logger.LogInformation(
                    "Plugin {PluginId} enabled and loaded ({DisplayName}){Contracts}",
                    status.PluginId, status.DisplayName, contracts);
            }
            else
            {
                _logger.LogWarning(
                    "Plugin {PluginId} not loaded: {Reason}",
                    status.PluginId, DescribeSkip(status.SkipReason));
            }
        }

        foreach (var assembly in _loader.GetAssemblyReports().Where(static a => !a.Loaded))
        {
            _logger.LogWarning(
                "Plugin assembly {Path} did not load: {Reason}{Detail}",
                assembly.AssemblyPath,
                DescribeSkip(assembly.SkipReason),
                string.IsNullOrEmpty(assembly.Detail) ? string.Empty : $" ({assembly.Detail})");
        }

        var unmet = new List<PluginToolRequirement>();
        foreach (var plugin in plugins)
        {
            foreach (var tool in plugin.RequiredTools ?? (IReadOnlyList<PluginToolRequirement>)[])
            {
                bool available;
                try
                {
                    available = _toolProbe.IsAvailable(tool.Binary);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex, "Plugin {PluginId}: tool probe for '{Binary}' failed; treating as unmet",
                        tool.PluginId, tool.Binary);
                    available = false;
                }
                if (available)
                    continue;
                unmet.Add(tool);
                AuditLog.PluginToolRequirementUnmet(tool.PluginId, tool.Binary, tool.InstallHint);
                _logger.LogWarning(
                    "Plugin {PluginId} requires binary '{Binary}' which was not found on host PATH{Hint}; " +
                    "sandbox baselines carrying this plugin will fail verification until it is provisioned",
                    tool.PluginId, tool.Binary,
                    tool.InstallHint is null ? string.Empty : $" ({tool.InstallHint})");
            }
        }
        UnmetToolRequirements = unmet;

        var summary = PluginStartupSummary.Build(
            _loader.GetDiscoveryStatuses(),
            _loader.GetAssemblyReports(),
            _initializationFailures,
            _startupReportMaxEntries);
        _logger.LogInformation("{Header}", summary.Header);
        foreach (var detail in summary.Details)
            _logger.LogInformation("Plugin startup: {Detail}", detail);
        if (summary.Omitted > 0)
            _logger.LogInformation(
                "Plugin startup: …and {Omitted} more (full inventory on GET /plugins/status)",
                summary.Omitted);
    }

    private static string DescribeSkip(PluginSkipReason reason) => reason switch
    {
        PluginSkipReason.Disabled => "disabled — not in Plugins:Enabled (assembly not loaded)",
        PluginSkipReason.NotAllowlisted => "not in Plugins:Allowlist",
        PluginSkipReason.ApiVersionMismatch => "requires a newer host API version",
        PluginSkipReason.InvalidToolDeclaration => "invalid external-tool declaration (failed closed)",
        PluginSkipReason.FileMissing => "configured file not found",
        PluginSkipReason.InspectionFailed => "assembly metadata could not be inspected",
        PluginSkipReason.LoadFailed => "assembly could not be loaded for execution",
        PluginSkipReason.NoPluginEntry => "assembly contains no [CodeyBoxPlugin] types",
        PluginSkipReason.StaleHostContracts =>
            "built against a different version of the host contracts (CodeyBox.Core/CodeyBox.PluginSdk); rebuild the plugin",
        PluginSkipReason.InitializationFailed => "initialization threw",
        _ => "unknown reason",
    };

    private async Task InitializeTypeAsync(LoadedPlugin plugin, Type type, CancellationToken ct)
    {
        if (!typeof(IPluginInitializer).IsAssignableFrom(type))
            return;

        var coreAssembly = typeof(IAuditor).Assembly;
        var coreInterface = type.GetInterfaces()
            .FirstOrDefault(i => i.Assembly == coreAssembly);

        object? instance;
        try
        {
            // Prefer resolving via the concrete plugin type (registered by RegisterPlugins
            // as AddSingleton(type)). This guarantees the same singleton is returned for
            // all interfaces, which matters for multi-interface plugins: without it, only
            // the instance keyed by the first interface receives InitializeAsync.
            instance = _serviceProvider.GetService(type);

            if (instance is null)
            {
                if (coreInterface is null)
                {
                    _logger.LogWarning(
                        "Plugin {PluginId}: {TypeName} implements IPluginInitializer but no Core interface — cannot resolve from DI, skipping init",
                        plugin.PluginId, type.Name);
                    return;
                }
                instance = _serviceProvider.GetRequiredService(coreInterface);
            }
        }
        catch (Exception ex)
        {
            RecordInitializationFailure(plugin.PluginId, type.Name, ex);
            AuditLog.PluginInitializationFailed(plugin.PluginId, ex);
            _logger.LogError(ex, "Plugin {PluginId}: failed to resolve {TypeName} from DI", plugin.PluginId, type.Name);
            if (_failOnInitializationError)
                throw;
            return;
        }

        if (instance is not IPluginInitializer initializer)
            return;

        var repo = _serviceProvider.GetService<IProjectRepository>();
        Func<ProjectId, IReadOnlyDictionary<string, string>> resolver = projectId =>
        {
            if (repo is null) return new Dictionary<string, string>();
            // Run on a ThreadPool thread so there is no SynchronizationContext, preventing
            // the sync-over-async deadlock that would occur if called on an ASP.NET context thread.
            var project = Task.Run(() => repo.GetAsync(projectId, CancellationToken.None))
                .GetAwaiter().GetResult();
            return project?.Upstream.PluginConfig ?? new Dictionary<string, string>();
        };
        var host = new PluginHost(plugin.PluginId, _loggerFactory, _configuration, resolver);
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
            RecordInitializationFailure(plugin.PluginId, type.Name, ex);
            AuditLog.PluginInitializationFailed(plugin.PluginId, ex);
            _logger.LogError(ex, "Plugin {PluginId}: initialization failed", plugin.PluginId);
            if (_failOnInitializationError)
                throw;
        }
    }

    private void RecordInitializationFailure(string pluginId, string typeName, Exception ex)
    {
        var error = ex.GetType().Name + ": " + FirstLine(ex.Message);
        _initializationFailures.Add(new PluginInitializationFailure(pluginId, typeName, error));
    }

    private static string FirstLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "(no detail)";
        var line = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        const int maxLength = 256;
        return line.Length > maxLength ? line[..maxLength] + "…" : line;
    }
}
