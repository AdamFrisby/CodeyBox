using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the host-env credential path for Antigravity: the
/// <c>refresh_token</c> field MUST be stripped from the OAuth JSON before it
/// crosses into the sandbox env var (the host CLI is the sole party allowed
/// to refresh — matches the Claude isolation invariant; the documented
/// guarantee in <c>docs/agents.md</c>).
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
            """{"access_token":"ya29.x"}""");
        var provider = new AntigravityEnvironmentCredentialProvider();

        var credential = await provider.GetAsync(AgentKind.Gemini, CancellationToken.None);

        Assert.Null(credential);
    }

    [Fact]
    public async Task GetAsync_StripsRefreshTokenFromBundle()
    {
        Environment.SetEnvironmentVariable(
            AntigravityEnvironmentCredentialProvider.HostEnvironmentVariable,
            """{"access_token":"ya29.live","refresh_token":"rt-host-only","expiry_date":1900000000000}""");
        var provider = new AntigravityEnvironmentCredentialProvider();

        var credential = await provider.GetAsync(AgentKind.Antigravity, CancellationToken.None);

        Assert.NotNull(credential);
        Assert.Equal(AgentKind.Antigravity, credential!.Agent);
        Assert.True(credential.EnvironmentVariables.TryGetValue(
            AntigravityConstants.OAuthCredsEnvVar, out var sandboxBundle));
        Assert.Contains("\"access_token\":\"ya29.live\"", sandboxBundle);
        Assert.DoesNotContain("refresh_token", sandboxBundle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rt-host-only", sandboxBundle);
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
