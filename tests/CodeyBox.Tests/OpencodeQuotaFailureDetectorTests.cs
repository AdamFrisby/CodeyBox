using CodeyBox.Agents.Opencode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class OpencodeQuotaFailureDetectorTests
{
    private readonly OpencodeQuotaFailureDetector _detector = new();

    [Fact]
    public void Detect_PaymentRequired402_ClassifiesLimitReached()
    {
        // 402 surfaces when the opencode Go subscription has been billed to
        // its hard cap — treat as quota exhaustion so the router falls onto
        // a paid-fallback class member.
        var detection = _detector.Detect(stderr: "HTTP 402 Payment Required", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void Detect_InsufficientCredits_ClassifiesLimitReached()
    {
        var detection = _detector.Detect(stderr: "Error: insufficient credits for this request", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void Detect_QuotaWord_ClassifiesLimitReached()
    {
        var detection = _detector.Detect(stderr: "monthly quota exhausted", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void Detect_LimitReachedPhrase_ClassifiesLimitReached()
    {
        var detection = _detector.Detect(stderr: "Plan limit reached. Try again later.", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void Detect_Unauthorized_ClassifiesUnauthorized()
    {
        var detection = _detector.Detect(stderr: "401 Unauthorized: token rejected", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void Detect_StdoutSource_Recognised()
    {
        var detection = _detector.Detect(stderr: null, stdout: "Error: HTTP 402 Payment Required");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void Detect_NoMatch_ReturnsNull()
    {
        Assert.Null(_detector.Detect(stderr: "ordinary model refusal", stdout: null));
    }

    [Fact]
    public void Detect_EmptyInputs_ReturnsNull()
    {
        Assert.Null(_detector.Detect(stderr: null, stdout: null));
        Assert.Null(_detector.Detect(stderr: string.Empty, stdout: string.Empty));
    }

    [Fact]
    public void Kind_IsOpencode()
    {
        Assert.Equal(AgentKind.Opencode, _detector.Kind);
    }
}
