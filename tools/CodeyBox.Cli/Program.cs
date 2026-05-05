using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Runtime.CompilerServices;
using CodeyBox.Cli;

[assembly: InternalsVisibleTo("CodeyBox.Cli.Tests")]

var parser = new CommandLineBuilder(CliApp.BuildRootCommand())
    .UseDefaults()
    .Build();

return await parser.InvokeAsync(args);
