using CodeyBox.Agents;
using CodeyBox.Agents.CavemanCode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="CavemanCodeQuotaFailureDetector"/>. Every row
/// pins a production pattern to the exact stderr/stdout shape that motivated
/// it; removing or mistyping a pattern breaks the corresponding row.
/// </summary>
public sealed class CavemanCodeQuotaFailureDetectorTests
{
    private readonly CavemanCodeQuotaFailureDetector _detector = new();

    [Fact]
    public void Kind_IsCavemanCode()
    {
        Assert.Equal(AgentKind.CavemanCode, _detector.Kind);
    }

    [Fact]
    public void Detect_NullStreams_ReturnsNull()
    {
        Assert.Null(_detector.Detect(null, null));
        Assert.Null(_detector.Detect("", ""));
    }

    // --- Unauthorized: missing-key output observed live (0.65.2) ------------

    [Fact]
    public void Detect_NoApiKeyFound_ClassifiesUnauthorized()
    {
        // Exact first line of the live keyless run; stdout carries it because
        // the CLI exits 0 on missing auth.
        var detection = _detector.Detect(stderr: null, stdout: "No API key found for unknown.");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void Detect_SetApiKeyEnvHint_ClassifiesUnauthorized()
    {
        var detection = _detector.Detect(
            stderr: "Use /login or set an API key environment variable.",
            stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void Detect_BareUnauthorizedWord_DoesNotClassify()
    {
        // The bare word appears in code under review (access-control prose);
        // only the HTTP-anchored rows may match.
        Assert.Null(_detector.Detect("discusses Unauthorized access in the handler", null));
    }

    // --- RateLimitExceeded: transient provider refusals ---------------------

    public static IEnumerable<object[]> RateLimitExceededSamples()
    {
        // Shared anchored 429 rows (mirrors the Copilot/opencode detectors).
        yield return new object[] { "Error: HTTP 429 from model endpoint", "HTTP 429" };
        yield return new object[] { "Status: 429 Too Many Requests, retry later", "429 Too Many Requests" };
        yield return new object[] { "Upstream: rate_limit_exceeded, backing off", "rate_limit_exceeded" };
        yield return new object[] { "Provider says: Too Many Requests", "Too Many Requests" };
        // Provider error code matched by the CLI's own retry classifier.
        yield return new object[] { "Request failed with overloaded_error, retrying", "overloaded_error" };
    }

    [Theory]
    [MemberData(nameof(RateLimitExceededSamples))]
    public void Detect_RateLimitShapes_AllClassifyAsRateLimitExceeded(string stderr, string pattern)
    {
        var detection = _detector.Detect(stderr: stderr, stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.Contains(pattern, stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_BareOverloadedWord_DoesNotClassify()
    {
        // Suffix-anchored: prose about an "overloaded" server must not bench
        // the agent; only the provider's overloaded_error code matches.
        Assert.Null(_detector.Detect("the overloaded server dropped connections", null));
    }

    [Fact]
    public void Detect_Bare429Number_DoesNotClassify()
    {
        // A bare number in reviewed code (retry counts, line numbers) must
        // not gate dispatch — rows stay anchored with companion text.
        Assert.Null(_detector.Detect("retry 429 times per the spec", null));
    }

    [Fact]
    public void Detect_UnrelatedError_ReturnsNull()
    {
        Assert.Null(_detector.Detect("Error: something entirely different broke", "plain output"));
    }
}
