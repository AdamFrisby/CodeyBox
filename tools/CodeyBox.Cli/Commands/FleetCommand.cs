using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class FleetCommand
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("fleet", "Show fleet summary by project");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");

        cmd.AddOption(jsonOpt);
        cmd.SetHandler((InvocationContext ctx) => JsonEndpointCommand.RunAsync(
            ctx,
            apiUrlOpt,
            apiKeyOpt,
            jsonOpt,
            clientFactory,
            static (client, ct) => client.GetFleetSummaryAsync(ct),
            Render));

        return cmd;
    }

    private static void Render(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected top-level JSON array.");

        DisplayHelpers.PrintTable(
            [
                new("PROJECT", 16),
                new("NAME", 24),
                new("QUEUED", 6),
                new("IN_FLIGHT", 9),
                new("PHASE", 16),
                new("PAUSED", 6),
                new("FAILURES", 8),
                new("SPEND", 10),
                new("BUDGET", 10),
                new("RECENT", 24),
            ],
            root.EnumerateArray().Select<JsonElement, IReadOnlyList<string?>>(project =>
            [
                DisplayHelpers.Field(project, "projectId"),
                DisplayHelpers.Field(project, "displayName"),
                DisplayHelpers.Field(project, "queuedCount"),
                DisplayHelpers.Field(project, "inFlightCount"),
                DisplayHelpers.Field(project, "currentPhase"),
                DisplayHelpers.Field(project, "isPaused"),
                DisplayHelpers.Field(project, "hasRecentFailures"),
                DisplayHelpers.Field(project, "monthlySpendUsd"),
                DisplayHelpers.Field(project, "budgetThresholdState"),
                DisplayHelpers.JoinArray(project, "recentOutcomes"),
            ]));
    }
}
