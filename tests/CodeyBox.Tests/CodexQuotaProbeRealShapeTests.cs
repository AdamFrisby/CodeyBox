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

        // Overall account is at 63% available (the most-constrained window in the
        // top-level rate_limit). Per-model buckets must be capped by this — even
        // though the per-model windows alone show 100% available, the account
        // can deny calls account-wide, so per-model availability tracks overall.
        Assert.Equal(63, snapshot.AvailablePct);
        Assert.True(snapshot.PerModel.ContainsKey("GPT-5.3-Codex-Spark"));
        Assert.True(snapshot.PerModel.ContainsKey(CodexQuotaProbe.DefaultRoutedModelId));
        Assert.Equal(63, snapshot.PerModel["GPT-5.3-Codex-Spark"].AvailablePct);
        Assert.Equal(63, snapshot.PerModel[CodexQuotaProbe.DefaultRoutedModelId].AvailablePct);
        Assert.Contains("capped by overall", snapshot.PerModel["GPT-5.3-Codex-Spark"].Window);
        Assert.NotNull(snapshot.ResetAt);
    }
}
