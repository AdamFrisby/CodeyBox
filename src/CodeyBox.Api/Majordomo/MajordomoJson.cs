using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodeyBox.Core;
using CodeyBox.Majordomo;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// The serializer contract for the majordomo MCP surface: snake_case
/// property names (matching the tool names), string forms for the typed
/// identifier wrappers, snake_case enum names, and strict input handling —
/// the wire is a model, so unknown fields are ignored but malformed values
/// are never silently reinterpreted.
/// </summary>
internal static class MajordomoJson
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
    internal static string WirePropertyName(string clrName) =>
        Options.PropertyNamingPolicy?.ConvertName(clrName) ?? clrName;

    /// <summary>
    /// The JSON schema a tool advertises for its argument contract. Generated
    /// from the typed contract so the schema cannot drift from the
    /// deserializer; the custom-converter types (ids, durations) are
    /// rewritten to their wire shape (string) so the advertised schema
    /// matches what the server actually accepts.
    /// </summary>
    public static JsonElement InputSchemaFor(Type argumentsType)
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            TransformSchemaNode = static (context, schema) =>
                StringWireTypes.Contains(context.TypeInfo.Type)
                    ? new JsonObject { ["type"] = "string" }
                    : schema,
        };
        var node = JsonSchemaExporter.GetJsonSchemaAsNode(Options, argumentsType, exporterOptions);
        if (node is not JsonObject root)
            return JsonSerializer.SerializeToElement(node, Options);

        // The exporter marks the root nullable (type: ["object","null"]) since
        // the reference type can be null in C# terms; MCP requires a plain
        // object root — arguments are never null on the wire.
        if (root["type"] is JsonArray { Count: 2 })
            root["type"] = "object";

        if (root["properties"] is JsonObject properties)
        {
            // AffectedItemCount is a computed read-back on the mutate contract
            // base — an output, never an input. The exporter cannot know that;
            // strip it so the advertised schema matches what binds. Absent on
            // a mutate contract means the exporter shape drifted — fail loudly
            // at registration rather than advertise a computed property as
            // input.
            if (typeof(MajordomoMutateArgs).IsAssignableFrom(argumentsType)
                && !properties.Remove(WirePropertyName(nameof(MajordomoMutateArgs.AffectedItemCount))))
                throw new InvalidOperationException(
                    $"the generated schema for {argumentsType.Name} lacks the '{WirePropertyName(nameof(MajordomoMutateArgs.AffectedItemCount))}' " +
                    $"read-back property — {nameof(InputSchemaFor)} must be updated alongside the contract");

            // The chain node's custom converter makes the exporter treat
            // items[] as opaque. Pin the real wire shape, embedding the full
            // NewWorkItemSpec schema so the advertised contract is complete.
            if (argumentsType == typeof(CreateWorkItemChainArgs))
            {
                var itemsName = WirePropertyName(nameof(CreateWorkItemChainArgs.Items));
                if (properties[itemsName] is not JsonObject itemsSchema)
                    throw new InvalidOperationException(
                        $"the generated schema for {argumentsType.Name} lacks the '{itemsName}' array — " +
                        $"{nameof(InputSchemaFor)} must be updated alongside the contract");
                var specSchema = JsonSchemaExporter.GetJsonSchemaAsNode(
                    Options, typeof(NewWorkItemSpec), exporterOptions);
                if (specSchema is JsonObject specObj && specObj["type"] is JsonArray)
                    specObj["type"] = "object";
                itemsSchema["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray(WorkItemChainNodeConverter.ItemField),
                    ["properties"] = new JsonObject
                    {
                        [WorkItemChainNodeConverter.ItemField] = specSchema,
                        [WorkItemChainNodeConverter.DependsOnIndexesField] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "integer" },
                        },
                    },
                };
            }
        }

        return JsonSerializer.SerializeToElement(node, Options);
    }

    private static readonly HashSet<Type> StringWireTypes =
    [
        typeof(WorkItemId), typeof(ProjectId), typeof(ReleaseId),
        typeof(AgentKind), typeof(AuditTarget), typeof(TimeSpan),
    ];

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
        // The wire field names, derived through the contract's naming policy
        // and shared with the schema patch in InputSchemaFor — one source of
        // truth so a contract rename cannot drift reader/writer/schema apart.
        // Properties, not fields: this type is constructed inside Create()
        // while Options is still being assigned.
        internal static string ItemField => WirePropertyName(nameof(WorkItemChainNode.Item));
        internal static string DependsOnIndexesField => WirePropertyName(nameof(WorkItemChainNode.DependsOnIndexes));

        public override WorkItemChainNode Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var node = JsonNode.Parse(ref reader) as JsonObject
                ?? throw new JsonException("chain node must be a JSON object");
            var item = node[ItemField]?.Deserialize<NewWorkItemSpec>(options)
                ?? throw new JsonException($"chain node requires an '{ItemField}' object");
            var edges = node[DependsOnIndexesField]?.Deserialize<int[]>(options) ?? [];
            return new WorkItemChainNode(item, edges);
        }

        public override void Write(
            Utf8JsonWriter writer, WorkItemChainNode value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName(ItemField);
            JsonSerializer.Serialize(writer, value.Item, options);
            writer.WritePropertyName(DependsOnIndexesField);
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
