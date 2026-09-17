using CodeyBox.Agents.Qwen;
using CodeyBox.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="QwenQuotaFailureDetector"/> and
/// <see cref="QwenSmokeProbe"/>. Detector fixtures use the provider-error
/// shapes qwen relays verbatim in <c>result.error.message</c> and the
/// stderr <c>AlreadyReportedError</c> envelope (recorded live against qwen
/// 0.24.0), plus the operator-extensibility hook.
/// </summary>
public sealed class QwenQuotaFailureDetectorTests
{
    private static readonly QwenQuotaFailureDetector Detector = new();

    [Fact]
    public void Kind_IsQwen()
    {
        Assert.Equal(AgentKind.Qwen, Detector.Kind);
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
        // Live shape: bogus OPENAI_API_KEY, exit-1 run, error on stdout.
        const string stdout =
            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true," +
            "\"error\":{\"message\":\"[API Error: 401 Missing Authentication header]\"}}";
        const string stderr =
            "{\"error\":{\"type\":\"AlreadyReportedError\",\"message\":\"[API Error: 401 Missing Authentication header]\",\"code\":1}}";

        var detection = Detector.Detect(stderr, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void StderrEnvelopeAlone_DetectedAsUnauthorized()
    {
        const string stderr =
            "{\"error\":{\"type\":\"AlreadyReportedError\",\"message\":\"[API Error: 401 Missing Authentication header]\",\"code\":1}}";

        var detection = Detector.Detect(stderr, null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void LiveSpendLimitError_DetectedAsLimitReached()
    {
        // Live shape: paid model on a $0-spend-limit OpenRouter key.
        const string stdout =
            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true," +
            "\"error\":{\"message\":\"[API Error: 403 Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/abc]\"}}";

        var detection = Detector.Detect(null, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void RateLimitError_DetectedAsRateLimitExceeded()
    {
        var detection = Detector.Detect(
            null,
            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"error\":{\"message\":\"[API Error: 429 rate_limit_exceeded]\"}}");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void BareNumber_NotDetected()
    {
        // Model output citing "403"/"429" (retry counts, code under review)
        // must not gate dispatch — patterns stay anchored to provider
        // phrasing.
        Assert.Null(Detector.Detect(null, "retry 429 times then back off"));
        Assert.Null(Detector.Detect("http 403 in the test", null));
        Assert.Null(Detector.Detect("quota", "quota"));
    }

    [Fact]
    public void HealthyRun_NotDetected()
    {
        const string stdout =
            "{\"type\":\"system\",\"subtype\":\"init\",\"qwen_code_version\":\"0.24.0\"}\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\"}\n";

        Assert.Null(Detector.Detect(null, stdout));
    }

    [Fact]
    public void OperatorExtraPatterns_Appended()
    {
        var detector = new QwenQuotaFailureDetector(
            [new QuotaFailurePattern("qwen says no", QuotaFailureKind.LimitReached)]);

        var detection = detector.Detect(null, "qwen says no today");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }
}

/// <summary>
/// Tests for <see cref="QwenSmokeProbe"/>: credential-presence only, no network.
/// </summary>
public sealed class QwenSmokeProbeTests
{
    private static QwenSmokeProbe NewProbe() => new(NullLogger<QwenSmokeProbe>.Instance);

    private static AgentCredential CredWithKey(string key = "sk-or-v1-test") =>
        new(AgentKind.Qwen,
            new Dictionary<string, string> { ["OPENAI_API_KEY"] = key },
            new Dictionary<string, string>());

    private static AgentCredential EmptyCred() =>
        new(AgentKind.Qwen,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

    [Fact]
    public async Task Kind_IsQwen()
    {
        Assert.Equal(AgentKind.Qwen, NewProbe().Kind);
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
        Assert.Contains("CODEYBOX_QWEN_API_KEY", result.FailureReason);
    }

    [Fact]
    public async Task CredentialWithUnrelatedEnvVars_ReturnsFail()
    {
        var credential = new AgentCredential(
            AgentKind.Qwen,
            new Dictionary<string, string> { ["UNRELATED"] = "value" },
            new Dictionary<string, string>());

        var result = await NewProbe().SmokeTestAsync(credential, CancellationToken.None);

        Assert.False(result.Ok);
    }
}
