using CodeyBox.Agents.Goose;
using CodeyBox.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="GooseModelListProbe"/> and
/// <see cref="GooseKnownModels"/>: the catalog is per provider and
/// server-side, so the probe returns the curated seed and unknown ids only
/// warn (goose accepts any provider-native id via <c>--model</c>).
/// </summary>
public sealed class GooseModelListProbeTests
{
    [Fact]
    public void Kind_IsGoose()
    {
        Assert.Equal(AgentKind.Goose, new GooseModelListProbe().Kind);
    }

    [Fact]
    public async Task GetModelListAsync_ReturnsSeed()
    {
        var result = await new GooseModelListProbe().GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains("nvidia/nemotron-3.5-lightning:free", result.ModelIds);
    }

    [Fact]
    public void IsKnown_MatchesCaseInsensitively()
    {
        Assert.True(GooseKnownModels.IsKnown("nvidia/nemotron-3.5-lightning:free"));
        Assert.True(GooseKnownModels.IsKnown("NVIDIA/NEMOTRON-3.5-LIGHTNING:FREE"));
        Assert.False(GooseKnownModels.IsKnown("openai/gpt-5.5"));
        Assert.False(GooseKnownModels.IsKnown(null));
        Assert.False(GooseKnownModels.IsKnown("  "));
    }

    [Fact]
    public void ValidateModelId_KnownId_ReturnsNullWithoutWarning()
    {
        var log = NullLogger.Instance;

        var warning = GooseKnownModels.ValidateModelIdAgainstProviderList(
            "frontier-coding", "nvidia/nemotron-3.5-lightning:free", log);

        Assert.Null(warning);
    }

    [Fact]
    public void ValidateModelId_UnknownId_WarnsButDoesNotReject()
    {
        var log = NullLogger.Instance;

        var warning = GooseKnownModels.ValidateModelIdAgainstProviderList(
            "frontier-coding", "openai/gpt-5.5", log);

        // Warn-only: goose accepts any provider-native id beyond the seed.
        Assert.NotNull(warning);
        Assert.Contains("openai/gpt-5.5", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateModelId_BlankId_ReturnsNull()
    {
        var log = NullLogger.Instance;

        Assert.Null(GooseKnownModels.ValidateModelIdAgainstProviderList("frontier-coding", null, log));
        Assert.Null(GooseKnownModels.ValidateModelIdAgainstProviderList("frontier-coding", "  ", log));
    }
}
