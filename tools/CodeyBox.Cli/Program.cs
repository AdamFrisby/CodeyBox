using System.Runtime.CompilerServices;
using CodeyBox.Cli;

[assembly: InternalsVisibleTo("CodeyBox.Cli.Tests")]

return await CliApp.InvokeAsync(args);
