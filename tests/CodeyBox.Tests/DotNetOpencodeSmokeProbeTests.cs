using CodeyBox.Agents.DotNetOpencode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DotNetOpencodeSmokeProbe"/>,
/// <see cref="DotNetOpencodeModelListProbe"/>, and
/// <see cref="DotNetOpencodeKnownModels"/>.
/// </summary>
public sealed class DotNetOpencodeSmokeProbeTests
{
    private static AgentCredential Cred(params string[] envKeys)
    {
        var env = new Dictionary<string, string>();
        foreach (var k in envKeys) env[k] = "{\"provider\":{}}";
        return new AgentCredential(AgentKind.DotNetOpencode, env, new Dictionary<string, string>());
    }

    [Fact]
    public void Kind_IsDotNetOpencode()
    {
        Assert.Equal(AgentKind.DotNetOpencode, new DotNetOpencodeSmokeProbe().Kind);
    }

    [Fact]
    public async Task SmokeTest_WithConfigJson_Passes()
    {
        var probe = new DotNetOpencodeSmokeProbe();

        var result = await probe.SmokeTestAsync(Cred("DOTNETOPENCODE_CONFIG_JSON"), CancellationToken.None);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task SmokeTest_WithoutConfigJson_FailsPersistentNamingCause()
    {
        // Missing credential must fail closed with a persistent (don't-retry)
        // failure naming the variable to set — never a silent pass.
        var probe = new DotNetOpencodeSmokeProbe();

        var result = await probe.SmokeTestAsync(Cred(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
        Assert.Contains("CODEYBOX_DOTNETOPENCODE_CONFIG_JSON", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelListProbe_ReturnsKnownSeed()
    {
        var probe = new DotNetOpencodeModelListProbe();

        Assert.Equal(AgentKind.DotNetOpencode, probe.Kind);
        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.NotEmpty(result.ModelIds);
        Assert.Contains("anthropic/claude-haiku-4-5", result.ModelIds);
    }

    [Fact]
    public void KnownModels_VerifiedId_IsKnown()
    {
        Assert.True(DotNetOpencodeKnownModels.IsKnown("anthropic/claude-haiku-4-5"));
        Assert.False(DotNetOpencodeKnownModels.IsKnown("anthropic/does-not-exist-xyz"));
        Assert.False(DotNetOpencodeKnownModels.IsKnown(null));
    }
}
