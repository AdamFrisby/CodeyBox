using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueDismissQuestion
{
    private const string NoOpStatus = "no-op";

    // Used when the operator dismisses without supplying an explicit --reason. The server
    // requires a non-empty reason, so the CLI provides this neutral default.
    private const string DefaultReason = "Dismissed by operator via CLI";

    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("dismiss-question", "Dismiss an operator question raised by a work item");

        var idArg = new Argument<string>("id", "Work item ID");
        var questionIdArg = new Argument<string>("questionId", "ID of the question to dismiss");
        var reasonOpt = new Option<string>(
            "--reason",
            () => DefaultReason,
            "Reason for dismissing the question");
        var quietOpt = new Option<bool>("--quiet", "Print only the resulting status");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");

        cmd.AddArgument(idArg);
        cmd.AddArgument(questionIdArg);
        cmd.AddOption(reasonOpt);
        cmd.AddOption(quietOpt);
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();

            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var questionId = ctx.ParseResult.GetValueForArgument(questionIdArg);
            var reason = ctx.ParseResult.GetValueForOption(reasonOpt) ?? DefaultReason;
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
                var raw = await client.DismissQuestionAsync(id, questionId, reason, ct);

                if (json)
                {
                    Console.WriteLine(raw);
                    return;
                }

                using var doc = JsonDocument.Parse(raw);
                var status = DisplayHelpers.Field(doc.RootElement, "status");

                if (quiet)
                {
                    Console.WriteLine(status);
                    return;
                }

                // id/questionId are caller-supplied argv; sanitize before echoing to the terminal.
                var safeId = DisplayHelpers.Sanitize(id);
                var safeQuestionId = DisplayHelpers.Sanitize(questionId);

                if (string.Equals(status, NoOpStatus, StringComparison.Ordinal))
                {
                    var questionState = DisplayHelpers.Field(doc.RootElement, "questionState");
                    Console.WriteLine(
                        $"No change - question '{safeQuestionId}' on work item {safeId} is already {questionState}.");
                }
                else
                {
                    Console.WriteLine($"Dismissed question '{safeQuestionId}' on work item {safeId}.");
                }
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
}
