using CodeyBox.Agents;
using CodeyBox.Agents.DotNetOpencode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DotNetOpencodeQuotaFailureDetector"/>. Quota/auth
/// shapes pin the error frames recorded live against
/// 0.1.0-ci.20260905083303.33955573552.1 (bogus-key 401, no-model
/// invalid-request); the CLI is a BYOK front with no subscription meter, so
/// only provider HTTP shapes are recognised.
/// </summary>
public sealed class DotNetOpencodeQuotaFailureDetectorTests
{
    private static readonly DotNetOpencodeQuotaFailureDetector Detector = new();

    [Fact]
    public void Kind_IsDotNetOpencode()
    {
        Assert.Equal(AgentKind.DotNetOpencode, Detector.Kind);
    }

    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        Assert.Null(Detector.Detect(null, null));
        Assert.Null(Detector.Detect("", ""));
    }

    [Fact]
    public void RecordedProvider401_OnStdout_DetectedAsUnauthorized()
    {
        // Recorded live 2026-09-16: the error frame rides on stdout, not stderr.
        const string stdout =
            "{\"type\":\"error\",\"timestamp\":1789512734146,\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"error\":{\"type\":\"provider.auth\",\"message\":\"Provider request failed with HTTP 401.\",\"status\":401}}\n";

        var detection = Detector.Detect(null, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void RecordedNoModel_OnStdout_DetectedAsUnauthorized()
    {
        // Recorded live 2026-09-16: no usable model/catalog entry reads as an
        // auth-adjacent misconfiguration, not a quota limit.
        const string stdout =
            "{\"type\":\"error\",\"timestamp\":1789512652804,\"sessionID\":\"ses_0a7440db5001svgNvxOKaMl9i7\",\"error\":{\"type\":\"provider.invalid-request\",\"message\":\"No available model is present in the configured catalog.\"}}\n";

        var detection = Detector.Detect(null, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void ProviderRateLimit_DetectedAsRateLimited()
    {
        var detection = Detector.Detect("provider request failed: 429 Too Many Requests", null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void InsufficientQuota_DetectedAsLimitReached()
    {
        var detection = Detector.Detect(null, "error: insufficient_quota from provider");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void BareNumbersInCodeReview_NotDetected()
    {
        // Model output reviewing quota code must not gate dispatch: bare
        // numbers and the lone word "quota" carry no exhaustion verb.
        Assert.Null(Detector.Detect("retry after 429 ms in quota_manager.rs", null));
        Assert.Null(Detector.Detect(null, "the quota table schema changed"));
    }

    [Fact]
    public void SuccessfulRunText_NotDetected()
    {
        const string stdout =
            "{\"type\":\"step_start\",\"timestamp\":1,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_1\",\"sessionID\":\"ses_1\",\"messageID\":\"msg_1\",\"type\":\"step-start\"}}\n" +
            "{\"type\":\"step_finish\",\"timestamp\":2,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_1\",\"sessionID\":\"ses_1\",\"messageID\":\"msg_1\",\"type\":\"step-finish\",\"tokens\":{\"input\":10,\"output\":5,\"cache\":{\"read\":0,\"write\":0}}}}\n";

        Assert.Null(Detector.Detect(null, stdout));
    }

    [Fact]
    public void AdditionalPatterns_AppendedAfterDefaults()
    {
        var detector = new DotNetOpencodeQuotaFailureDetector(
            [new QuotaFailurePattern("custom-exhaustion-shape", QuotaFailureKind.LimitReached)]);

        var custom = detector.Detect("custom-exhaustion-shape seen", null);
        Assert.NotNull(custom);
        Assert.Equal(QuotaFailureKind.LimitReached, custom!.Kind);

        // Defaults still apply alongside extras.
        var builtin = detector.Detect("provider.auth failure", null);
        Assert.NotNull(builtin);
        Assert.Equal(QuotaFailureKind.Unauthorized, builtin!.Kind);
    }

    [Fact]
    public void EmptyAdditionalPatterns_BehavesLikeDefaults()
    {
        var detector = new DotNetOpencodeQuotaFailureDetector([]);

        var detection = detector.Detect("provider.auth failure", null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }
}
