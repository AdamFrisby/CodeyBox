using CodeyBox.Agents.Pi;
using CodeyBox.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="PiQuotaFailureDetector"/> and <see cref="PiSmokeProbe"/>.
/// Detector fixtures use the provider-error shapes pi relays verbatim in
/// <c>errorMessage</c> (verified against pi 0.85.1) plus the plaintext
/// pre-session failure line.
/// </summary>
public sealed class PiQuotaFailureDetectorTests
{
    private static readonly PiQuotaFailureDetector Detector = new();

    [Fact]
    public void Kind_IsPi()
    {
        Assert.Equal(AgentKind.Pi, Detector.Kind);
    }

    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        Assert.Null(Detector.Detect(null, null));
        Assert.Null(Detector.Detect("", ""));
    }

    [Fact]
    public void LiveAuthErrorFrame_DetectedAsUnauthorized()
    {
        // Live shape: bogus ANTHROPIC_API_KEY, exit-0 run, error on stdout.
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"stopReason\":\"error\"," +
            "\"errorMessage\":\"401 {\\\"type\\\":\\\"error\\\",\\\"error\\\":{\\\"type\\\":\\\"authentication_error\\\",\\\"message\\\":\\\"API key is invalid\\\"}}\"}}";

        var detection = Detector.Detect(null, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void NoApiKeyPlaintext_DetectedAsUnauthorized()
    {
        var detection = Detector.Detect(null, "No API key found for the selected model.\n");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void RateLimitError_DetectedAsRateLimitExceeded()
    {
        var detection = Detector.Detect(
            null,
            "{\"type\":\"message_end\",\"message\":{\"stopReason\":\"error\",\"errorMessage\":\"429 rate_limit_exceeded\"}}");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void InsufficientQuota_DetectedAsLimitReached()
    {
        var detection = Detector.Detect(
            "Error: insufficient_quota: You exceeded your current quota",
            null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void BareNumber_NotDetected()
    {
        // Model output citing "429" (retry counts, code under review) must
        // not gate dispatch — patterns stay anchored to provider phrasing.
        Assert.Null(Detector.Detect(null, "retry 429 times then back off"));
        Assert.Null(Detector.Detect("quota", "quota"));
    }

    [Fact]
    public void HealthyRun_NotDetected()
    {
        const string stdout =
            "{\"type\":\"session\",\"version\":3}\n{\"type\":\"agent_end\",\"messages\":[],\"willRetry\":false}\n";

        Assert.Null(Detector.Detect(null, stdout));
    }

    [Fact]
    public void OperatorExtraPatterns_Appended()
    {
        var detector = new PiQuotaFailureDetector(
            [new QuotaFailurePattern("pi says no", QuotaFailureKind.LimitReached)]);

        var detection = detector.Detect(null, "pi says no today");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }
}

/// <summary>
/// Tests for <see cref="PiSmokeProbe"/>: credential-presence only, no network.
/// </summary>
public sealed class PiSmokeProbeTests
{
    private static PiSmokeProbe NewProbe() => new(NullLogger<PiSmokeProbe>.Instance);

    private static AgentCredential CredWithKey(string key = "sk-ant-test") =>
        new(AgentKind.Pi,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = key },
            new Dictionary<string, string>());

    private static AgentCredential EmptyCred() =>
        new(AgentKind.Pi,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

    [Fact]
    public async Task Kind_IsPi()
    {
        Assert.Equal(AgentKind.Pi, NewProbe().Kind);
    }

    [Fact]
    public async Task CredentialBundleContainsApiKey_ReturnsOk()
    {
        var result = await NewProbe().SmokeTestAsync(CredWithKey(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task CredentialBundleContainsApiKey_OkRegardlessOfValueContents()
    {
        // The probe deliberately does not validate the key — that is the
        // runner's responsibility on the first real CLI invocation.
        var result = await NewProbe().SmokeTestAsync(CredWithKey("bogus"), CancellationToken.None);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task EmptyCredentialBundle_ReturnsFail_WithConfigurationHint()
    {
        var result = await NewProbe().SmokeTestAsync(EmptyCred(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("CODEYBOX_PI_API_KEY", result.FailureReason);
    }

    [Fact]
    public async Task CredentialWithUnrelatedEnvVars_ReturnsFail()
    {
        var credential = new AgentCredential(
            AgentKind.Pi,
            new Dictionary<string, string> { ["UNRELATED"] = "value" },
            new Dictionary<string, string>());

        var result = await NewProbe().SmokeTestAsync(credential, CancellationToken.None);

        Assert.False(result.Ok);
    }
}
