using CodeyBox.Agents.Claude;

namespace CodeyBox.Tests;

public sealed class ClaudeQuotaProbeRealShapeTests
{
    [Fact]
    public async Task CapturedRollupShape_ParsesOverallAndPerModelLimits()
    {
        var capturedShape = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Quota", "claude-oauth-usage.redacted.json"));

        var snapshot = ClaudeQuotaProbe.ParseResponse(capturedShape);

        Assert.Equal(80, snapshot.AvailablePct);
        Assert.True(snapshot.PerModel.ContainsKey("claude-sonnet-4-6"));
        Assert.True(snapshot.PerModel.ContainsKey("claude-opus-4-7"));
        Assert.Equal(60, snapshot.PerModel["claude-sonnet-4-6"].AvailablePct);
        Assert.Equal(0, snapshot.PerModel["claude-opus-4-7"].AvailablePct);
        Assert.Equal("5h-rolling", snapshot.PerModel["claude-opus-4-7"].Window);
    }
}
