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

        root.AddCommand(queueCmd);
        root.AddCommand(ConfigureCommand.Build());
        root.AddCommand(VersionCommand.Build());

        return root;
    }
}
