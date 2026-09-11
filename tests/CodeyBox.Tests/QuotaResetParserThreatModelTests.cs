using CodeyBox.Agents;
using CodeyBox.Agents.Opencode;

namespace CodeyBox.Tests;

/// <summary>
/// Both quota-reset parsers share one threat model: agent output is
/// prompt-injectable prose, so word-form durations must not widen the
/// quota-reset pause window unless the subscription-limit anchor vouches for
/// them. Every unanchored input below is rejected by both parsers; the
/// anchored subscription limit is the single documented exception.
/// </summary>
public sealed class QuotaResetParserThreatModelTests
{
    private static readonly TimeSpan AssertSkew = TimeSpan.FromSeconds(10);

    public static IEnumerable<object[]> AgentAuthoredProseWithWordFormDurations()
    {
        // Prompt-injectable prose: an agent (or a repo under review quoted in
        // its output) claims a word-form reset window with no subscription
        // anchor. Both parsers must refuse to park quota retry on it.
        yield return new object[] { "The agent wrote: it will reset in 5 hours 23 minutes, so just wait." };
        yield return new object[] { "Review note: quota resets in 2 hours 30 minutes after deploy; retry then." };
        yield return new object[] { "Please reset in 1 hour and try again." };
        yield return new object[] { "429 Error from provider: rate limit exceeded. It will reset in 5 hours 23 minutes." };
        yield return new object[] { "Usage is high; the limit will reset in 45 minutes according to the dashboard." };
    }

    [Theory]
    [MemberData(nameof(AgentAuthoredProseWithWordFormDurations))]
    public void BothParsers_RejectWordFormDurationsInUnanchoredProse(string source)
    {
        Assert.Null(QuotaResetParser.TryParseResetAt([source]));
        Assert.Null(OpencodeQuotaResetParser.TryParseResetAt([source]));
    }

    [Theory]
    [InlineData("Please retry after a brief wait.")]
    [InlineData("ordinary model refusal")]
    [InlineData("reset in a moment")]
    public void BothParsers_RejectDurationlessProse(string source)
    {
        Assert.Null(QuotaResetParser.TryParseResetAt([source]));
        Assert.Null(OpencodeQuotaResetParser.TryParseResetAt([source]));
    }

    [Fact]
    public void AnchoredSubscriptionLimit_OnlyOpencodeParsesWordForm()
    {
        const string stderr = "5 hour usage limit reached. It will reset in 5 hours 23 minutes.";

        var opencode = OpencodeQuotaResetParser.TryParseResetAt([stderr]);

        Assert.NotNull(opencode);
        var diff = opencode!.Value - DateTimeOffset.UtcNow;
        var expected = TimeSpan.FromHours(5) + TimeSpan.FromMinutes(23);
        Assert.InRange(diff.TotalSeconds, expected.TotalSeconds - AssertSkew.TotalSeconds, expected.TotalSeconds + AssertSkew.TotalSeconds);

        // The shared parser has no anchor exception: word forms stay unparsed.
        Assert.Null(QuotaResetParser.TryParseResetAt([stderr]));
    }

    [Theory]
    [InlineData("reset in 30m", 0, 30, 0)]
    [InlineData("reset after 5m", 0, 5, 0)]
    [InlineData("retry after 1h", 1, 0, 0)]
    [InlineData("available in 13m", 0, 13, 0)]
    public void BothParsers_AgreeOnCompactDurations(string source, int hours, int minutes, int seconds)
    {
        var expectedSeconds = hours * 3600d + minutes * 60d + seconds;

        var shared = QuotaResetParser.TryParseResetAt([source]);
        var opencode = OpencodeQuotaResetParser.TryParseResetAt([source]);

        Assert.NotNull(shared);
        Assert.NotNull(opencode);
        Assert.InRange((shared!.Value - DateTimeOffset.UtcNow).TotalSeconds, expectedSeconds - AssertSkew.TotalSeconds, expectedSeconds + AssertSkew.TotalSeconds);
        Assert.InRange(Math.Abs((shared.Value - opencode!.Value).TotalSeconds), 0, AssertSkew.TotalSeconds);
    }
}
