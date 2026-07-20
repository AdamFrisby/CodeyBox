using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueCosts
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("costs", "Show token usage and cost breakdown for a work item");

        var idArg = new Argument<string>("id", "Work item ID");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");

        cmd.AddArgument(idArg);
        cmd.AddOption(jsonOpt);

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var id = ctx.ParseResult.GetValueForArgument(idArg);
            return JsonEndpointCommand.RunAsync(
                ctx,
                apiUrlOpt,
                apiKeyOpt,
                jsonOpt,
                clientFactory,
                (client, ct) => client.GetWorkItemCostsAsync(id, ct),
                Render);
        });

        return cmd;
    }

    private static void Render(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected top-level JSON object.");

        Console.WriteLine($"Costs for {DisplayHelpers.Field(root, "workItemId")}");
        Console.WriteLine();

        if (root.TryGetProperty("totals", out var totals) && totals.ValueKind == JsonValueKind.Object)
        {
            DisplayHelpers.PrintTable(
                [
                    new("METRIC", 20),
                    new("VALUE", 22),
                ],
                [
                    ["Input tokens", DisplayHelpers.Field(totals, "inputTokens")],
                    ["Cached input", DisplayHelpers.Field(totals, "cachedInputTokens")],
                    ["Output tokens", DisplayHelpers.Field(totals, "outputTokens")],
                    ["Estimated USD", DisplayHelpers.Field(totals, "estimatedUsd")],
                    ["Elapsed (ms)", DisplayHelpers.Field(totals, "elapsedMs")],
                    ["Invocations", DisplayHelpers.Field(totals, "invocationCount")],
                ]);
        }

        if (root.TryGetProperty("byPhase", out var byPhase) &&
            byPhase.ValueKind == JsonValueKind.Object &&
            byPhase.EnumerateObject().Any())
        {
            Console.WriteLine();
            Console.WriteLine("By phase");
            DisplayHelpers.PrintTable(
                [
                    new("PHASE", 24),
                    new("INPUT", 12),
                    new("OUTPUT", 12),
                    new("USD", 12),
                    new("INVOCATIONS", 12),
                ],
                byPhase.EnumerateObject().Select<JsonProperty, IReadOnlyList<string?>>(p =>
                [
                    p.Name,
                    DisplayHelpers.Field(p.Value, "inputTokens"),
                    DisplayHelpers.Field(p.Value, "outputTokens"),
                    DisplayHelpers.Field(p.Value, "estimatedUsd"),
                    DisplayHelpers.Field(p.Value, "invocationCount"),
                ]));
        }

        if (root.TryGetProperty("byAgent", out var byAgent) &&
            byAgent.ValueKind == JsonValueKind.Array &&
            byAgent.GetArrayLength() > 0)
        {
            Console.WriteLine();
            Console.WriteLine("By agent");
            DisplayHelpers.PrintTable(
                [
                    new("AGENT", 14),
                    new("MODEL", 30),
                    new("INPUT", 12),
                    new("OUTPUT", 12),
                    new("USD", 12),
                    new("INVOCATIONS", 12),
                ],
                byAgent.EnumerateArray().Select<JsonElement, IReadOnlyList<string?>>(a =>
                [
                    DisplayHelpers.Field(a, "agent"),
                    DisplayHelpers.Field(a, "modelId"),
                    DisplayHelpers.Field(a, "inputTokens"),
                    DisplayHelpers.Field(a, "outputTokens"),
                    DisplayHelpers.Field(a, "estimatedUsd"),
                    DisplayHelpers.Field(a, "invocationCount"),
                ]));
        }
    }
}
