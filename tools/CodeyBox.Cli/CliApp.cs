using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using CodeyBox.Cli.Commands;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli;

internal static class CliApp
{
    internal const string CliVersion = "1.0.0";

    internal static Task<int> InvokeAsync(
        string[] args,
        Func<ResolvedConfig, CodeyBoxClient>? clientFactory = null,
        CancellationToken cancellationToken = default)
    {
        args = RewriteTemplateShortcut(args);
        var parser = new CommandLineBuilder(BuildRootCommand(clientFactory, cancellationToken))
            .UseDefaults()
            .Build();

        return parser.InvokeAsync(args);
    }

    internal static RootCommand BuildRootCommand(
        Func<ResolvedConfig, CodeyBoxClient>? clientFactory = null,
        CancellationToken externalCancellation = default)
    {
        clientFactory ??= CodeyBoxClient.Create;

        var apiUrlOpt = new Option<string?>("--api-url", "Override the orchestrator API base URL");
        var apiKeyOpt = new Option<string?>("--api-key",
            "Override the API bearer token (visible in process list on Linux; prefer CODEYBOX_CLI_API_KEY env var in scripts)");

        var root = new RootCommand("CodeyBox CLI — interact with the CodeyBox orchestrator REST API");
        root.AddGlobalOption(apiUrlOpt);
        root.AddGlobalOption(apiKeyOpt);

        var queueCmd = new Command("queue", "Manage work items in the queue");
        queueCmd.AddCommand(QueueAdd.Build(apiUrlOpt, apiKeyOpt, clientFactory));
        queueCmd.AddCommand(QueueList.Build(apiUrlOpt, apiKeyOpt, clientFactory));
        queueCmd.AddCommand(QueueShow.Build(apiUrlOpt, apiKeyOpt, clientFactory));
        queueCmd.AddCommand(QueueCancel.Build(apiUrlOpt, apiKeyOpt, clientFactory));
        queueCmd.AddCommand(QueueRetry.Build(apiUrlOpt, apiKeyOpt, clientFactory));
        queueCmd.AddCommand(QueueWatch.Build(apiUrlOpt, apiKeyOpt, clientFactory, externalCancellation));
        queueCmd.AddCommand(QueuePause.Build(apiUrlOpt, apiKeyOpt, clientFactory));
        queueCmd.AddCommand(QueueResume.Build(apiUrlOpt, apiKeyOpt, clientFactory));
        queueCmd.AddCommand(QueueStatus.Build(apiUrlOpt, apiKeyOpt, clientFactory));
        queueCmd.AddCommand(QueueReorder.Build(apiUrlOpt, apiKeyOpt, clientFactory));
        queueCmd.AddCommand(QueueTemplate.Build(apiUrlOpt, apiKeyOpt, clientFactory));

        root.AddCommand(queueCmd);
        root.AddCommand(WorkersCommand.Build(apiUrlOpt, apiKeyOpt, clientFactory));
        root.AddCommand(QuotaCommand.Build(apiUrlOpt, apiKeyOpt, clientFactory));
        root.AddCommand(ConcurrencyCommand.Build(apiUrlOpt, apiKeyOpt, clientFactory));
        root.AddCommand(FleetCommand.Build(apiUrlOpt, apiKeyOpt, clientFactory));
        root.AddCommand(AgentsCommand.Build(apiUrlOpt, apiKeyOpt, clientFactory));
        root.AddCommand(ConfigureCommand.Build());
        root.AddCommand(VersionCommand.Build());

        return root;
    }

    private static string[] RewriteTemplateShortcut(string[] args)
    {
        if (args.Length < 2) return args;
        if (!string.Equals(args[0], "queue", StringComparison.OrdinalIgnoreCase)) return args;
        var templateRef = args[1];
        if (!templateRef.StartsWith("templates/", StringComparison.OrdinalIgnoreCase)
            && !templateRef.StartsWith("templates\\", StringComparison.OrdinalIgnoreCase))
        {
            return args;
        }

        var rewritten = new string[args.Length + 1];
        rewritten[0] = args[0];
        rewritten[1] = "template";
        Array.Copy(args, 1, rewritten, 2, args.Length - 1);
        return rewritten;
    }
}
