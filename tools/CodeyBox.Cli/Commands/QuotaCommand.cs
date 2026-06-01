using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QuotaCommand
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("quota", "Show quota probe status");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");

        cmd.AddOption(jsonOpt);
        cmd.SetHandler((InvocationContext ctx) => JsonEndpointCommand.RunAsync(
            ctx,
            apiUrlOpt,
            apiKeyOpt,
            jsonOpt,
            clientFactory,
            static (client, ct) => client.GetQuotaAsync(ct),
            Render));

        return cmd;
    }

    private static void Render(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected top-level JSON object.");

        var observedWindow = DisplayHelpers.Field(root, "observedFailureWindowMinutes");

        DisplayHelpers.PrintTable(
            [
                new("METRIC", 30),
                new("VALUE", 42),
            ],
            [
                ["Generated", DisplayHelpers.Field(root, "generatedAt")],
                ["Min quota", DisplayHelpers.Percent(root, "minQuotaPct")],
                ["Unknown policy", DisplayHelpers.Field(root, "unknownPolicy")],
                ["Observed failure window", string.IsNullOrEmpty(observedWindow) ? "" : observedWindow + "m"],
                ["Budgets error", DisplayHelpers.Field(root, "budgetsError")],
            ]);

        Console.WriteLine();
        Console.WriteLine("Probes");
        DisplayHelpers.PrintTable(
            [
                new("AGENT", 12),
                new("AVAILABLE", 10),
                new("RESET", 24),
                new("ALLOW", 7),
                new("DEFAULT", 8),
                new("MODELS", 6),
                new("FAILURES", 8),
                new("NOTES", 28),
            ],
            GetProbeRows(root));

        if (root.TryGetProperty("budgets", out var budgets) && budgets.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine();
            Console.WriteLine("Budgets");
            DisplayHelpers.PrintTable(
                [
                    new("AGENT", 12),
                    new("MODEL", 30),
                    new("WINDOWS", 7),
                    new("FIRST_WINDOW", 14),
                    new("REMAINING", 10),
                    new("RESET", 24),
                ],
                budgets.EnumerateArray().Select<JsonElement, IReadOnlyList<string?>>(BudgetRow));
        }
    }

    private static IEnumerable<IReadOnlyList<string?>> GetProbeRows(JsonElement root)
    {
        if (!root.TryGetProperty("probes", out var probes) || probes.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var probe in probes.EnumerateArray())
        {
            probe.TryGetProperty("latestSnapshot", out var snapshot);
            var modelCount = Math.Max(
                DisplayHelpers.CountObjectProperties(snapshot, "perModel"),
                DisplayHelpers.CountObjectProperties(probe, "perModelWouldAllow"));

            yield return
            [
                DisplayHelpers.Field(probe, "agent"),
                DisplayHelpers.Percent(snapshot, "availablePct"),
                DisplayHelpers.Field(snapshot, "resetAt"),
                DisplayHelpers.Field(probe, "wouldAllow"),
                DisplayHelpers.Field(probe, "defaultModelWouldAllow"),
                modelCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DisplayHelpers.CountArray(probe, "observedFailuresLast60m")
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                DisplayHelpers.Field(snapshot, "notes"),
            ];
        }
    }

    private static IReadOnlyList<string?> BudgetRow(JsonElement budget)
    {
        JsonElement firstWindow = default;
        if (budget.TryGetProperty("windows", out var windows) &&
            windows.ValueKind == JsonValueKind.Array &&
            windows.GetArrayLength() > 0)
        {
            firstWindow = windows.EnumerateArray().First();
        }

        return
        [
            DisplayHelpers.Field(budget, "agent"),
            DisplayHelpers.Field(budget, "model"),
            DisplayHelpers.CountArray(budget, "windows")
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            DisplayHelpers.Field(firstWindow, "kind"),
            DisplayHelpers.Percent(firstWindow, "percentRemaining"),
            DisplayHelpers.Field(firstWindow, "resetAt"),
        ];
    }
}
