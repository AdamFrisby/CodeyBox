using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class PluginEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/plugins", GetAuditorPluginsAsync);
        app.MapGet("/plugins/status", GetPluginStatusAsync);
    }

    /// <summary>
    /// Returns the set of loaded plugins that implement <see cref="IAuditor"/>,
    /// suitable for display in the admin dashboard and for operator reference
    /// when configuring <c>Custom[].PluginId</c> in project config.
    /// </summary>
    private static async Task<IResult> GetAuditorPluginsAsync(
        IPluginLoader pluginLoader,
        CancellationToken ct)
    {
        var loaded = await pluginLoader.DiscoverAndLoadAsync(ct);

        // Collect the plugin IDs of types that implement IAuditor by checking
        // each loaded plugin's RegisteredTypes. This works because the plugin
        // ALC explicitly falls back to the host ALC for CodeyBox.Core, ensuring
        // type identity is preserved and IsAssignableFrom succeeds.
        var auditorPluginIds = loaded
            .Where(p => p.RegisteredTypes.Any(t => typeof(IAuditor).IsAssignableFrom(t)))
            .Select(p => p.PluginId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = loaded
            .Where(p => auditorPluginIds.Contains(p.PluginId))
            .Select(p => new AuditorPluginDto(p.PluginId, p.DisplayName))
            .ToList();

        return Results.Ok(result);
    }

    private sealed record AuditorPluginDto(string PluginId, string DisplayName);

    /// <summary>
    /// Returns the full loaded-plugin inventory: every loaded plugin (any
    /// contract, not just auditors), the per-plugin discovery outcomes, and
    /// the per-path assembly reports — exactly what discovery reported, so an
    /// operator can answer "is my plugin actually running" without reading
    /// logs or restarting. <c>GET /plugins</c> stays auditor-only for the
    /// dashboard and project-config reference.
    /// </summary>
    private static async Task<IResult> GetPluginStatusAsync(
        IPluginLoader pluginLoader,
        CancellationToken ct)
    {
        var loaded = await pluginLoader.DiscoverAndLoadAsync(ct);
        var statuses = pluginLoader.GetDiscoveryStatuses();
        var assemblies = pluginLoader.GetAssemblyReports();

        var contractsById = statuses
            .Where(static s => s.Loaded)
            .GroupBy(static s => s.PluginId, StringComparer.Ordinal)
            .ToDictionary(
                static g => g.Key,
                static g => (IReadOnlyList<string>)(g.SelectMany(static s => s.Contracts ?? [])
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList()),
                StringComparer.Ordinal);

        return Results.Ok(new PluginStatusDto(
            Loaded: loaded
                .OrderBy(static p => p.PluginId, StringComparer.Ordinal)
                .Select(p => new LoadedPluginDto(
                    p.PluginId,
                    p.DisplayName,
                    p.AssemblyPath,
                    contractsById.TryGetValue(p.PluginId, out var contracts)
                        ? contracts
                        : []))
                .ToList(),
            Discovery: statuses
                .OrderBy(static s => s.PluginId, StringComparer.Ordinal)
                .Select(static s => new PluginDiscoveryDto(
                    s.PluginId,
                    s.DisplayName,
                    s.AssemblyPath,
                    s.Enabled,
                    s.Allowlisted,
                    s.Loaded,
                    s.SkipReason.ToString(),
                    s.Contracts ?? [],
                    s.Detail))
                .ToList(),
            Assemblies: assemblies
                .OrderBy(static a => a.AssemblyPath, StringComparer.Ordinal)
                .Select(static a => new PluginAssemblyDto(
                    a.AssemblyPath,
                    a.Found,
                    a.Loaded,
                    a.PluginIds,
                    a.Contracts,
                    a.SkipReason.ToString(),
                    a.Detail))
                .ToList()));
    }

    private sealed record PluginStatusDto(
        IReadOnlyList<LoadedPluginDto> Loaded,
        IReadOnlyList<PluginDiscoveryDto> Discovery,
        IReadOnlyList<PluginAssemblyDto> Assemblies);

    private sealed record LoadedPluginDto(
        string PluginId,
        string DisplayName,
        string AssemblyPath,
        IReadOnlyList<string> Contracts);

    private sealed record PluginDiscoveryDto(
        string PluginId,
        string DisplayName,
        string AssemblyPath,
        bool Enabled,
        bool Allowlisted,
        bool Loaded,
        string SkipReason,
        IReadOnlyList<string> Contracts,
        string? Detail);

    private sealed record PluginAssemblyDto(
        string AssemblyPath,
        bool Found,
        bool Loaded,
        IReadOnlyList<string> PluginIds,
        IReadOnlyList<string> Contracts,
        string SkipReason,
        string? Detail);
}
