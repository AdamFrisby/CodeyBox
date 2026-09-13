using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CodeyBox.Api;

/// <summary>
/// Operator introspection for configuration reload semantics: answers "will
/// editing this key take effect without a restart" without reading source.
/// Backed by <see cref="ConfigReloadClassification"/>, which derives its
/// answers from the same policy sets the reload path uses.
/// </summary>
internal static class ConfigReloadEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/config/reload-effects", GetReloadEffects);
    }

    private static IResult GetReloadEffects([FromQuery] string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (!ConfigReloadClassification.TryGetEffect(key, out var single))
            {
                return Results.Ok(new
                {
                    key,
                    found = false,
                    effect = (string?)null,
                });
            }

            return Results.Ok(new
            {
                key,
                found = true,
                effect = single.ToString(),
            });
        }

        var entries = new List<object>();
        foreach (var field in CodeyBox.Orchestrator.WorkerPoolHotReloadPolicy.HotReloadableFields)
            entries.Add(new { key = $"CodeyBox:WorkerPool:{field}", effect = ConfigReloadEffect.HotReload.ToString() });
        foreach (var field in CodeyBox.Orchestrator.WorkerPoolHotReloadPolicy.RestartRequiredFields)
            entries.Add(new { key = $"CodeyBox:WorkerPool:{field}", effect = ConfigReloadEffect.RestartRequired.ToString() });
        foreach (var field in CodeyBox.Orchestrator.PipelineTuningHotReloadPolicy.HotReloadableFields)
            entries.Add(new { key = $"CodeyBox:PipelineTuning:{field}", effect = ConfigReloadEffect.HotReload.ToString() });
        foreach (var field in CodeyBox.Orchestrator.PipelineTuningHotReloadPolicy.RestartRequiredFields)
            entries.Add(new { key = $"CodeyBox:PipelineTuning:{field}", effect = ConfigReloadEffect.RestartRequired.ToString() });

        entries.Add(new { key = ConfigReloadClassification.LegacyConcurrencyKey, effect = ConfigReloadEffect.HotReload.ToString() });
        foreach (var guarded in ConfigReloadClassification.ValidatorGuardedKeyPaths)
            entries.Add(new { key = guarded, effect = ConfigReloadEffect.RestartRequired.ToString() });

        return Results.Ok(entries);
    }
}
