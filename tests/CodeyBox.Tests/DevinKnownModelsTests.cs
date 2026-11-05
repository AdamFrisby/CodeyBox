using CodeyBox.Agents.Devin;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DevinKnownModels"/>: membership of the curated
/// <c>--model</c> seed, including the swe-2-flash family the catalog lists
/// as Free only under a time-limited promotion.
/// </summary>
public sealed class DevinKnownModelsTests
{
    [Theory]
    [InlineData("claude-sonnet-5-medium")]
    [InlineData("swe-2-high")]
    [InlineData("swe-2-medium")]
    [InlineData("swe-2-max")]
    [InlineData("swe-2-flash-high")]
    [InlineData("swe-2-flash-medium")]
    [InlineData("swe-2-flash-max")]
    [InlineData("SWE-2-FLASH-HIGH")] // case-insensitive
    public void IsKnown_CuratedUid_ReturnsTrue(string modelId)
        => Assert.True(DevinKnownModels.IsKnown(modelId));

    [Theory]
    [InlineData("swe-3-high")]
    [InlineData("")]
    [InlineData(null)]
    public void IsKnown_UnknownOrEmpty_ReturnsFalse(string? modelId)
        => Assert.False(DevinKnownModels.IsKnown(modelId));

    [Fact]
    public void Validator_KnownFlashUid_DoesNotWarn()
    {
        var warning = DevinKnownModels.ValidateModelIdAgainstProviderList(
            "frontier", "swe-2-flash-high", NullLogger.Instance);

        Assert.Null(warning);
    }

    [Fact]
    public void Validator_UnknownUid_Warns()
    {
        var warning = DevinKnownModels.ValidateModelIdAgainstProviderList(
            "frontier", "swe-9-ultra", NullLogger.Instance);

        Assert.NotNull(warning);
        Assert.Contains("swe-9-ultra", warning);
    }
}
