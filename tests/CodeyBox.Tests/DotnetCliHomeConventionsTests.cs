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
    public void ApplyIfDotnetInvocation_PinsHomeToCliHome()
    {
        // Regression: the shell build/test gates (csharp:build-WaE,
        // csharp:test-pass) only pinned DOTNET_CLI_HOME, so on NuGet builds that
        // derive ~/.nuget from $HOME restore aborted reading a root-owned
        // ~/.nuget. HOME must be pinned to the same repo-local CLI home.
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        DotnetCliHomeConventions.ApplyIfDotnetInvocation(
            ["dotnet", "test", "--no-build"],
            "/work",
            env);

        Assert.Equal("/work/.dotnet-cli-home", env["DOTNET_CLI_HOME"]);
        Assert.Equal("/work/.dotnet-cli-home", env["HOME"]);
        // Runtime 10.0.13+ user-scoped named mutexes resolve under
        // XDG_RUNTIME_DIR before the $HOME fallback, so it is pinned too.
        Assert.Equal("/work/.dotnet-cli-home", env["XDG_RUNTIME_DIR"]);
    }

    [Fact]
    public void ApplyIfDotnetInvocation_RespectsExistingXdgRuntimeDir()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["XDG_RUNTIME_DIR"] = "/caller/run",
        };

        DotnetCliHomeConventions.ApplyIfDotnetInvocation(["dotnet", "build"], "/work", env);

        Assert.Equal("/caller/run", env["XDG_RUNTIME_DIR"]);
    }

    [Fact]
    public void ApplyIfDotnetInvocation_PinsHomeToPresetCliHome()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_HOME"] = "/custom/home",
        };

        DotnetCliHomeConventions.ApplyIfDotnetInvocation(["dotnet", "build"], "/work", env);

        Assert.Equal("/custom/home", env["DOTNET_CLI_HOME"]);
        Assert.Equal("/custom/home", env["HOME"]);
    }

    [Fact]
    public void ApplyIfDotnetInvocation_RespectsExistingHome()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = "/caller/home",
        };

        DotnetCliHomeConventions.ApplyIfDotnetInvocation(["dotnet", "build"], "/work", env);

        Assert.Equal("/work/.dotnet-cli-home", env["DOTNET_CLI_HOME"]);
        Assert.Equal("/caller/home", env["HOME"]);
    }

    [Fact]
    public void ApplyIfDotnetInvocation_SkipsNonDotnetArgv()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        DotnetCliHomeConventions.ApplyIfDotnetInvocation(["sh", "-c", "dotnet build"], "/work", env);

        Assert.False(env.ContainsKey("DOTNET_CLI_HOME"));
        Assert.False(env.ContainsKey("HOME"));
    }
}
