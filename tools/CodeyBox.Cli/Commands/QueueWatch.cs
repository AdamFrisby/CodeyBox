using System.CommandLine;
using System.CommandLine.Invocation;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueWatch
{
    internal static TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("watch", "Poll a work item and print each state transition");

        var idArg = new Argument<string>("id", "Work item ID");
        var streamOpt = new Option<bool>("--stream", "Attempt to stream agent stdout (falls back to polling)");

        cmd.AddArgument(idArg);
        cmd.AddOption(streamOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();

            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var stream = ctx.ParseResult.GetValueForOption(streamOpt);
            var flagUrl = ctx.ParseResult.GetValueForOption(apiUrlOpt);
            var flagKey = ctx.ParseResult.GetValueForOption(apiKeyOpt);

            if (stream)
                await Console.Error.WriteLineAsync("Note: streaming not yet available; using state polling.");

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
                string? lastState = null;

                while (!ct.IsCancellationRequested)
                {
                    var item = await client.GetWorkItemAsync(id, ct);
                    if (item is null)
                    {
                        await Console.Error.WriteLineAsync($"Error: work item '{id}' not found.");
                        ctx.ExitCode = 1;
                        return;
                    }

                    if (item.State != lastState)
                    {
                        var timestamp = DateTimeOffset.UtcNow.ToString("HH:mm:ss");
                        Console.WriteLine($"[{timestamp}] {item.State}");
                        lastState = item.State;
                    }

                    if (item.IsTerminal)
                        return;

                    await Task.Delay(PollingInterval, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal exit on Ctrl+C
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
