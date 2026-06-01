using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class JsonEndpointCommand
{
    internal static async Task RunAsync(
        InvocationContext ctx,
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Option<bool> jsonOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory,
        Func<CodeyBoxClient, CancellationToken, Task<string>> fetchRaw,
        Action<JsonElement> render)
    {
        var ct = ctx.GetCancellationToken();

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
            var raw = await fetchRaw(client, ct);
            if (json)
            {
                Console.WriteLine(raw);
                return;
            }

            using var doc = JsonDocument.Parse(raw);
            render(doc.RootElement);
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
    }
}
