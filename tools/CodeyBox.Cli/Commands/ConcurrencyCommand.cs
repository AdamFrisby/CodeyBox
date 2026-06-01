using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class ConcurrencyCommand
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("concurrency", "Show concurrency caps and live usage");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");

        cmd.AddOption(jsonOpt);
        cmd.SetHandler((InvocationContext ctx) => JsonEndpointCommand.RunAsync(
            ctx,
            apiUrlOpt,
            apiKeyOpt,
            jsonOpt,
            clientFactory,
            static (client, ct) => client.GetConcurrencyAsync(ct),
            Render));

        return cmd;
    }

    private static void Render(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected top-level JSON object.");

        DisplayHelpers.PrintTable(
            [
                new("METRIC", 28),
                new("VALUE", 20),
            ],
            [
                ["Global max concurrent", DisplayHelpers.Field(root, "globalMaxConcurrent")],
                ["Currently running total", DisplayHelpers.Field(root, "currentlyRunningTotal")],
            ]);

        Console.WriteLine();
        Console.WriteLine("Agents");
        DisplayHelpers.PrintTable(
            [
                new("AGENT", 12),
                new("CAP", 6),
                new("RUNNING", 8),
                new("EXCLUDED", 8),
                new("REASON", 36),
            ],
            GetAgentRows(root));

        Console.WriteLine();
        Console.WriteLine("Burn Estimates");
        DisplayHelpers.PrintTable(
            [
                new("AGENT", 12),
                new("AVG_BURN", 10),
                new("SAMPLES", 7),
            ],
            GetArrayRows(root, "burnEstimates", burn =>
            [
                DisplayHelpers.Field(burn, "agent"),
                DisplayHelpers.Percent(burn, "avgBurnPctPerItem"),
                DisplayHelpers.Field(burn, "sampleCount"),
            ]));

        Console.WriteLine();
        Console.WriteLine("Member Fits");
        DisplayHelpers.PrintTable(
            [
                new("CLASS", 16),
                new("AGENT", 12),
                new("MODEL", 24),
                new("AVAILABLE", 10),
                new("AVG_BURN", 10),
                new("FIT", 8),
                new("RUNNING", 8),
            ],
            GetArrayRows(root, "memberFits", fit =>
            [
                DisplayHelpers.Field(fit, "classId"),
                DisplayHelpers.Field(fit, "agent"),
                DisplayHelpers.Field(fit, "modelId"),
                DisplayHelpers.Percent(fit, "availablePct"),
                DisplayHelpers.Percent(fit, "avgBurnPctPerItem"),
                DisplayHelpers.Field(fit, "fitInWindow"),
                DisplayHelpers.Field(fit, "runningOnAgent"),
            ]));
    }

    private static IEnumerable<IReadOnlyList<string?>> GetAgentRows(JsonElement root)
    {
        var caps = ObjectPropertyValues(root, "perAgentCaps");
        var running = ObjectPropertyValues(root, "currentlyRunningPerAgent");
        var availability = AvailabilityByAgent(root);

        var agents = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in caps.Keys) agents.Add(agent);
        foreach (var agent in running.Keys) agents.Add(agent);
        foreach (var agent in availability.Keys) agents.Add(agent);

        foreach (var agent in agents)
        {
            var hasStatus = availability.TryGetValue(agent, out var status);
            yield return
            [
                agent,
                caps.TryGetValue(agent, out var cap) ? cap : "",
                running.TryGetValue(agent, out var active) ? active : "0",
                hasStatus ? DisplayHelpers.Field(status, "excluded") : "",
                hasStatus ? DisplayHelpers.Field(status, "reason") : "",
            ];
        }
    }

    private static IEnumerable<IReadOnlyList<string?>> GetArrayRows(
        JsonElement root,
        string propertyName,
        Func<JsonElement, IReadOnlyList<string?>> rowFactory)
    {
        if (!root.TryGetProperty(propertyName, out var rows) || rows.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var row in rows.EnumerateArray())
            yield return rowFactory(row);
    }

    private static Dictionary<string, string> ObjectPropertyValues(JsonElement root, string propertyName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in value.EnumerateObject())
            result[property.Name] = DisplayHelpers.Value(property.Value);

        return result;
    }

    private static Dictionary<string, JsonElement> AvailabilityByAgent(JsonElement root)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("agentAvailability", out var availability) ||
            availability.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in availability.EnumerateArray())
        {
            var agent = DisplayHelpers.Field(item, "agent");
            if (!string.IsNullOrWhiteSpace(agent))
                result[agent] = item;
        }

        return result;
    }
}
