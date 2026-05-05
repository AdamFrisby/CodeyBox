using System.CommandLine;
using System.CommandLine.Invocation;
using CodeyBox.Cli.Models;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueAdd
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("add", "Create a new work item");

        var projectOpt = new Option<string>("--project", "Project ID") { IsRequired = true };
        var titleOpt = new Option<string>("--title", "Work item title") { IsRequired = true };
        var promptOpt = new Option<string?>("--prompt", "Inline prompt text");
        var promptFileOpt = new Option<string?>("--prompt-file", "Path to prompt file, or '-' for stdin");
        var agentOpt = new Option<string?>("--agent", "Agent name (e.g. claude, gemini)");
        var baseBranchOpt = new Option<string?>("--base-branch", "Override base branch");
        var workBranchOpt = new Option<string?>("--work-branch", "Work branch name");
        var pushUpstreamOpt = new Option<bool>("--push-upstream", "Push completed branch to upstream");
        var dependsOnOpt = new Option<string[]>("--depends-on", "Work item IDs this depends on")
        {
            AllowMultipleArgumentsPerToken = false,
        };
        dependsOnOpt.Arity = ArgumentArity.ZeroOrMore;
        var quietOpt = new Option<bool>("--quiet", "Print only the new work item ID");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");

        cmd.AddOption(projectOpt);
        cmd.AddOption(titleOpt);
        cmd.AddOption(promptOpt);
        cmd.AddOption(promptFileOpt);
        cmd.AddOption(agentOpt);
        cmd.AddOption(baseBranchOpt);
        cmd.AddOption(workBranchOpt);
        cmd.AddOption(pushUpstreamOpt);
        cmd.AddOption(dependsOnOpt);
        cmd.AddOption(quietOpt);
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();

            var project = ctx.ParseResult.GetValueForOption(projectOpt)!;
            var title = ctx.ParseResult.GetValueForOption(titleOpt)!;
            var promptText = ctx.ParseResult.GetValueForOption(promptOpt);
            var promptFile = ctx.ParseResult.GetValueForOption(promptFileOpt);
            var agent = ctx.ParseResult.GetValueForOption(agentOpt);
            var baseBranch = ctx.ParseResult.GetValueForOption(baseBranchOpt);
            var workBranch = ctx.ParseResult.GetValueForOption(workBranchOpt);
            var pushUpstream = ctx.ParseResult.GetValueForOption(pushUpstreamOpt);
            var dependsOn = ctx.ParseResult.GetValueForOption(dependsOnOpt);
            var quiet = ctx.ParseResult.GetValueForOption(quietOpt);
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);
            var flagUrl = ctx.ParseResult.GetValueForOption(apiUrlOpt);
            var flagKey = ctx.ParseResult.GetValueForOption(apiKeyOpt);

            string? prompt;
            if (promptFile is not null)
            {
                if (promptFile == "-")
                    prompt = await Console.In.ReadToEndAsync(ct);
                else
                    prompt = await File.ReadAllTextAsync(promptFile, ct);
            }
            else if (promptText is not null)
            {
                prompt = promptText;
            }
            else
            {
                await Console.Error.WriteLineAsync("Error: provide --prompt or --prompt-file.");
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

            var req = new CreateWorkItemRequest
            {
                ProjectId = project,
                Title = title,
                Prompt = prompt,
                Agent = agent,
                BaseBranch = baseBranch,
                WorkBranch = workBranch,
                PushUpstream = pushUpstream ? true : null,
                DependsOn = dependsOn is { Length: > 0 } ? [.. dependsOn] : null,
            };

            try
            {
                var item = await client.CreateWorkItemAsync(req, ct);

                if (json)
                {
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(item, CliJsonContext.Default.WorkItemDto));
                }
                else if (quiet)
                {
                    Console.WriteLine(item.Id);
                }
                else
                {
                    Console.WriteLine($"Created work item {item.Id}");
                    Console.WriteLine($"  State:   {item.State}");
                    Console.WriteLine($"  Project: {item.ProjectId}");
                    Console.WriteLine($"  Title:   {item.Title}");
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
