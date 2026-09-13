using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

/// <summary>
/// Read-only test-selection soundness surface: renders the
/// <c>GET /audit/test-selection/soundness</c> report — unsafe-skip count and
/// would-be savings over the last N audits plus the explicit
/// enforce-readiness gate — from real persisted shadow telemetry.
/// </summary>
internal static class AuditCommand
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("audit", "Inspect audit reports and selection soundness");
        cmd.AddCommand(BuildSoundness(apiUrlOpt, apiKeyOpt, clientFactory));
        return cmd;
    }

    internal static Command BuildSoundness(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command(
            "test-selection-soundness",
            "Show unsafe-skip count, would-be savings, and the enforce-readiness gate over recent shadow runs");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");
        var limitOpt = new Option<int?>("--limit", "How many recent audits to evaluate (server clamps to its max)");
        var selectorOpt = new Option<string?>("--selector", "Only evaluate runs for this selector (exact match)");

        cmd.AddOption(jsonOpt);
        cmd.AddOption(limitOpt);
        cmd.AddOption(selectorOpt);
        cmd.SetHandler((InvocationContext ctx) =>
        {
            var limit = ctx.ParseResult.GetValueForOption(limitOpt);
            var selector = ctx.ParseResult.GetValueForOption(selectorOpt);
            return JsonEndpointCommand.RunAsync(
                ctx,
                apiUrlOpt,
                apiKeyOpt,
                jsonOpt,
                clientFactory,
                (client, ct) => client.GetTestSelectionSoundnessAsync(limit, selector, ct),
                Render);
        });

        return cmd;
    }

    private static void Render(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected top-level JSON object.");

        DisplayHelpers.PrintTable(
            [
                new("METRIC", 22),
                new("VALUE", 56),
            ],
            [
                ["Window", DisplayHelpers.Field(root, "windowSize")],
                ["Evaluated", DisplayHelpers.Field(root, "evaluatedCount")],
                ["Unsafe skips", DisplayHelpers.Field(root, "unsafeSkipCount")],
                ["Safe", DisplayHelpers.Field(root, "safeCount")],
                ["Full suite", DisplayHelpers.Field(root, "fullSuiteCount")],
                ["Unverifiable", DisplayHelpers.Field(root, "unverifiableCount")],
                ["Tests saved", DisplayHelpers.Field(root, "totalTestsSaved")],
                ["Est. saved ms", DisplayHelpers.Field(root, "estimatedSavedMs")],
            ]);

        if (root.TryGetProperty("bySelector", out var slices) && slices.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine();
            Console.WriteLine("By selector");
            DisplayHelpers.PrintTable(
                [
                    new("SELECTOR", 16),
                    new("RUNS", 6),
                    new("UNSAFE", 7),
                    new("SAFE", 6),
                    new("TESTS_SAVED", 12),
                    new("SAVED_MS", 10),
                ],
                slices.EnumerateArray().Select<JsonElement, IReadOnlyList<string?>>(slice =>
                [
                    DisplayHelpers.Field(slice, "selector"),
                    DisplayHelpers.Field(slice, "runs"),
                    DisplayHelpers.Field(slice, "unsafeSkipCount"),
                    DisplayHelpers.Field(slice, "safeCount"),
                    DisplayHelpers.Field(slice, "testsSaved"),
                    DisplayHelpers.Field(slice, "estimatedSavedMs"),
                ]));
        }

        if (root.TryGetProperty("gate", out var gate) && gate.ValueKind == JsonValueKind.Object)
        {
            Console.WriteLine();
            Console.WriteLine("Enforce-readiness gate (zero unsafe skips across the calibration window)");
            DisplayHelpers.PrintTable(
                [
                    new("METRIC", 22),
                    new("VALUE", 56),
                ],
                [
                    ["Ready", DisplayHelpers.Field(gate, "readyForEnforcement")],
                    ["Max unsafe allowed", DisplayHelpers.Field(gate, "maxAllowedUnsafeSkips")],
                    ["Calibration window", DisplayHelpers.Field(gate, "calibrationWindowSize")],
                    ["Assessable runs", DisplayHelpers.Field(gate, "assessableCount")],
                    ["Reason", DisplayHelpers.Field(gate, "reason")],
                ]);
        }
    }
}
