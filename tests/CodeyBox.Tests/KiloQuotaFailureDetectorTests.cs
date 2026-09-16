using CodeyBox.Agents.Kilo;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="KiloQuotaFailureDetector"/>: the recorded real
/// @kilocode/cli 7.7.2 failure shapes (missing-key 401 → Unauthorized, the
/// $0-spend-limit paid-model refusal → LimitReached), the deliberate
/// non-matches (configuration/generic give-up shapes and reviewed-code
/// prose), and the operator-extensible pattern hook
/// (<c>CodeyBox:QuotaFailurePatterns:kilo</c>).
/// </summary>
public sealed class KiloQuotaFailureDetectorTests
{
    private const string AuthErrorStdout =
        "{\"type\":\"error\",\"timestamp\":1789585744519,\"sessionID\":\"ses_def\"," +
        "\"error\":{\"name\":\"APIError\",\"data\":{\"message\":\"No cookie auth credentials found\"," +
        "\"statusCode\":401,\"isRetryable\":false," +
        "\"responseBody\":\"{\\\"error\\\":{\\\"message\\\":\\\"No cookie auth credentials found\\\",\\\"code\\\":401}}\"}}}";

    private const string AuthErrorStderr = "Error: No cookie auth credentials found";

    [Fact]
    public void Kind_IsKilo()
    {
        Assert.Equal(AgentKind.Kilo, new KiloQuotaFailureDetector().Kind);
    }

    [Fact]
    public void Detect_MissingKeyAuthFailure_ReturnsUnauthorized()
    {
        // Recorded real shape: missing API key against OpenRouter (exit 1).
        var detection = new KiloQuotaFailureDetector().Detect(AuthErrorStderr, AuthErrorStdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection.Kind);
    }

    [Fact]
    public void Detect_SpendLimitRefusal_ReturnsLimitReached()
    {
        // The $0 footgun: a $0-spend-limit OpenRouter key against a paid
        // model fails with `forbidden: Key limit exceeded (total limit)`
        // (observed on the same key via a sibling CLI; kilo relays the
        // provider body verbatim in error.data.message).
        var detection = new KiloQuotaFailureDetector().Detect(
            "Error: forbidden: Key limit exceeded (total limit). ...",
            "{\"type\":\"error\",\"error\":{\"name\":\"APIError\",\"data\":{\"message\":\"forbidden: Key limit exceeded (total limit).\"}}}");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection.Kind);
    }

    [Fact]
    public void Detect_RateLimitShape_ReturnsRateLimitExceeded()
    {
        var detection = new KiloQuotaFailureDetector().Detect(
            "Error: API Error: 429 Rate limit exceeded, retry after 12s",
            null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection.Kind);
    }

    [Fact]
    public void Detect_ModelNotFound_ReturnsNull()
    {
        // Recorded real shape (unseeded models map, exit 1): a configuration
        // error, not quota/auth state — must not gate dispatch.
        var detection = new KiloQuotaFailureDetector().Detect(
            "Error: Model not found: openai-compatible/nvidia/nemotron-3.5-lightning:free.",
            "{\"type\":\"error\",\"error\":{\"name\":\"UnknownError\",\"data\":{\"message\":\"Model not found: openai-compatible/nvidia/nemotron-3.5-lightning:free.\"}}}");

        Assert.Null(detection);
    }

    [Fact]
    public void Detect_GenericServerErrorFrame_ReturnsNull()
    {
        // The content-free companion frame carries no cause — not evidence
        // of quota/auth state.
        var detection = new KiloQuotaFailureDetector().Detect(
            null,
            "{\"type\":\"error\",\"error\":{\"name\":\"UnknownError\",\"data\":{\"message\":\"Unexpected server error. Check server logs for details.\"}}}");

        Assert.Null(detection);
    }

    [Fact]
    public void Detect_ReviewingQuotaCode_ReturnsNull()
    {
        // Bare numbers and quota-discussion prose in reviewed repository
        // content must not gate dispatch: patterns require anchored
        // provider-shaped phrases.
        var detection = new KiloQuotaFailureDetector().Detect(
            "retry budget 429/402 exceeded in backoff table",
            "the quota module handles per-tenant limits; 401 means unauthenticated here");

        Assert.Null(detection);
    }

    [Fact]
    public void Detect_EmptyStreams_ReturnsNull()
    {
        var detector = new KiloQuotaFailureDetector();

        Assert.Null(detector.Detect(null, null));
        Assert.Null(detector.Detect(string.Empty, "  "));
    }

    [Fact]
    public void Detect_OperatorExtras_AppendAfterDefaults()
    {
        // Operator patterns from CodeyBox:QuotaFailurePatterns:kilo are
        // honoured (defaults still win on ordering for overlapping shapes).
        var detector = new KiloQuotaFailureDetector(
            [new QuotaFailurePattern("custom-provider-cap", QuotaFailureKind.LimitReached)]);

        var detection = detector.Detect("Error: custom-provider-cap hit for tenant", null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection.Kind);
        Assert.Null(new KiloQuotaFailureDetector().Detect("Error: custom-provider-cap hit for tenant", null));
    }
}
