using System.CommandLine;
using System.CommandLine.Invocation;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueRetry
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("retry", "Retry a failed work item");

        var idArg = new Argument<string>("id", "Work item ID");
        var fromOpt = new Option<string?>("--from", "Retry from phase: work, rework, audit, merge, or upstream");

        cmd.AddArgument(idArg);
        cmd.AddOption(fromOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();

            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var from = ctx.ParseResult.GetValueForOption(fromOpt);
            var flagUrl = ctx.ParseResult.GetValueForOption(apiUrlOpt);
            var flagKey = ctx.ParseResult.GetValueForOption(apiKeyOpt);

            string[] validFromValues = ["work", "rework", "audit", "merge", "upstream"];
            if (!string.IsNullOrWhiteSpace(from) && !validFromValues.Contains(from, StringComparer.OrdinalIgnoreCase))
            {
                await Console.Error.WriteLineAsync(
                    $"Error: --from must be one of: work, rework, audit, merge, upstream (got '{from}').");
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
                var requestedFrom = string.IsNullOrWhiteSpace(from) ? null : from;
                var item = await client.RetryWorkItemAsync(id, requestedFrom, ct);
                Console.WriteLine($"Retrying {item.Id} from '{requestedFrom ?? "auto"}' — state: {item.State}");
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
