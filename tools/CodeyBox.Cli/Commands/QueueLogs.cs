using System.CommandLine;
using System.CommandLine.Invocation;
using CodeyBox.Cli.Models;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueLogs
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("logs", "Show the tail of a work item's captured agent stdout");

        var idArg = new Argument<string>("id", "Work item ID");
        var jsonOpt = new Option<bool>("--json", "Print the tail wrapped as a JSON object");

        cmd.AddArgument(idArg);
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();

            var id = ctx.ParseResult.GetValueForArgument(idArg);
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
                var tail = await client.GetWorkItemStdoutTailAsync(id, ct);

                if (json)
                {
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                        new StdoutTailDto(id, tail), CliJsonContext.Default.StdoutTailDto));
                    return;
                }

                if (string.IsNullOrEmpty(tail))
                {
                    Console.WriteLine($"No output captured yet for '{id}'.");
                    return;
                }

                // Agent stdout is untrusted: strip terminal escapes but keep line layout.
                var sanitized = DisplayHelpers.SanitizeMultiline(tail);
                Console.Write(sanitized);
                if (!tail.EndsWith('\n'))
                    Console.WriteLine();
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
}
