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
        var auditorProfileOpt = new Option<string?>("--auditor-profile", "Audit profile name for this work item");
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
        cmd.AddOption(auditorProfileOpt);
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
            var auditorProfile = ctx.ParseResult.GetValueForOption(auditorProfileOpt);
            var baseBranch = ctx.ParseResult.GetValueForOption(baseBranchOpt);
            var workBranch = ctx.ParseResult.GetValueForOption(workBranchOpt);
            var pushUpstream = ctx.ParseResult.GetValueForOption(pushUpstreamOpt);
            var dependsOn = ctx.ParseResult.GetValueForOption(dependsOnOpt);
            var quiet = ctx.ParseResult.GetValueForOption(quietOpt);
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);
            var flagUrl = ctx.ParseResult.GetValueForOption(apiUrlOpt);
            var flagKey = ctx.ParseResult.GetValueForOption(apiKeyOpt);

            const int MaxPromptLength = 10 * 1024 * 1024; // 10 MB character cap

            string? prompt;
            if (promptFile is not null)
            {
                string? cappedText;
                if (promptFile == "-")
                {
                    cappedText = await ReadCappedAsync(Console.In, MaxPromptLength, ct);
                }
                else
                {
                    using var fs = File.OpenRead(promptFile);
                    using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
                    cappedText = await ReadCappedAsync(sr, MaxPromptLength, ct);
                }

                if (cappedText is null)
                {
                    await Console.Error.WriteLineAsync("Error: prompt exceeds 10 MB limit. Use a smaller file.");
                    ctx.ExitCode = 1;
                    return;
                }

                prompt = cappedText;
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
                AuditorProfile = auditorProfile,
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

    // Reads up to maxLength+1 characters from reader; returns null if the limit is exceeded,
    // so the guard fires before the full content is materialised in memory.
    private static async Task<string?> ReadCappedAsync(TextReader reader, int maxLength, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        var buf = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buf.AsMemory(), ct)) > 0)
        {
            sb.Append(buf, 0, read);
            if (sb.Length > maxLength) return null;
        }
        return sb.ToString();
    }
}
