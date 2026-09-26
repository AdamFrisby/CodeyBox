using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>
/// The serializer contract for the majordomo surface: snake_case
/// property names (matching the tool names), string forms for the typed
/// identifier wrappers, snake_case enum names, and strict input handling —
/// the wire is a model, so unknown fields are ignored but malformed values
/// are never silently reinterpreted. The same options drive wire binding,
/// the proposal store's persisted payload, and the executor's echo-back, so
/// what the operator reviews is byte-for-byte what a commit replays.
/// </summary>
public static class MajordomoJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            // Reflection-based metadata: the schema exporter requires an
            // explicit resolver, and this surface is never published under
            // trimming/AOT.
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new WorkItemIdConverter());
        options.Converters.Add(new ProjectIdConverter());
        options.Converters.Add(new ReleaseIdConverter());
        options.Converters.Add(new AgentKindConverter());
        options.Converters.Add(new AuditTargetConverter());
        options.Converters.Add(new DurationMinutesConverter());
        options.Converters.Add(new WorkItemChainNodeConverter());
        options.Converters.Add(new WorkItemStateSetConverter());
        options.Converters.Add(new JsonStringEnumConverter<WorkItemState>(JsonNamingPolicy.SnakeCaseLower));
        options.Converters.Add(new JsonStringEnumConverter<WorkItemRetryFrom>(JsonNamingPolicy.SnakeCaseLower));
        options.Converters.Add(new JsonStringEnumConverter<QueueState>(JsonNamingPolicy.SnakeCaseLower));
        options.Converters.Add(new JsonStringEnumConverter<WorkItemCancellationReason>(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    /// <summary>
    /// The wire name of a contract member under this surface's naming policy.
    /// Every place that patches or reads a serialized property by name goes
    /// through here so a contract rename cannot silently drift the wire
    /// shape, the advertised schema, or refusal field names apart.
    /// </summary>
    public static string WirePropertyName(string clrName) =>
        Options.PropertyNamingPolicy?.ConvertName(clrName) ?? clrName;

    private sealed class WorkItemIdConverter : JsonConverter<WorkItemId>
    {
        public override WorkItemId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var raw = reader.GetString();
            if (raw is null || !Guid.TryParse(raw, out var g))
                throw new JsonException($"work item id must be a GUID, got '{Validation.DescribeUntrustedValue(raw)}'");
            return new WorkItemId(g);
        }

        public override void Write(Utf8JsonWriter writer, WorkItemId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    private sealed class ProjectIdConverter : JsonConverter<ProjectId>
    {
        public override ProjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var raw = reader.GetString();
            if (raw is null)
                throw new JsonException("project id must be a string");
            try { return new ProjectId(raw); }
            catch (ArgumentException ex) { throw new JsonException(ex.Message); }
        }

        public override void Write(Utf8JsonWriter writer, ProjectId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);
    }

    private sealed class ReleaseIdConverter : JsonConverter<ReleaseId>
    {
        public override ReleaseId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var raw = reader.GetString();
            if (raw is null || !ReleaseId.TryParse(raw, out var id))
                throw new JsonException($"release id must be a GUID, got '{Validation.DescribeUntrustedValue(raw)}'");
            return id;
        }

        public override void Write(Utf8JsonWriter writer, ReleaseId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    private sealed class AgentKindConverter : JsonConverter<AgentKind>
    {
        public override AgentKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                throw new JsonException("agent must be a non-empty string");
            return new AgentKind(raw.Trim().ToLowerInvariant());
        }

        public override void Write(Utf8JsonWriter writer, AgentKind value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);
    }

    private sealed class AuditTargetConverter : JsonConverter<AuditTarget>
    {
        public override AuditTarget Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException("audit targets are output-only on this surface");

        public override void Write(Utf8JsonWriter writer, AuditTarget value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.IsDefault ? null : value.ToString());
    }

    /// <summary>
    /// STJ cannot instantiate <see cref="IReadOnlySet{T}"/> on the
    /// <c>list_work_items</c> states filter — bind it as a HashSet, which the
    /// contract constructor immediately copies into a frozen set.
    /// </summary>
    private sealed class WorkItemStateSetConverter : JsonConverter<IReadOnlySet<WorkItemState>>
    {
        public override IReadOnlySet<WorkItemState> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Null
                ? null!
                : JsonSerializer.Deserialize<HashSet<WorkItemState>>(ref reader, options)
                  ?? new HashSet<WorkItemState>();

        public override void Write(
            Utf8JsonWriter writer, IReadOnlySet<WorkItemState> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var state in value)
                JsonSerializer.Serialize(writer, state, options);
            writer.WriteEndArray();
        }
    }

    /// <summary>
    /// <see cref="WorkItemChainNode"/> has two public constructors (the
    /// positional one and the root-node convenience overload), so STJ cannot
    /// pick a binding by itself. This converter pins the wire shape:
    /// <c>{ "item": {…}, "depends_on_indexes": […] }</c>.
    /// </summary>
    private sealed class WorkItemChainNodeConverter : JsonConverter<WorkItemChainNode>
    {
        public override WorkItemChainNode Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var node = JsonNode.Parse(ref reader) as JsonObject
                ?? throw new JsonException("chain node must be a JSON object");
            var itemName = WirePropertyName(nameof(WorkItemChainNode.Item));
            var edgesName = WirePropertyName(nameof(WorkItemChainNode.DependsOnIndexes));
            var item = node[itemName]?.Deserialize<NewWorkItemSpec>(options)
                ?? throw new JsonException($"chain node requires an '{itemName}' object");
            var edges = node[edgesName]?.Deserialize<int[]>(options) ?? [];
            return new WorkItemChainNode(item, edges);
        }

        public override void Write(
            Utf8JsonWriter writer, WorkItemChainNode value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName(WirePropertyName(nameof(WorkItemChainNode.Item)));
            JsonSerializer.Serialize(writer, value.Item, options);
            writer.WritePropertyName(WirePropertyName(nameof(WorkItemChainNode.DependsOnIndexes)));
            JsonSerializer.Serialize(writer, value.DependsOnIndexes, options);
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// Timeouts ride the wire as a whole number of minutes (a JSON number) or
    /// a duration string ("hh:mm:ss"). The downstream command contracts speak
    /// whole minutes, so a sub-minute value is refused at bind time rather
    /// than truncated — a model's 90-second timeout must not silently become
    /// 1 minute.
    /// </summary>
    private sealed class DurationMinutesConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            TimeSpan value;
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    if (!reader.TryGetDouble(out var minutes))
                        throw new JsonException("duration must be a whole number of minutes or an 'hh:mm:ss' string");
                    value = FromMinutes(minutes);
                    break;
                case JsonTokenType.String:
                    var raw = reader.GetString();
                    // A bare numeric string is a minute count, same as a JSON
                    // number — TimeSpan.TryParse would read "120" as 120 days.
                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMinutes))
                        value = FromMinutes(parsedMinutes);
                    else if (!TimeSpan.TryParse(raw, out value))
                        throw new JsonException($"duration '{Validation.DescribeUntrustedValue(raw)}' must be a number of minutes or an 'hh:mm:ss' string");
                    break;
                default:
                    throw new JsonException("duration must be a number of minutes or an 'hh:mm:ss' string");
            }

            if (value < TimeSpan.Zero)
                throw new JsonException("duration must not be negative");
            if (value.Ticks % TimeSpan.TicksPerMinute != 0)
                throw new JsonException(
                    $"duration '{value}' is not a whole number of minutes — this surface expresses timeouts in minutes");
            return value;
        }

        // FromMinutes throws OverflowException/ArgumentException on
        // out-of-range or non-finite input; converter errors must surface as
        // JsonException so the binder maps them to a refusal with the field
        // path instead of an unhandled transport error.
        private static TimeSpan FromMinutes(double minutes)
        {
            try
            {
                return TimeSpan.FromMinutes(minutes);
            }
            catch (Exception ex) when (ex is ArgumentException or OverflowException)
            {
                throw new JsonException(
                    $"duration of {minutes.ToString(CultureInfo.InvariantCulture)} minutes is outside the representable range");
            }
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value.TotalMinutes);
    }
}
