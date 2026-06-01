using System.CommandLine;
using System.CommandLine.Invocation;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueuePause
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("pause", "Pause the queue, optionally with a reason");

        var reasonOpt = new Option<string?>("--reason", "Reason for pausing the queue");

        cmd.AddOption(reasonOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();

            var reason = ctx.ParseResult.GetValueForOption(reasonOpt);
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
                await client.PauseQueueAsync(reason, ct);
                var suffix = reason is not null ? $" (reason: {reason})" : "";
                Console.WriteLine($"Queue paused{suffix}");
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
