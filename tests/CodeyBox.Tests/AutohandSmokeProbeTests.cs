using CodeyBox.Agents.Autohand;
using CodeyBox.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AutohandSmokeProbe"/>: the credential-presence gate.
/// Autohand is a multi-provider front with no single lightweight "whoami",
/// so the probe performs no network call and no quota spend — viability
/// beyond presence is proven by the first real dispatch.
/// </summary>
public sealed class AutohandSmokeProbeTests
{
    private static AgentCredential Cred(params (string Key, string Value)[] entries)
    {
        var env = new Dictionary<string, string>();
        foreach (var (key, value) in entries) env[key] = value;
        return new(AgentKind.Autohand, env, new Dictionary<string, string>());
    }

    private static AutohandSmokeProbe Probe() => new(NullLogger<AutohandSmokeProbe>.Instance);

    [Fact]
    public void Kind_IsAutohand()
    {
        Assert.Equal(AgentKind.Autohand, Probe().Kind);
    }

    [Fact]
    public async Task WithBareGateKey_ReturnsOk()
    {
        var result = await Probe().SmokeTestAsync(
            Cred((AutohandSmokeProbe.PrimaryCredentialVariable, "test-autohand-key-material")), CancellationToken.None);

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
            Cred((AutohandSmokeProbe.PrimaryCredentialVariable, string.Empty)), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
    }

    [Fact]
    public void PrimaryCredentialVariable_IsBareGateKey()
    {
        // The probe must require the variable the shipped credential mapping
        // populates (CODEYBOX_AUTOHAND_API_KEY -> AUTOHAND_API_KEY): it gates
        // bare mode AND seeds the guest config.
        Assert.Equal("AUTOHAND_API_KEY", AutohandSmokeProbe.PrimaryCredentialVariable);
    }
}
