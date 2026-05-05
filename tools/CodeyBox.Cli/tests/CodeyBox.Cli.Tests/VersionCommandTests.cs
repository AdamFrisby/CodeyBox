using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class VersionCommandTests
{
    [Fact]
    public async Task Version_PrintsCliVersion()
    {
        using var output = new TestOutput();
        var code = await CliApp.InvokeAsync(["version"]);
        Assert.Equal(0, code);
        Assert.Contains(CliApp.CliVersion, output.Out.ToString());
        Assert.Empty(output.Error.ToString());
    }
}
