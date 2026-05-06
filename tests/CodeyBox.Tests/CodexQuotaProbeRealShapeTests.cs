using CodeyBox.Agents.Codex;

namespace CodeyBox.Tests;

public sealed class CodexQuotaProbeRealShapeTests
{
    [Fact]
    public async Task CapturedWhamUsageShape_ParsesOverallAndPerModelLimits()
    {
        var capturedWhamUsageShape = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Quota", "codex-wham-usage.redacted.json"));

        var snapshot = CodexQuotaProbe.ParseResponse(capturedWhamUsageShape);

        Assert.Equal(63, snapshot.AvailablePct);
        Assert.True(snapshot.PerModel.ContainsKey("GPT-5.3-Codex-Spark"));
        Assert.Equal(100, snapshot.PerModel["GPT-5.3-Codex-Spark"].AvailablePct);
        Assert.Equal("5h-rolling", snapshot.PerModel["GPT-5.3-Codex-Spark"].Window);
        Assert.NotNull(snapshot.ResetAt);
    }
}
