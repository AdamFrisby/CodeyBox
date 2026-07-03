using CodeyBox.Agents;
using CodeyBox.Agents.Antigravity;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class AntigravityQuotaFailureDetectorTests
{
    private readonly AntigravityQuotaFailureDetector _detector = new();

    [Fact]
    public void Detect_WeeklyLockoutWithStructuredReset_ParksUntilLockoutEnd()
    {
        // The AI Pro weekly cap surfaces with a structured quota_metadata.lockout_until
        // alongside human-readable text. A 7-day lockout must NOT churn the breaker;
        // it must surface the absolute reset so the work item parks WaitingForQuotaReset
        // until then.
        var lockoutUntil = DateTimeOffset.UtcNow.AddDays(6).AddHours(23);
        var stdout =
            "{\"type\":\"result\",\"status\":\"error\","
            + "\"quota_metadata\":{\"lockout_until\":\"" + lockoutUntil.ToString("o") + "\"},"
            + "\"error\":{\"message\":\"Weekly limit reached — account locked until " + lockoutUntil.ToString("o") + "\"}}";

        var detection = _detector.Detect(stderr: null, stdout: stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        Assert.InRange(detection.ResetAt!.Value, lockoutUntil.AddMinutes(-1), lockoutUntil.AddMinutes(1));
    }

    [Fact]
    public void Detect_ResourceExhaustedStderr_ClassifiesRateLimit()
    {
        var detection = _detector.Detect(stderr: "RESOURCE_EXHAUSTED reset after 1h17m", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
    }

    [Fact]
    public void Detect_ConsumerQuota429_ParsesRelativeResetToPreciseWindow()
    {
        // agy's consumer-tier 429 reports its rolling-window reset as a RELATIVE
        // duration ("Resets in 8m14s"), not an absolute ISO timestamp. The item
        // must park in WaitingForQuotaReset with resetAt ≈ now + 494s so it
        // retries right after the window clears — not on a coarse default backoff.
        var before = DateTimeOffset.UtcNow;
        var stderr = "RESOURCE_EXHAUSTED (code 429): Individual quota reached (Resets in 8m14s)";

        var detection = _detector.Detect(stderr, stdout: null);
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        // 8m14s = 494s. Bracket by the wall-clock reads to stay flake-free.
        Assert.InRange(
            detection.ResetAt!.Value,
            before.AddSeconds(494),
            after.AddSeconds(494));
    }

    [Fact]
    public void Detect_CapturedTerminalRegionWithRelativeReset_ParksWithPreciseReset()
    {
        // End-to-end over the shape the runner folds into Stderr on a terminal
        // agy 429: ExtractTerminalErrorRegion slices the glog tail, then Detect
        // classifies it and parses the relative reset off the same terminal line.
        var glog = "Model resolved: gemini-3.5-flash\n"
            + "applyAuthResult: authMethod=consumer\n"
            + "RESOURCE_EXHAUSTED (code 429): Individual quota reached (Resets in 8m14s)";
        var region = AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(glog);
        Assert.NotNull(region);

        var before = DateTimeOffset.UtcNow;
        var detection = _detector.Detect(stderr: region, stdout: null);
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        Assert.InRange(detection.ResetAt!.Value, before.AddSeconds(494), after.AddSeconds(494));
    }

    [Fact]
    public void Detect_ResourceExhaustedWithoutResetDuration_ClassifiesWithNullReset()
    {
        // A RESOURCE_EXHAUSTED 429 that carries NO parseable reset duration must
        // still classify as a rate-limit — with a null ResetAt — so the router
        // falls back to its default backoff rather than null-crashing or silently
        // dropping the detection. (Exercises the `reset = … ?? null` fallback.)
        var detection = _detector.Detect(
            stderr: "RESOURCE_EXHAUSTED (code 429): Individual quota reached", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.Null(detection.ResetAt);
    }

    [Fact]
    public void ResetParser_RelativeDuration_ComputesFromInjectedClock()
    {
        // The relative reset is now + duration off an INJECTED clock, so the parked
        // resetAt is deterministic (no wall-clock bracketing needed). "8m14s" = 494s.
        var now = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);
        var reset = QuotaResetParser.TryParseResetAt(
            new[] { "RESOURCE_EXHAUSTED (code 429): Individual quota reached (Resets in 8m14s)" },
            utcNow: now);

        Assert.Equal(now.AddSeconds(494), reset);
    }

    [Fact]
    public void ResetParser_ZeroDurationPrefixBeforeRealDuration_DoesNotShadowRealWindow()
    {
        // A non-duration "resets in a moment" prefix yields an all-zero first match;
        // scanning must continue to the real "retry after 8m" later in the SAME
        // string instead of bailing to the coarse default backoff.
        var now = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);
        var reset = QuotaResetParser.TryParseResetAt(
            new[] { "quota resets in a moment; retry after 8m" },
            utcNow: now);

        Assert.Equal(now.AddMinutes(8), reset);
    }

    [Fact]
    public void Detect_CleanNoOpRunWithoutQuotaMarker_ReturnsNoDetection()
    {
        // A genuine no-op — agy ran, emitted its ordinary diagnostics, and made no
        // file changes with NO 429 anywhere — must NOT be misread as a quota park.
        // It has to fall through to the "produced no changes" terminal failure.
        var glog = "Model resolved: gemini-3.5-flash\n"
            + "applyAuthResult: authMethod=consumer\n"
            + "read 12 files, wrote 0 files\n"
            + "done";

        Assert.Null(AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(glog));
        Assert.Null(_detector.Detect(stderr: glog, stdout: null));
    }

    [Fact]
    public void Detect_AbsoluteLockoutInPlainText_StillExtractsReset()
    {
        var when = DateTimeOffset.UtcNow.AddHours(48);
        var stdout = $"agent_error: account locked until {when:o} due to weekly cap";

        var detection = _detector.Detect(stderr: null, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
    }

    [Fact]
    public void Detect_Unauthorized_FlagsAsUnauthorized()
    {
        var detection = _detector.Detect(stderr: "API Error: 401 invalid token", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void Detect_GenericRuntimeError_ReturnsNull()
    {
        var stdout = """{"type":"result","status":"error","error":{"message":"compilation failed: missing semicolon"}}""";
        Assert.Null(_detector.Detect(stderr: null, stdout));
    }

    [Fact]
    public void Detect_EmptyStreams_ReturnsNull()
    {
        Assert.Null(_detector.Detect(stderr: null, stdout: null));
        Assert.Null(_detector.Detect(stderr: "", stdout: ""));
    }

    [Fact]
    public void ExtractTerminalErrorRegion_TerminalMarkerAtEnd_ReturnsFromLastMarkerToEnd()
    {
        // The terminal 429 sits at the end of the cumulative glog; the region is the
        // slice from the last marker to end — the leading non-marker noise is dropped
        // so only the terminal cause reaches the classifiers.
        var glog = "Model resolved: gemini-3.5-flash\n"
            + "applyAuthResult: authMethod=consumer\n"
            + "RESOURCE_EXHAUSTED (code 429): Individual quota reached (Resets in 8m14s)";

        var region = AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(glog);

        Assert.NotNull(region);
        Assert.Contains("RESOURCE_EXHAUSTED", region);
        Assert.Contains("Resets in 8m14s", region); // reset hint preserved for Detect()
        Assert.DoesNotContain("Model resolved", region!);
        // And the sliced region still classifies as a rate-limit via Detect().
        var detection = _detector.Detect(stderr: region, stdout: null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void ExtractTerminalErrorRegion_MarkerOutsideTailWindow_ReturnsNull()
    {
        // A recovered-then-cleared 429 followed by lots of further work (the run then
        // ends on a markerless failure) must NOT be surfaced: the marker scrolled out
        // of the tail window, so the region is null and nothing reaches the classifiers.
        var lines = new List<string> { "RESOURCE_EXHAUSTED (code 429): recovered after retry" };
        for (var i = 0; i < 60; i++) lines.Add($"tool call {i}");
        lines.Add("Error: timed out waiting for response");

        Assert.Null(AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(string.Join("\n", lines)));
    }

    [Fact]
    public void ExtractTerminalErrorRegion_NoMarker_ReturnsNull()
    {
        Assert.Null(AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(
            "Model resolved: gemini-3.5-flash\nwrote 3 files\ndone"));
        Assert.Null(AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(null));
        Assert.Null(AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(""));
    }
}
