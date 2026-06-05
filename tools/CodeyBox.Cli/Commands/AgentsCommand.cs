using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.Text.Json;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class AgentsCommand
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var cmd = new Command("agents", "Manage agent runtime controls");
        cmd.AddCommand(BuildPause(apiUrlOpt, apiKeyOpt, clientFactory));
        cmd.AddCommand(BuildResume(apiUrlOpt, apiKeyOpt, clientFactory));
        cmd.AddCommand(BuildPaused(apiUrlOpt, apiKeyOpt, clientFactory));
        return cmd;
    }

    private static Command BuildPause(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var kindArg = new Argument<string>("kind", "Agent kind to pause");
        var reasonOpt = new Option<string>("--reason", "Reason for pausing the agent")
        {
            IsRequired = true,
        };
        var forOpt = new Option<string?>("--for", "Optional duration such as 30m, 6h, or 2d");

        var cmd = new Command("pause", "Pause one agent for new dispatch");
        cmd.AddArgument(kindArg);
        cmd.AddOption(reasonOpt);
        cmd.AddOption(forOpt);
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();
            var kind = ctx.ParseResult.GetValueForArgument(kindArg);
            var reason = ctx.ParseResult.GetValueForOption(reasonOpt);
            var duration = ctx.ParseResult.GetValueForOption(forOpt);
            if (!TryResolveClient(ctx, apiUrlOpt, apiKeyOpt, clientFactory, out var client))
                return;

            double? seconds = null;
            if (!string.IsNullOrWhiteSpace(duration))
            {
                if (!TryParseDuration(duration!, out var parsed))
                {
                    await Console.Error.WriteLineAsync("Error: --for must be a positive duration such as 30m, 6h, 2d, or 01:30:00.");
                    ctx.ExitCode = 1;
                    return;
                }
                seconds = parsed.TotalSeconds;
            }

            try
            {
                await client.PauseAgentAsync(kind, reason!, seconds, ct);
                Console.WriteLine(seconds is null
                    ? $"Agent {kind} paused (reason: {reason})"
                    : $"Agent {kind} paused for {duration} (reason: {reason})");
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

    private static Command BuildResume(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var kindArg = new Argument<string>("kind", "Agent kind to resume");
        var cmd = new Command("resume", "Resume one paused agent");
        cmd.AddArgument(kindArg);
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();
            var kind = ctx.ParseResult.GetValueForArgument(kindArg);
            if (!TryResolveClient(ctx, apiUrlOpt, apiKeyOpt, clientFactory, out var client))
                return;

            try
            {
                await client.ResumeAgentAsync(kind, ct);
                Console.WriteLine($"Agent {kind} resumed");
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

    private static Command BuildPaused(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");
        var cmd = new Command("paused", "List paused agents");
        cmd.AddOption(jsonOpt);
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);
            if (!TryResolveClient(ctx, apiUrlOpt, apiKeyOpt, clientFactory, out var client))
                return;

            try
            {
                var raw = await client.GetPausedAgentsAsync(ct);
                if (json)
                {
                    Console.WriteLine(raw);
                    return;
                }

                using var doc = JsonDocument.Parse(raw);
                var rows = doc.RootElement.EnumerateArray().ToList();
                if (rows.Count == 0)
                {
                    Console.WriteLine("No agents paused");
                    return;
                }

                foreach (var row in rows)
                {
                    var agent = row.GetProperty("agent").GetString();
                    var display = row.TryGetProperty("agentInstanceId", out var instanceEl)
                        && instanceEl.ValueKind != JsonValueKind.Null
                        && !string.IsNullOrWhiteSpace(instanceEl.GetString())
                        ? instanceEl.GetString()
                        : agent;
                    var reason = row.TryGetProperty("pausedReason", out var reasonEl)
                        && reasonEl.ValueKind != JsonValueKind.Null
                        ? reasonEl.GetString()
                        : "";
                    var expires = row.TryGetProperty("expiresAt", out var expiresEl)
                        && expiresEl.ValueKind != JsonValueKind.Null
                        ? $" expires={expiresEl.GetString()}"
                        : "";
                    Console.WriteLine($"{display}: {reason}{expires}");
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

    private static bool TryResolveClient(
        InvocationContext ctx,
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory,
        out CodeyBoxClient client)
    {
        var flagUrl = ctx.ParseResult.GetValueForOption(apiUrlOpt);
        var flagKey = ctx.ParseResult.GetValueForOption(apiKeyOpt);
        var config = ConfigResolver.Resolve(flagUrl, flagKey);
        if (!config.HasApiKey)
        {
            Console.Error.WriteLine(
                "Error: API key not configured. Run 'codeybox configure' or set CODEYBOX_CLI_API_KEY.");
            ctx.ExitCode = 1;
            client = null!;
            return false;
        }

        client = clientFactory(config);
        return true;
    }

    private static bool TryParseDuration(string raw, out TimeSpan duration)
    {
        var value = raw.Trim();
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration) && duration > TimeSpan.Zero)
            return true;

        duration = TimeSpan.Zero;
        if (value.Length < 2)
            return false;

        var suffix = value[^1];
        if (!double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
            || amount <= 0)
        {
            return false;
        }

        duration = suffix switch
        {
            's' or 'S' => TimeSpan.FromSeconds(amount),
            'm' or 'M' => TimeSpan.FromMinutes(amount),
            'h' or 'H' => TimeSpan.FromHours(amount),
            'd' or 'D' => TimeSpan.FromDays(amount),
            _ => TimeSpan.Zero,
        };
        return duration > TimeSpan.Zero;
    }
}
