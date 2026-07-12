using System.Text.Json;

namespace CodeyBox.Agents;

/// <summary>
/// Single source of truth for reading a CrockCode <c>config.json</c> payload.
/// CrockCode's config shape is
/// <c>{ "anthropic_api_key": "sk-…", "tunnel_provider": "…" }</c>.
///
/// <para>Lives in <c>CodeyBox.Agents</c> (referenced by both the Crock agent
/// assembly and the orchestrator) so the quota probe and the per-instance
/// credential resolver parse the config identically — a config-shape change is
/// made in exactly one place and both boundaries move together.</para>
/// </summary>
public static class CrockConfigParser
{
    /// <summary>
    /// Extracts the Anthropic API key from a CrockCode config JSON payload.
    /// Tolerant of leading/trailing whitespace and the alternate casings
    /// operators sometimes hand-write (<c>anthropic_api_key</c>,
    /// <c>ANTHROPIC_API_KEY</c>, camelCase <c>anthropicApiKey</c> — all matched
    /// case-insensitively). Returns <c>null</c> on blank input, a non-object
    /// root, a missing/blank key, or any parse failure; callers treat
    /// <c>null</c> as "no credential".
    /// </summary>
    public static string? TryGetAnthropicApiKey(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                // OrdinalIgnoreCase already matches ANTHROPIC_API_KEY and the
                // camelCase spelling, so one comparison per accepted name.
                if (string.Equals(prop.Name, "anthropic_api_key", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(prop.Name, "anthropicApiKey", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = prop.Value.GetString();
                    return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
