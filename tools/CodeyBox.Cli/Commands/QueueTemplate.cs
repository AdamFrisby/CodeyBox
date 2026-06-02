using System.CommandLine;
using System.CommandLine.Invocation;
using CodeyBox.Cli.Models;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueTemplate
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("template", "Queue every check in a task template");
        cmd.AddAlias("templates");

        var templateArg = new Argument<string>("template", "Template name or path under templates/");
        var projectOpt = new Option<string>("--project", "Project ID") { IsRequired = true };
        var agentOpt = new Option<string?>("--agent", "Agent name for the check work items");
        var agentClassOpt = new Option<string?>("--agent-class", "Agent class ID for the check work items");
        var priorityOpt = new Option<int?>("--priority", "Priority for the check work items");
        var minModelScoreOpt = new Option<int?>("--min-model-score", "Minimum model score for the check work items");
        var quietOpt = new Option<bool>("--quiet", "Print only created work item IDs");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");

        cmd.AddArgument(templateArg);
        cmd.AddOption(projectOpt);
        cmd.AddOption(agentOpt);
        cmd.AddOption(agentClassOpt);
        cmd.AddOption(priorityOpt);
        cmd.AddOption(minModelScoreOpt);
        cmd.AddOption(quietOpt);
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();

            var template = ctx.ParseResult.GetValueForArgument(templateArg);
            var project = ctx.ParseResult.GetValueForOption(projectOpt)!;
            var agent = ctx.ParseResult.GetValueForOption(agentOpt);
            var agentClass = ctx.ParseResult.GetValueForOption(agentClassOpt);
            var priority = ctx.ParseResult.GetValueForOption(priorityOpt);
            var minModelScore = ctx.ParseResult.GetValueForOption(minModelScoreOpt);
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
            var req = new QueueTemplateRequest
            {
                Template = template,
                ProjectId = project,
                Agent = agent,
                AgentClassId = agentClass,
                Priority = priority,
                MinModelScore = minModelScore,
            };

            try
            {
                var result = await client.QueueTemplateAsync(req, ct);

                if (json)
                {
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, CliJsonContext.Default.QueueTemplateResponse));
                    return;
                }

                if (quiet)
                {
                    foreach (var item in result.WorkItems)
                        Console.WriteLine(item.Id);
                    return;
                }

                Console.WriteLine($"Queued {result.Enqueued} check-and-act work items from template {result.Template}");
                foreach (var item in result.WorkItems)
                    Console.WriteLine($"  {item.Id}  {item.Title}");
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
