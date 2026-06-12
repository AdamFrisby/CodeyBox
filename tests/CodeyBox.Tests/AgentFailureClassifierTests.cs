using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="AgentFailureClassifier"/> and the default
/// <see cref="IAgentRunner.ClassifyFailure"/> implementation. Mid-iteration
/// quota fallback depends on this classification — a mis-classified work
/// failure as <see cref="AgentFailureKind.QuotaExhausted"/> would burn through
/// every class member on a task no agent can complete; the inverse would
/// silently fail items that a fallback could have rescued.
/// </summary>
public sealed class AgentFailureClassifierTests
{
    [Theory]
    [InlineData("[error] usage_limit reached: weekly cap")]
    [InlineData("hit your usage limit")]
    [InlineData("hit your limit")]
    [InlineData("RESOURCE_EXHAUSTED")]
    [InlineData("quota exceeded for project")]
    [InlineData("[API Error: You have exhausted your capacity on this model.]")]
    public void HardQuotaPatterns_Classified_AsHardQuota(string snippet)
    {
        var c = AgentFailureClassifier.Classify(stderr: snippet);
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.HardQuota, c.QuotaFailure);
        Assert.Equal(AgentFailureClassifier.HardQuotaReason, c.Reason);
    }

    [Theory]
    [InlineData("API Error: rate_limit_exceeded")]
    [InlineData("rate limit exceeded")]
    [InlineData("status 429 too many requests")]
    [InlineData("HTTP 529")]
    [InlineData("HTTP 429")]
    [InlineData("API Error: 429")]
    [InlineData("status 529")]
    [InlineData("overloaded_error")]
    [InlineData("exceeded the rate limit")]
    public void SoftRateLimitPatterns_Classified_AsSoftRateLimit(string snippet)
    {
        var c = AgentFailureClassifier.Classify(stderr: snippet);
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.SoftRateLimit, c.QuotaFailure);
        Assert.Equal(AgentFailureClassifier.SoftRateLimitReason, c.Reason);
    }

    [Theory]
    [InlineData("compile error: missing semicolon at line 42")]
    [InlineData("test failures: 3/100 assertions failed")]
    [InlineData("agent refused: cannot perform this task")]
    [InlineData("ENOENT: no such file 'foo.txt'")]
    public void NormalFailures_NotClassified_AsQuota(string snippet)
    {
        var c = AgentFailureClassifier.Classify(stderr: snippet);
        Assert.Equal(AgentFailureKind.Normal, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.None, c.QuotaFailure);
    }

    [Theory]
    [InlineData("API Error: 401 Unauthorized")]
    [InlineData("invalid_api_key supplied")]
    [InlineData("OAuth token expired; please reauthenticate")]
    public void AuthPatterns_Classified_AsAuthError(string snippet)
    {
        var c = AgentFailureClassifier.Classify(stderr: snippet);
        Assert.Equal(AgentFailureKind.AuthError, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.None, c.QuotaFailure);
    }

    [Theory]
    [InlineData("ECONNRESET while contacting api.anthropic.com")]
    [InlineData("Temporary failure in name resolution")]
    [InlineData("503 Service Unavailable")]
    [InlineData("socket hang up")]
    [InlineData("fetch failed")]
    public void NetworkPatterns_Classified_AsTransient(string snippet)
    {
        var c = AgentFailureClassifier.Classify(stderr: snippet);
        Assert.Equal(AgentFailureKind.TransientNetwork, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.None, c.QuotaFailure);
    }

    [Fact]
    public void Quota_BeatsNetwork_WhenBothPatternsPresent()
    {
        // A 429-with-ECONNRESET-tail must classify as quota — falling back to
        // quota/rate handling is still correct even when the native session
        // resume loop later chooses to spend its bounded soft-rate retry.
        var c = AgentFailureClassifier.Classify(
            stderr: "API rate_limit_exceeded\nECONNRESET while retrying");
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Kind);
    }

    [Fact]
    public void QuotaClassification_SeparatesHardQuotaFromSoftRateLimitReason()
    {
        var hard = AgentFailureClassifier.Classify(stderr: "usage_limit reached");
        Assert.Equal(AgentFailureKind.QuotaExhausted, hard.Kind);
        Assert.Equal(AgentFailureClassifier.HardQuotaReason, hard.Reason);
        Assert.Equal(AgentQuotaFailureKind.HardQuota, hard.QuotaFailure);

        var soft = AgentFailureClassifier.Classify(stderr: "API Error: 429 rate_limit_exceeded");
        Assert.Equal(AgentFailureKind.QuotaExhausted, soft.Kind);
        Assert.Equal(AgentFailureClassifier.SoftRateLimitReason, soft.Reason);
        Assert.Equal(AgentQuotaFailureKind.SoftRateLimit, soft.QuotaFailure);
    }

    [Fact]
    public void Quota_BeatsAuth_WhenBothPatternsPresent()
    {
        // Defence against a re-ordering bug: a 429 with an "invalid_api_key"
        // tail must still classify as quota. Reversing the check order would
        // silently misclassify quota events as auth failures, sending operators
        // to rotate credentials rather than waiting for the quota window.
        var c = AgentFailureClassifier.Classify(
            stderr: "API Error: rate_limit_exceeded\nfollow-up: invalid_api_key reported");
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Kind);
    }

    [Fact]
    public void Auth_BeatsNetwork_WhenBothPatternsPresent()
    {
        var c = AgentFailureClassifier.Classify(
            stderr: "API Error: 401 Unauthorized; subsequent ECONNRESET");
        Assert.Equal(AgentFailureKind.AuthError, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.None, c.QuotaFailure);
    }

    [Fact]
    public void EmptyOutput_ClassifiedAsUnknown()
    {
        var c = AgentFailureClassifier.Classify(stderr: null, stdout: null);
        Assert.Equal(AgentFailureKind.Unknown, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.None, c.QuotaFailure);
    }

    [Fact]
    public void DefaultClassifyFailure_OnSuccessResult_ReturnsNormal()
    {
        IAgentRunner runner = new ProbeOnlyRunner();
        var c = runner.ClassifyFailure(new AgentResult(true, "ok", "", null));
        Assert.Equal(AgentFailureKind.Normal, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.None, c.QuotaFailure);
    }

    [Fact]
    public void DefaultClassifyFailure_DelegatesToSharedClassifier()
    {
        IAgentRunner runner = new ProbeOnlyRunner();
        var c = runner.ClassifyFailure(new AgentResult(false, "exit 1", "", "RESOURCE_EXHAUSTED"));
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Kind);
    }

    private sealed class ProbeOnlyRunner : IAgentRunner
    {
        public AgentKind Kind => new("probe");
        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => throw new NotSupportedException("test fixture only");
    }
}
