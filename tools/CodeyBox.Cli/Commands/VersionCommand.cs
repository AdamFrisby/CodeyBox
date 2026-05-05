using System.CommandLine;
using System.CommandLine.Invocation;

namespace CodeyBox.Cli.Commands;

internal static class VersionCommand
{
    internal static Command Build()
    {
        var cmd = new Command("version", "Print the CLI version");
        cmd.SetHandler((InvocationContext _) =>
        {
            Console.WriteLine(CliApp.CliVersion);
        });
        return cmd;
    }
}
