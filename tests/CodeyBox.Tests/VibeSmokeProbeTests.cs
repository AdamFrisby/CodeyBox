using CodeyBox.Agents.Vibe;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="VibeSmokeProbe"/> (host-side credential-presence
/// check) and <see cref="VibeModelListProbe"/> (static curated seed).
/// </summary>
public sealed class VibeSmokeProbeTests
{
    private static AgentCredential CredentialWithKey() =>
        new(AgentKind.Vibe,
            new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = "k" },
            new Dictionary<string, string>());

    private static AgentCredential CredentialWithoutKey() =>
        new(AgentKind.Vibe,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

    [Fact]
    public void Kind_IsVibe()
    {
        Assert.Equal(AgentKind.Vibe, new VibeSmokeProbe().Kind);
    }

    [Fact]
    public async Task SmokeTestAsync_WithKey_ReturnsOk()
    {
        var result = await new VibeSmokeProbe().SmokeTestAsync(CredentialWithKey(), CancellationToken.None);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task SmokeTestAsync_WithoutKey_ReturnsPersistentFailNamingVariable()
    {
        var result = await new VibeSmokeProbe().SmokeTestAsync(CredentialWithoutKey(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("CODEYBOX_VIBE_API_KEY", result.FailureReason, StringComparison.Ordinal);
    }
}

/// <summary>
/// Tests for <see cref="VibeModelListProbe"/> and
/// <see cref="VibeKnownModels"/>: the static seed plus the warn-only
/// validator.
/// </summary>
public sealed class VibeModelListProbeTests
{
    [Fact]
    public void Kind_IsVibe()
    {
        Assert.Equal(AgentKind.Vibe, new VibeModelListProbe().Kind);
    }

    [Fact]
    public async Task GetModelListAsync_ReturnsSeedContainingShippedAlias()
    {
        var result = await new VibeModelListProbe().GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains("nemotron-free", result.ModelIds);
    }

    [Fact]
    public void IsKnown_ShippedAlias_ReturnsTrue()
    {
        Assert.True(VibeKnownModels.IsKnown("nemotron-free"));
        Assert.False(VibeKnownModels.IsKnown("no-such-alias"));
        Assert.False(VibeKnownModels.IsKnown(null));
        Assert.False(VibeKnownModels.IsKnown("  "));
    }
}
