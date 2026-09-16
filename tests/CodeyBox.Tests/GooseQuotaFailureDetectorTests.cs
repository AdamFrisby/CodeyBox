using CodeyBox.Agents.Goose;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="GooseQuotaFailureDetector"/>: the relayed-provider
/// error shapes goose emits (verified against goose 1.50.1 live frames),
/// operator-extensible patterns, and non-matching noise.
/// </summary>
public sealed class GooseQuotaFailureDetectorTests
{
    private static readonly GooseQuotaFailureDetector Detector = new();

    [Fact]
    public void Kind_IsGoose()
    {
        Assert.Equal(AgentKind.Goose, Detector.Kind);
    }

    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        Assert.Null(Detector.Detect(null, null));
        Assert.Null(Detector.Detect(string.Empty, string.Empty));
    }

    [Fact]
    public void RecordedAuthError_DetectsUnauthorized()
    {
        // Error frame lives on STDOUT for goose (stream-json transport).
        const string stdout =
            """{"type":"message","message":{"id":"msg_1","role":"assistant","created":1789551852,"content":[{"type":"error","kind":"authentication","message":"Ran into this error: Authentication error: Authentication failed for https://openrouter.ai/api/v1/chat/completions. Status: 401 Unauthorized. Response: Missing Authentication header."}],"metadata":{"userVisible":true,"agentVisible":false}}}""";

        var detection = Detector.Detect(stderr: string.Empty, stdout: stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void MissingKeyPlaintext_DetectsUnauthorized()
    {
        const string stdout =
            "error: Error Configuration value not found: OPENROUTER_API_KEY.";

        var detection = Detector.Detect(stderr: null, stdout: stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Theory]
    [InlineData("rate_limit_exceeded")]
    [InlineData("429 Too Many Requests")]
    [InlineData("HTTP 429")]
    [InlineData("too many requests")]
    public void RateLimitShapes_DetectRateLimitExceeded(string shape)
    {
        var detection = Detector.Detect($"provider says: {shape}", null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Theory]
    [InlineData("insufficient_quota")]
    [InlineData("insufficient credits")]
    [InlineData("billing_hard_limit_reached")]
    [InlineData("HTTP 402")]
    [InlineData("quota exceeded")]
    [InlineData("usage limit reached")]
    public void BillingShapes_DetectLimitReached(string shape)
    {
        var detection = Detector.Detect(null, $"provider says: {shape}");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void HealthyRun_DetectsNothing()
    {
        const string stdout =
            """{"type":"message","message":{"id":"gen-1","role":"assistant","created":1,"content":[{"type":"text","text":"done"}],"metadata":{}}}""" + "\n" +
            """{"type":"complete","total_tokens":10,"input_tokens":8,"output_tokens":2,"cache_read_input_tokens":0,"cache_write_input_tokens":0,"cost_usd":0.0}""";

        Assert.Null(Detector.Detect(null, stdout));
    }

    [Fact]
    public void BareQuotaWord_DoesNotDetect()
    {
        // Repository content under review may discuss quota code; only
        // exhaustion verbs gate dispatch.
        Assert.Null(Detector.Detect("export QUOTA=429", null));
        Assert.Null(Detector.Detect(null, "the quota module handles retries"));
    }

    [Fact]
    public void OperatorExtras_AppendToDefaults()
    {
        var detector = new GooseQuotaFailureDetector(
            [new QuotaFailurePattern("custom-provider-cap", QuotaFailureKind.LimitReached)]);

        var custom = detector.Detect(null, "custom-provider-cap hit");
        Assert.NotNull(custom);
        Assert.Equal(QuotaFailureKind.LimitReached, custom!.Kind);

        // Defaults still apply alongside the extras.
        var builtin = detector.Detect(null, "HTTP 429");
        Assert.NotNull(builtin);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, builtin!.Kind);
    }

    [Fact]
    public void EmptyExtras_BehavesLikeDefaults()
    {
        var detector = new GooseQuotaFailureDetector([]);

        var detection = detector.Detect(null, "HTTP 429");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }
}
