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
        var cmd = new Command("watch", "Watch a work item and print each state transition");

        var idArg = new Argument<string>("id", "Work item ID");
        var pollOpt = new Option<bool>("--poll", "Use HTTP polling instead of the SSE event stream");
        var streamOpt = new Option<bool>("--stream", "Also stream agent stdout when available");

        cmd.AddArgument(idArg);
        cmd.AddOption(pollOpt);
        cmd.AddOption(streamOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();

            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var forcePoll = ctx.ParseResult.GetValueForOption(pollOpt);
            _ = ctx.ParseResult.GetValueForOption(streamOpt);
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
                if (!forcePoll)
                {
                    var usedSse = await WatchViaSseAsync(client, id, ct);
                    if (usedSse)
                        return;
                    await Console.Error.WriteLineAsync("Note: SSE unavailable; using state polling.");
                }

                if (!await WatchViaPollingAsync(client, id, ct))
                    ctx.ExitCode = 1;
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

    private static async Task<bool> WatchViaSseAsync(CodeyBoxClient client, string id, CancellationToken ct)
    {
        return await client.TryWatchWorkItemEventsAsync(id, PrintStateTransition, ct);
    }

    /// <returns><c>false</c> when the work item was not found.</returns>
    private static async Task<bool> WatchViaPollingAsync(CodeyBoxClient client, string id, CancellationToken ct)
    {
        string? lastState = null;

        while (!ct.IsCancellationRequested)
        {
            var item = await client.GetWorkItemAsync(id, ct);
            if (item is null)
            {
                await Console.Error.WriteLineAsync($"Error: work item '{id}' not found.");
                return false;
            }

            if (item.State != lastState)
            {
                PrintStateTransition(item.State);
                lastState = item.State;
            }

            if (item.IsTerminal)
                return true;

            await Task.Delay(PollingInterval, ct);
        }

        return true;
    }

    private static void PrintStateTransition(string state)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("HH:mm:ss");
        Console.WriteLine($"[{timestamp}] {state}");
    }
}
