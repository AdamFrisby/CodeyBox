using CodeyBox.Agents.Antigravity;

namespace CodeyBox.Tests;

/// <summary>
/// Parser tests for the gateway's <c>:retrieveUserQuotaSummary</c> payload. The sample is the real
/// response shape captured from agy 1.1.26, trimmed of prose fields.
/// </summary>
public sealed class AntigravityQuotaSummaryParserTests
{
    private const string RealPayload = """
    {
      "groups": [
        {
          "displayName": "Gemini Models",
          "description": "Models within this group: Gemini Flash, Gemini Pro",
          "buckets": [
            {"bucketId":"gemini-weekly","displayName":"Weekly Limit Remaining","remainingFraction":0.99817276,"resetTime":"2026-09-11T20:54:22Z","window":"weekly"},
            {"bucketId":"gemini-5h","displayName":"Five Hour Limit Remaining","remainingFraction":0.9890364,"resetTime":"2026-09-05T01:54:22Z","window":"5h"}
          ]
        },
        {
          "displayName": "Claude and GPT models",
          "description": "Models within this group: Claude Opus, Claude Sonnet, GPT-OSS",
          "buckets": [
            {"bucketId":"3p-weekly","displayName":"Weekly Limit Remaining","remainingFraction":1,"resetTime":"2026-09-11T22:46:08Z","window":"weekly"},
            {"bucketId":"3p-5h","displayName":"Five Hour Limit Remaining","remainingFraction":1,"resetTime":"2026-09-05T03:46:08Z","window":"5h"}
          ]
        }
      ]
    }
    """;

    [Fact]
    public void Parse_ReadsBothGroupsAndTheirWindows()
    {
        var groups = AntigravityQuotaSummaryParser.Parse(RealPayload);

        Assert.Equal(2, groups.Count);
        Assert.Equal("Gemini Models", groups[0].DisplayName);
        Assert.Equal(2, groups[0].Buckets.Count);
    }

    [Fact]
    public void Parse_NormalisesWindowNamesToTheOnesFloorsKeyOn()
    {
        // QuotaRouter.MinQuotaPctByWindow is keyed five_hour/seven_day; the gateway says 5h/weekly.
        // If these don't line up the per-window floors silently stop applying.
        var buckets = AntigravityQuotaSummaryParser.Parse(RealPayload)[0].Buckets;

        Assert.Contains(buckets, b => b.Window == "seven_day" && b.BucketId == "gemini-weekly");
        Assert.Contains(buckets, b => b.Window == "five_hour" && b.BucketId == "gemini-5h");
    }

    [Fact]
    public void Parse_ConvertsFractionToPercent_AndReadsResetTime()
    {
        var weekly = AntigravityQuotaSummaryParser.Parse(RealPayload)[0].Buckets
            .Single(b => b.BucketId == "gemini-weekly");

        Assert.Equal(99.817276, weekly.AvailablePct, precision: 4);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 11, 20, 54, 22, TimeSpan.Zero),
            weekly.ResetAt);
    }

    [Fact]
    public void Parse_AcceptsIntegerRemainingFraction()
    {
        // The gateway sends a bare 1 (not 1.0) for an untouched window.
        var full = AntigravityQuotaSummaryParser.Parse(RealPayload)[1].Buckets
            .Single(b => b.BucketId == "3p-weekly");

        Assert.Equal(100.0, full.AvailablePct);
    }

    [Theory]
    [InlineData("gemini-3.8-flash-high", "gemini-")]
    [InlineData("gemini-3.1-pro-low", "gemini-")]
    [InlineData("claude-opus-4-6-thinking", "3p-")]
    [InlineData("claude-sonnet-4-6", "3p-")]
    [InlineData("gpt-oss-120b-medium", "3p-")]
    public void BucketsForModel_SelectsTheGroupThatMetersTheModel(string modelId, string expectedPrefix)
    {
        // Gemini members must not be gated on the Claude group's consumption, or vice versa.
        var buckets = AntigravityQuotaSummaryParser.BucketsForModel(
            AntigravityQuotaSummaryParser.Parse(RealPayload), modelId);

        Assert.Equal(2, buckets.Count);
        Assert.All(buckets, b => Assert.StartsWith(expectedPrefix, b.BucketId, StringComparison.Ordinal));
    }

    [Fact]
    public void BucketsForModel_UnknownModel_FallsBackToEveryBucket()
    {
        // Conservative on purpose: aggregating across all groups reports the most constrained window,
        // where guessing a group could report a fresh window for an exhausted one and dispatch into 429.
        var buckets = AntigravityQuotaSummaryParser.BucketsForModel(
            AntigravityQuotaSummaryParser.Parse(RealPayload), "some-future-model");

        Assert.Equal(4, buckets.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"groups":[]}""")]
    [InlineData("""{"groups":[{"displayName":"x","buckets":[]}]}""")]
    [InlineData("""{"groups":[{"buckets":[{"bucketId":"a"}]}]}""")]
    public void Parse_ReturnsEmptyForUnusablePayloads(string? json)
    {
        // An unusable payload must yield nothing so the probe falls back to the liveness read rather
        // than inventing a reading.
        Assert.Empty(AntigravityQuotaSummaryParser.Parse(json));
    }
}
