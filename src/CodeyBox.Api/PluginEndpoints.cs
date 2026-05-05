using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class PluginEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/plugins", GetAuditorPluginsAsync);
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
}
