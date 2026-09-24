using CodeyBox.Agents;
using CodeyBox.Agents.Unreal;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="UnrealQuotaFailureDetector"/>:
/// - Detects rate-limit errors (429, rate limit exceeded)
/// - Detects quota limits (insufficient_quota, credit balance too low, key limit exceeded)
/// - Detects auth errors (unauthorized, invalid_api_key)
/// - Operator custom patterns support
/// </summary>
public sealed class UnrealQuotaFailureDetectorTests
{
    private readonly UnrealQuotaFailureDetector _detector = new();

    [Fact]
    public void Kind_IsUnreal()
    {
        Assert.Equal(AgentKind.Unreal, _detector.Kind);
    }

    [Theory]
    [InlineData("rate limit exceeded", QuotaFailureKind.RateLimitExceeded)]
    [InlineData("error 429: too many requests", QuotaFailureKind.RateLimitExceeded)]
    [InlineData("insufficient_quota for model", QuotaFailureKind.LimitReached)]
    [InlineData("credit balance too low", QuotaFailureKind.LimitReached)]
    [InlineData("unauthorized: invalid api key", QuotaFailureKind.Unauthorized)]
    public void Detect_StandardPatterns_MatchesCorrectKind(string message, QuotaFailureKind expectedKind)
    {
        var detection = _detector.Detect(message, null);
        Assert.NotNull(detection);
        Assert.Equal(expectedKind, detection.Kind);
    }

    [Fact]
    public void Detect_JsonStdoutError_MatchesQuotaPattern()
    {
        const string stdout =
            """
            {"Sequence":1,"Kind":"turn","Data":{}}
            {"type":"error","message":"rate limit exceeded: please slow down requests"}
            """;

        var detection = _detector.Detect(null, stdout);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection.Kind);
    }

    [Fact]
    public void Detect_HealthyOutput_ReturnsNull()
    {
        const string stdout = "{\"Sequence\":5,\"Kind\":\"model_response\",\"Data\":{}}";
        Assert.Null(_detector.Detect(null, stdout));
        Assert.Null(_detector.Detect(string.Empty, string.Empty));
        Assert.Null(_detector.Detect(null, null));
    }

    [Fact]
    public void Detect_CustomOperatorPatterns_Matches()
    {
        var custom = new UnrealQuotaFailureDetector([
            new QuotaFailurePattern("custom-hard-limit-hit", QuotaFailureKind.LimitReached)
        ]);

        var detection = custom.Detect("provider returned custom-hard-limit-hit", null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection.Kind);
    }
}
