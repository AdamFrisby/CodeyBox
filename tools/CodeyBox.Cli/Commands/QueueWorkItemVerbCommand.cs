using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueWorkItemVerbCommand
{
    internal static Command Build(
        string verb,
        string description,
        string resultLabel,
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command(verb, description);

        var idArg = new Argument<string>("id", "Work item ID");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");
        var quietOpt = new Option<bool>("--quiet", "Print only the resulting state");

        cmd.AddArgument(idArg);
        cmd.AddOption(jsonOpt);
        cmd.AddOption(quietOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);
            var quiet = ctx.ParseResult.GetValueForOption(quietOpt);

            await RunAsync(
                ctx,
                apiUrlOpt,
                apiKeyOpt,
                clientFactory,
                verb,
                resultLabel,
                id,
                json,
                quiet);
        });

        return cmd;
    }

    internal static async Task RunAsync(
        InvocationContext ctx,
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory,
        string verb,
        string resultLabel,
        string id,
        bool json,
        bool quiet)
    {
        var ct = ctx.GetCancellationToken();

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
            var raw = await client.PostWorkItemVerbAsync(id, verb, ct);
            if (json)
            {
                Console.WriteLine(raw);
                return;
            }

            using var doc = JsonDocument.Parse(raw);
            var state = ExtractState(doc.RootElement);
            if (quiet)
            {
                Console.WriteLine(state);
                return;
            }

            var resultId = ExtractId(doc.RootElement) ?? id;
            Console.WriteLine($"{resultLabel} {resultId} - state: {state}");
        }
        catch (JsonException ex)
        {
            await Console.Error.WriteLineAsync($"Error parsing response: {ex.Message}");
            ctx.ExitCode = 1;
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
    }

    private static string ExtractState(JsonElement root)
    {
        if (TryGetStringProperty(root, "state", out var state))
            return state;
        if (TryGetObjectProperty(root, "workItem", out var workItem) &&
            TryGetStringProperty(workItem, "state", out state))
            return state;
        if (TryGetObjectProperty(root, "item", out var item) &&
            TryGetStringProperty(item, "state", out state))
            return state;

        throw new JsonException("response did not contain a state field");
    }

    private static string? ExtractId(JsonElement root)
    {
        if (TryGetStringProperty(root, "id", out var id))
            return id;
        if (TryGetObjectProperty(root, "workItem", out var workItem) &&
            TryGetStringProperty(workItem, "id", out id))
            return id;
        if (TryGetStringProperty(root, "workItemId", out id))
            return id;

        return null;
    }

    private static bool TryGetObjectProperty(JsonElement element, string name, out JsonElement value)
    {
        if (TryGetProperty(element, name, out value) && value.ValueKind == JsonValueKind.Object)
            return true;

        value = default;
        return false;
    }

    private static bool TryGetStringProperty(JsonElement element, string name, out string value)
    {
        if (TryGetProperty(element, name, out var property))
        {
            value = DisplayHelpers.Value(property);
            return !string.IsNullOrWhiteSpace(value);
        }

        value = "";
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value))
                return true;

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
