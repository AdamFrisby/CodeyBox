using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueuePrompt
{
    private const int MaxPromptLength = 64 * 1024;
    private const string PromptLimitLabel = "64 KB";

    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("prompt", "Update the prompt of a work item");

        var idArg = new Argument<string>("id", "Work item ID");
        var textArg = new Argument<string?>("text", () => null, "Prompt text")
        {
            Arity = ArgumentArity.ZeroOrOne,
        };
        var promptFileOpt = new Option<string?>("--prompt-file", "Path to prompt file, or '-' for stdin");
        var quietOpt = new Option<bool>("--quiet", "Print only the new prompt revision");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");

        cmd.AddArgument(idArg);
        cmd.AddArgument(textArg);
        cmd.AddOption(promptFileOpt);
        cmd.AddOption(quietOpt);
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();

            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var promptText = ctx.ParseResult.GetValueForArgument(textArg);
            var promptFile = ctx.ParseResult.GetValueForOption(promptFileOpt);
            var quiet = ctx.ParseResult.GetValueForOption(quietOpt);
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);
            var flagUrl = ctx.ParseResult.GetValueForOption(apiUrlOpt);
            var flagKey = ctx.ParseResult.GetValueForOption(apiKeyOpt);

            string? prompt;
            if (promptText is not null && promptFile is not null)
            {
                await Console.Error.WriteLineAsync("Error: provide either prompt text or --prompt-file, not both.");
                ctx.ExitCode = 1;
                return;
            }

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
                    await Console.Error.WriteLineAsync(
                        $"Error: prompt exceeds {PromptLimitLabel} limit. Use a smaller prompt.");
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
                var cappedText = await ReadCappedAsync(Console.In, MaxPromptLength, ct);
                if (cappedText is null)
                {
                    await Console.Error.WriteLineAsync(
                        $"Error: prompt exceeds {PromptLimitLabel} limit. Use a smaller prompt.");
                    ctx.ExitCode = 1;
                    return;
                }

                prompt = cappedText;
            }

            if (string.IsNullOrEmpty(prompt))
            {
                await Console.Error.WriteLineAsync("Error: prompt is required. Provide text, --prompt-file, or pipe stdin.");
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

            try
            {
                var raw = await client.PutPromptAsync(id, prompt, ct);

                if (json)
                {
                    Console.WriteLine(raw);
                    return;
                }

                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (!TryGetInt32(root, "promptRevision", out var revision))
                {
                    await Console.Error.WriteLineAsync("Error: response missing numeric promptRevision.");
                    ctx.ExitCode = 1;
                    return;
                }

                if (quiet)
                {
                    Console.WriteLine(revision);
                }
                else
                {
                    Console.WriteLine($"Updated prompt for work item {id} - new prompt revision: {revision}");
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

    private static bool TryGetInt32(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out value);
    }

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
