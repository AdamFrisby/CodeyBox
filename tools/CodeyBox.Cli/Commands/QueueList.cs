using System.CommandLine;
using System.CommandLine.Invocation;
using CodeyBox.Cli.Models;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueList
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("ls", "List work items");

        var projectOpt = new Option<string?>("--project", "Filter by project ID");
        var stateOpt = new Option<string?>("--state", "Filter by state(s), comma-separated (e.g. Working,Auditing)");
        var limitOpt = new Option<int?>("--limit", "Maximum number of items to show");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");
        var quietOpt = new Option<bool>("--quiet", "Print only work item IDs");

        cmd.AddOption(projectOpt);
        cmd.AddOption(stateOpt);
        cmd.AddOption(limitOpt);
        cmd.AddOption(jsonOpt);
        cmd.AddOption(quietOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();

            var project = ctx.ParseResult.GetValueForOption(projectOpt);
            var state = ctx.ParseResult.GetValueForOption(stateOpt);
            var limit = ctx.ParseResult.GetValueForOption(limitOpt);
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
                var items = await client.GetWorkItemsAsync(project, state, limit, ct);

                if (json)
                {
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(items, CliJsonContext.Default.ListWorkItemDto));
                    return;
                }

                if (quiet)
                {
                    foreach (var item in items)
                        Console.WriteLine(item.Id);
                    return;
                }

                PrintTable(items);
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

    private static void PrintTable(List<WorkItemDto> items)
    {
        const int idW = 10;
        const int stateW = 12;
        const int agentW = 10;
        const int projW = 12;
        const int titleW = 35;
        const int updW = 10;

        Console.WriteLine(
            $"{"ID",-idW}  {"STATE",-stateW}  {"AGENT",-agentW}  {"PROJECT",-projW}  {"TITLE",-titleW}  {"UPDATED",-updW}");
        Console.WriteLine(new string('-', idW + stateW + agentW + projW + titleW + updW + 10));

        foreach (var item in items)
        {
            var id = Truncate(item.ShortId + "...", idW);
            var state = Truncate(item.State, stateW);
            var agent = Truncate(item.Agent, agentW);
            var proj = Truncate(item.ProjectId, projW);
            var title = Truncate(item.Title, titleW);
            var upd = Truncate(item.RelativeAge, updW);

            Console.WriteLine($"{id,-idW}  {state,-stateW}  {agent,-agentW}  {proj,-projW}  {title,-titleW}  {upd,-updW}");
        }
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 1)] + "…";
}
