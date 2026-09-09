using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Covers <see cref="EnvironmentCredentialProvider"/>'s multi-mapping behaviour: an agent whose CLI
/// reads more than one credential from the environment (GitHub Copilot takes a GitHub token for
/// subscription mode and a separate provider key under BYOK) must receive all of the ones that are set.
/// </summary>
public sealed class EnvironmentCredentialProviderTests : IDisposable
{
    private static readonly AgentKind Agent = AgentKind.Copilot;
    private const string HostTokenVar = "CODEYBOX_TEST_CRED_TOKEN";
    private const string HostKeyVar = "CODEYBOX_TEST_CRED_KEY";

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HostTokenVar, null);
        Environment.SetEnvironmentVariable(HostKeyVar, null);
    }

    private static EnvironmentCredentialProvider Provider() => new(
    [
        new AgentCredentialMapping(Agent, HostTokenVar, "GH_TOKEN"),
        new AgentCredentialMapping(Agent, HostKeyVar, "COPILOT_PROVIDER_API_KEY"),
    ]);

    [Fact]
    public async Task GetAsync_MergesEveryPopulatedMappingForTheAgent()
    {
        Environment.SetEnvironmentVariable(HostTokenVar, "gh-token-value");
        Environment.SetEnvironmentVariable(HostKeyVar, "byok-key-value");

        var credential = await Provider().GetAsync(Agent);

        Assert.NotNull(credential);
        Assert.Equal("gh-token-value", credential!.EnvironmentVariables["GH_TOKEN"]);
        Assert.Equal("byok-key-value", credential.EnvironmentVariables["COPILOT_PROVIDER_API_KEY"]);
    }

    [Fact]
    public async Task GetAsync_OmitsMappingsWhoseHostVariableIsUnset()
    {
        // A BYOK key is optional — local servers often need none — so its absence must not suppress the
        // credential that IS present.
        Environment.SetEnvironmentVariable(HostTokenVar, "gh-token-value");
        Environment.SetEnvironmentVariable(HostKeyVar, null);

        var credential = await Provider().GetAsync(Agent);

        Assert.NotNull(credential);
        Assert.Equal("gh-token-value", credential!.EnvironmentVariables["GH_TOKEN"]);
        Assert.DoesNotContain("COPILOT_PROVIDER_API_KEY", credential.EnvironmentVariables.Keys);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullWhenNoMappingIsPopulated()
    {
        Environment.SetEnvironmentVariable(HostTokenVar, null);
        Environment.SetEnvironmentVariable(HostKeyVar, null);

        Assert.Null(await Provider().GetAsync(Agent));
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForAnUnmappedAgent()
    {
        Environment.SetEnvironmentVariable(HostTokenVar, "gh-token-value");

        Assert.Null(await Provider().GetAsync(AgentKind.Codex));
    }

    [Fact]
    public void Constructor_RejectsTwoMappingsTargetingTheSameSandboxVariable()
    {
        // Ambiguous wiring, not a fallback: silently letting one win would make which credential the
        // agent receives depend on declaration order.
        var ex = Assert.Throws<ArgumentException>(() => new EnvironmentCredentialProvider(
        [
            new AgentCredentialMapping(Agent, HostTokenVar, "GH_TOKEN"),
            new AgentCredentialMapping(Agent, HostKeyVar, "GH_TOKEN"),
        ]));

        Assert.Contains("GH_TOKEN", ex.Message);
    }

    [Fact]
    public void Constructor_AllowsTheSameSandboxVariableForDifferentAgents()
    {
        var provider = new EnvironmentCredentialProvider(
        [
            new AgentCredentialMapping(AgentKind.Copilot, HostTokenVar, "GH_TOKEN"),
            new AgentCredentialMapping(AgentKind.Codex, HostKeyVar, "GH_TOKEN"),
        ]);

        Assert.NotNull(provider);
    }
}
