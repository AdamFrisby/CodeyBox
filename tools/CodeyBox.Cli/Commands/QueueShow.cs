using System.CommandLine;
using System.CommandLine.Invocation;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueShow
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("show", "Show details of a work item");

        var idArg = new Argument<string>("id", "Work item ID");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");

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
                var item = await client.GetWorkItemAsync(id, ct);
                if (item is null)
                {
                    await Console.Error.WriteLineAsync($"Error: work item '{id}' not found.");
                    ctx.ExitCode = 1;
                    return;
                }

                if (json)
                {
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(item, CliJsonContext.Default.WorkItemDto));
                    return;
                }

                Console.WriteLine($"ID:          {item.Id}");
                Console.WriteLine($"State:       {item.State}");
                Console.WriteLine($"Project:     {item.ProjectId}");
                Console.WriteLine($"Title:       {item.Title}");
                Console.WriteLine($"Agent:       {item.Agent}");
                if (item.Initiator is not null)
                    Console.WriteLine($"Initiator:   {item.Initiator.DisplayName} ({item.Initiator.Issuer})");
                if (item.WorkBranch is not null)
                    Console.WriteLine($"Work branch: {item.WorkBranch}");
                if (item.BaseBranch is not null)
                    Console.WriteLine($"Base branch: {item.BaseBranch}");
                Console.WriteLine($"Created:     {item.CreatedAt:u}");
                Console.WriteLine($"Updated:     {item.UpdatedAt:u}");
                if (item.DependsOn.Count > 0)
                    Console.WriteLine($"Depends on:  {string.Join(", ", item.DependsOn)}");
                if (item.LastError is not null)
                    Console.WriteLine($"Last error:  {item.LastError}");
                if (item.Prompt.Length > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Prompt:");
                    Console.WriteLine(item.Prompt);
                }
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
