using CodeyBox.Agents.Goose;
using CodeyBox.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="GooseSmokeProbe"/>: the credential-presence gate.
/// Goose fronts 30+ providers behind one CLI, so the probe performs no
/// network call and no quota spend — viability beyond presence is proven by
/// the first real dispatch.
/// </summary>
public sealed class GooseSmokeProbeTests
{
    private static AgentCredential Cred(params (string Key, string Value)[] entries)
    {
        var env = new Dictionary<string, string>();
        foreach (var (key, value) in entries) env[key] = value;
        return new(AgentKind.Goose, env, new Dictionary<string, string>());
    }

    private static GooseSmokeProbe Probe() => new(NullLogger<GooseSmokeProbe>.Instance);

    [Fact]
    public void Kind_IsGoose()
    {
        Assert.Equal(AgentKind.Goose, Probe().Kind);
    }

    [Fact]
    public async Task WithOpenRouterKey_ReturnsOk()
    {
        var result = await Probe().SmokeTestAsync(
            Cred((GooseSmokeProbe.PrimaryCredentialVariable, "test-key")), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(SmokeFailureCategory.None, result.Category);
    }

    [Fact]
    public async Task MissingKey_ReturnsPersistentFailure()
    {
        var result = await Probe().SmokeTestAsync(
            Cred(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task EmptyKey_ReturnsPersistentFailure()
    {
        var result = await Probe().SmokeTestAsync(
            Cred((GooseSmokeProbe.PrimaryCredentialVariable, string.Empty)), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
    }

    [Fact]
    public void ProviderApiKeyList_ContainsShippedMapping()
    {
        // The probe's documented provider list must include the variable the
        // shipped credential mapping populates.
        Assert.Contains(
            GooseSmokeProbe.PrimaryCredentialVariable,
            GooseSmokeProbe.ProviderApiKeyEnvironmentVariables);
        Assert.Equal(13, GooseSmokeProbe.ProviderApiKeyEnvironmentVariables.Count);
    }
}
