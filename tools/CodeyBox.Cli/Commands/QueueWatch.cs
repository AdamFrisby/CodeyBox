using System.CommandLine;
using System.CommandLine.Invocation;
using CodeyBox.Cli.Services;
using CliApp = CodeyBox.Cli.CliApp;

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
        var streamOpt = new Option<bool>(
            "--stream",
            "Stream agent stdout when available (not implemented; watch uses state transitions)");

        cmd.AddArgument(idArg);
        cmd.AddOption(pollOpt);
        cmd.AddOption(streamOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();
            if (CliApp.TestCancellationToken.CanBeCanceled)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    ct, CliApp.TestCancellationToken);
                await RunWatchAsync(
                    ctx, idArg, pollOpt, streamOpt, apiUrlOpt, apiKeyOpt, clientFactory, linked.Token);
                return;
            }

            await RunWatchAsync(
                ctx, idArg, pollOpt, streamOpt, apiUrlOpt, apiKeyOpt, clientFactory, ct);
        });

        return cmd;
    }

    private static async Task RunWatchAsync(
        InvocationContext ctx,
        Argument<string> idArg,
        Option<bool> pollOpt,
        Option<bool> streamOpt,
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory,
        CancellationToken ct)
    {

            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var forcePoll = ctx.ParseResult.GetValueForOption(pollOpt);
            var streamStdout = ctx.ParseResult.GetValueForOption(streamOpt);
            var flagUrl = ctx.ParseResult.GetValueForOption(apiUrlOpt);
            var flagKey = ctx.ParseResult.GetValueForOption(apiKeyOpt);

            if (streamStdout)
            {
                await Console.Error.WriteLineAsync(
                    "Note: --stream is not implemented yet; watching state transitions only.");
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
                string? lastPrintedState = null;

                if (!forcePoll)
                {
                    var sseResult = await client.TryWatchWorkItemEventsAsync(id, state =>
                    {
                        PrintStateTransition(state);
                        lastPrintedState = state;
                    }, ct);

                    if (sseResult == SseWatchResult.Completed)
                        return;

                    await Console.Error.WriteLineAsync(
                        "Note: SSE unavailable; using state polling.");
                }

                if (!await WatchViaPollingAsync(client, id, lastPrintedState, ct))
                    ctx.ExitCode = 1;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Ctrl+C: exit 0 without treating cancellation as SSE failure or polling error.
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

    /// <returns><c>false</c> when the work item was not found.</returns>
    private static async Task<bool> WatchViaPollingAsync(
        CodeyBoxClient client,
        string id,
        string? lastState,
        CancellationToken ct)
    {

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
        Console.WriteLine($"[{timestamp}] {SanitizeStateForOutput(state)}");
    }

    private static string SanitizeStateForOutput(string state) =>
        string.Concat(state.Where(static c => !char.IsControl(c)));
}
