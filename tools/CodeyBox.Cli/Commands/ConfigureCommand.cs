using System.CommandLine;
using System.CommandLine.Invocation;

namespace CodeyBox.Cli.Commands;

internal static class ConfigureCommand
{
    internal static Command Build()
    {
        var cmd = new Command("configure", "Set API base URL and key, saved to ~/.config/codeybox/config.json");
        cmd.SetHandler(RunAsync);
        return cmd;
    }

    private static async Task RunAsync(InvocationContext ctx)
    {
        var ct = ctx.GetCancellationToken();

        var existing = ConfigResolver.LoadConfigFile();

        Console.Write($"API base URL [{existing?.ApiBaseUrl ?? ResolvedConfig.DefaultApiBaseUrl}]: ");
        var urlInput = await ReadLineAsync();
        var url = string.IsNullOrWhiteSpace(urlInput)
            ? (existing?.ApiBaseUrl ?? ResolvedConfig.DefaultApiBaseUrl)
            : urlInput.Trim();

        Console.Write("API key (input hidden): ");
        var key = ReadPassword();
        if (string.IsNullOrWhiteSpace(key))
            key = existing?.ApiKey;

        if (string.IsNullOrEmpty(key))
        {
            await Console.Error.WriteLineAsync("Error: API key is required.");
            ctx.ExitCode = 1;
            return;
        }

        var config = new CliConfig { ApiBaseUrl = url, ApiKey = key };
        ConfigResolver.SaveConfigFile(config);

        Console.WriteLine($"Configuration saved to {ConfigResolver.ConfigFilePath}");
    }

    private static Task<string?> ReadLineAsync() => Task.Run(Console.ReadLine);

    private static string? ReadPassword()
    {
        // When stdin is redirected (tests / piped input) just read a line normally.
        if (Console.IsInputRedirected)
            return Console.ReadLine();

        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Remove(sb.Length - 1, 1);
            }
            else if (key.KeyChar != '\0')
            {
                sb.Append(key.KeyChar);
            }
        }
        Console.WriteLine();
        return sb.Length > 0 ? sb.ToString() : null;
    }
}
