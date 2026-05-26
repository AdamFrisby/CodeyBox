using CodeyBox.Agents.Opencode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class OpencodeSmokeProbeTests
{
    [Fact]
    public async Task SmokeTest_NoCredentialMaterial_ReturnsFail()
    {
        var probe = new OpencodeSmokeProbe();
        var cred = new AgentCredential(AgentKind.Opencode,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("no token in credential bundle", result.FailureReason);
    }

    [Fact]
    public async Task SmokeTest_AuthJsonPresent_ReturnsOk()
    {
        var probe = new OpencodeSmokeProbe();
        var cred = new AgentCredential(AgentKind.Opencode,
            new Dictionary<string, string> { ["OPENCODE_AUTH_JSON"] = "{\"key\":\"value\"}" },
            new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task SmokeTest_ApiKeyPresent_ReturnsOk()
    {
        var probe = new OpencodeSmokeProbe();
        var cred = new AgentCredential(AgentKind.Opencode,
            new Dictionary<string, string> { ["OPENCODE_API_KEY"] = "sk-opencode-test" },
            new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task SmokeTest_EmptyValueTreatedAsAbsent()
    {
        var probe = new OpencodeSmokeProbe();
        var cred = new AgentCredential(AgentKind.Opencode,
            new Dictionary<string, string>
            {
                ["OPENCODE_AUTH_JSON"] = string.Empty,
                ["OPENCODE_API_KEY"] = string.Empty,
            },
            new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public void Kind_IsOpencode()
    {
        Assert.Equal(AgentKind.Opencode, new OpencodeSmokeProbe().Kind);
    }
}
