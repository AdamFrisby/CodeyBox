using CodeyBox.Agents.CavemanCode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class CavemanCodeSmokeProbeTests
{
    [Fact]
    public async Task SmokeTest_NoKeys_ReturnsFail()
    {
        var probe = new CavemanCodeSmokeProbe();
        var cred = new AgentCredential(AgentKind.CavemanCode,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("no provider API key in credential bundle", result.FailureReason);
    }

    [Fact]
    public async Task SmokeTest_EmptyKeyValue_ReturnsFail()
    {
        var probe = new CavemanCodeSmokeProbe();
        var cred = new AgentCredential(AgentKind.CavemanCode,
            new Dictionary<string, string> { ["OPENAI_API_KEY"] = "" },
            new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("GEMINI_API_KEY")]
    [InlineData("OPENROUTER_API_KEY")]
    public async Task SmokeTest_AnyProviderKey_ReturnsOk(string variable)
    {
        var probe = new CavemanCodeSmokeProbe();
        var cred = new AgentCredential(AgentKind.CavemanCode,
            new Dictionary<string, string> { [variable] = "test-key" },
            new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.FailureReason);
    }
}

public sealed class CavemanCodeInVmSmokeProbeTests
{
    private readonly CavemanCodeInVmSmokeProbe _probe = new();

    [Fact]
    public void Kind_IsCavemanCode()
    {
        Assert.Equal(AgentKind.CavemanCode, _probe.Kind);
    }

    [Fact]
    public void BuildSteps_NoCredential_ReturnsVersionCheckOnly()
    {
        var steps = _probe.BuildSteps(credential: null);

        var argv = Assert.Single(steps).Argv;
        Assert.Equal(["caveman-code", "--version"], argv);
    }

    [Fact]
    public void BuildSteps_KeylessCredential_ReturnsVersionCheckOnly()
    {
        var cred = new AgentCredential(AgentKind.CavemanCode,
            new Dictionary<string, string> { ["SOME_OTHER_VAR"] = "x" },
            new Dictionary<string, string>());

        var steps = _probe.BuildSteps(cred);

        Assert.Single(steps);
    }

    [Fact]
    public void BuildSteps_WithProviderKey_AddsListModelsStep()
    {
        var cred = new AgentCredential(AgentKind.CavemanCode,
            new Dictionary<string, string> { ["OPENAI_API_KEY"] = "sk-test" },
            new Dictionary<string, string>());

        var steps = _probe.BuildSteps(cred);

        Assert.Equal(2, steps.Count);
        Assert.Equal(["caveman-code", "--version"], steps[0].Argv);
        Assert.Equal(["caveman-code", "--list-models"], steps[1].Argv);
    }
}
