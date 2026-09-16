using System.Text.Json;

namespace CodeyBox.Agents.Cline;

/// <summary>
/// Shared JSON helpers for the cline <c>--json</c> NDJSON readers.
/// Extracted so the cost extractor and the stream parser cannot drift into
/// two subtly different copies of the same lookup.
/// </summary>
internal static class ClineJson
{
    /// <summary>
    /// Returns the first named property of <paramref name="el"/> that is a
    /// JSON object, or null when none of <paramref name="names"/> is present
    /// as an object. Never throws on missing or mistyped properties.
    /// </summary>
    internal static JsonElement? FirstObject(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Object)
                return prop;
        }

        return null;
    }
}
