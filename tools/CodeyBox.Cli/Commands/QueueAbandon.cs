using System.CommandLine;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueAbandon
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        return QueueWorkItemVerbCommand.Build(
            "abandon",
            "Abandon a work item",
            "Abandoned",
            apiUrlOpt,
            apiKeyOpt,
            clientFactory);
    }
}
