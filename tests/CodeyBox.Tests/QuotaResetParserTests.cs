using CodeyBox.Agents;

namespace CodeyBox.Tests;

public sealed class QuotaResetParserTests
{
    [Theory]
    [InlineData("reset after 21h41m24s", 21, 41, 24)]
    [InlineData("will reset after 5m17s", 0, 5, 17)]
    [InlineData("reset in 30m", 0, 30, 0)]
    // agy's consumer-quota 429 phrasing: "Individual quota reached (Resets in 8m14s)".
    [InlineData("Resets in 8m14s", 0, 8, 14)]
    [InlineData("Individual quota reached (Resets in 8m14s)", 0, 8, 14)]
    [InlineData("retry after 1h", 1, 0, 0)]
    [InlineData("try again after 2h30m", 2, 30, 0)]
    [InlineData("available in 13m", 0, 13, 0)]
    public void TryParseResetAt_CompactDurationForms_ReturnExpectedOffset(
        string source, int hours, int minutes, int seconds)
    {
        var resetAt = QuotaResetParser.TryParseResetAt([source]);

        Assert.NotNull(resetAt);
        var diff = resetAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(diff.TotalSeconds, ExpectedSeconds(hours, minutes, seconds) - 2, ExpectedSeconds(hours, minutes, seconds) + 2);
    }

    [Theory]
    [InlineData("It will reset in 5 hours 23 minutes.")]
    [InlineData("reset in 2 hours 30 minutes")]
    public void TryParseResetAt_WordFormDurations_ReturnNull(string source)
    {
        Assert.Null(QuotaResetParser.TryParseResetAt([source]));
    }

    private static double ExpectedSeconds(int hours, int minutes, int seconds) =>
        hours * 3600d + minutes * 60d + seconds;
}
