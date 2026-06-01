using System.CommandLine;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

internal static class QueueReplay
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        return QueueWorkItemVerbCommand.Build(
            "replay",
            "Replay a work item",
            "Replayed",
            apiUrlOpt,
            apiKeyOpt,
            clientFactory);
    }
}
