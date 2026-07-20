using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueTimeline
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("timeline", "Show the event timeline of a work item");

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
                (client, ct) => client.GetWorkItemTimelineAsync(id, ct),
                Render);
        });

        return cmd;
    }

    private static void Render(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected top-level JSON object.");

        Console.WriteLine($"Timeline for {DisplayHelpers.Field(root, "workItemId")}");

        if (!root.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array ||
            entries.GetArrayLength() == 0)
        {
            Console.WriteLine("(no timeline entries)");
            return;
        }

        Console.WriteLine();
        DisplayHelpers.PrintTable(
            [
                new("TIME", 22),
                new("KIND", 24),
                new("SUMMARY", 60),
            ],
            entries.EnumerateArray().Select<JsonElement, IReadOnlyList<string?>>(e =>
            [
                DisplayHelpers.Field(e, "occurredAt"),
                DisplayHelpers.Field(e, "kind"),
                DisplayHelpers.Field(e, "summary"),
            ]));
    }
}
