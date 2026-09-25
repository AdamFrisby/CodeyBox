using System.Text.Json;

namespace CodeyBox.PluginSdk.Credentials;

/// <summary>
/// Shared <see cref="JsonElement"/> field helpers for credential-provider
/// plugins: typed optional-field reads over already-bounded response
/// bodies. One implementation so field-shape policy (what counts as
/// present, what a null or wrong-kind field means) cannot fork per
/// backend.
/// </summary>
public static class CredentialJson
{
    /// <summary>
    /// Reads a string field: true when the property exists and is a string
    /// or JSON null (null reads as empty), false when absent or another
    /// kind.
    /// </summary>
    public static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }
        return property.ValueKind == JsonValueKind.Null;
    }

    /// <summary>
    /// Reads a string field as nullable: the string value when present,
    /// null when absent, JSON null, or another kind.
    /// </summary>
    public static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    /// <summary>Reads an integer field; false when absent or not a 32-bit number.</summary>
    public static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    /// <summary>Reads an integer field; false when absent or not a 64-bit number.</summary>
    public static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value);
    }

    /// <summary>Reads a floating-point field; false when absent or not numeric.</summary>
    public static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value);
    }

    /// <summary>Reads a string field as a timestamp; false when absent, non-string, or unparseable.</summary>
    public static bool TryGetDateTime(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
            return false;
        var text = property.GetString();
        return !string.IsNullOrEmpty(text) && DateTimeOffset.TryParse(text, out value);
    }

    /// <summary>
    /// Flattens a response object into string fields — the credential
    /// payload shape every leasing backend shares. Strings pass through;
    /// numbers, booleans and other primitives keep their raw JSON text;
    /// JSON null reads as empty. One coercion so field-shape policy cannot
    /// fork per backend.
    /// Requires <paramref name="data"/> to be <see cref="JsonValueKind.Object"/>;
    /// anything else is a caller bug and throws.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadStringFields(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw new ArgumentException(
                $"ReadStringFields requires a JSON object, got {data.ValueKind}.", nameof(data));
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
        {
            fields[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => string.Empty,
                _ => property.Value.GetRawText(),
            };
        }
        return fields;
    }
}
