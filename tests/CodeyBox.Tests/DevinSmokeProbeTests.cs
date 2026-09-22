using CodeyBox.Agents.Devin;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class DevinSmokeProbeTests
{
    private static AgentCredential Cred(Dictionary<string, string> env) =>
        new(AgentKind.Devin, env, new Dictionary<string, string>());

    [Fact]
    public async Task SmokeTest_NoCredentialMaterial_ReturnsFail()
    {
        var probe = new DevinSmokeProbe();
        var cred = Cred(new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("no credentials in credential bundle", result.FailureReason);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
    }

    [Fact]
    public async Task SmokeTest_AuthTomlWithApiKey_ReturnsOk()
    {
        var probe = new DevinSmokeProbe();
        var cred = Cred(new Dictionary<string, string>
        {
            [DevinAgentRunner.AuthTomlEnvironmentVariable] =
                "api_key = \"sk-test\"\napi_server_url = \"https://u.example\"\n",
        });

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task SmokeTest_AuthTomlWithWindsurfKeyOnly_ReturnsOk()
    {
        // Live-verified: current `devin auth login` writes only
        // windsurf_api_key (a devin-session-token$… bearer) — no api_key.
        var probe = new DevinSmokeProbe();
        var cred = Cred(new Dictionary<string, string>
        {
            [DevinAgentRunner.AuthTomlEnvironmentVariable] =
                "windsurf_api_key = \"devin-session-token$abc\"\napi_server_url = \"https://u.example\"\n",
        });

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task SmokeTest_TomlWithoutApiKey_ReturnsFail()
    {
        // A credentials file missing any token field cannot authenticate;
        // catch it here rather than as a per-item dispatch failure.
        var probe = new DevinSmokeProbe();
        var cred = Cred(new Dictionary<string, string>
        {
            [DevinAgentRunner.AuthTomlEnvironmentVariable] =
                "api_server_url = \"https://u.example\"\n",
        });

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("credentials.toml has no api key", result.FailureReason);
    }

    [Fact]
    public async Task SmokeTest_EmptyValueTreatedAsAbsent()
    {
        var probe = new DevinSmokeProbe();
        var cred = Cred(new Dictionary<string, string>
        {
            [DevinAgentRunner.AuthTomlEnvironmentVariable] = string.Empty,
        });

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task SmokeTest_UnrelatedVariableOnly_ReturnsFail()
    {
        // There is intentionally no DEVIN_API_KEY-style env var — a bundle
        // carrying only an unrelated variable must NOT pass.
        var probe = new DevinSmokeProbe();
        var cred = Cred(new Dictionary<string, string>
        {
            ["DEVIN_API_KEY"] = "sk-test",
        });

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public void Kind_IsDevin()
    {
        Assert.Equal(AgentKind.Devin, new DevinSmokeProbe().Kind);
    }
}
