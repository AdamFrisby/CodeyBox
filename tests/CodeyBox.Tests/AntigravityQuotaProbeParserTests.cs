using CodeyBox.Agents.Antigravity;

namespace CodeyBox.Tests;

/// <summary>
/// Parser-only tests for <see cref="AntigravityQuotaProbe"/>. The HTTP layer
/// is exercised indirectly by the live-ping branch in
/// <see cref="AntigravityAgentRunnerTests"/>; these tests pin the JSON
/// shapes the probe is expected to accept across both
/// <c>:retrieveUserQuotaSummary</c> and <c>:retrieveUserQuota</c>.
/// </summary>
public sealed class AntigravityQuotaProbeParserTests
{
    [Fact]
    public void ParseSummaryResponse_PerModelArray_ReturnsPerModelQuotas()
    {
        var json = """
        {
          "windows": [
            {"name": "weekly", "remainingFraction": 0.42, "resetTime": "2026-06-16T12:00:00Z"}
          ],
          "perModel": [
            {"modelId": "gemini-3.5-flash-high", "remainingFraction": 0.20, "resetTime": "2026-06-16T12:00:00Z", "window": "weekly"},
            {"modelId": "claude-opus-4-6-thinking", "remainingFraction": 0.65, "resetTime": "2026-06-16T12:00:00Z", "window": "weekly"}
          ]
        }
        """;

        var snap = AntigravityQuotaProbe.ParseSummaryResponse(json);

        Assert.NotNull(snap);
        Assert.Equal(2, snap!.PerModel.Count);
        Assert.True(snap.PerModel.ContainsKey("gemini-3.5-flash-high"));
        Assert.True(snap.PerModel.ContainsKey("claude-opus-4-6-thinking"));
        Assert.Equal(20.0, snap.PerModel["gemini-3.5-flash-high"].AvailablePct, 1);
        // Most-constrained window drives the aggregated AvailablePct.
        Assert.Equal(42.0, snap.AvailablePct, 1);
    }

    [Fact]
    public void ParseSummaryResponse_BucketsAlias_StillParses()
    {
        // Defensive alias: if Google quietly renames the array key back to
        // "buckets" (matching retrieveUserQuota), the probe still works.
        var json = """
        {
          "buckets": [
            {"modelId": "gpt-oss-120b-medium", "remainingFraction": 0.10, "resetTime": "2026-06-16T12:00:00Z"}
          ]
        }
        """;

        var snap = AntigravityQuotaProbe.ParseSummaryResponse(json);

        Assert.NotNull(snap);
        Assert.Single(snap!.PerModel);
        Assert.Equal(10.0, snap.PerModel["gpt-oss-120b-medium"].AvailablePct, 1);
    }

    [Fact]
    public void ParseQuotaResponse_PicksMostConstrainedBucket()
    {
        var json = """
        {
          "buckets": [
            {"modelId": "gemini-3.5-flash-medium", "remainingFraction": 0.90},
            {"modelId": "gemini-3.5-flash-high", "remainingFraction": 0.05}
          ]
        }
        """;

        var snap = AntigravityQuotaProbe.ParseQuotaResponse(json);

        Assert.NotNull(snap);
        Assert.Equal(5.0, snap!.AvailablePct, 1);
    }

    [Fact]
    public void ParseQuotaResponse_NoBuckets_ReturnsNull()
    {
        Assert.Null(AntigravityQuotaProbe.ParseQuotaResponse("""{"other":"shape"}"""));
        Assert.Null(AntigravityQuotaProbe.ParseQuotaResponse("not json"));
    }
}
