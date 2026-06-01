using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// JSON serialization helpers for <see cref="SessionTrace"/>. Uses
/// <see cref="System.Text.Json"/> with camelCase naming and indented
/// output for human inspectability.
/// </summary>
public static class SessionTraceJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new DateTimeOffsetConverter(),
        },
    };

    /// <summary>
    /// Serializes <paramref name="trace"/> to a JSON string.
    /// </summary>
    public static string Serialize(SessionTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        return JsonSerializer.Serialize(trace, Options);
    }

    /// <summary>
    /// Deserializes a <see cref="SessionTrace"/> from a JSON string.
    /// </summary>
    public static SessionTrace Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<SessionTrace>(json, Options)
            ?? throw new InvalidOperationException("Deserialization returned null.");
    }

    /// <summary>
    /// Writes <paramref name="trace"/> to the given file path as JSON.
    /// </summary>
    public static async Task WriteToFileAsync(SessionTrace trace, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(path);
        var json = Serialize(trace);
        await File.WriteAllTextAsync(path, json, ct);
    }

    /// <summary>
    /// Reads a <see cref="SessionTrace"/> from the given file path.
    /// </summary>
    public static async Task<SessionTrace> ReadFromFileAsync(string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        var json = await File.ReadAllTextAsync(path, ct);
        return Deserialize(json);
    }

    /// <summary>
    /// Custom converter that writes DateTimeOffset as ISO 8601 with
    /// timezone offset for human readability.
    /// </summary>
    private sealed class DateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => DateTimeOffset.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString("O"));
    }
}
