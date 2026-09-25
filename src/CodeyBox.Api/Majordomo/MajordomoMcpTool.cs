using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using CodeyBox.Majordomo;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// One published MCP tool, bound to a <see cref="CodeyBox.Majordomo.MajordomoTool"/>
/// descriptor from the closed vocabulary. The advertised input schema is
/// generated from the declared argument contract, so the schema can never
/// advertise a shape the binder would refuse. All behavior lives in
/// <see cref="MajordomoExecutor"/> — the tool is metadata plus dispatch.
/// </summary>
internal sealed class MajordomoMcpTool : McpServerTool
{
    private readonly Tool _protocolTool;

    public MajordomoMcpTool(MajordomoTool descriptor)
    {
        Descriptor = descriptor;
        _protocolTool = new Tool
        {
            Name = descriptor.Name,
            Description = descriptor.Description,
            InputSchema = MajordomoJson.InputSchemaFor(descriptor.ArgumentsType),
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = descriptor.Class == MajordomoToolClass.Read,
                DestructiveHint = descriptor.Class == MajordomoToolClass.Mutate,
                // Cancel is the one mutation whose replay is a no-op; create
                // and update are not idempotent and retry is explicitly not.
                IdempotentHint = descriptor == MajordomoTools.CancelWorkItem,
                OpenWorldHint = false,
            },
        };
    }

    /// <summary>The vocabulary descriptor this tool publishes.</summary>
    public MajordomoTool Descriptor { get; }

    public override Tool ProtocolTool => _protocolTool;

    /// <inheritdoc />
    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var services = request.Services ?? request.Server?.Services
            ?? throw new InvalidOperationException("no service provider on the MCP request context");
        var executor = services.GetRequiredService<MajordomoExecutor>();
        var argsNode = request.Params?.Arguments is { } raw
            ? System.Text.Json.JsonSerializer.SerializeToNode(raw)
            : null;
        return await executor.ExecuteAsync(
            request.Params?.Name ?? Descriptor.Name, argsNode, cancellationToken).ConfigureAwait(false);
    }
}
