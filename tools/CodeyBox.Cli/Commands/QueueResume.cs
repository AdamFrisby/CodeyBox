using System.CommandLine;
using System.CommandLine.Invocation;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueResume
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("resume", "Resume a paused queue or a cancelled work item");

        var idArg = new Argument<string?>("id", () => null, "Work item ID to resume")
        {
            Arity = ArgumentArity.ZeroOrOne,
        };
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");
        var quietOpt = new Option<bool>("--quiet", "Print only the resulting state");

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

            if (!string.IsNullOrWhiteSpace(id))
            {
                await QueueWorkItemVerbCommand.RunAsync(
                    ctx,
                    apiUrlOpt,
                    apiKeyOpt,
                    clientFactory,
                    "resume",
                    "Resumed",
                    id,
                    json,
                    quiet);
                return;
            }

            if (json || quiet)
            {
                await Console.Error.WriteLineAsync("Error: --json and --quiet require a work item ID for 'queue resume'.");
                ctx.ExitCode = 1;
                return;
            }

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
                await client.ResumeQueueAsync(ct);
                Console.WriteLine("Queue resumed");
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
