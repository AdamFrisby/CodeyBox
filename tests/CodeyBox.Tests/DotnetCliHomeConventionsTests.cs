using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class DotnetCliHomeConventionsTests
{
    [Fact]
    public void ApplyIfAbsent_SetsRepoLocalPathWhenUnset()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        DotnetCliHomeConventions.ApplyIfAbsent(env, "/work");

        Assert.Equal("/work/.dotnet-cli-home", env["DOTNET_CLI_HOME"]);
    }

    [Fact]
    public void ApplyIfAbsent_DoesNotOverrideExistingValue()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_HOME"] = "/custom/home",
        };

        DotnetCliHomeConventions.ApplyIfAbsent(env, "/work");

        Assert.Equal("/custom/home", env["DOTNET_CLI_HOME"]);
    }

    [Fact]
    public void ApplyIfDotnetInvocation_SetsForDotnetArgv()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        DotnetCliHomeConventions.ApplyIfDotnetInvocation(
            ["dotnet", "build", "--no-incremental", "/warnaserror"],
            "/work",
            env);

        Assert.Equal("/work/.dotnet-cli-home", env["DOTNET_CLI_HOME"]);
    }

    [Fact]
    public void ApplyIfDotnetInvocation_SkipsNonDotnetArgv()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        DotnetCliHomeConventions.ApplyIfDotnetInvocation(["sh", "-c", "dotnet build"], "/work", env);

        Assert.False(env.ContainsKey("DOTNET_CLI_HOME"));
    }
}
