using CodeyBox.Agents.Antigravity;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class AntigravityKnownModelsTests
{
    [Fact]
    public void IsKnown_RecognisesGatewayModels()
    {
        Assert.True(AntigravityKnownModels.IsKnown("gemini-3.8-flash-high"));
        Assert.True(AntigravityKnownModels.IsKnown("claude-sonnet-4-6"));
        Assert.True(AntigravityKnownModels.IsKnown("claude-opus-4-6-thinking"));
        // Delisted by the gateway (it moved to 3.6/3.7/3.8) — must no longer validate clean.
        Assert.False(AntigravityKnownModels.IsKnown("gemini-3.5-flash-high"));
        // Sonnet dropped its "-thinking" suffix; the old id is gone.
        Assert.False(AntigravityKnownModels.IsKnown("claude-sonnet-4-6-thinking"));
        Assert.True(AntigravityKnownModels.IsKnown("gpt-oss-120b-medium"));
        Assert.False(AntigravityKnownModels.IsKnown("totally-made-up-model"));
        Assert.False(AntigravityKnownModels.IsKnown(null));
        Assert.False(AntigravityKnownModels.IsKnown(""));
    }

    [Fact]
    public void ValidateModelIdAgainstProviderList_UnknownId_EmitsWarning()
    {
        var warning = AntigravityKnownModels.ValidateModelIdAgainstProviderList(
            "google-gateway", "rogue-model-x", NullLogger.Instance);
        Assert.NotNull(warning);
        Assert.Contains("rogue-model-x", warning);
    }

    [Fact]
    public void ValidateModelIdAgainstProviderList_KnownId_NoWarning()
    {
        var warning = AntigravityKnownModels.ValidateModelIdAgainstProviderList(
            "google-gateway", "gemini-3.8-flash-high", NullLogger.Instance);
        Assert.Null(warning);
    }

    [Fact]
    public void ValidateModelIdAgainstProviderList_EmptyId_NoWarning()
    {
        Assert.Null(AntigravityKnownModels.ValidateModelIdAgainstProviderList(
            "google-gateway", null, NullLogger.Instance));
        Assert.Null(AntigravityKnownModels.ValidateModelIdAgainstProviderList(
            "google-gateway", "", NullLogger.Instance));
    }
}
