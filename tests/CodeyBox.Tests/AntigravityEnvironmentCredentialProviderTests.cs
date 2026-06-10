using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the host-env credential path for Antigravity: the agy OAuth token
/// bundle is shipped <em>verbatim</em> (refresh_token retained) into the sandbox
/// env var, because the in-VM agy must self-refresh its short-lived access_token
/// and the host authenticates from the keyring (a separate store).
/// </summary>
public sealed class AntigravityEnvironmentCredentialProviderTests : IDisposable
{
    private readonly string? _priorEnv;

    public AntigravityEnvironmentCredentialProviderTests()
    {
        _priorEnv = Environment.GetEnvironmentVariable(
            AntigravityEnvironmentCredentialProvider.HostEnvironmentVariable);
        Environment.SetEnvironmentVariable(
            AntigravityEnvironmentCredentialProvider.HostEnvironmentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            AntigravityEnvironmentCredentialProvider.HostEnvironmentVariable, _priorEnv);
    }

    [Fact]
    public async Task GetAsync_NoEnvVar_ReturnsNull()
    {
        var provider = new AntigravityEnvironmentCredentialProvider();

        var credential = await provider.GetAsync(AgentKind.Antigravity, CancellationToken.None);

        Assert.Null(credential);
    }

    [Fact]
    public async Task GetAsync_OtherAgentKind_ReturnsNull()
    {
        Environment.SetEnvironmentVariable(
            AntigravityEnvironmentCredentialProvider.HostEnvironmentVariable,
            """{"auth_method":"consumer","token":{"access_token":"ya29.x"}}""");
        var provider = new AntigravityEnvironmentCredentialProvider();

        var credential = await provider.GetAsync(AgentKind.Gemini, CancellationToken.None);

        Assert.Null(credential);
    }

    [Fact]
    public async Task GetAsync_ShipsAgyTokenBundleVerbatimWithRefreshToken()
    {
        const string raw =
            """{"auth_method":"consumer","token":{"access_token":"ya29.live","token_type":"Bearer","refresh_token":"rt-in-vm","expiry":"2026-06-10T19:57:49+10:00"}}""";
        Environment.SetEnvironmentVariable(
            AntigravityEnvironmentCredentialProvider.HostEnvironmentVariable, raw);
        var provider = new AntigravityEnvironmentCredentialProvider();

        var credential = await provider.GetAsync(AgentKind.Antigravity, CancellationToken.None);

        Assert.NotNull(credential);
        Assert.Equal(AgentKind.Antigravity, credential!.Agent);
        Assert.True(credential.EnvironmentVariables.TryGetValue(
            AntigravityConstants.OAuthCredsEnvVar, out var sandboxBundle));
        // Verbatim — agy's fileTokenStorage reads this exact envelope and needs
        // the refresh_token to refresh the access_token in-VM.
        Assert.Equal(raw, sandboxBundle);
        Assert.Contains("rt-in-vm", sandboxBundle);
    }

    [Fact]
    public async Task GetAsync_MalformedJson_ReturnsNull()
    {
        // Avoids shipping an env var whose value the agy CLI can't parse;
        // upstream provider chain falls through to other providers (or none).
        Environment.SetEnvironmentVariable(
            AntigravityEnvironmentCredentialProvider.HostEnvironmentVariable,
            "not-json");
        var provider = new AntigravityEnvironmentCredentialProvider();

        var credential = await provider.GetAsync(AgentKind.Antigravity, CancellationToken.None);

        Assert.Null(credential);
    }
}
