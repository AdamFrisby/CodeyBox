using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueQuestions
{
    private const string OpenState = "open";

    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("questions", "List operator questions raised by a work item");

        var idArg = new Argument<string>("id", "Work item ID");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");
        var quietOpt = new Option<bool>("--quiet", "Print only the IDs of unanswered (open) questions");

        cmd.AddArgument(idArg);
        cmd.AddOption(jsonOpt);
        cmd.AddOption(quietOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();

            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);
            var quiet = ctx.ParseResult.GetValueForOption(quietOpt);
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
                var raw = await client.GetWorkItemQuestionsAsync(id, ct);

                if (json)
                {
                    Console.WriteLine(raw);
                    return;
                }

                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array)
                {
                    await Console.Error.WriteLineAsync("Error: expected a JSON array of questions.");
                    ctx.ExitCode = 1;
                    return;
                }

                if (quiet)
                {
                    foreach (var question in root.EnumerateArray())
                    {
                        if (!string.Equals(DisplayHelpers.Field(question, "state"), OpenState, StringComparison.Ordinal))
                            continue;
                        // Question IDs originate from agent output: strip control chars before printing.
                        Console.WriteLine(DisplayHelpers.Sanitize(DisplayHelpers.Field(question, "questionId")));
                    }
                    return;
                }

                Render(id, root);
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
        });

        return cmd;
    }

    private static void Render(string id, JsonElement root)
    {
        // The id is caller-supplied argv; sanitize before echoing to the terminal.
        Console.WriteLine($"Questions for {DisplayHelpers.Sanitize(id)}");

        if (root.GetArrayLength() == 0)
        {
            Console.WriteLine("(no questions)");
            return;
        }

        Console.WriteLine();
        // PrintTable sanitizes every cell, guarding the terminal against escape sequences
        // embedded in the untrusted question text.
        DisplayHelpers.PrintTable(
            [
                new("QUESTION-ID", 20),
                new("STATE", 10),
                new("ASKED", 22),
                new("TEXT", 50),
            ],
            root.EnumerateArray().Select<JsonElement, IReadOnlyList<string?>>(q =>
            [
                DisplayHelpers.Field(q, "questionId"),
                DisplayHelpers.Field(q, "state"),
                DisplayHelpers.Field(q, "askedAt"),
                DisplayHelpers.Field(q, "questionText"),
            ]));
    }
}
