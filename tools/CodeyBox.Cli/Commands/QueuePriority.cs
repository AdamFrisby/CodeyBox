using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueuePriority
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("priority", "Update the dispatch priority of a work item");

        var idArg = new Argument<string>("id", "Work item ID");
        var priorityArg = new Argument<int>("priority", "New priority value");
        var quietOpt = new Option<bool>("--quiet", "Print only the new priority value");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");

        cmd.AddArgument(idArg);
        cmd.AddArgument(priorityArg);
        cmd.AddOption(quietOpt);
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();

            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var priority = ctx.ParseResult.GetValueForArgument(priorityArg);
            var quiet = ctx.ParseResult.GetValueForOption(quietOpt);
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);
            var flagUrl = ctx.ParseResult.GetValueForOption(apiUrlOpt);
            var flagKey = ctx.ParseResult.GetValueForOption(apiKeyOpt);

            var config = ConfigResolver.Resolve(flagUrl, flagKey);
            if (!config.HasApiKey)
            {
                await Console.Error.WriteLineAsync(
                    "Error: API key not configured. Run 'codeybox configure' or set CODEYBOX_CLI_API_KEY.");
                ctx.ExitCode = 1;
                return;
            }

            var client = clientFactory(config);

            try
            {
                var raw = await client.PatchPriorityAsync(id, priority, ct);

                if (json)
                {
                    Console.WriteLine(raw);
                    return;
                }

                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (!TryGetInt32(root, "priority", out var actualPriority))
                {
                    await Console.Error.WriteLineAsync("Error: response missing numeric priority.");
                    ctx.ExitCode = 1;
                    return;
                }

                if (quiet)
                {
                    Console.WriteLine(actualPriority);
                }
                else
                {
                    Console.WriteLine($"Updated priority for work item {id} - new priority: {actualPriority}");
                }
            }
            catch (CodeyBoxApiException ex)
            {
                await Console.Error.WriteLineAsync($"Error ({ex.StatusCode}): {ex.Message}");
                ctx.ExitCode = 1;
            }
            catch (HttpRequestException ex)
            {
                await Console.Error.WriteLineAsync($"Connection error: {ex.Message}");
                ctx.ExitCode = 1;
            }
        });

        return cmd;
    }

    private static bool TryGetInt32(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out value);
    }
}
