using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class WorkersCommand
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("workers", "List registered workers");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");

        cmd.AddOption(jsonOpt);
        cmd.AddCommand(BuildStatus(apiUrlOpt, apiKeyOpt, clientFactory));
        cmd.SetHandler((InvocationContext ctx) => JsonEndpointCommand.RunAsync(
            ctx,
            apiUrlOpt,
            apiKeyOpt,
            jsonOpt,
            clientFactory,
            static (client, ct) => client.GetWorkersAsync(ct),
            RenderWorkers));

        return cmd;
    }

    private static Command BuildStatus(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("status", "Show worker pool heartbeat status");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");

        cmd.AddOption(jsonOpt);
        cmd.SetHandler((InvocationContext ctx) => JsonEndpointCommand.RunAsync(
            ctx,
            apiUrlOpt,
            apiKeyOpt,
            jsonOpt,
            clientFactory,
            static (client, ct) => client.GetWorkerStatusAsync(ct),
            RenderStatus));

        return cmd;
    }

    private static void RenderWorkers(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected top-level JSON array.");

        DisplayHelpers.PrintTable(
            [
                new("WORKER", 16),
                new("HOST", 18),
                new("PID", 7),
                new("WORK_ITEM", 16),
                new("STARTED", 24),
                new("LAST_HEARTBEAT", 24),
            ],
            root.EnumerateArray().Select<JsonElement, IReadOnlyList<string?>>(worker =>
            [
                DisplayHelpers.ShortId(DisplayHelpers.Field(worker, "workerId"), 16),
                DisplayHelpers.Field(worker, "hostName"),
                DisplayHelpers.Field(worker, "processId"),
                DisplayHelpers.ShortId(DisplayHelpers.Field(worker, "currentWorkItemId"), 16),
                DisplayHelpers.Field(worker, "startedAt"),
                DisplayHelpers.Field(worker, "lastHeartbeatAt"),
            ]));
    }

    private static void RenderStatus(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected top-level JSON object.");

        DisplayHelpers.PrintTable(
            [
                new("METRIC", 24),
                new("VALUE", 28),
            ],
            [
                ["Max concurrent", DisplayHelpers.Field(root, "maxConcurrent")],
                ["Currently running", DisplayHelpers.Field(root, "currentlyRunning")],
                ["Queued count", DisplayHelpers.Field(root, "queuedCount")],
                ["Last spawn", DisplayHelpers.Field(root, "lastSpawnAt")],
            ]);
    }
}
