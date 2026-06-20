using System.CommandLine;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueuePromote
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        return QueueWorkItemVerbCommand.Build(
            "promote",
            "Promote a work item",
            "Promoted",
            apiUrlOpt,
            apiKeyOpt,
            clientFactory);
    }
}
