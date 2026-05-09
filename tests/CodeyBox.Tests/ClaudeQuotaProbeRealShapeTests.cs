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

    [Fact]
    public async Task CapturedFlatBucketShape_RespectsMostRestrictiveBucket()
    {
        // The live Anthropic OAuth usage endpoint returns a flat shape with
        // named buckets (`five_hour`, `seven_day`, `seven_day_<model>`),
        // each carrying `utilization` (0-100). Overall availability is the
        // most-restrictive of the global buckets; per-model is min(overall,
        // model-specific bucket).
        var capturedShape = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Quota", "claude-oauth-usage-flat.redacted.json"));

        var snapshot = ClaudeQuotaProbe.ParseResponse(capturedShape);

        // five_hour=3% (97% avail), seven_day=84% (16% avail).
        // Overall = min(97, 16) = 16.
        Assert.Equal(16, snapshot.AvailablePct);

        // seven_day_sonnet=100% (0% avail). Sonnet is capped account-wide.
        Assert.True(snapshot.PerModel.ContainsKey("claude-sonnet-4-6"));
        Assert.Equal(0, snapshot.PerModel["claude-sonnet-4-6"].AvailablePct);

        // seven_day_opus=null (no opus-specific bucket). Opus availability
        // is just the overall cap.
        Assert.True(snapshot.PerModel.ContainsKey("claude-opus-4-7"));
        Assert.Equal(16, snapshot.PerModel["claude-opus-4-7"].AvailablePct);

        // Reset time should be from the most-restrictive global bucket
        // (seven_day in this fixture).
        Assert.NotNull(snapshot.ResetAt);
    }
}
