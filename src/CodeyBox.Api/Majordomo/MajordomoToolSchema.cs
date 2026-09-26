using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using CodeyBox.Core;
using CodeyBox.Majordomo;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// JSON schema advertisement for the majordomo tool contracts. Generated
/// from the typed contract so the schema cannot drift from the
/// deserializer; the custom-converter types (ids, durations) are
/// rewritten to their wire shape (string) so the advertised schema
/// matches what the server actually accepts.
/// </summary>
internal static class MajordomoToolSchema
{
    /// <summary>
    /// The JSON schema a tool advertises for its argument contract.
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
        var node = JsonSchemaExporter.GetJsonSchemaAsNode(MajordomoJson.Options, argumentsType, exporterOptions);
        if (node is not JsonObject root)
            return JsonSerializer.SerializeToElement(node, MajordomoJson.Options);

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
                && !properties.Remove(MajordomoJson.WirePropertyName(nameof(MajordomoMutateArgs.AffectedItemCount))))
                throw new InvalidOperationException(
                    $"the generated schema for {argumentsType.Name} lacks the '{MajordomoJson.WirePropertyName(nameof(MajordomoMutateArgs.AffectedItemCount))}' " +
                    $"read-back property — {nameof(InputSchemaFor)} must be updated alongside the contract");

            // The chain node's custom converter makes the exporter treat
            // items[] as opaque. Pin the real wire shape, embedding the full
            // NewWorkItemSpec schema so the advertised contract is complete.
            if (argumentsType == typeof(CreateWorkItemChainArgs))
            {
                var itemsName = MajordomoJson.WirePropertyName(nameof(CreateWorkItemChainArgs.Items));
                if (properties[itemsName] is not JsonObject itemsSchema)
                    throw new InvalidOperationException(
                        $"the generated schema for {argumentsType.Name} lacks the '{itemsName}' array — " +
                        $"{nameof(InputSchemaFor)} must be updated alongside the contract");
                var specSchema = JsonSchemaExporter.GetJsonSchemaAsNode(
                    MajordomoJson.Options, typeof(NewWorkItemSpec), exporterOptions);
                if (specSchema is JsonObject specObj && specObj["type"] is JsonArray)
                    specObj["type"] = "object";
                var itemField = MajordomoJson.WirePropertyName(nameof(WorkItemChainNode.Item));
                var edgesField = MajordomoJson.WirePropertyName(nameof(WorkItemChainNode.DependsOnIndexes));
                itemsSchema["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray(itemField),
                    ["properties"] = new JsonObject
                    {
                        [itemField] = specSchema,
                        [edgesField] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "integer" },
                        },
                    },
                };
            }
        }

        return JsonSerializer.SerializeToElement(node, MajordomoJson.Options);
    }

    private static readonly HashSet<Type> StringWireTypes =
    [
        typeof(WorkItemId), typeof(ProjectId), typeof(ReleaseId),
        typeof(AgentKind), typeof(AuditTarget), typeof(TimeSpan),
    ];
}
