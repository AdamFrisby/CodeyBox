using System.CommandLine;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueUncancel
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        return QueueWorkItemVerbCommand.Build(
            "uncancel",
            "Uncancel a work item",
            "Uncancelled",
            apiUrlOpt,
            apiKeyOpt,
            clientFactory);
    }
}
