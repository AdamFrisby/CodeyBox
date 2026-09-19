using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Watches <c>CodeyBox:Plugins:Enabled</c> for runtime changes and tells the
/// operator a restart is required. Unloading a live plugin is not safe —
/// plugin assemblies load into non-collectible
/// <c>AssemblyLoadContext</c>s and instances are already constructed as DI
/// singletons — so the change is reported explicitly and ignored until
/// restart, never silently applied and never partially applied.
/// Per-plugin scoped settings (read live from <c>IConfiguration</c>) remain
/// hot-reloadable; only membership in the enabled set is restart-gated.
/// </summary>
internal sealed class PluginOptionsChangeWatcher : IHostedService, IDisposable
{
    private readonly ILogger<PluginOptionsChangeWatcher> _logger;
    private readonly IDisposable? _subscription;
    private HashSet<string> _lastSeen;

    public PluginOptionsChangeWatcher(
        IOptionsMonitor<PluginOptions> monitor,
        ILogger<PluginOptionsChangeWatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lastSeen = EnabledSet(monitor.CurrentValue);
        _subscription = monitor.OnChange(OnOptionsChanged);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _subscription?.Dispose();

    private void OnOptionsChanged(PluginOptions options, string? name)
    {
        _ = name;
        var current = EnabledSet(options);
        if (EnabledSetsEqual(_lastSeen, current))
            return;
        _logger.LogError(
            "Plugins:Enabled changed at runtime ({Before} → {After}); " +
            "unloading a live plugin is not safe, so the change is ignored until the host restarts. " +
            "Restart the host to apply the new enabled set.",
            string.Join(",", _lastSeen.OrderBy(static s => s, StringComparer.Ordinal)),
            string.Join(",", current.OrderBy(static s => s, StringComparer.Ordinal)));
        _lastSeen = current;
    }

    /// <summary>
    /// Pure comparison used by tests: order- and case-insensitive set equality.
    /// </summary>
    internal static bool EnabledSetsEqual(ISet<string> before, ISet<string> after) =>
        before.SetEquals(after);

    internal static HashSet<string> EnabledSet(PluginOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new HashSet<string>(options.Enabled, StringComparer.OrdinalIgnoreCase);
    }
}
