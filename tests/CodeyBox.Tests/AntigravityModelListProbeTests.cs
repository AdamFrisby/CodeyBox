using CodeyBox.Agents.Antigravity;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// <see cref="AntigravityModelListProbe"/> has no live source — no reachable
/// endpoint enumerates the agy gateway models for our credential (the
/// cloudcode-pa Code Assist surface returns the wrong gemini-2.5 catalog; the
/// daily-cloudcode-pa gateway 403s on :retrieveUserQuota* and
/// :fetchAvailableModels). So it returns the curated
/// <see cref="AntigravityKnownModels.All"/> list, which is what the startup
/// validator checks configured ids against.
/// </summary>
public sealed class AntigravityModelListProbeTests
{
    [Fact]
    public void Kind_IsAntigravity()
    {
        Assert.Equal(AgentKind.Antigravity, new AntigravityModelListProbe().Kind);
    }

    [Fact]
    public async Task GetModelListAsync_ReturnsCuratedKnownModels()
    {
        var result = await new AntigravityModelListProbe().GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Equal(AntigravityKnownModels.All, result.ModelIds);
        // The real gateway ids that previously logged "NOT in provider model
        // list" against the Code Assist catalog must now validate.
        Assert.Contains("gemini-3.5-flash-high", result.ModelIds);
        Assert.Contains("claude-opus-4-6-thinking", result.ModelIds);
    }
}
