using CodeyBox.Agents.Autohand;
using CodeyBox.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AutohandModelListProbe"/> and
/// <see cref="AutohandKnownModels"/>: the catalog is per provider and
/// server-side, so the probe returns the curated seed and unknown ids only
/// warn (autohand accepts any provider-native id via <c>--model</c>).
/// </summary>
public sealed class AutohandModelListProbeTests
{
    [Fact]
    public void Kind_IsAutohand()
    {
        Assert.Equal(AgentKind.Autohand, new AutohandModelListProbe().Kind);
    }

    [Fact]
    public async Task GetModelListAsync_ReturnsSeed()
    {
        var result = await new AutohandModelListProbe().GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains("nvidia/nemotron-3.5-lightning:free", result.ModelIds);
    }

    [Fact]
    public void IsKnown_MatchesCaseInsensitively()
    {
        Assert.True(AutohandKnownModels.IsKnown("nvidia/nemotron-3.5-lightning:free"));
        Assert.True(AutohandKnownModels.IsKnown("NVIDIA/NEMOTRON-3.5-LIGHTNING:FREE"));
        Assert.False(AutohandKnownModels.IsKnown("openai/gpt-5.5"));
        Assert.False(AutohandKnownModels.IsKnown(null));
        Assert.False(AutohandKnownModels.IsKnown("  "));
    }

    [Fact]
    public void ValidateModelId_KnownId_ReturnsNullWithoutWarning()
    {
        var log = NullLogger.Instance;

        var warning = AutohandKnownModels.ValidateModelIdAgainstProviderList(
            "frontier-coding", "nvidia/nemotron-3.5-lightning:free", log);

        Assert.Null(warning);
    }

    [Fact]
    public void ValidateModelId_UnknownId_WarnsButDoesNotReject()
    {
        // Warn-only: autohand accepts any provider-native id beyond the seed.
        // A paid id on a $0 key fails at dispatch with "Key limit exceeded",
        // which the detector parks as quota exhaustion.
        var log = NullLogger.Instance;

        var warning = AutohandKnownModels.ValidateModelIdAgainstProviderList(
            "frontier-coding", "anthropic/claude-sonnet-4", log);

        Assert.NotNull(warning);
        Assert.Contains("anthropic/claude-sonnet-4", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateModelId_BlankId_ReturnsNull()
    {
        var log = NullLogger.Instance;

        Assert.Null(AutohandKnownModels.ValidateModelIdAgainstProviderList("frontier-coding", null, log));
        Assert.Null(AutohandKnownModels.ValidateModelIdAgainstProviderList("frontier-coding", "  ", log));
    }
}
