using CodeyBox.Agents;
using CodeyBox.Agents.Autohand;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AutohandQuotaFailureDetector"/> over recorded real
/// autohand-cli 0.9.7 output (auth failure, $0-spend-limit quota refusal,
/// healthy run). Recorded messages are sanitised of workspace key ids.
/// </summary>
public sealed class AutohandQuotaFailureDetectorTests
{
    private static AutohandQuotaFailureDetector Detector() => new();

    [Fact]
    public void Kind_IsAutohand()
    {
        Assert.Equal(AgentKind.Autohand, Detector().Kind);
    }

    [Fact]
    public void Detect_RecordedAuthFailure_ReturnsUnauthorized()
    {
        // Recorded real shape (bad OpenRouter key, exit 1): the error frame
        // on stdout, echoed on stderr.
        const string stdout =
            "{\"type\":\"error\",\"message\":\"Authentication failed. Please verify your OpenRouter API key in ~/.autohand/config.json.\\nUser not found.\"}";
        const string stderr =
            "Authentication failed. Please verify your OpenRouter API key in ~/.autohand/config.json.\nUser not found.";

        var detection = Detector().Detect(stderr, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection.Kind);
    }

    [Fact]
    public void Detect_RecordedQuotaRefusal_ReturnsLimitReached()
    {
        // Recorded real shape ($0-spend-limit key against a paid model, exit
        // 1): carries BOTH "Access denied" and "Key limit exceeded". The
        // spend-limit row is ordered first so this parks as quota exhaustion
        // (LimitReached), not auth recovery.
        const string stdout =
            "{\"type\":\"error\",\"message\":\"Access denied. Your OpenRouter API key may not have permission for this model.\\nKey limit exceeded (total limit).\"}";

        var detection = Detector().Detect("Key limit exceeded (total limit).", stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection.Kind);
    }

    [Fact]
    public void Detect_GenericNonCompletingError_ReturnsNull()
    {
        // "Command did not complete successfully." is the generic give-up
        // shape (exit 0) — not evidence of quota/auth state, deliberately
        // unmatched so it never gates dispatch.
        const string stdout =
            "{\"type\":\"error\",\"message\":\"Command did not complete successfully.\"}";

        Assert.Null(Detector().Detect("Failed in 0m 00s · tokens unavailable used", stdout));
    }

    [Fact]
    public void Detect_HealthyRun_ReturnsNull()
    {
        const string stdout =
            "{\"type\":\"tool_end\",\"toolId\":\"call-1\",\"toolName\":\"read_file\",\"toolSuccess\":true}\n" +
            "{\"type\":\"result\",\"content\":\"The task is complete.\"}";

        Assert.Null(Detector().Detect("Completed in 0m 07s · 12.0k tokens used", stdout));
    }

    [Fact]
    public void Detect_EmptyInputs_ReturnsNull()
    {
        Assert.Null(Detector().Detect(null, null));
        Assert.Null(Detector().Detect(string.Empty, string.Empty));
    }

    [Fact]
    public void Detect_UserNotFoundProseAlone_ReturnsNull()
    {
        // "User not found" without the anchored provider sentence matches
        // reviewed prose (user-management code, docs) — never a signal.
        Assert.Null(Detector().Detect("Error: User not found in database seed.", "user not found"));
    }

    [Fact]
    public void Detect_RateLimitPhrase_ReturnsRateLimitExceeded()
    {
        var detection = Detector().Detect("HTTP 429 Too Many Requests: rate limit exceeded", null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection.Kind);
    }

    [Fact]
    public void Detect_AdditionalPatterns_AppendedAfterDefaults()
    {
        var detector = new AutohandQuotaFailureDetector(
            [new QuotaFailurePattern("custom-cap-shape", QuotaFailureKind.LimitReached)]);

        var detection = detector.Detect("custom-cap-shape hit", null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection.Kind);
        // Defaults still apply alongside extras.
        Assert.NotNull(detector.Detect("quota exceeded for project", null));
    }

    [Fact]
    public void Detect_BlankAdditionalPatterns_BehavesLikeDefaults()
    {
        var detector = new AutohandQuotaFailureDetector(
            [new QuotaFailurePattern(string.Empty, QuotaFailureKind.LimitReached)]);

        Assert.Null(detector.Detect("healthy output", "all good"));
    }
}
