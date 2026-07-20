using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueDiff
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("diff", "Show the diff of a work item's branch against its base");

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
                var raw = await client.GetWorkItemDiffAsync(id, ct);

                // 204 No Content: bare repo not created, work branch not pushed, or no changes.
                if (raw is null)
                {
                    if (json)
                    {
                        Console.WriteLine("{}");
                    }
                    else
                    {
                        Console.WriteLine(
                            $"No diff available for '{id}' (work not started, or no changes on the work branch).");
                    }

                    return;
                }

                if (json)
                {
                    Console.WriteLine(raw);
                    return;
                }

                using var doc = JsonDocument.Parse(raw);
                Render(doc.RootElement);
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

    private static void Render(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected top-level JSON object.");

        var baseBranch = DisplayHelpers.Field(root, "baseBranch");
        var workBranch = DisplayHelpers.Field(root, "workBranch");
        var baseSha = ShortSha(DisplayHelpers.Field(root, "baseCommitSha"));
        var workSha = ShortSha(DisplayHelpers.Field(root, "workCommitSha"));

        Console.WriteLine($"Work item:   {DisplayHelpers.Field(root, "workItemId")}");
        Console.WriteLine($"Base branch: {DisplayHelpers.Sanitize(baseBranch)} ({baseSha})");
        Console.WriteLine($"Work branch: {DisplayHelpers.Sanitize(workBranch)} ({workSha})");
        Console.WriteLine(
            $"Files changed: {DisplayHelpers.Field(root, "filesChanged")}  " +
            $"(+{DisplayHelpers.Field(root, "linesAdded")} / -{DisplayHelpers.Field(root, "linesRemoved")})");

        if (string.Equals(DisplayHelpers.Field(root, "truncated"), "true", StringComparison.Ordinal))
            Console.WriteLine("(diff truncated by the server — fetch the raw diff for full output)");

        // Large diffs omit the body and carry a hint plus a changed-file list instead.
        if (root.TryGetProperty("diff", out var diff) && diff.ValueKind == JsonValueKind.String)
        {
            var body = diff.GetString();
            if (!string.IsNullOrEmpty(body))
            {
                Console.WriteLine();
                Console.Write(DisplayHelpers.SanitizeMultiline(body));
                if (!body.EndsWith('\n'))
                    Console.WriteLine();
            }
        }
        else
        {
            var hint = DisplayHelpers.Field(root, "hint");
            if (!string.IsNullOrEmpty(hint))
            {
                Console.WriteLine();
                Console.WriteLine(DisplayHelpers.Sanitize(hint));
            }
        }
    }

    private static string ShortSha(string sha) =>
        sha.Length >= 8 ? sha[..8] : sha;
}
